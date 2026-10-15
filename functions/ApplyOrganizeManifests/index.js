/*
 * ApplyOrganizeManifests -- timer, every 15 minutes.
 *
 * WHY
 * The job-folder organizer (a scheduled Claude task) decides a name and a
 * destination folder for each loose file in a job's root, picks social
 * photos, and writes a post brief. Until now those decisions were applied by
 * PowerShell pasted by hand. This applies them instead, from a manifest JSON
 * per job dropped into SharePoint:
 *     Claude/Nightly Organize/Manifests/<anything>.json
 * After a manifest is processed it moves to Manifests/Applied, with
 * "<name>.results.json" beside it: one row per action, with a status
 * (DONE / ALREADY DONE / BULK MOVED / DRY RUN / SKIP / NOT FOUND / ERROR / LEFT IN ROOT).
 * Schema and the dryRun flag are in README.md ("Organize manifests").
 *
 * HOW IT REACHES JOBTREAD
 * It never calls JobTread. It only moves and renames items inside the job
 * folder; SharePointSync sees those in its delta and updates the JT files.
 * Social copies, briefs and the manifests themselves live outside every job
 * folder, so SharePointSync ignores them and no JT file is created.
 *
 * HARD LIMITS (each enforced below, not just assumed)
 *   - Never deletes anything. Moves and renames use conflictBehavior "fail",
 *     so nothing is ever overwritten. The brief is the one overwrite (PUT
 *     replace into the package), by design.
 *   - Moves only files sitting in the job ROOT with the exact name and exact
 *     byte size the manifest expects, and only to a folder inside the same
 *     job folder (destPath is built from the job folder id; ".." and empty
 *     segments are refused).
 *   - Touches only items the manifest names.
 *   - Photos/Job Walk, Progress and Completed may only be renumbered to
 *     1 Job Walk, 2 Progress, 3 Completed, and only when the numbered folder
 *     does not exist. Package folders are never renamed or moved.
 *   - dryRun anything but false: every check runs, the results JSON is
 *     written, and nothing in a job folder or package is changed.
 *
 * TWO SKIPS THAT PROTECT JOBTREAD
 *   - Files over 100 MB: SharePointSync skips them before it checks for a
 *     move, so the move would never reach JT and the two sides would drift.
 *   - A move whose newName matches ANOTHER file in the job root. When a file
 *     that started in JT moves, JT fires fileUpdated back to JobTreadWebhook,
 *     which deletes the file named <newName> from the OLD folder (the root).
 *     If an unrelated root file has that name, it would be deleted.
 *
 * TIME BUDGET
 * The Function App kills a run at 5 minutes. After RUN_BUDGET_MS the run
 * stops starting new actions, leaves the unfinished manifest in
 * Applied/processing, and the next run picks it up; actions already done
 * report ALREADY DONE.
 *
 * ONE RUN AT A TIME
 * A run lock (a SyncState row with a 10 minute expiry) makes a second
 * invocation exit, and each manifest is claimed by moving it into
 * Applied/processing before anything else happens; a manifest that cannot
 * be claimed is skipped. The lock is only taken when a manifest exists, so
 * idle runs still write nothing.
 *
 * Idle when there is no manifest: a token, the site/drive lookups and one
 * folder listing, and no writes.
 */

const https = require('https');
const zlib = require('zlib');
const { TableClient } = require('@azure/data-tables');

const GRAPH = 'https://graph.microsoft.com/v1.0';
const MANIFESTS_PATH = 'Claude/Nightly Organize/Manifests';
const APPLIED_FOLDER = 'Applied';
const PROCESSING_FOLDER = 'processing';   // Applied/processing: claimed, not finished
const POST_PACKAGES_PATH = '08 Sales & Marketing/Social Media/Post Packages';
const SOCIAL_FOLDERS = ['Before and After', 'Post-Worthy', 'Our Crew'];
const PROTECTED_FOLDERS = ['photos/job walk', 'photos/progress', 'photos/completed'];
/* The only renames allowed on those: legacy name -> numbered name, inside Photos. */
const LEGACY_RENAMES = { 'Job Walk': '1 Job Walk', 'Progress': '2 Progress', 'Completed': '3 Completed' };
const MAX_BYTES = 100 * 1024 * 1024;   // same cap as SharePointSync
const MOVE_GAP_MS = 750;
const RUN_BUDGET_MS = 3.5 * 60 * 1000;
/* Longer than the 5 minute Function App limit, so a run that is killed
 * without releasing the lock frees it before the next timer fire. */
const LOCK_MS = 10 * 60 * 1000;

/* Every outside call goes through api, so the logic can be exercised against
 * an in-memory drive without touching SharePoint. */
const api = {
  request: httpRequest,
  download: downloadFile,
  getJobMapping: getJobMapping,
  acquireLock: acquireLock,
  releaseLock: releaseLock,
  sleep: function (ms) { return new Promise(function (r) { setTimeout(r, ms); }); },
  now: function () { return Date.now(); }
};

function OutOfTime() { this.outOfTime = true; this.message = 'run time budget reached'; }

module.exports = async function (context, myTimer) {
  const started = api.now();
  try {
    const token = await getAccessToken();
    const siteId = await getSiteId(token);
    const driveId = await getDriveId(token, siteId);

    const folder = await getItemByPath(token, driveId, MANIFESTS_PATH);
    if (!folder) {
      context.log('ApplyOrganizeManifests: "' + MANIFESTS_PATH + '" not found, nothing to do');
      return;
    }
    const isManifest = function (f) { return f.file && /\.json$/i.test(f.name); };
    const byName = function (a, b) { return a.name.localeCompare(b.name); };
    const waiting = (await listAllChildren(token, driveId, folder.id)).filter(isManifest).sort(byName);
    const applied = await getChildByName(token, driveId, folder.id, APPLIED_FOLDER);
    const processing = applied ? await getChildByName(token, driveId, applied.id, PROCESSING_FOLDER) : null;
    const stranded = processing
      ? (await listAllChildren(token, driveId, processing.id)).filter(isManifest).sort(byName) : [];
    if (!waiting.length && !stranded.length) {
      context.log('ApplyOrganizeManifests: no manifests, nothing to do');
      return;
    }

    /*
     * RUN LOCK (2026-10-14). A manual "Run" at 19:44 and the timer at 19:45
     * both processed the same manifests: duplicate results files and a 409
     * on the second results upload. One SyncState row with an expiry; a
     * second invocation that cannot take it exits without writing anything.
     */
    const owner = 'run-' + started + '-' + Math.random().toString(36).slice(2, 8);
    if (!(await api.acquireLock(owner))) {
      context.log('ApplyOrganizeManifests: another run holds the lock, exiting');
      return;
    }
    try {
      const appliedId = (applied || await ensureChildFolder(token, driveId, folder.id, APPLIED_FOLDER)).id;
      const processingId = (processing || await ensureChildFolder(token, driveId, appliedId, PROCESSING_FOLDER)).id;
      const run = {
        context: context, token: token, driveId: driveId, started: started,
        checkTime: function () { if (api.now() - started > RUN_BUDGET_MS) throw new OutOfTime(); }
      };
      context.log('ApplyOrganizeManifests: ' + waiting.length + ' new and ' + stranded.length +
                  ' unfinished manifest(s) to process');

      /* Unfinished ones first: they were claimed by a run that ran out of
       * time or was killed. Holding the lock means no other run owns them. */
      const queue = stranded.map(function (m) { return { item: m, claimed: true }; })
        .concat(waiting.map(function (m) { return { item: m, claimed: false }; }));
      for (const q of queue) {
        if (api.now() - started > RUN_BUDGET_MS) {
          context.log.warn('ApplyOrganizeManifests: time budget reached, remaining manifests wait for the next run');
          break;
        }
        let m = q.item;
        if (!q.claimed) {
          /* CLAIM: move it into Applied/processing before touching anything,
           * only if it is unchanged since we listed it (If-Match on its eTag:
           * a second claimant gets 412, since moving an item to where it
           * already is would otherwise succeed). If the claim fails, skip it. */
          try {
            m = await patchItem(token, driveId, m.id, { parentReference: { id: processingId } }, 'fail',
                                m.eTag ? { 'If-Match': m.eTag } : null);
            m.name = m.name || q.item.name;
          } catch (e) {
            context.log.warn('ApplyOrganizeManifests: could not claim ' + q.item.name + ', skipping: ' + e.message);
            continue;
          }
        }
        const finished = await processManifest(run, m, appliedId);
        if (!finished) break;
      }
    } finally {
      await api.releaseLock(owner).catch(function (e) {
        context.log.warn('ApplyOrganizeManifests: could not release the lock (it expires on its own): ' + e.message);
      });
    }
  } catch (err) {
    const detail = (err && (err.stack || err.message)) || String(err);
    context.log.error('ApplyOrganizeManifests FAILED: ' + detail);
  }
};

