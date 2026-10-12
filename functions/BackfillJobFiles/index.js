/*
 * BackfillJobFiles  --  HTTP-triggered, one-shot file replay.
 *
 * WHY THIS EXISTS
 * When a job had no JobFolders row, JobTreadWebhook's fileCreated handler hit
 * "No folder mapping", returned 200, and discarded the event. Nothing replays
 * it. BackfillJobFolders repaired the mappings, but only for FUTURE events --
 * every file uploaded to those jobs before the repair is still JobTread-only.
 * This walks those jobs and pushes the missing files across.
 *
 * THE KEYING TRAP (important)
 * JobTreadWebhook writes SyncedFiles rows keyed by the JOBTREAD file id:
 *     upsertEntity({ partitionKey: jobId, rowKey: jtFileId, ... })
 * SharePointSync looks the same table up by the SHAREPOINT item id:
 *     syncedFiles.getEntity(jobId, item.id)
 * Those never match, so SharePointSync treats a file it just received as brand
 * new and copies it back into JobTread as a duplicate.
 *
 * This function writes rows keyed by SHAREPOINT item id (with origin:'jt'),
 * plus the SyncedFilesByJT index row. origin:'jt' makes SharePointSync skip
 * its content-hash replace branch, so a backfilled file is left alone unless
 * somebody actually moves it.
 *
 * DRY RUN BY DEFAULT.
 *
 *   GET /api/BackfillJobFiles                          report only
 *   GET /api/BackfillJobFiles?apply=true               replay the repaired jobs
 *   GET /api/BackfillJobFiles?jobId=22Pd8YxSus4t&apply=true    one job
 *   GET /api/BackfillJobFiles?all=true                 every job with a mapping
 */

const https = require('https');
const { TableClient } = require('@azure/data-tables');

const JT_ORG_ID = '22PWNY9u7qZd';
const SP_SYNC_TAG_ID = '22PYNSeQCN7F';

// Files larger than this are skipped and reported, never buffered into memory.
// A 313MB plan set is what stalled SharePointSync on 7/27.
const MAX_BYTES = 100 * 1024 * 1024;

/*
 * NEVER processed, whatever parameters are passed. Goleta Valley Cottage
 * Hospital is under litigation hold -- do not copy its files anywhere. This is
 * checked inside the job loop, so it holds for jobId= and all=true alike.
 */
const BLOCKED_JOBS = {
  '22PX9MaHxFt8': 'Goleta Valley Cottage Hospital -- litigation hold'
};

/*
 * Default targets: the jobs holding daily log photos, which is what this
 * function is currently being used for. Verified 2026-09-25.
 *
 * 541 Grove Ln (22PZNW5vXbED) is deliberately OMITTED: it has two SharePoint
 * folders ("541 Grove Ln" and "541 Grove Ln 1") from an old folder-creation
 * collision, so its files could land in whichever one the table happens to
 * point at. Add it back once that is merged.
 *
 * Use all=true only for a genuine full sweep -- that targets every JobFolders
 * row (~199 jobs) and will run past the function timeout.
 */
const PHOTO_JOBS = [
  '22PXjg7k8Eu3', // 1509 Shoreline Dr      -- 60 photos, 20 generic-named
  '22PapufhAVXs', // 4040 Cuervo Ave        -- 44 photos, done 2026-09-25
  '22PZiRNE3RFP', // St. Rafael's           -- 43 photos, 2 generic-named
  '22PbFdnx6fgJ', // 3834 Center Ave        -- 14 photos, 3 generic-named
  '22PY2Z6jbCfV', // SBCC Cosmetology       -- 8 photos
  '22PXfPfhTEhn', // 1014 Via Los Padres    -- 7 photos
  '22Pcvvy83WjG', // 863 N San Marcos       -- 4 photos
  '22PeHQGzn7k9', // 252 Brentwood Way      -- 2 photos, done 2026-09-25
  '22PYnarU2kPN'  // 2737 Miradero Dr.      -- 1 photo
];