module.exports._test = { api: api, globToRegex: globToRegex, buildBriefDocx: buildBriefDocx, sameLink: sameLink, MANIFESTS_PATH: MANIFESTS_PATH, POST_PACKAGES_PATH: POST_PACKAGES_PATH };

// ------------------------------------------------------------ one manifest

/* Returns false when the run ran out of time part-way (manifest left in place). */
async function processManifest(run, m, appliedId) {
  const context = run.context;
  const rows = [];
  const report = {
    manifest: m.name,
    job: '',
    jtJobId: '',
    dryRun: true,
    startedAt: new Date(api.now()).toISOString(),
    finishedAt: null,
    summary: {},
    warnings: [],
    rows: rows
  };

  let manifest = null;
  try {
    const buf = await downloadItem(run.token, run.driveId, m.id);
    manifest = JSON.parse(buf.toString('utf8').replace(/^﻿/, ''));
    if (!manifest || typeof manifest !== 'object' || Array.isArray(manifest)) throw new Error('not a JSON object');
  } catch (e) {
    manifest = null;
    rows.push(row('manifest', m.name, '', 'ERROR', 'Could not read manifest: ' + e.message));
  }

  if (manifest) {
    report.job = String(manifest.job || '');
    report.jtJobId = String(manifest.jtJobId || '');
    /* Only a literal false applies changes. A typo or a missing flag is a dry run. */
    report.dryRun = manifest.dryRun !== false;
    try {
      await applyManifest(run, manifest, report.dryRun, rows, report.warnings);
    } catch (e) {
      if (e && e.outOfTime) {
        context.log.warn('ApplyOrganizeManifests: ' + m.name + ' not finished before the time budget; ' +
                         'left in Applied/processing, the next run resumes it (finished actions report ALREADY DONE)');
        return false;
      }
      rows.push(row('manifest', m.name, '', 'ERROR', e.message));
    }
  }

  report.finishedAt = new Date(api.now()).toISOString();
  for (const r of rows) report.summary[r.status] = (report.summary[r.status] || 0) + 1;

  /* Results first, then the manifest. If the results write fails the
   * manifest stays in Applied/processing and the next run retries it; a
   * rerun is safe (ALREADY DONE). Results are written with replace, so a
   * retry overwrites a half-finished attempt instead of failing on it. */
  const taken = new Set((await listAllChildren(run.token, run.driveId, appliedId))
    .map(function (c) { return c.name.toLowerCase(); }));
  let stem = m.name.replace(/\.json$/i, '');
  if (taken.has((stem + '.json').toLowerCase()) || taken.has((stem + '.results.json').toLowerCase())) {
    stem = stem + ' ' + new Date(api.now()).toISOString().replace(/[-:]/g, '').replace(/\..*$/, '').replace('T', '-');
  }
  await putContent(run.token, run.driveId, appliedId, stem + '.results.json',
                   Buffer.from(JSON.stringify(report, null, 2), 'utf8'), 'application/json', 'replace');
  await patchItem(run.token, run.driveId, m.id, { name: stem + '.json', parentReference: { id: appliedId } }, 'fail');

  for (const w of report.warnings) context.log.warn('ApplyOrganizeManifests: ' + m.name + ': ' + w);
  const counts = Object.keys(report.summary).map(function (k) { return report.summary[k] + ' ' + k; }).join(', ');
  context.log('ApplyOrganizeManifests: ' + m.name + ' | JT ' + report.jtJobId + ' "' + report.job + '" | ' +
              (report.dryRun ? 'DRY RUN' : 'APPLIED') + ' | ' + (counts || 'no actions'));
  return true;
}

async function applyManifest(run, man, dryRun, rows, warnings) {
  const token = run.token;
  const driveId = run.driveId;

  const jtJobId = typeof man.jtJobId === 'string' ? man.jtJobId.trim() : '';
  if (!jtJobId) throw new Error('Manifest has no "jtJobId". Manifest stopped.');
  const moves = man.moves === undefined || man.moves === null ? [] : man.moves;
  const renames = man.renames === undefined || man.renames === null ? [] : man.renames;
  if (!Array.isArray(moves)) throw new Error('"moves" must be an array');
  if (!Array.isArray(renames)) throw new Error('"renames" must be an array');
  const bulkMoves = man.bulkMoves === undefined || man.bulkMoves === null ? [] : man.bulkMoves;
  if (!Array.isArray(bulkMoves)) throw new Error('"bulkMoves" must be an array');

  /* Match the job by its JT job id, the JobFolders row key, and open the
   * folder by the SharePoint item id stored there: the same id both sync
   * directions use, and no /shares permission needed. Names drift (JT "#"
   * becomes "-", trailing periods are stripped, jobs get renamed), so "job"
   * and "folderLink" are only cross-checks that add a warning. */
  const mapping = await api.getJobMapping(jtJobId);
  if (!mapping || !mapping.spFolderDriveId) {
    throw new Error('No JobFolders row for JT job ' + jtJobId + ', so it is not a synced job folder. Manifest stopped.');
  }
  const jobRes = await graph('GET', GRAPH + '/drives/' + driveId + '/items/' + mapping.spFolderDriveId +
                             '?$select=id,name,folder,deleted', { token: token });
  const job = jobRes.status === 404 ? null : ok(jobRes, 'Open job folder');
  if (!job || !job.folder || job.deleted) {
    throw new Error('JobFolders row for JT job ' + jtJobId + ' points at folder ' + mapping.spFolderDriveId +
                    ', which no longer exists. Manifest stopped.');
  }
  const expected = typeof man.job === 'string' ? man.job : '';
  if (job.name !== expected) {
    warnings.push('Job folder for JT job ' + jtJobId + ' is named "' + job.name + '", manifest says "' + expected +
                  '"; used the JobFolders folder');
  }
  if (!sameLink(man.folderLink, mapping.spFolderUrl)) {
    warnings.push('folderLink does not match the JobFolders link for JT job ' + jtJobId + ' (' +
                  (mapping.spFolderUrl || 'none stored') + '); used the JobFolders folder');
  }

  const ctx = {
    run: run, token: token, driveId: driveId, dryRun: dryRun, rows: rows,
    jobId: job.id, children: new Map(), folders: new Map(),
    packageName: typeof man.package === 'string' ? man.package.trim() : '',
    pkg: undefined, pkgError: null, wroteOnce: false
  };

  /* Root files by lower-case name: SharePoint names are case-insensitive. */
  const rootFiles = new Map();
  for (const c of await children(ctx, job.id)) if (c.file) rootFiles.set(c.name.toLowerCase(), c);
  const named = new Set();
  const wouldLeave = new Set();

  for (let i = 0; i < moves.length; i++) {
    run.checkTime();
    const mv = moveFields(moves[i]);
    if (typeof mv.expectedName === 'string') named.add(mv.expectedName.toLowerCase());
    try {
      await doMove(ctx, mv, rootFiles, wouldLeave);
    } catch (e) {
      if (e && e.outOfTime) throw e;
      rows.push(row('move', String(mv.expectedName || '#' + (i + 1)), String(mv.destPath || ''), 'ERROR', e.message));
    }
  }

  for (let i = 0; i < renames.length; i++) {
    run.checkTime();
    const rn = renames[i] || {};
    const inPkg = rn.in === 'package';
    if (!inPkg && !String(rn.folderPath || '').trim()) {
      if (typeof rn.oldName === 'string') named.add(rn.oldName.toLowerCase());
      if (typeof rn.newName === 'string') named.add(rn.newName.toLowerCase());
    }
    try {
      await doRename(ctx, man, rn, rootFiles, wouldLeave);
    } catch (e) {
      if (e && e.outOfTime) throw e;
      rows.push(row('rename', String(rn.oldName || '#' + (i + 1)), String(rn.folderPath || ''), 'ERROR', e.message));
    }
  }

  /* Bulk rules run last, on what is still in the root. Every file the
   * "moves" or root "renames" named is already in `named`, so explicit rows
   * always win over a bulk rule. */
  if (bulkMoves.length) await doBulkMoves(ctx, bulkMoves, rootFiles, named, wouldLeave);

  if (man.brief) {
    run.checkTime();
    try {
      await doBrief(ctx, man);
    } catch (e) {
      if (e && e.outOfTime) throw e;
      rows.push(row('brief', String(man.brief.file || ''), String(man.package || ''), 'ERROR', e.message));
    }
  }

  /* LEFT IN ROOT: files still in the root (or, in a dry run, files that
   * would still be there) that the manifest never named. */
  let remaining;
  if (dryRun) {
    remaining = Array.from(rootFiles.values()).filter(function (f) { return !wouldLeave.has(f.id); });
  } else {
    remaining = (await listAllChildren(token, driveId, job.id)).filter(function (f) { return f.file; });
  }
  for (const f of remaining) {
    if (!named.has(f.name.toLowerCase())) {
      rows.push(row('root', f.name, '', 'LEFT IN ROOT', 'not named in the manifest (' + f.size + ' bytes)'));
    }
  }
}

// ------------------------------------------------------------ moves

/* The nightly writes from / size / toFolder; accept them as aliases. The
 * canonical name wins when both are present. Other fields (jtJobNumber,
 * spFolderItemId, batchDate, source, notes, jtFileId, ...) are ignored. */
function moveFields(raw) {
  const mv = Object.assign({}, raw && typeof raw === 'object' ? raw : {});
  const alias = function (canonical, short) {
    if ((mv[canonical] === undefined || mv[canonical] === null || mv[canonical] === '') &&
        mv[short] !== undefined && mv[short] !== null) {
      mv[canonical] = mv[short];
    }
  };
  alias('expectedName', 'from');
  alias('expectedSize', 'size');
  alias('destPath', 'toFolder');
  return mv;
}

async function doMove(ctx, mv, rootFiles, wouldLeave) {
  const rows = ctx.rows;
  const expectedName = typeof mv.expectedName === 'string' ? mv.expectedName : '';
  const newName = (typeof mv.newName === 'string' && mv.newName) ? mv.newName : expectedName;
  const segs = parsePath(mv.destPath);
  const target = (Array.isArray(segs) ? segs.join('/') : String(mv.destPath || '')) + '/' + newName;
  const size = Number(mv.expectedSize);

  const bad = !expectedName ? 'no expectedName'
    : (typeof segs === 'string') ? segs
    : !segs.length ? 'destPath is empty; moves go into a folder inside the job'
    : badFileName(newName)
    || (!Number.isFinite(size) || size < 0 ? 'expectedSize is not a number' : null)
    || extensionChange(expectedName, newName);
  if (bad) {
    rows.push(row('move', expectedName || '(none)', target, 'SKIP', bad));
    return;
  }

  let placed = null;     // the item at destPath/newName once moved or found there
  let status = null;
  let wouldMove = false;

  const destId = await resolveFolder(ctx, ctx.jobId, segs, false);
  const existing = destId ? await childByName(ctx, destId, newName) : null;
  if (existing) {
    if (!existing.file) {
      rows.push(row('move', expectedName, target, 'SKIP', 'a folder named "' + newName + '" is already there'));
      return;
    }
    if (Number(existing.size) !== size) {
      rows.push(row('move', expectedName, target, 'SKIP', 'a different file (' + existing.size +
                    ' bytes, expected ' + size + ') already has that name there'));
      return;
    }
    status = 'ALREADY DONE';
    placed = existing;
    rows.push(row('move', expectedName, target, status, 'already at ' + target));
  } else {
    const src = rootFiles.get(expectedName.toLowerCase());
    if (!src || src.name !== expectedName) {
      rows.push(row('move', expectedName, target, 'NOT FOUND', src
        ? 'root has "' + src.name + '" (different case), not "' + expectedName + '"'
        : 'not in the job root'));
      return;
    }
    if (Number(src.size) !== size) {
      rows.push(row('move', expectedName, target, 'SKIP', 'size is ' + src.size + ' bytes, manifest expects ' + size));
      return;
    }
    if (Number(src.size) > MAX_BYTES) {
      rows.push(row('move', expectedName, target, 'SKIP', 'over 100 MB, never syncs to JT'));
      return;
    }
    const clash = rootFiles.get(newName.toLowerCase());
    if (clash && clash.id !== src.id) {
      rows.push(row('move', expectedName, target, 'SKIP', 'another file in the job root is named "' + clash.name +
                    '"; the JT echo of this move would delete it'));
      return;
    }

    if (ctx.dryRun) {
      status = 'DRY RUN';
      wouldMove = true;
      wouldLeave.add(src.id);
      rows.push(row('move', expectedName, target, status, 'would move' +
                    (destId ? '' : ' (and create the destination folder)')));
    } else {
      await pace(ctx);
      const folderId = destId || await resolveFolder(ctx, ctx.jobId, segs, true);
      placed = await patchItem(ctx.token, ctx.driveId, src.id,
                               { name: newName, parentReference: { id: folderId } }, 'fail');
      rootFiles.delete(expectedName.toLowerCase());
      addChild(ctx, folderId, placed);
      status = 'DONE';
      rows.push(row('move', expectedName, target, status, 'moved'));
    }
  }

  const social = typeof mv.social === 'string' ? mv.social.trim() : '';
  if (!social) return;
  const socialName = (typeof mv.socialName === 'string' && mv.socialName) ? mv.socialName : newName;
  if (status === 'DONE' || status === 'ALREADY DONE' || wouldMove) {
    try {
      await doSocialCopy(ctx, placed, social, socialName, newName);
    } catch (e) {
      if (e && e.outOfTime) throw e;
      ctx.rows.push(row('social', newName, social + '/' + socialName, 'ERROR', e.message));
    }
  }
}