const MIME = {
  pdf: 'application/pdf',
  jpg: 'image/jpeg', jpeg: 'image/jpeg', png: 'image/png', gif: 'image/gif',
  heic: 'image/heic', webp: 'image/webp',
  doc: 'application/msword',
  docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  xls: 'application/vnd.ms-excel',
  xlsx: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  ppt: 'application/vnd.ms-powerpoint',
  pptx: 'application/vnd.openxmlformats-officedocument.presentationml.presentation',
  mp4: 'video/mp4', mov: 'video/quicktime', txt: 'text/plain', csv: 'text/csv'
};

function contentTypeFor(name) {
  const ext = String(name || '').split('.').pop().toLowerCase();
  return MIME[ext] || 'application/octet-stream';
}

/*
 * Daily log photos come out of JobTread as "Photo 1", "Photo 2"... with no
 * extension, and the names repeat across every log on a job. Uploading those
 * to :/content with no conflictBehavior defaults to REPLACE, which is exactly
 * how ~42 SharePoint copies were destroyed in the first place. Re-pulling them
 * with the old code would have destroyed them a second time.
 */
function sniffExtension(buf) {
  if (!buf || buf.length < 12) return null;
  const hex = buf.slice(0, 12).toString('hex').toLowerCase();

  if (hex.startsWith('ffd8ff')) return '.jpg';
  if (hex.startsWith('89504e470d0a1a0a')) return '.png';
  if (hex.startsWith('474946383')) return '.gif';
  if (hex.startsWith('25504446')) return '.pdf';

  if (buf.slice(0, 4).toString('latin1') === 'RIFF' &&
      buf.slice(8, 12).toString('latin1') === 'WEBP') return '.webp';

  if (buf.slice(4, 8).toString('latin1') === 'ftyp') {
    const brand = buf.slice(8, 12).toString('latin1');
    if (brand.startsWith('hei') || brand.startsWith('mif1') || brand.startsWith('msf1')) return '.heic';
    if (brand.startsWith('qt')) return '.mov';
    return '.mp4';
  }

  /* Not sniffing the zip signature: docx/xlsx/pptx are all zips. */
  return null;
}

function ensureExtension(name, buffer, context) {
  const base = String(name || 'file');
  if (/\.[A-Za-z0-9]{1,6}$/.test(base)) return base;
  const ext = sniffExtension(buffer);
  if (!ext) return base;
  if (context) context.log('    no extension on "' + base + '" -- detected ' + ext);
  return base + ext;
}

/* Strip a trailing extension, for matching JobTread's "Photo 1" against
 * SharePoint's "Photo 1.jpg". */
function baseName(name) {
  return String(name || '').replace(/\.[A-Za-z0-9]{1,6}$/, '');
}

/* Keep JobTread's name in step after an extension is added, so a later
 * JobTread-side edit does not re-upload under the old name and duplicate. */
async function updateJTFileName(fileId, newName) {
  const result = await jtQuery({
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      updateFile: { $: { id: fileId, name: newName } }
    }
  });
  if (!result || typeof result !== 'object' || result.errors) {
    throw new Error('updateJTFileName failed: ' + JSON.stringify(result).slice(0, 300));
  }
  return result;
}