async function doSocialCopy(ctx, item, social, socialName, sourceName) {
  const rows = ctx.rows;
  const target = social + '/' + socialName;
  if (SOCIAL_FOLDERS.indexOf(social) < 0) {
    rows.push(row('social', sourceName, target, 'SKIP', '"' + social + '" is not one of: ' + SOCIAL_FOLDERS.join(', ')));
    return;
  }
  const bad = badFileName(socialName) ||
    (extOf(socialName) !== extOf(sourceName) ? 'socialName changes the file extension' : null);
  if (bad) {
    rows.push(row('social', sourceName, target, 'SKIP', bad));
    return;
  }

  const pkgId = await getPackage(ctx, true);
  if (pkgId === null) {
    rows.push(row('social', sourceName, target, 'SKIP', ctx.pkgError));
    return;
  }
  const subId = pkgId ? await resolveFolder(ctx, pkgId, [social], false) : null;
  const there = subId ? await childByName(ctx, subId, socialName) : null;
  if (there) {
    rows.push(row('social', sourceName, target, 'ALREADY DONE', 'copy exists'));
    return;
  }
  if (ctx.dryRun) {
    rows.push(row('social', sourceName, target, 'DRY RUN', 'would copy' +
                  (pkgId ? '' : ' (and create the package "' + ctx.packageName + '")')));
    return;
  }

  await pace(ctx);
  const folderId = subId || await resolveFolder(ctx, pkgId, [social], true);
  const res = await graph('POST', GRAPH + '/drives/' + ctx.driveId + '/items/' + item.id + '/copy' +
                          '?%40microsoft.graph.conflictBehavior=fail', {
    token: ctx.token,
    body: { parentReference: { driveId: ctx.driveId, id: folderId }, name: socialName }
  });
  ok(res, 'Copy "' + sourceName + '"');
  const monitor = res.headers && (res.headers.location || res.headers.Location);
  const outcome = monitor ? await waitForCopy(monitor) : 'completed';
  if (outcome === 'failed') {
    rows.push(row('social', sourceName, target, 'ERROR', 'SharePoint reported the copy failed'));
    return;
  }
  addChild(ctx, folderId, { id: 'copy:' + socialName, name: socialName, file: {} });
  rows.push(row('social', sourceName, target, 'DONE', outcome === 'completed' ? 'copied' : 'copy started, still finishing'));
}

/* Copy is asynchronous. Poll the monitor URL (no auth header) for up to ~20s. */
async function waitForCopy(monitorUrl) {
  for (let i = 0; i < 10; i++) {
    await api.sleep(2000);
    try {
      const res = await api.request('GET', monitorUrl, {});
      const s = res && res.data && res.data.status;
      if (s === 'completed' || res.status === 200 && res.data && res.data.resourceId) return 'completed';
      if (s === 'failed') return 'failed';
    } catch (e) { /* keep polling */ }
  }
  return 'pending';
}

// ------------------------------------------------------------ bulk moves

/*
 * "bulkMoves": [ { "match": "*.jpeg|*.jpg|*.heic|*.png", "destPath": "Photos/1 Job Walk" }, ... ]
 * For each file still in the job root that no explicit row named, the first
 * rule whose glob matches its name (case-insensitive, globs separated by |)
 * moves it to destPath, keeping its name. One row per file: BULK MOVED,
 * DRY RUN or SKIP (over 100 MB, zip, name taken in the destination).
 */
async function doBulkMoves(ctx, rules, rootFiles, named, wouldLeave) {
  const rows = ctx.rows;
  const parsed = [];
  rules.forEach(function (raw, i) {
    const r = raw && typeof raw === 'object' ? raw : {};
    const label = typeof r.match === 'string' && r.match.trim() ? r.match : '#' + (i + 1);
    const re = globToRegex(r.match);
    const segs = parsePath(r.destPath);
    const bad = !re ? 'match is empty'
      : (typeof segs === 'string') ? segs
      : !segs.length ? 'destPath is empty; bulk moves go into a folder inside the job' : null;
    if (bad) {
      rows.push(row('bulk', label, String(r.destPath || ''), 'SKIP', 'rule not used: ' + bad));
      return;
    }
    parsed.push({ re: re, segs: segs });
  });
  if (!parsed.length) return;

  const candidates = Array.from(rootFiles.values())
    .filter(function (f) { return !wouldLeave.has(f.id) && !named.has(f.name.toLowerCase()); })
    .sort(function (a, b) { return a.name.localeCompare(b.name); });

  for (const f of candidates) {
    ctx.run.checkTime();
    const rule = parsed.find(function (p) { return p.re.test(f.name); });
    if (!rule) continue;
    named.add(f.name.toLowerCase());
    const target = rule.segs.join('/') + '/' + f.name;
    try {
      if (Number(f.size) > MAX_BYTES) {
        rows.push(row('bulk', f.name, target, 'SKIP', 'over 100 MB, never syncs to JT'));
        continue;
      }
      if (extOf(f.name) === 'zip') {
        rows.push(row('bulk', f.name, target, 'SKIP', 'zip files are not bulk moved'));
        continue;
      }
      const destId = await resolveFolder(ctx, ctx.jobId, rule.segs, false);
      const taken = destId ? await childByName(ctx, destId, f.name) : null;
      if (taken) {
        rows.push(row('bulk', f.name, target, 'SKIP', 'name taken: "' + taken.name + '" is already in ' + rule.segs.join('/')));
        continue;
      }
      if (ctx.dryRun) {
        wouldLeave.add(f.id);
        rows.push(row('bulk', f.name, target, 'DRY RUN', 'would move' + (destId ? '' : ' (and create the destination folder)')));
        continue;
      }
      await pace(ctx);
      const folderId = destId || await resolveFolder(ctx, ctx.jobId, rule.segs, true);
      const placed = await patchItem(ctx.token, ctx.driveId, f.id, { parentReference: { id: folderId } }, 'fail');
      rootFiles.delete(f.name.toLowerCase());
      addChild(ctx, folderId, placed);
      rows.push(row('bulk', f.name, target, 'BULK MOVED', 'moved, name kept'));
    } catch (e) {
      if (e && e.outOfTime) throw e;
      rows.push(row('bulk', f.name, target, 'ERROR', e.message));
    }
  }
}

/* "*.jpg|*.HEIC|IMG_?.png" -> one case-insensitive regex over the whole name. */
function globToRegex(match) {
  if (typeof match !== 'string') return null;
  const parts = match.split('|').map(function (p) { return p.trim(); }).filter(Boolean);
  if (!parts.length) return null;
  const src = parts.map(function (p) {
    return p.replace(/[.+^${}()[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.');
  }).join('|');
  return new RegExp('^(?:' + src + ')$', 'i');
}

// ------------------------------------------------------------ renames

async function doRename(ctx, man, rn, rootFiles, wouldLeave) {
  const rows = ctx.rows;
  const inPkg = rn.in === 'package';
  const oldName = typeof rn.oldName === 'string' ? rn.oldName : '';
  const newName = typeof rn.newName === 'string' ? rn.newName : '';
  const segs = parsePath(rn.folderPath);
  const where = (inPkg ? 'package' : 'job') + (Array.isArray(segs) && segs.length ? ':' + segs.join('/') : ':');
  const label = oldName || '(none)';

  const bad = !oldName ? 'no oldName'
    : (typeof segs === 'string') ? segs
    : badFileName(newName);
  if (bad) {
    rows.push(row('rename', label, where, 'SKIP', bad));
    return;
  }

  let baseId = ctx.jobId;
  if (inPkg) {
    baseId = await getPackage(ctx, false);
    if (baseId === null) { rows.push(row('rename', label, where, 'SKIP', ctx.pkgError)); return; }
    if (!baseId) { rows.push(row('rename', label, where, 'NOT FOUND', 'package does not exist yet')); return; }
  }

  const parentId = await resolveFolder(ctx, baseId, segs, false);
  if (!parentId) { rows.push(row('rename', label, where, 'NOT FOUND', 'folder not found')); return; }
  const oldItem = await childByName(ctx, parentId, oldName, true);
  const newItem = await childByName(ctx, parentId, newName);

  if (!oldItem) {
    rows.push(row('rename', label, where, newItem ? 'ALREADY DONE' : 'NOT FOUND',
                  newItem ? 'already named "' + newItem.name + '"' : 'nothing named "' + oldName + '" there'));
    return;
  }
  if (newItem && newItem.id !== oldItem.id) {
    rows.push(row('rename', label, where, 'SKIP', '"' + newItem.name + '" already exists there'));
    return;
  }
  if (oldItem.folder) {
    if (inPkg) {
      rows.push(row('rename', label, where, 'SKIP', 'package folders are never renamed'));
      return;
    }
    const full = segs.concat([oldItem.name]).join('/').toLowerCase();
    /* Reaching here means the numbered folder does not exist (checked above). */
    if (PROTECTED_FOLDERS.indexOf(full) >= 0 && LEGACY_RENAMES[oldItem.name] !== newName) {
      rows.push(row('rename', label, where, 'SKIP', 'Photos/Job Walk, Progress and Completed can only become ' +
                    '1 Job Walk, 2 Progress and 3 Completed'));
      return;
    }
    const badSeg = badFolderName(newName);
    if (badSeg) { rows.push(row('rename', label, where, 'SKIP', badSeg)); return; }
  } else if (extOf(newName) !== extOf(oldItem.name)) {
    rows.push(row('rename', label, where, 'SKIP', 'newName changes the file extension'));
    return;
  }

  if (ctx.dryRun) {
    rows.push(row('rename', label, where, 'DRY RUN', 'would rename to "' + newName + '"'));
    return;
  }
  await pace(ctx);
  const renamed = await patchItem(ctx.token, ctx.driveId, oldItem.id, { name: newName }, 'fail');
  const list = ctx.children.get(parentId);
  if (list) {
    const idx = list.findIndex(function (c) { return c.id === oldItem.id; });
    if (idx >= 0) list[idx] = Object.assign({}, list[idx], { name: renamed.name || newName });
  }
  if (!inPkg && !segs.length && oldItem.file) {
    rootFiles.delete(oldItem.name.toLowerCase());
    rootFiles.set(newName.toLowerCase(), Object.assign({}, oldItem, { name: newName }));
  }
  rows.push(row('rename', label, where, 'DONE', 'renamed to "' + newName + '"'));
}

// ------------------------------------------------------------ brief

async function doBrief(ctx, man) {
  const b = man.brief;
  const file = (typeof b.file === 'string' && b.file) ? b.file : '';
  const pkgName = ctx.packageName;
  const bad = !file ? 'brief has no "file"'
    : badFileName(file) || (!/\.docx$/i.test(file) ? 'brief file must end in .docx' : null);
  if (bad) { ctx.rows.push(row('brief', file || '(none)', pkgName || '', 'SKIP', bad)); return; }

  const pkgId = await getPackage(ctx, true);
  if (pkgId === null) { ctx.rows.push(row('brief', file, pkgName || '', 'SKIP', ctx.pkgError)); return; }

  const buf = buildBriefDocx(b);   // build in a dry run too, so a bad brief shows up as an ERROR
  if (ctx.dryRun) {
    ctx.rows.push(row('brief', file, pkgName, 'DRY RUN', 'would write ' + buf.length + ' bytes' +
                      (pkgId ? '' : ' (and create the package)')));
    return;
  }
  await putContent(ctx.token, ctx.driveId, pkgId, file, buf,
                   'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 'replace');
  ctx.rows.push(row('brief', file, pkgName, 'DONE', 'written (' + buf.length + ' bytes)'));
}

// ------------------------------------------------------------ package

/*
 * The package folder id: a string when it exists (or was just created),
 * '' when it does not exist yet (dry run, or create=false), null when it
 * cannot be used (ctx.pkgError says why). Created with its final name if
 * missing; never renamed or moved.
 */
async function getPackage(ctx, create) {
  const name = ctx.packageName;
  if (!name) { ctx.pkgError = 'manifest has no "package"'; return null; }
  const bad = badFolderName(name);
  if (bad) { ctx.pkgError = 'package name: ' + bad; return null; }
  if (ctx.pkg) return ctx.pkg;
  const parent = await getItemByPath(ctx.token, ctx.driveId, POST_PACKAGES_PATH);
  if (!parent) { ctx.pkgError = '"' + POST_PACKAGES_PATH + '" not found'; return null; }
  const hit = await childByName(ctx, parent.id, name);
  if (hit && !hit.folder) { ctx.pkgError = '"' + name + '" in Post Packages is a file, not a folder'; return null; }
  if (hit) { ctx.pkg = hit.id; return ctx.pkg; }
  if (ctx.dryRun || !create) return '';
  const created = await ensureChildFolder(ctx.token, ctx.driveId, parent.id, name);
  addChild(ctx, parent.id, created);
  ctx.pkg = created.id;
  return ctx.pkg;
}

// ------------------------------------------------------------ folder helpers

/* Walk segs under baseId. create=false: a missing segment returns null. */
async function resolveFolder(ctx, baseId, segs, create) {
  let parentId = baseId;
  let key = baseId;
  for (const seg of segs) {
    key = key + '/' + seg.toLowerCase();
    if (ctx.folders.has(key)) { parentId = ctx.folders.get(key); continue; }
    let hit = await childByName(ctx, parentId, seg);
    if (hit && !hit.folder) throw new Error('"' + seg + '" is a file, not a folder');
    if (!hit) {
      if (!create) return null;
      hit = await ensureChildFolder(ctx.token, ctx.driveId, parentId, seg);
      addChild(ctx, parentId, hit);
    }
    ctx.folders.set(key, hit.id);
    parentId = hit.id;
  }
  return parentId;
}

async function children(ctx, folderId) {
  if (!ctx.children.has(folderId)) {
    ctx.children.set(folderId, await listAllChildren(ctx.token, ctx.driveId, folderId));
  }
  return ctx.children.get(folderId);
}

/* exact=true matches the name exactly; otherwise case-insensitively, as SharePoint does. */
async function childByName(ctx, folderId, name, exact) {
  const lower = String(name).toLowerCase();
  return (await children(ctx, folderId)).find(function (c) {
    return exact ? c.name === name : c.name.toLowerCase() === lower;
  }) || null;
}

function addChild(ctx, folderId, item) {
  const list = ctx.children.get(folderId);
  if (list && item) list.push(item);
}

/* 750 ms between writes, so a big manifest does not trip Graph throttling. */
async function pace(ctx) {
  ctx.run.checkTime();
  if (ctx.wroteOnce) await api.sleep(MOVE_GAP_MS);
  ctx.wroteOnce = true;
}

// ------------------------------------------------------------ validation

/* The characters JobTreadWebhook's sanitizePathSegment rewrites to "-". A
 * folder named with one of them would come back from JT under a different
 * name and the webhook would create a second folder. */
const BAD_FOLDER_CHARS = /[:*?'"<>|#%\\/]/;
const BAD_FILE_CHARS = /[:*?"<>|\\/]/;

function badFolderName(name) {
  if (typeof name !== 'string' || !name.trim()) return 'empty folder name';
  if (BAD_FOLDER_CHARS.test(name)) return 'folder name "' + name + '" has a character JT sync rewrites (: * ? \' " < > | # %)';
  if (/^[.\s]|[.\s]$/.test(name)) return 'folder name "' + name + '" starts or ends with a dot or space';
  if (name.length > 200) return 'folder name is too long';
  return null;
}

function badFileName(name) {
  if (typeof name !== 'string' || !name.trim()) return 'empty file name';
  if (BAD_FILE_CHARS.test(name)) return 'name "' + name + '" has a character SharePoint refuses';
  if (/^\s|[.\s]$/.test(name)) return 'name "' + name + '" starts with a space or ends with a dot or space';
  if (name.length > 200) return 'name is too long';
  return null;
}

/* Path relative to the job (or package): array of segments, or a string saying what is wrong. */
function parsePath(p) {
  if (p === undefined || p === null) p = '';
  if (typeof p !== 'string') return 'path is not text';
  const segs = p.split('/').map(function (s) { return s.trim(); });
  if (p.trim() === '') return [];
  if (segs.some(function (s) { return !s; })) return 'path "' + p + '" has an empty segment or a leading/trailing "/"';
  for (const s of segs) {
    if (s === '.' || s === '..') return 'path "' + p + '" leaves the folder';
    const bad = badFolderName(s);
    if (bad) return bad;
  }
  return segs;
}

/* Files with no extension (JT daily-log photos arrive as "Photo 1") may gain
 * one of ADDABLE_EXTENSIONS. Changing an existing extension is refused. */
const ADDABLE_EXTENSIONS = ['jpg', 'jpeg', 'png', 'heic', 'mov', 'pdf'];

function extensionChange(fromName, toName) {
  const from = extOf(fromName);
  const to = extOf(toName);
  if (from === to) return null;
  if (!from) {
    return ADDABLE_EXTENSIONS.indexOf(to) >= 0 ? null
      : 'newName adds ".' + to + '"; only ' + ADDABLE_EXTENSIONS.map(function (e) { return '.' + e; }).join(' ') +
        ' can be added to a file with no extension';
  }
  return 'newName changes the file extension ("' + from + '" -> "' + to + '")';
}

function extOf(name) {
  const m = /\.([^.\/\\]+)$/.exec(String(name || ''));
  return m ? m[1].toLowerCase() : '';
}

function row(action, name, target, status, detail) {
  return { action: action, name: name, target: target, status: status, detail: detail || '' };
}

/* Share links compare without the query string (?e=... changes per copy). */
function sameLink(a, b) {
  const norm = function (u) { return String(u || '').trim().replace(/[?#].*$/, '').replace(/\/+$/, '').toLowerCase(); };
  return !!norm(a) && norm(a) === norm(b);
}

// ------------------------------------------------------------ brief .docx

/*
 * A Word file is a zip of a few XML parts. Built here with zlib so there is
 * no new dependency, the same approach as the PowerShell version.
 *   Calibri 11; Title bold purple #5B2A86 22pt; subtitle gray with a purple
 *   bottom border; blurb italic with a light purple left border; Heading 1
 *   bold purple 14pt; bulleted sections in a fixed order (empty ones left out).
 */
const BRIEF_SECTIONS = [
  ['did', 'What we did'],
  ['team', 'The team'],
  ['tags', 'Project type and who to tag'],
  ['personal', 'Personal touches'],
  ['photos', 'Photos in this folder'],
  ['ideas', 'Quick post ideas'],
  ['captions', 'Captions to start from'],
  ['check', 'Before you post']
];

function buildBriefDocx(b) {
  const W = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main';
  const body = [];
  if (b.title) body.push(para('Title', b.title));
  if (b.sub) body.push(para('Subtitle', b.sub));
  if (b.blurb) body.push(para('Blurb', b.blurb));
  for (const s of BRIEF_SECTIONS) {
    const items = b[s[0]];
    if (items !== undefined && items !== null && !Array.isArray(items)) {
      throw new Error('brief "' + s[0] + '" must be a list');
    }
    const list = (items || []).filter(function (x) { return x !== null && x !== undefined && String(x).trim(); });
    if (!list.length) continue;
    body.push(para('Heading1', s[1]));
    for (const item of list) body.push(para('ListBullet', item));
  }

  const document = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
    '<w:document xmlns:w="' + W + '"><w:body>' + body.join('') +
    '<w:sectPr><w:pgSz w:w="12240" w:h="15840"/>' +
    '<w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" w:footer="720" w:gutter="0"/>' +
    '</w:sectPr></w:body></w:document>';

  const styles = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
    '<w:styles xmlns:w="' + W + '">' +
    '<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="Calibri" w:cs="Calibri"/>' +
    '<w:sz w:val="22"/><w:szCs w:val="22"/><w:lang w:val="en-US"/></w:rPr></w:rPrDefault>' +
    '<w:pPrDefault><w:pPr><w:spacing w:after="120" w:line="264" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>' +
    '<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>' +
    '<w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>' +
    '<w:pPr><w:spacing w:after="60"/></w:pPr>' +
    '<w:rPr><w:b/><w:bCs/><w:color w:val="5B2A86"/><w:sz w:val="44"/><w:szCs w:val="44"/></w:rPr></w:style>' +
    '<w:style w:type="paragraph" w:styleId="Subtitle"><w:name w:val="Subtitle"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>' +
    '<w:pPr><w:pBdr><w:bottom w:val="single" w:sz="8" w:space="4" w:color="5B2A86"/></w:pBdr><w:spacing w:after="240"/></w:pPr>' +
    '<w:rPr><w:color w:val="666666"/></w:rPr></w:style>' +
    '<w:style w:type="paragraph" w:customStyle="1" w:styleId="Blurb"><w:name w:val="Blurb"/><w:basedOn w:val="Normal"/><w:qFormat/>' +
    '<w:pPr><w:pBdr><w:left w:val="single" w:sz="24" w:space="8" w:color="D9C8EA"/></w:pBdr>' +
    '<w:spacing w:after="240"/><w:ind w:left="240"/></w:pPr>' +
    '<w:rPr><w:i/><w:iCs/></w:rPr></w:style>' +
    '<w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/>' +
    '<w:pPr><w:keepNext/><w:spacing w:before="240" w:after="80"/><w:outlineLvl w:val="0"/></w:pPr>' +
    '<w:rPr><w:b/><w:bCs/><w:color w:val="5B2A86"/><w:sz w:val="28"/><w:szCs w:val="28"/></w:rPr></w:style>' +
    '<w:style w:type="paragraph" w:styleId="ListBullet"><w:name w:val="List Bullet"/><w:basedOn w:val="Normal"/>' +
    '<w:pPr><w:numPr><w:numId w:val="1"/></w:numPr><w:spacing w:after="60"/></w:pPr></w:style>' +
    '</w:styles>';

  const numbering = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
    '<w:numbering xmlns:w="' + W + '"><w:abstractNum w:abstractNumId="0">' +
    '<w:multiLevelType w:val="hybridMultilevel"/>' +
    '<w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="•"/><w:lvlJc w:val="left"/>' +
    '<w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri"/></w:rPr></w:lvl>' +
    '</w:abstractNum><w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num></w:numbering>';

  const contentTypes = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
    '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">' +
    '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>' +
    '<Default Extension="xml" ContentType="application/xml"/>' +
    '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>' +
    '<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>' +
    '<Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>' +
    '</Types>';

  const REL = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships';
  const rootRels = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
    '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' +
    '<Relationship Id="rId1" Type="' + REL + '/officeDocument" Target="word/document.xml"/></Relationships>';
  const docRels = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
    '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' +
    '<Relationship Id="rId1" Type="' + REL + '/styles" Target="styles.xml"/>' +
    '<Relationship Id="rId2" Type="' + REL + '/numbering" Target="numbering.xml"/></Relationships>';

  return zipFiles([
    ['[Content_Types].xml', contentTypes],
    ['_rels/.rels', rootRels],
    ['word/document.xml', document],
    ['word/_rels/document.xml.rels', docRels],
    ['word/styles.xml', styles],
    ['word/numbering.xml', numbering]
  ]);
}

/* One paragraph; line breaks inside the text become Word line breaks. */
function para(style, text) {
  const lines = String(text).split(/\r?\n/);
  const runs = lines.map(function (l, i) {
    return (i ? '<w:r><w:br/></w:r>' : '') + '<w:r><w:t xml:space="preserve">' + xmlEscape(l) + '</w:t></w:r>';
  }).join('');
  return '<w:p><w:pPr><w:pStyle w:val="' + style + '"/></w:pPr>' + runs + '</w:p>';
}

function xmlEscape(s) {
  return String(s)
    .replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/g, '')
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

const CRC_TABLE = (function () {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1);
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(buf) {
  let c = 0xFFFFFFFF;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xFF] ^ (c >>> 8);
  return (c ^ 0xFFFFFFFF) >>> 0;
}

function zipFiles(entries) {
  const now = new Date(api.now());
  const dosTime = (now.getHours() << 11) | (now.getMinutes() << 5) | Math.floor(now.getSeconds() / 2);
  const dosDate = ((now.getFullYear() - 1980) << 9) | ((now.getMonth() + 1) << 5) | now.getDate();
  const locals = [];
  const centrals = [];
  let offset = 0;
  for (const e of entries) {
    const name = Buffer.from(e[0], 'utf8');
    const data = Buffer.from(e[1], 'utf8');
    const comp = zlib.deflateRawSync(data);
    const crc = crc32(data);

    const lh = Buffer.alloc(30);
    lh.writeUInt32LE(0x04034b50, 0);
    lh.writeUInt16LE(20, 4);
    lh.writeUInt16LE(0x0800, 6);       // names are UTF-8
    lh.writeUInt16LE(8, 8);            // deflate
    lh.writeUInt16LE(dosTime, 10);
    lh.writeUInt16LE(dosDate, 12);
    lh.writeUInt32LE(crc, 14);
    lh.writeUInt32LE(comp.length, 18);
    lh.writeUInt32LE(data.length, 22);
    lh.writeUInt16LE(name.length, 26);
    lh.writeUInt16LE(0, 28);
    locals.push(lh, name, comp);

    const ch = Buffer.alloc(46);
    ch.writeUInt32LE(0x02014b50, 0);
    ch.writeUInt16LE(20, 4);
    ch.writeUInt16LE(20, 6);
    ch.writeUInt16LE(0x0800, 8);
    ch.writeUInt16LE(8, 10);
    ch.writeUInt16LE(dosTime, 12);
    ch.writeUInt16LE(dosDate, 14);
    ch.writeUInt32LE(crc, 16);
    ch.writeUInt32LE(comp.length, 20);
    ch.writeUInt32LE(data.length, 24);
    ch.writeUInt16LE(name.length, 28);
    ch.writeUInt32LE(offset, 42);
    centrals.push(ch, name);

    offset += 30 + name.length + comp.length;
  }
  const cd = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(cd.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat(locals.concat([cd, end]));
}

// ------------------------------------------------------------ sharepoint

async function getItemByPath(token, driveId, path) {
  const encoded = path.split('/').map(encodeURIComponent).join('/');
  const res = await graph('GET', GRAPH + '/drives/' + driveId + '/root:/' + encoded, { token: token });
  if (res.status === 404) return null;
  return ok(res, 'Get "' + path + '"');
}

async function listAllChildren(token, driveId, parentId) {
  const out = [];
  let url = GRAPH + '/drives/' + driveId + '/items/' + parentId + '/children?$top=200&$select=id,name,folder,file,size,eTag';
  while (url) {
    const page = ok(await graph('GET', url, { token: token }), 'List children of ' + parentId);
    for (const item of (page.value || [])) out.push(item);
    url = page['@odata.nextLink'] || null;
  }
  return out;
}

async function getChildByName(token, driveId, parentId, name) {
  return (await listAllChildren(token, driveId, parentId))
    .find(function (c) { return c.name.toLowerCase() === name.toLowerCase(); }) || null;
}

/* Find or create a child folder. Created with conflictBehavior "fail", so a
 * folder that appeared in the meantime is adopted, never renamed or replaced. */
async function ensureChildFolder(token, driveId, parentId, name) {
  const existing = (await listAllChildren(token, driveId, parentId))
    .find(function (c) { return c.name.toLowerCase() === name.toLowerCase(); });
  if (existing) {
    if (!existing.folder) throw new Error('"' + name + '" exists as a file, not a folder');
    return existing;
  }
  const res = await graph('POST', GRAPH + '/drives/' + driveId + '/items/' + parentId + '/children', {
    token: token,
    body: { name: name, folder: {}, '@microsoft.graph.conflictBehavior': 'fail' }
  });
  if (res.status === 409) {
    const again = (await listAllChildren(token, driveId, parentId))
      .find(function (c) { return c.folder && c.name.toLowerCase() === name.toLowerCase(); });
    if (again) return again;
  }
  const created = ok(res, 'Create folder "' + name + '"');
  if (!created || !created.id) throw new Error('Create folder "' + name + '" returned no id');
  return created;
}

async function patchItem(token, driveId, itemId, fields, conflictBehavior, headers) {
  const res = await graph('PATCH', GRAPH + '/drives/' + driveId + '/items/' + itemId +
                          (conflictBehavior ? '?%40microsoft.graph.conflictBehavior=' + conflictBehavior : ''),
                          { token: token, body: fields, headers: headers || undefined });
  const item = ok(res, 'Update item ' + itemId);
  return item && typeof item === 'object' ? item : { id: itemId, name: fields.name };
}

async function putContent(token, driveId, folderId, name, buffer, contentType, conflictBehavior) {
  const res = await graph('PUT', GRAPH + '/drives/' + driveId + '/items/' + folderId + ':/' +
                          encodeURIComponent(name) + ':/content?%40microsoft.graph.conflictBehavior=' + conflictBehavior,
                          { token: token, body: buffer, headers: { 'Content-Type': contentType } });
  return ok(res, 'Upload "' + name + '"');
}

async function downloadItem(token, driveId, itemId) {
  const item = ok(await graph('GET', GRAPH + '/drives/' + driveId + '/items/' + itemId, { token: token }), 'Get item ' + itemId);
  const url = item && item['@microsoft.graph.downloadUrl'];
  if (!url) throw new Error('no download URL');
  return await api.download(url);
}

async function getAccessToken() {
  const body = 'grant_type=client_credentials' +
    '&client_id=' + process.env.AZURE_CLIENT_ID +
    '&client_secret=' + encodeURIComponent(process.env.AZURE_CLIENT_SECRET) +
    '&scope=https%3A%2F%2Fgraph.microsoft.com%2F.default';
  const res = await api.request('POST', 'https://login.microsoftonline.com/' + process.env.AZURE_TENANT_ID + '/oauth2/v2.0/token', {
    body: body, headers: { 'Content-Type': 'application/x-www-form-urlencoded' }
  });
  const data = res && res.data;
  if (!data || !data.access_token) {
    throw new Error('Token request returned no access_token (check AZURE_CLIENT_SECRET expiry): ' + snippet(data));
  }
  return data.access_token;
}

async function getSiteId(token) {
  const data = ok(await graph('GET', GRAPH + '/sites/purplepainting.sharepoint.com:/sites/purplepainting', { token: token }), 'Get site');
  if (!data || !data.id) throw new Error('Could not resolve site id: ' + snippet(data));
  return data.id;
}

async function getDriveId(token, siteId) {
  const data = ok(await graph('GET', GRAPH + '/sites/' + siteId + '/drives', { token: token }), 'List drives');
  const drive = (data.value || []).find(function (d) { return d.name === 'Documents'; }) || (data.value || [])[0];
  if (!drive) throw new Error('No drive found on site ' + siteId);
  return drive.id;
}

/* Read-only: the JobFolders row for a JT job id (row key), or null. */
async function getJobMapping(jtJobId) {
  const table = TableClient.fromConnectionString(process.env.STORAGE_CONNECTION_STRING, 'JobFolders',
                                                 { allowInsecureConnection: false });
  try {
    return await table.getEntity('jobs', jtJobId);
  } catch (e) {
    if (e && e.statusCode === 404) return null;
    throw e;
  }
}

// ------------------------------------------------------------ run lock

/*
 * One row in SyncState: { partitionKey 'lock', rowKey 'ApplyOrganizeManifests',
 * owner, expiresAt }. Create it, or take it over once expired with an
 * etag-matched update, so two runs racing for an expired lock cannot both
 * win. Released by deleting it (only by its owner).
 */
const LOCK_PK = 'lock';
const LOCK_RK = 'ApplyOrganizeManifests';

function lockTable() {
  return TableClient.fromConnectionString(process.env.STORAGE_CONNECTION_STRING, 'SyncState',
                                          { allowInsecureConnection: false });
}

async function acquireLock(owner) {
  const table = lockTable();
  try { await table.createTable(); } catch (e) { /* already exists */ }
  const entity = { partitionKey: LOCK_PK, rowKey: LOCK_RK, owner: owner, expiresAt: api.now() + LOCK_MS };
  try {
    await table.createEntity(entity);
    return true;
  } catch (e) {
    if (!e || e.statusCode !== 409) throw e;
  }
  let current;
  try {
    current = await table.getEntity(LOCK_PK, LOCK_RK);
  } catch (e) {
    if (e && e.statusCode === 404) return false;   // released between our two calls; next run gets it
    throw e;
  }
  if (Number(current.expiresAt) > api.now()) return false;
  try {
    await table.updateEntity(entity, 'Replace', { etag: current.etag });
    return true;
  } catch (e) {
    if (e && (e.statusCode === 412 || e.statusCode === 404)) return false;
    throw e;
  }
}

async function releaseLock(owner) {
  const table = lockTable();
  let current;
  try {
    current = await table.getEntity(LOCK_PK, LOCK_RK);
  } catch (e) {
    if (e && e.statusCode === 404) return;
    throw e;
  }
  if (current.owner !== owner) return;
  await table.deleteEntity(LOCK_PK, LOCK_RK, { etag: current.etag });
}

// ------------------------------------------------------------ http

/* Graph call with a retry on throttling (429) and brief outages (503/504). */
async function graph(method, url, opts) {
  for (let attempt = 0; ; attempt++) {
    const res = await api.request(method, url, opts);
    if ((res.status === 429 || res.status === 503 || res.status === 504) && attempt < 3) {
      const after = parseInt(res.headers && res.headers['retry-after'], 10);
      await api.sleep(Math.min(30, Number.isFinite(after) ? after : 2 << attempt) * 1000);
      continue;
    }
    return res;
  }
}

function ok(res, label) {
  if (res.status < 200 || res.status >= 300) {
    throw new Error(label + ' -> ' + res.status + ': ' + snippet(res.data));
  }
  return res.data;
}

function snippet(data) {
  return (typeof data === 'string' ? data : JSON.stringify(data) || '').slice(0, 300);
}

/* Resolves { status, headers, data } for any status; rejects only on network errors and timeouts. */
function httpRequest(method, url, opts) {
  opts = opts || {};
  return new Promise(function (resolve, reject) {
    const u = new URL(url);
    const headers = Object.assign({}, opts.headers || {});
    if (opts.token) headers['Authorization'] = 'Bearer ' + opts.token;
    let body = opts.body;
    if (body !== undefined && body !== null && !Buffer.isBuffer(body)) {
      if (typeof body !== 'string') {
        body = JSON.stringify(body);
        if (!headers['Content-Type']) headers['Content-Type'] = 'application/json';
      }
      body = Buffer.from(body, 'utf8');
    }
    if (body) headers['Content-Length'] = body.length;
    else if (method !== 'GET') headers['Content-Length'] = 0;

    const req = https.request({ hostname: u.hostname, path: u.pathname + u.search, method: method, headers: headers }, function (res) {
      const chunks = [];
      res.on('data', function (c) { chunks.push(c); });
      res.on('end', function () {
        const text = Buffer.concat(chunks).toString('utf8');
        let data = null;
        try { data = text ? JSON.parse(text) : null; } catch (e) { data = text; }
        resolve({ status: res.statusCode, headers: res.headers, data: data });
      });
      res.on('error', reject);
    });
    req.setTimeout(60000, function () { req.destroy(new Error(method + ' timed out: ' + url)); });
    req.on('error', reject);
    if (body) req.write(body);
    req.end();
  });
}

/* Manifests are small; anything over 5 MB is not a manifest. */
function downloadFile(url) {
  const cap = 5 * 1024 * 1024;
  return new Promise(function (resolve, reject) {
    const go = function (u, hops) {
      if (hops > 5) return reject(new Error('Too many redirects'));
      const req = https.get(u, function (res) {
        if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
          res.resume();
          return go(res.headers.location, hops + 1);
        }
        if (res.statusCode < 200 || res.statusCode >= 300) {
          res.resume();
          return reject(new Error('Download failed: HTTP ' + res.statusCode));
        }
        const chunks = [];
        let total = 0;
        res.on('data', function (c) {
          total += c.length;
          if (total > cap) { res.destroy(); return reject(new Error('manifest is over 5 MB')); }
          chunks.push(c);
        });
        res.on('end', function () { resolve(Buffer.concat(chunks)); });
        res.on('error', reject);
      });
      req.setTimeout(60000, function () { req.destroy(new Error('Download timed out')); });
      req.on('error', reject);
    };
    go(url, 0);
  });
}