function sanitizePathSegment(name) {
  return String(name || '')
    .replace(/[:*?'"<>|#%\\/]/g, '-')
    .replace(/^[.\s]+/, '')
    .replace(/[.\s]+$/, '')
    .trim();
}

async function ensureTable(tableName) {
  const client = TableClient.fromConnectionString(
    process.env.STORAGE_CONNECTION_STRING, tableName, { allowInsecureConnection: false }
  );
  try { await client.createTable(); } catch (e) { /* exists */ }
  return client;
}

// ---------------------------------------------------------------- http

function httpGet(url, token) {
  return new Promise(function (resolve, reject) {
    const req = https.get(url, { headers: { Authorization: 'Bearer ' + token } }, function (res) {
      let data = '';
      res.on('data', function (c) { data += c; });
      res.on('end', function () {
        if (res.statusCode < 200 || res.statusCode >= 300) {
          return reject(new Error('GET ' + res.statusCode + ': ' + data.slice(0, 300)));
        }
        try { resolve(JSON.parse(data)); }
        catch (e) { reject(new Error('GET bad JSON: ' + data.slice(0, 300))); }
      });
    });
    req.setTimeout(30000, function () { req.destroy(new Error('GET timed out: ' + url)); });
    req.on('error', reject);
  });
}

function httpPost(url, body, headers) {
  return new Promise(function (resolve, reject) {
    const u = new URL(url);
    const req = https.request({
      hostname: u.hostname, path: u.pathname + u.search, method: 'POST',
      headers: Object.assign({}, headers, { 'Content-Length': Buffer.byteLength(body) })
    }, function (res) {
      let data = '';
      res.on('data', function (c) { data += c; });
      res.on('end', function () {
        if (res.statusCode < 200 || res.statusCode >= 300) {
          return reject(new Error('POST ' + res.statusCode + ': ' + String(data).slice(0, 300)));
        }
        try { resolve(JSON.parse(data)); } catch (e) { resolve(data); }
      });
    });
    req.setTimeout(30000, function () { req.destroy(new Error('POST timed out')); });
    req.on('error', reject);
    req.write(body);
    req.end();
  });
}

function jtQuery(payload) {
  return new Promise(function (resolve, reject) {
    const body = JSON.stringify(payload);
    const req = https.request({
      hostname: 'api.jobtread.com', path: '/pave', method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) }
    }, function (res) {
      let data = '';
      res.on('data', function (c) { data += c; });
      res.on('end', function () {
        try { resolve(JSON.parse(data)); }
        catch (e) { reject(new Error('JT bad JSON (' + res.statusCode + '): ' + data.slice(0, 300))); }
      });
    });
    req.setTimeout(60000, function () { req.destroy(new Error('JT timed out')); });
    req.on('error', reject);
    req.write(body);
    req.end();
  });
}

/*
 * Streams to a buffer but aborts the moment MAX_BYTES is exceeded, so an
 * oversized file costs one aborted request instead of blowing the heap.
 */
function downloadCapped(url, maxBytes) {
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
        const declared = parseInt(res.headers['content-length'] || '0', 10);
        if (declared && declared > maxBytes) {
          res.destroy();
          return reject(new Error('OVERSIZE:' + declared));
        }
        const chunks = [];
        let total = 0;
        res.on('data', function (c) {
          total += c.length;
          if (total > maxBytes) {
            res.destroy();
            return reject(new Error('OVERSIZE:' + total));
          }
          chunks.push(c);
        });
        res.on('end', function () { resolve(Buffer.concat(chunks)); });
        res.on('error', reject);
      });
      req.setTimeout(120000, function () { req.destroy(new Error('Download timed out')); });
      req.on('error', reject);
    };
    go(url, 0);
  });
}

function uploadToSharePoint(url, buffer, token, contentType) {
  return new Promise(function (resolve, reject) {
    const u = new URL(url);
    const req = https.request({
      hostname: u.hostname, path: u.pathname + u.search, method: 'PUT',
      headers: {
        Authorization: 'Bearer ' + token,
        'Content-Type': contentType,
        'Content-Length': buffer.length
      }
    }, function (res) {
      let data = '';
      res.on('data', function (c) { data += c; });
      res.on('end', function () {
        if (res.statusCode < 200 || res.statusCode >= 300) {
          return reject(new Error('SP upload ' + res.statusCode + ': ' + String(data).slice(0, 300)));
        }
        try { resolve(JSON.parse(data)); } catch (e) { resolve({}); }
      });
    });
    req.setTimeout(180000, function () { req.destroy(new Error('SP upload timed out')); });
    req.on('error', reject);
    req.write(buffer);
    req.end();
  });
}

// ---------------------------------------------------------------- graph

async function getAccessToken() {
  const body = 'grant_type=client_credentials' +
    '&client_id=' + process.env.AZURE_CLIENT_ID +
    '&client_secret=' + encodeURIComponent(process.env.AZURE_CLIENT_SECRET) +
    '&scope=https%3A%2F%2Fgraph.microsoft.com%2F.default';
  const data = await httpPost(
    'https://login.microsoftonline.com/' + process.env.AZURE_TENANT_ID + '/oauth2/v2.0/token',
    body, { 'Content-Type': 'application/x-www-form-urlencoded' }
  );
  if (!data || !data.access_token) throw new Error('No access_token: ' + JSON.stringify(data));
  return data.access_token;
}

async function listChildren(token, driveId, parentId) {
  const out = [];
  let url = 'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + parentId +
            '/children?$top=200&$select=id,name,size,folder,file';
  while (url) {
    const page = await httpGet(url, token);
    for (const i of (page.value || [])) out.push(i);
    url = page['@odata.nextLink'] || null;
  }
  return out;
}

async function itemExists(token, driveId, itemId) {
  try {
    const item = await httpGet(
      'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + itemId + '?$select=id,name,size,deleted',
      token
    );
    return item && item.id && !item.deleted ? item : null;
  } catch (e) { return null; }
}

async function createFolder(token, driveId, parentId, name) {
  const result = await httpPost(
    'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + parentId + '/children',
    JSON.stringify({ name: name, folder: {}, '@microsoft.graph.conflictBehavior': 'fail' }),
    { 'Content-Type': 'application/json', Authorization: 'Bearer ' + token }
  );
  if (!result || !result.id) throw new Error('createFolder gave no id for "' + name + '"');
  return result;
}

// Mirrors a JobTread folder path into SharePoint, creating levels as needed.
async function resolveSubfolder(token, driveId, rootId, jtFolderPath, cache, context) {
  if (!jtFolderPath) return rootId;
  const segments = String(jtFolderPath).split('/').map(sanitizePathSegment).filter(Boolean);
  let parentId = rootId;
  let key = rootId;
  for (const seg of segments) {
    key = key + '/' + seg;
    if (cache.has(key)) { parentId = cache.get(key); continue; }
    const children = await listChildren(token, driveId, parentId);
    let match = children.find(function (c) { return c.folder && c.name === seg; });
    if (!match) {
      context.log('    creating subfolder "' + seg + '"');
      match = await createFolder(token, driveId, parentId, seg);
    }
    cache.set(key, match.id);
    parentId = match.id;
  }
  return parentId;
}

// ---------------------------------------------------------------- jobtread

async function fetchJobFiles(jobId) {
  const files = [];
  let page = null;
  let guard = 0;
  while (guard++ < 50) {
    const args = { size: 100 };
    if (page) args.page = page;
    const res = await jtQuery({
      query: {
        $: { grantKey: process.env.JOBTREAD_API_KEY },
        job: {
          $: { id: jobId },
          name: {},
          files: {
            $: args,
            nextPage: {},
            nodes: {
              id: {}, name: {}, url: {}, folder: {}, size: {}, createdAt: {},
              fileTags: { nodes: { id: {} } }
            }
          }
        }
      }
    });
    const job = res && res.job;
    if (!job) throw new Error('Job not found: ' + jobId);
    const conn = job.files || {};
    for (const n of (conn.nodes || [])) {
      const tags = (n.fileTags && n.fileTags.nodes) || [];
      files.push({
        id: n.id,
        name: n.name,
        url: n.url,
        folder: n.folder || '',
        size: n.size || 0,
        fromSharePoint: tags.some(function (t) { return t.id === SP_SYNC_TAG_ID; })
      });
    }
    page = conn.nextPage;
    if (!page) break;
  }
  return files;
}

// ---------------------------------------------------------------- main

module.exports = async function (context, req) {
  const body = (req && req.body) || {};
  function param(name) {
    const v = (req.query && req.query[name]) !== undefined ? req.query[name] : body[name];
    return v === undefined || v === null ? '' : String(v).trim();
  }
  const apply = param('apply').toLowerCase() === 'true';
  const onlyJobId = param('jobId') || null;
  const doAll = param('all').toLowerCase() === 'true';

  context.log('params -> apply=' + apply + ' jobId=' + (onlyJobId || '(none)') + ' all=' + doAll);

  const report = {
    mode: apply ? 'APPLY' : 'DRY RUN',
    jobs: [],
    totals: { jobs: 0, uploaded: 0, adopted: 0, alreadyTracked: 0, repulled: 0, skippedSpOrigin: 0, oversize: 0, failed: 0 },
    startedAt: new Date().toISOString()
  };

  try {
    context.log('BackfillJobFiles starting -- ' + report.mode);

    const token = await getAccessToken();
    const jobFolders = await ensureTable('JobFolders');
    const syncedFiles = await ensureTable('SyncedFiles');
    const jtIndex = await ensureTable('SyncedFilesByJT');

    const rows = new Map();
    for await (const e of jobFolders.listEntities()) rows.set(e.rowKey, e);
    context.log('JobFolders holds ' + rows.size + ' rows');

    let targets;
    if (onlyJobId) targets = [onlyJobId];
    else if (doAll) targets = Array.from(rows.keys());
    else targets = PHOTO_JOBS;

    context.log('Targeting ' + targets.length + ' job(s)');

    for (const jobId of targets) {
      if (BLOCKED_JOBS[jobId]) {
        context.log('BLOCKED, skipping ' + jobId + ': ' + BLOCKED_JOBS[jobId]);
        report.blocked = report.blocked || [];
        report.blocked.push({ jobId: jobId, reason: BLOCKED_JOBS[jobId] });
        continue;
      }
      const jobReport = {
        jobId: jobId, name: null,
        uploaded: [], adopted: [], alreadyTracked: 0,
        skippedSpOrigin: 0, oversize: [], failed: []
      };
      report.jobs.push(jobReport);
      report.totals.jobs++;

      try {
        const row = rows.get(jobId);
        if (!row || !row.spFolderDriveId) {
          jobReport.failed.push({ error: 'No JobFolders row -- run BackfillJobFolders first' });
          report.totals.failed++;
          continue;
        }

        const driveId = row.driveId;
        const rootId = row.spFolderDriveId;

        // A row pointing at a deleted folder is worse than no row at all.
        const folder = await itemExists(token, driveId, rootId);
        if (!folder) {
          jobReport.failed.push({ error: 'JobFolders row points at a missing folder (' + rootId + ')' });
          report.totals.failed++;
          context.log(jobId + ': STALE ROW -- folder ' + rootId + ' no longer exists');
          continue;
        }

        const files = await fetchJobFiles(jobId);
        jobReport.name = row.jobName || jobId;
        context.log(jobId + ' (' + jobReport.name + '): ' + files.length + ' JT files');

        /*
         * WHICH SHAREPOINT ITEM DOES EACH JOBTREAD FILE CLAIM?
         *
         * A tracking row proves a sync was ATTEMPTED, not that the file
         * survived. Daily logs upload several files called "Photo 1" into the
         * same folder within milliseconds of each other. The old upload used
         * PUT :/content with no conflictBehavior, which REPLACES -- and a
         * replace keeps the same SharePoint item id. So all three "Photo 1"
         * rows point at one item holding only the last photo's bytes. Two
         * photos are gone from SharePoint, yet every one of them looks
         * "already tracked" and gets skipped.
         *
         * Build the claim map up front so a duplicate claim is visible before
         * any file is processed.
         */
        const claims = new Map();   // spItemId -> [jtFileId, ...]
        const claimOf = new Map();  // jtFileId -> spItemId
        for (const f of files) {
          if (f.fromSharePoint) continue;
          try {
            const idx = await jtIndex.getEntity(jobId, f.id);
            if (idx && idx.spItemId) {
              claimOf.set(f.id, idx.spItemId);
              if (!claims.has(idx.spItemId)) claims.set(idx.spItemId, []);
              claims.get(idx.spItemId).push(f.id);
            }
          } catch (e) { /* no row -- handled below */ }
        }

        const contested = Array.from(claims.values()).filter(function (a) { return a.length > 1; }).length;
        if (contested) {
          context.log('  ' + contested + ' SharePoint item(s) claimed by more than one JobTread file ' +
                      '-- overwritten copies, will re-pull the losers');
        }

        const spItemCache = new Map();
        async function spItemFor(id) {
          if (spItemCache.has(id)) return spItemCache.get(id);
          const item = await itemExists(token, driveId, id);
          spItemCache.set(id, item);
          return item;
        }

        const subfolderCache = new Map();
        const childCache = new Map();

        for (const f of files) {
          try {
            if (f.fromSharePoint) { jobReport.skippedSpOrigin++; report.totals.skippedSpOrigin++; continue; }

            /*
             * A row only counts as tracked if the SharePoint item it names
             * still exists, is not claimed by a different JobTread file, and
             * matches this file's size. Anything else means the SharePoint
             * copy is not this file, so re-pull it -- conflictBehavior=rename
             * now lands it beside the survivor instead of overwriting.
             */
            const claimedId = claimOf.get(f.id);
            if (claimedId) {
              const spItem = await spItemFor(claimedId);
              const sharers = claims.get(claimedId) || [];

              let verdict = null;
              if (!spItem) {
                verdict = 'SharePoint item ' + claimedId + ' no longer exists';
              } else if (sharers.length > 1 && f.size && Number(spItem.size) !== Number(f.size)) {
                verdict = 'SharePoint item holds ' + spItem.size + 'b, this file is ' + f.size +
                          'b -- overwritten by one of ' + sharers.length + ' files sharing the name';
              }
              /*
               * NO size check on a UNIQUE claim. JobTread's reported size does
               * not match the bytes it serves for Office formats -- a docx
               * listed at 10098b downloads as ~17000b -- so a mismatch check
               * here fired on every run, re-uploaded the file as "name 1.docx",
               * and did it again next time. Observed accumulating copies of
               * 4040_Cuervo_Job_Walk_Summary and SBCC PreCon Walk Transcript
               * on 2026-09-25.
               *
               * A size mismatch is only evidence of an overwrite when several
               * JobTread files point at the same SharePoint item. With one
               * claimant and the item present, trust the row.
               */

              if (!verdict) {
                jobReport.alreadyTracked++; report.totals.alreadyTracked++;
                continue;
              }

              context.log('    RE-PULLING "' + f.name + '" (' + f.id + '): ' + verdict);
              jobReport.repulled = (jobReport.repulled || 0) + 1;
              report.totals.repulled = (report.totals.repulled || 0) + 1;

              /* Drop the bad index row so the fresh upload can claim its own
               * item. The SyncedFiles row stays -- it still correctly
               * describes the file that DID survive. */
              if (apply) {
                try { await jtIndex.deleteEntity(jobId, f.id); } catch (e) { }
              }
            }

            if (f.size && f.size > MAX_BYTES) {
              jobReport.oversize.push({ name: f.name, mb: Math.round(f.size / 1048576) });
              report.totals.oversize++;
              continue;
            }

            const targetFolderId = apply
              ? await resolveSubfolder(token, driveId, rootId, f.folder, subfolderCache, context)
              : rootId;

            // Does a file of this name already sit there? Adopt it -- writing
            // the tracking rows -- rather than uploading a second copy.
            let siblings = childCache.get(targetFolderId);
            if (!siblings) {
              siblings = await listChildren(token, driveId, targetFolderId);
              childCache.set(targetFolderId, siblings);
            }
            /*
             * Match on base name AND size. Base name because JobTread's
             * "Photo 1" is "Photo 1.jpg" in SharePoint once an extension is
             * added; size because a job can hold several different files all
             * called "Photo 1" (one per daily log), and matching on name alone
             * would map every one of them onto the same SharePoint item.
             */
            const wantBase = baseName(f.name);
            let existing = siblings.find(function (c) {
              if (c.folder) return false;
              if (baseName(c.name) !== wantBase) return false;
              if (f.size && c.size && Number(f.size) !== Number(c.size)) return false;
              return true;
            });

            /*
             * Second pass for files Graph already collision-renamed:
             * "Photo 1" -> "Photo 1 1.jpg". Only accepted on an EXACT size
             * match, which is the same rule used for the OneDrive dedup work --
             * identical byte count means identical file. Without this the
             * re-pull adds a third copy alongside the two that exist.
             */
            if (!existing && f.size) {
              existing = siblings.find(function (c) {
                if (c.folder) return false;
                if (Number(c.size) !== Number(f.size)) return false;
                return baseName(c.name).indexOf(wantBase + ' ') === 0;
              }) || undefined;
              if (existing) {
                context.log('    matched collision-renamed copy "' + existing.name +
                            '" by exact size (' + f.size + 'b)');
              }
            }

            if (!apply) {
              if (existing) {
                jobReport.adopted.push({ name: f.name, folder: f.folder || '(root)' });
                report.totals.adopted++;
              } else {
                jobReport.uploaded.push({ name: f.name, folder: f.folder || '(root)' });
                report.totals.uploaded++;
              }
              continue;
            }

            let spItem = existing;
            let storedName = existing ? existing.name : f.name;
            let extensionAdded = false;

            if (!spItem) {
              let buffer;
              try {
                buffer = await downloadCapped(f.url, MAX_BYTES);
              } catch (e) {
                if (String(e.message).indexOf('OVERSIZE:') === 0) {
                  const bytes = parseInt(String(e.message).split(':')[1], 10);
                  jobReport.oversize.push({ name: f.name, mb: Math.round(bytes / 1048576) });
                  report.totals.oversize++;
                  continue;
                }
                throw e;
              }

              const uploadName = ensureExtension(f.name, buffer, context);
              extensionAdded = uploadName !== f.name;

              /* conflictBehavior=rename: several daily logs on one job all
               * produce "Photo 1". Without this each upload overwrites the
               * last -- the exact bug this backfill exists to undo. */
              const uploadUrl = 'https://graph.microsoft.com/v1.0/drives/' + driveId +
                '/items/' + targetFolderId + ':/' + encodeURIComponent(uploadName) +
                ':/content?%40microsoft.graph.conflictBehavior=rename';

              spItem = await uploadToSharePoint(uploadUrl, buffer, token, contentTypeFor(uploadName));
              storedName = (spItem && spItem.name) || uploadName;

              siblings.push({ id: spItem.id, name: storedName, size: f.size });
              jobReport.uploaded.push({ name: storedName, folder: f.folder || '(root)' });
              report.totals.uploaded++;
              context.log('    uploaded ' + storedName + (storedName !== f.name ? ' (JobTread: "' + f.name + '")' : ''));
            } else {
              jobReport.adopted.push({ name: storedName, folder: f.folder || '(root)' });
              report.totals.adopted++;
              context.log('    adopted existing ' + storedName);
            }

            if (!spItem || !spItem.id) throw new Error('No SharePoint item id for ' + f.name);

            /*
             * Push the name back to JobTread ONLY when an extension was added
             * ("Photo 1" -> "Photo 1.jpg"). Use the FULL stored name, suffix
             * included: three re-pulled files land as "Photo 1.jpg",
             * "Photo 1 1.jpg", "Photo 1 2.jpg", and JobTread must match each
             * one exactly or the sides diverge again.
             *
             * When no extension was added, don't rename at all. Graph turning
             * "Summary.docx" into "Summary 1.docx" is a SharePoint-side
             * artifact and must not degrade a clean JobTread name.
             * (Observed on 4040 Cuervo, 2026-09-25.)
             */
            if (extensionAdded) {
              try {
                await updateJTFileName(f.id, storedName);
                context.log('    renamed in JobTread to "' + storedName + '"');
              } catch (e) {
                context.log('    could not rename in JobTread (names now differ): ' + e.message);
              }
            }

            /*
             * Keyed by SHAREPOINT item id -- this is what SharePointSync reads.
             * origin:'jt' keeps it out of the content-hash replace branch.
             */
            await syncedFiles.upsertEntity({
              partitionKey: jobId,
              rowKey: spItem.id,
              fileName: storedName,
              origin: 'jt',
              jtFileId: f.id,
              jtFolderPath: f.folder || '',
              spFolderPath: f.folder || '',
              syncedAt: new Date().toISOString()
            }, 'Replace');

            await jtIndex.upsertEntity({
              partitionKey: jobId,
              rowKey: f.id,
              spItemId: spItem.id
            }, 'Replace');

          } catch (e) {
            context.log('    FAILED ' + f.name + ': ' + e.message);
            jobReport.failed.push({ name: f.name, error: e.message });
            report.totals.failed++;
          }
        }
      } catch (e) {
        context.log(jobId + ' FAILED: ' + (e.stack || e.message));
        jobReport.failed.push({ error: e.message });
        report.totals.failed++;
      }
    }

    report.finishedAt = new Date().toISOString();
    const t = report.totals;
    report.summary = t.jobs + ' jobs, ' + t.uploaded + ' uploaded, ' + t.adopted +
      ' adopted, ' + t.alreadyTracked + ' already tracked, ' +
      (t.repulled || 0) + ' re-pulled (overwritten copies), ' + t.skippedSpOrigin +
      ' skipped (SP origin), ' + t.oversize + ' oversize, ' + t.failed + ' failed';
    context.log(report.summary);

    context.res = { status: 200, headers: { 'Content-Type': 'application/json' }, body: report };

  } catch (err) {
    const detail = (err && (err.stack || err.message)) || String(err);
    context.log.error('BackfillJobFiles aborted: ' + detail);
    report.fatal = detail;
    context.res = { status: 500, headers: { 'Content-Type': 'application/json' }, body: report };
  }
};