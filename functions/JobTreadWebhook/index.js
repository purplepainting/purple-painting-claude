/*
 * JobTreadWebhook -- hardened.
 *
 * WHAT WAS BREAKING, AND WHAT CHANGED
 *
 * 1. TRAILING PERIODS killed folder creation (7 of the 9 known failures).
 *    sanitizeFolderName() called .trim(), which strips whitespace but not a
 *    trailing dot. SharePoint rejects any item name ending in "." -- so
 *    "26 Celine Dr." / "1308 Bath St." / "1147 Coast Village Rd." all failed.
 *    FIX: strip leading/trailing dots and whitespace.
 *
 * 2. FAILURES WERE INVISIBLE. httpPost resolved non-2xx responses as if they
 *    had succeeded, so a rejected folder name flowed onward as an error object
 *    with no .id, and the real cause surfaced later as a bogus "getShareLink
 *    failed". httpGet called JSON.parse inside the 'end' callback with no
 *    guard, so a non-JSON reply threw synchronously where reject() could not
 *    catch it -- the invocation died with no message at all ("n/a" in the
 *    portal). That is the second failure mode: Agilent BCSL, Community
 *    Memorial Hospital.
 *    FIX: both helpers check status, reject with the URL and body, and time
 *    out. getAccessToken verifies it actually got a token instead of sending
 *    "Bearer undefined" two calls later.
 *
 * 3. ORDERING MADE EVERY FAILURE PERMANENT. The JobFolders row was written
 *    only after createFolder AND getShareLink both succeeded. That row is what
 *    BOTH sync directions depend on -- JobTreadWebhook's file handlers bail
 *    with "No folder mapping", and SharePointSync's resolveJobAndPath() cannot
 *    see the folder at all. So one flaky share link silently killed file sync
 *    for that job forever.
 *    FIX: write the row the instant a folder id exists. Everything after it
 *    (share link, custom field, comment) is best-effort and logged, never
 *    fatal. A share link that fails now falls back to the item's own webUrl.
 *
 * 4. UNPAGED /children TRUNCATED AT 200. "Deal Documents" holds 287 folders,
 *    so the jobUpdated stage-move search was blind to roughly 87 of them and
 *    reported "Job folder not found".
 *    FIX: page through every child.
 *
 * 5. RENAMING A JOB LEFT THE FOLDER BEHIND. Nothing renamed the SharePoint
 *    folder when a job name changed, so names drifted apart over time.
 *    FIX: jobUpdated compares the stored name and renames in place.
 *
 * 6. (2026-09-28) STAGE DETECTION read any custom field; now reads Job Walk
 *    Bid Stage + Production Stage by field ID (see decideStage). Stage folders renamed to
 *    Bids - Active / Bids - Closed Lost / Projects - Active / Projects - Complete
 *    (IDs unchanged, resolved via SP_FOLDER_ID_* App Settings).
 *
 * 7. (2026-09-29) DIGITAL BIDS. Fix 6 read only Job Walk Bid Stage, so a
 *    digital bid (Digital Takeoff Bid Stage, 22PWNYKhXZxr) marked Closed/Lost
 *    or Dormant with no Production Stage resolved to null and its folder
 *    stayed in Bids - Active. All three stage fields are now read by ID.
 *    Production Stage Completed/Closed also matches loosely now, so renaming
 *    the option (e.g. "5) Completed") can't silently file finished jobs as Won.
 *
 * 8. (2026-09-29) STALE ROWS. jobUpdated trusted the JobFolders row even when
 *    its folder had been deleted, so the move 404'd and file sync for that job
 *    stayed dead (1509 Shoreline Dr: 132 JT files never reached SharePoint).
 *    It now verifies the folder, falls back to the exact-name search across
 *    the stage folders, and rewrites the row with the folder it moved. A job
 *    with no row at all gets one written the same way.
 *
 * UNCHANGED: the fileCreated / fileUpdated / fileDeleted / jobDeleted logic,
 * the SharePoint-origin tag loop guard, and the table schemas.
 */

const https = require('https');
const { TableClient } = require('@azure/data-tables');

/*
 * STAGE FOLDER RESOLUTION  --  rewritten 2026-09-09.
 *
 * WHY THIS CHANGED
 * The old code held a single ROOT_FOLDER ('Deal Documents & Projects') and a
 * FOLDER_MAP of stage folder NAMES, then resolved them with a path lookup:
 *     /root:/Deal%20Documents%20%26%20Projects:/children
 * The SharePoint reorganisation renamed that root to '01 Projects' and split
 * the four stage folders across two parents, so every one of those lookups
 * started throwing 404 and jobCreated / jobUpdated stopped working entirely.
 * File sync kept working the whole time because it reads spFolderDriveId --
 * an item ID -- out of the JobFolders table.
 *
 * THE FIX, IN TWO LAYERS
 * 1. PREFERRED: put the four folder IDs in App Settings. Item IDs survive
 *    renames AND moves, so a future reorganisation cannot break this again.
 *      SP_FOLDER_ID_DEAL, SP_FOLDER_ID_WON, SP_FOLDER_ID_LOST,
 *      SP_FOLDER_ID_COMPLETE
 *    Run resolveAllStageFolders() once (see /api/... log output) to print the
 *    current IDs, paste them into App Settings, and the name lookup is never
 *    used again.
 * 2. FALLBACK: per-stage parent + name, matching the layout as of 2026-09-09.
 *    Each stage now carries its OWN parent, because they no longer share one.
 *
 * Resolved IDs are memoised in a module-level Map. In Azure Functions that
 * survives for the life of the warm process, not just one invocation --
 * which is fine, because item IDs survive renames and moves. The only way
 * to cache a bad value is to delete and recreate a stage folder; restart
 * the Function App if that ever happens.
 */

const STAGE_FOLDERS = {
  deal:     { parent: '02 Estimates', name: 'Bids - Active',       env: 'SP_FOLDER_ID_DEAL' },
  won:      { parent: '01 Projects',  name: 'Projects - Active',   env: 'SP_FOLDER_ID_WON' },
  lost:     { parent: '02 Estimates', name: 'Bids - Closed Lost',  env: 'SP_FOLDER_ID_LOST' },
  complete: { parent: '01 Projects',  name: 'Projects - Complete', env: 'SP_FOLDER_ID_COMPLETE' }
};

const _stageIdCache = new Map();

/* Verify an ID from App Settings still points at a live folder. A stale ID is
 * worse than no ID -- it silently writes into the wrong place. */
async function verifyFolderId(token, driveId, itemId) {
  try {
    const item = await httpGet(
      'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + itemId +
      '?$select=id,name,folder,deleted', token
    );
    if (item && item.id && item.folder && !item.deleted) return item;
    return null;
  } catch (e) { return null; }
}

/* Page through a named parent under the drive root and find one child by name. */
async function findChildByName(token, driveId, parentPath, childName) {
  let url = 'https://graph.microsoft.com/v1.0/drives/' + driveId + '/root:/' +
            encodeURIComponent(parentPath) + ':/children?$top=200&$select=id,name,folder';
  while (url) {
    const page = await httpGet(url, token);
    const hit = (page.value || []).find(function (f) { return f.folder && f.name === childName; });
    if (hit) return hit;
    url = page['@odata.nextLink'] || null;
  }
  return null;
}

/*
 * Resolve one stage folder to an item ID.
 *   stageKey: 'deal' | 'won' | 'lost' | 'complete'
 * Throws with a message naming the stage and the path it looked in, so a
 * future rename produces a diagnosable error instead of a bare 404.
 */
async function getStageFolderId(token, driveId, stageKey, context) {
  const stage = STAGE_FOLDERS[stageKey];
  if (!stage) throw new Error('Unknown stage key "' + stageKey + '"');

  const cacheKey = driveId + '|' + stageKey;
  if (_stageIdCache.has(cacheKey)) return _stageIdCache.get(cacheKey);

  const configured = process.env[stage.env];
  if (configured) {
    const item = await verifyFolderId(token, driveId, configured);
    if (item) {
      if (context) context.log('Stage "' + stageKey + '" -> ' + stage.env + ' (' + item.name + ')');
      _stageIdCache.set(cacheKey, configured);
      return configured;
    }
    if (context) {
      context.log.error(stage.env + ' is set to "' + configured +
        '" but that folder no longer exists -- falling back to name lookup. Update App Settings.');
    }
  }

  const found = await findChildByName(token, driveId, stage.parent, stage.name);
  if (!found) {
    throw new Error(
      'Stage folder "' + stage.name + '" not found under "' + stage.parent + '". ' +
      'The SharePoint layout changed -- update STAGE_FOLDERS, or better, set ' +
      stage.env + ' to the folder id in App Settings.'
    );
  }
  if (context) {
    context.log('Stage "' + stageKey + '" -> resolved by name at ' +
                stage.parent + '/' + stage.name + ' (id ' + found.id + ')');
  }
  _stageIdCache.set(cacheKey, found.id);
  return found.id;
}

/* Prints every stage id so they can be pasted into App Settings once. */
async function resolveAllStageFolders(token, driveId, context) {
  const out = {};
  for (const key of Object.keys(STAGE_FOLDERS)) {
    try {
      out[STAGE_FOLDERS[key].env] = await getStageFolderId(token, driveId, key, context);
    } catch (e) {
      out[STAGE_FOLDERS[key].env] = 'UNRESOLVED: ' + e.message;
    }
  }
  if (context) context.log('Stage folder ids: ' + JSON.stringify(out, null, 2));
  return out;
}
const SP_SYNC_TAG_ID = '22PYNSeQCN7F';
const ONEDRIVE_FOLDER_LINK_FIELD = '22PYNFyRMxqa';

function getStorageClient(tableName) {
  return TableClient.fromConnectionString(
    process.env.STORAGE_CONNECTION_STRING,
    tableName,
    { allowInsecureConnection: false }
  );
}

async function ensureTable(tableName) {
  const client = getStorageClient(tableName);
  try { await client.createTable(); } catch (e) { }
  return client;
}

/*
 * Maps a custom-field value to a stage folder, or null if the value isn't a
 * stage at all.
 *
 * FIXED 2026-09-09. The last line used to be `return 'deal'`, so ANY non-empty
 * value matched. The caller below loops over every custom field and breaks on
 * the first truthy result -- so `Sales Person = "Kendall Martin"` returned
 * 'deal', the loop stopped, and the real stage field was never read. Every
 * stage change silently "moved" the job to Deal Documents, where it already
 * was, and logged success. Returning null for unrecognised values is the fix:
 * non-stage fields are now skipped and the loop continues.
 *
 * Covers both bid fields (verified against live option lists 2026-09-29):
 *   Job Walk:        1) Requested: Proposal ... 5) Committed: Verbal -> deal
 *                    6) Deposit Paid: Closed/Won -> won
 *                    7) Dormant: No Response, Closed/Lost -> lost
 *   Digital Takeoff: 1) Received: Bid Invite, 2) Qualified: Bid for Takeoff,
 *                    3) Received: Takeoff / Estimate, 4) Finalized: Takeoff
 *                    (internal), 5) Reviewed & Sent: Estimate to Client -> deal
 *                    6) Closed/Won - GC Awarded Contract -> won
 *                    7) Dormant: No Response, Closed/Lost -> lost
 */
function getTargetFolder(status) {
  if (!status) return null;
  const s = String(status).toLowerCase();

  if (s.includes('closed/won') || s.includes('gc awarded') || s.includes('deposit paid')) return 'won';
  if (s.includes('closed/lost') || s.includes('dormant')) return 'lost';
  /* DEAL stages are checked BEFORE 'completed' on purpose: the Job Walk
   * stage "2) Completed: Job Walk" contains the word "completed" and would
   * otherwise file a job that has only had its walk done into
   * Projects - Complete. */
  if (s.includes('requested')  || s.includes('bid invite') || s.includes('job walk') ||
      s.includes('takeoff')    || s.includes('estimate')   || s.includes('committed') ||
      s.includes('qualified')) return 'deal';

  if (s.includes('completed')) return 'complete';

  return null;
}

/*
 * Decide the stage folder from the three JobTread stage fields.
 * Order matters:
 *   1. Production Stage Completed or Closed               -> complete
 *   2. Either bid stage Closed/Won                        -> won
 *   3. Either bid stage Closed/Lost or Dormant            -> lost
 *   4. Any other Production Stage value                   -> won
 *      (Pre-Con, Scheduled, In Progress, On Hold: production only starts on a
 *      won job, and the bid stage often lags behind -- e.g. still
 *      "1) Requested: Proposal" or "2) Completed: Job Walk" on a job in
 *      progress. Without this rule those folders bounced back to Bids.)
 *   5. Otherwise either bid stage's mapping (deal), or null = leave it alone.
 * Won beats lost across the two bid fields: a job that carries both was
 * almost always re-bid and awarded, and a wrongly-filed won job is the
 * costlier mistake.
 */
function decideStage(jobWalkBid, digitalBid, production) {
  const p = String(production || '').toLowerCase().trim();
  if (p.includes('completed') || p.includes('closed')) return 'complete';
  const bids = [getTargetFolder(jobWalkBid), getTargetFolder(digitalBid)];
  if (bids.includes('won')) return 'won';
  if (bids.includes('lost')) return 'lost';
  if (p) return 'won';
  return bids[0] || bids[1] || null;
}

/* FIX 1: strip trailing dots and whitespace, not just whitespace. */
function sanitizeFolderName(name) {
  return String(name || '')
    .replace(/[:\\/*?'"<>|#%]/g, '-')
    .replace(/^[.\s]+/, '')
    .replace(/[.\s]+$/, '')
    .trim();
}

function sanitizePathSegment(name) {
  return String(name || '')
    .replace(/[:*?'"<>|#%]/g, '-')
    .replace(/^[.\s]+/, '')
    .replace(/[.\s]+$/, '')
    .trim();
}

module.exports = async function (context, req) {
  context.log('JobTread webhook received');

  const body = req.body;
  if (!body || !body.createdEvent) {
    context.res = { status: 200, body: "No event data" };
    return;
  }

  const event = body.createdEvent;
  const eventType = event.type;
  const jobData = event.data && event.data.next;
  const jobId = (event.job && event.job.id) || (jobData && jobData.id);
  const fileData = event.file;

  context.log('Event: ' + eventType + ', Job ID: ' + jobId);

  try {
    const token = await getAccessToken();
    const siteId = await getSiteId(token);
    const driveId = await getDriveId(token, siteId);

    if (eventType === 'jobCreated') {
      const rawName = (jobData && jobData.name) || ('Job ' + (jobData && jobData.number));
      const jobName = sanitizeFolderName(rawName);

      if (!jobName) {
        throw new Error('Job name "' + rawName + '" sanitizes to an empty string');
      }
      if (jobName !== rawName) {
        context.log('Sanitized folder name: "' + rawName + '" -> "' + jobName + '"');
      }

      context.log('Creating folder for: ' + jobName);
      const parentFolderId = await getStageFolderId(token, driveId, 'deal', context);
      const newFolder = await createFolder(token, driveId, parentFolderId, jobName);
      context.log('Folder created, id: ' + newFolder.id);

      /*
       * FIX 3: write the mapping row IMMEDIATELY. Both sync directions depend
       * on it; nothing below is allowed to prevent it from being written.
       */
      const jobFolders = await ensureTable('JobFolders');
      await jobFolders.upsertEntity({
        partitionKey: 'jobs',
        rowKey: jobId,
        jobName: jobName,
        spFolderDriveId: newFolder.id,
        spFolderUrl: '',
        driveId: driveId
      }, 'Replace');
      context.log('Stored folder mapping for job ' + jobId);

      // Best-effort from here down. Failures are logged, never fatal.
      let shareLink = null;
      try {
        shareLink = await getShareLink(token, driveId, newFolder.id, context);
        context.log('Share link: ' + shareLink);
        await jobFolders.upsertEntity({
          partitionKey: 'jobs',
          rowKey: jobId,
          jobName: jobName,
          spFolderDriveId: newFolder.id,
          spFolderUrl: shareLink,
          driveId: driveId
        }, 'Replace');
      } catch (e) {
        context.log.error('Share link failed for ' + jobName + ' (folder + mapping are intact): ' + e.message);
      }

      if (shareLink) {
        try { await postJobTreadComment(jobId, shareLink, context); }
        catch (e) { context.log.error('Pinned comment failed: ' + e.message); }

        try { await postJobTreadFile(jobId, shareLink, context); }
        catch (e) { context.log.error('Custom field update failed: ' + e.message); }
      }

      context.log('Done for ' + jobName);
    }

    if (eventType === 'fileCreated') {
      const fileId = fileData && fileData.id;
      if (!fileId || !jobId) {
        context.log('No file or job data, skipping');
        context.res = { status: 200, body: "No file data" };
        return;
      }

      const fileInfo = await getFileInfo(fileId, context);
      const tags = (fileInfo && fileInfo.fileTags && fileInfo.fileTags.nodes) || [];
      const isFromSharePoint = tags.some(function(t) { return t.id === SP_SYNC_TAG_ID; });
      if (isFromSharePoint) {
        context.log('File ' + fileId + ' came from SharePoint, skipping');
        context.res = { status: 200, body: "Skipped - SharePoint origin" };
        return;
      }

      const syncedFiles = await ensureTable('SyncedFiles');
      const jtIndex = await ensureTable('SyncedFilesByJT');

      try {
        await jtIndex.getEntity(jobId, fileId);
        context.log('File ' + fileId + ' already tracked, skipping');
        context.res = { status: 200, body: "Already synced" };
        return;
      } catch (e) { }

      const jobFolders = await ensureTable('JobFolders');
      let folderMapping;
      try {
        folderMapping = await jobFolders.getEntity('jobs', jobId);
      } catch (e) {
        context.log.error('NO FOLDER MAPPING for job ' + jobId + ' -- file ' + fileId +
                          ' will NOT sync. Run BackfillJobFolders then BackfillJobFiles.');
        context.res = { status: 200, body: "No folder mapping" };
        return;
      }

      const fileUrl = fileInfo && fileInfo.url;
      const fileName = fileInfo && fileInfo.name;
      const jtFolderPath = fileInfo && fileInfo.folder;
      if (!fileUrl || !fileName) {
        context.log('No file URL or name, skipping');
        context.res = { status: 200, body: "No file URL" };
        return;
      }

      const targetFolderId = await resolveSPFolderId(token, folderMapping.driveId, folderMapping.spFolderDriveId, jtFolderPath, context);
      context.log('Uploading ' + fileName + ' to SharePoint' + (jtFolderPath ? ' (folder: ' + jtFolderPath + ')' : ''));
      /* 'rename' so a repeated daily-log name ("Photo 1") cannot overwrite an
       * earlier file. This is a NEW file, so there is nothing to replace. */
      const upload = await uploadFileToSharePoint(token, folderMapping.driveId, targetFolderId, fileName, fileUrl, context, 'rename');
      const spItem = upload.item;
      const storedName = upload.name;

      /* Keep JobTread's name in step with SharePoint's. If they diverge, a
       * later JobTread-side edit re-uploads under the old name and leaves two
       * copies in SharePoint. */
      if (storedName !== fileName) {
        try {
          await updateJTFileName(fileId, storedName, context);
          context.log('Renamed in JobTread to match SharePoint: "' + storedName + '"');
        } catch (e) {
          context.log.error('Could not rename file in JobTread (names now differ, ' +
                            'JobTread "' + fileName + '" vs SharePoint "' + storedName + '"): ' + e.message);
        }
      }

      /*
       * Key by SHAREPOINT item id -- that is what SharePointSync looks up.
       * Keying by the JobTread id (as before) meant SharePointSync saw the
       * file as brand new and copied it straight back, duplicating it.
       */
      const spItemId = (spItem && spItem.id) || fileId;
      await syncedFiles.upsertEntity({
        partitionKey: jobId,
        rowKey: spItemId,
        fileName: storedName,
        origin: 'jt',
        jtFileId: fileId,
        jtFolderPath: jtFolderPath || '',
        spFolderPath: jtFolderPath || '',
        syncedAt: new Date().toISOString()
      }, 'Replace');

      if (spItem && spItem.id) {
        await jtIndex.upsertEntity({
          partitionKey: jobId,
          rowKey: fileId,
          spItemId: spItem.id
        }, 'Replace');
      }

      context.log('Synced file ' + fileName + ' to SharePoint');
    }

    if (eventType === 'fileUpdated') {
      const fileId = fileData && fileData.id;
      if (!fileId || !jobId) {
        context.log('No file or job data, skipping');
        context.res = { status: 200, body: "No file data" };
        return;
      }

      const fileInfo = await getFileInfo(fileId, context);
      const tags = (fileInfo && fileInfo.fileTags && fileInfo.fileTags.nodes) || [];
      const isFromSharePoint = tags.some(function(t) { return t.id === SP_SYNC_TAG_ID; });
      if (isFromSharePoint) {
        context.log('File ' + fileId + ' update came from SharePoint, skipping');
        context.res = { status: 200, body: "Skipped - SharePoint origin" };
        return;
      }

      const jobFolders = await ensureTable('JobFolders');
      let folderMapping;
      try {
        folderMapping = await jobFolders.getEntity('jobs', jobId);
      } catch (e) {
        context.log.error('NO FOLDER MAPPING for job ' + jobId + ' -- update to file ' + fileId + ' not synced');
        context.res = { status: 200, body: "No folder mapping" };
        return;
      }

      const fileUrl = fileInfo && fileInfo.url;
      const fileName = fileInfo && fileInfo.name;
      const newFolderPath = fileInfo && fileInfo.folder;
      if (!fileUrl || !fileName) {
        context.log('No file URL or name, skipping');
        context.res = { status: 200, body: "No file URL" };
        return;
      }

      const syncedFiles = await ensureTable('SyncedFiles');
      const jtIndex = await ensureTable('SyncedFilesByJT');

      let existingRecord = null;
      let existingSpItemId = null;
      try {
        const idx = await jtIndex.getEntity(jobId, fileId);
        existingSpItemId = idx.spItemId;
        existingRecord = await syncedFiles.getEntity(jobId, existingSpItemId);
      } catch (e) { }

      const oldFolderPath = existingRecord ? (existingRecord.jtFolderPath || '') : '';
      const folderChanged = existingRecord && oldFolderPath !== (newFolderPath || '');

      /*
       * RENAME IN PLACE. A JobTread rename fires fileUpdated, and the old code
       * simply re-uploaded under the new name -- leaving the file's previous
       * name sitting in SharePoint as a second copy, which SharePointSync then
       * bounced back into JobTread as a third.
       *
       * Renaming the item first preserves its id, so the replace-upload below
       * overwrites the same item instead of creating a new one. Do this before
       * any folder move: Graph handles name and parent separately, and moving
       * first would leave the rename looking for the item in the old place.
       */
      const previousName = existingRecord ? (existingRecord.fileName || '') : '';
      if (existingSpItemId && previousName && previousName !== fileName) {
        context.log('Renamed in JobTread: "' + previousName + '" -> "' + fileName + '"');
        try {
          await renameItem(token, folderMapping.driveId, existingSpItemId, fileName);
          context.log('Renamed SharePoint item in place (id preserved)');
        } catch (e) {
          context.log.error('SharePoint rename failed, falling back to re-upload (may leave "' +
                            previousName + '" behind): ' + e.message);
        }
      }

      if (folderChanged) {
        context.log('Folder changed for ' + fileName + ': "' + oldFolderPath + '" -> "' + (newFolderPath || '') + '"');
        try {
          const oldFolderId = await resolveSPFolderId(token, folderMapping.driveId, folderMapping.spFolderDriveId, oldFolderPath, context);
          await deleteSharePointFileByName(token, folderMapping.driveId, oldFolderId, fileName, context);
        } catch (e) {
          context.log('Could not remove old SharePoint copy: ' + e.message);
        }
      }

      const targetFolderId = await resolveSPFolderId(token, folderMapping.driveId, folderMapping.spFolderDriveId, newFolderPath, context);
      context.log('Re-uploading updated file ' + fileName + ' to SharePoint');
      /* 'replace' here on purpose: this IS an overwrite of the same file. */
      const upload = await uploadFileToSharePoint(token, folderMapping.driveId, targetFolderId, fileName, fileUrl, context, 'replace');
      const spItem = upload.item;
      const storedName = upload.name;

      const spItemId = (spItem && spItem.id) || existingSpItemId || fileId;

      if (existingSpItemId && existingSpItemId !== spItemId) {
        try { await syncedFiles.deleteEntity(jobId, existingSpItemId); } catch (e) { }
      }

      await syncedFiles.upsertEntity({
        partitionKey: jobId,
        rowKey: spItemId,
        fileName: storedName,
        origin: 'jt',
        jtFileId: fileId,
        jtFolderPath: newFolderPath || '',
        spFolderPath: newFolderPath || '',
        syncedAt: new Date().toISOString()
      }, 'Replace');

      await jtIndex.upsertEntity({
        partitionKey: jobId,
        rowKey: fileId,
        spItemId: spItemId
      }, 'Replace');

      context.log('Updated file ' + fileName + ' in SharePoint');
    }

    if (eventType === 'fileDeleted') {
      const fileId = fileData && fileData.id;
      if (!fileId || !jobId) {
        context.log('No file or job data, skipping');
        context.res = { status: 200, body: "No file data" };
        return;
      }

      const syncedFiles = await ensureTable('SyncedFiles');
      const jtIndex = await ensureTable('SyncedFilesByJT');

      let fileRecord = null;
      let spRowKey = null;

      try {
        const indexEntry = await jtIndex.getEntity(jobId, fileId);
        spRowKey = indexEntry.spItemId;
        fileRecord = await syncedFiles.getEntity(jobId, spRowKey);
      } catch (e) { }

      if (!fileRecord) {
        try {
          fileRecord = await syncedFiles.getEntity(jobId, fileId);
          spRowKey = fileId;
        } catch (e) { }
      }

      if (!fileRecord) {
        context.log('File ' + fileId + ' not tracked, nothing to delete');
        context.res = { status: 200, body: "Not tracked" };
        return;
      }

      const jobFolders = await ensureTable('JobFolders');
      let folderMapping;
      try {
        folderMapping = await jobFolders.getEntity('jobs', jobId);
      } catch (e) {
        context.log('No folder mapping for job ' + jobId + ', cannot delete from SharePoint');
        context.res = { status: 200, body: "No folder mapping" };
        return;
      }

      try {
        const folderPath = fileRecord.jtFolderPath || fileRecord.spFolderPath || '';
        const targetFolderId = await resolveSPFolderId(token, folderMapping.driveId, folderMapping.spFolderDriveId, folderPath, context);
        await deleteSharePointFileByName(token, folderMapping.driveId, targetFolderId, fileRecord.fileName, context);
        context.log('Deleted ' + fileRecord.fileName + ' from SharePoint');
      } catch (e) {
        context.log('Failed to delete from SharePoint: ' + e.message);
      }

      try { await syncedFiles.deleteEntity(jobId, spRowKey); } catch (e) { }
      try { await jtIndex.deleteEntity(jobId, fileId); } catch (e) { }
      context.log('Removed tracking records for ' + spRowKey);
    }

    if (eventType === 'jobUpdated') {
      if (!jobData) {
        context.res = { status: 200, body: "No job data" };
        return;
      }

      const jobFolders = await ensureTable('JobFolders');
      let mapping = null;
      try { mapping = await jobFolders.getEntity('jobs', jobId); } catch (e) { }

      const currentName = sanitizeFolderName(await getJobName(jobId, context));

      /* FIX 5: rename the SharePoint folder when the job name changes. */
      if (mapping && mapping.spFolderDriveId && currentName && mapping.jobName !== currentName) {
        context.log('Job renamed: "' + mapping.jobName + '" -> "' + currentName + '"');
        try {
          await renameItem(token, mapping.driveId || driveId, mapping.spFolderDriveId, currentName);
          await jobFolders.upsertEntity(
            Object.assign({}, mapping, { jobName: currentName }), 'Replace'
          );
          mapping.jobName = currentName;
          context.log('Renamed SharePoint folder to "' + currentName + '"');
        } catch (e) {
          context.log.error('Folder rename failed: ' + e.message);
        }
      }

      /* FIXED 2026-09-28: read the stage fields by ID. The old loop took
       * the first stage-looking value from ANY custom field, and both
       * "How Did Bid Come In" options ("Requested Job Walk", "Received Digital
       * Bid Invite") match the deal keywords -- so a won job could be filed
       * back into Bids - Active depending on field order.
       * 2026-09-29: Digital Takeoff Bid Stage added (fix 7). */
      const stages = await getStageFields(jobId, context);
      const newStatus = decideStage(stages.jobWalkBid, stages.digitalBid, stages.production);
      if (newStatus) {
        context.log('Stage fields: Job Walk "' + (stages.jobWalkBid || '') + '", Digital "' +
                    (stages.digitalBid || '') + '", Production "' +
                    (stages.production || '') + '" -> ' + newStatus);
      }

      if (!newStatus) {
        context.log('No relevant status change detected');
        context.res = { status: 200, body: "No action needed" };
        return;
      }

      // Prefer the mapped folder id over searching by name.
      let existingFolderId = mapping && mapping.spFolderDriveId ? mapping.spFolderDriveId : null;

      /* FIX 8 (2026-09-29): a row can point at a folder that was deleted or
       * replaced (1509 Shoreline Dr). File sync for that job then fails
       * silently forever. Verify the mapped folder still exists; if not, fall
       * through to the name search below and rewrite the row with what it
       * finds. */
      let staleRow = false;
      if (existingFolderId) {
        const live = await verifyFolderId(token, mapping.driveId || driveId, existingFolderId);
        if (!live) {
          context.log.error('STALE JobFolders row for ' + jobId + ': folder ' + existingFolderId +
                            ' no longer exists -- searching stage folders by name to repair');
          existingFolderId = null;
          staleRow = true;
        }
      }

      if (!existingFolderId) {
        context.log('No mapping -- searching all stage folders for: ' + currentName);
        for (const stageKey of Object.keys(STAGE_FOLDERS)) {
          try {
            const parentId = await getStageFolderId(token, driveId, stageKey, context);
            const children = await listAllChildren(token, driveId, parentId);
            const match = children.find(function(f) { return f.folder && f.name === currentName; });
            if (match) {
              existingFolderId = match.id;
              context.log('Found existing folder in stage: ' + stageKey);
              break;
            }
          } catch (e) {
            context.log('Skipping search in stage ' + stageKey + ': ' + e.message);
          }
        }
      }

      if (!existingFolderId) {
        context.log.error('Job folder not found in SharePoint for "' + currentName + '"');
        context.res = { status: 200, body: "Folder not found" };
        return;
      }

      const targetParentId = await getStageFolderId(token, driveId, newStatus, context);
      await moveFolder(token, driveId, existingFolderId, targetParentId, currentName);

      try {
        /* Write the row even when none existed (name-search hit), and always
         * record the folder id we actually moved -- this is what repairs a
         * missing or stale mapping. */
        const base = mapping || { partitionKey: 'jobs', rowKey: jobId, spFolderUrl: '' };
        await jobFolders.upsertEntity(
          Object.assign({}, base, {
            jobName: currentName,
            spFolderDriveId: existingFolderId,
            driveId: base.driveId || driveId,
            currentFolder: STAGE_FOLDERS[newStatus].name
          }), 'Replace'
        );
        if (staleRow || !mapping) {
          context.log('REPAIRED JobFolders row for ' + jobId + ' -> folder ' + existingFolderId +
                      ' ("' + currentName + '"). Run BackfillJobFiles?jobId=' + jobId +
                      ' to push files that were dropped while it was broken.');
        }
      } catch (e) {
        context.log('Could not update folder mapping: ' + e.message);
      }

      context.log('Moved folder to ' + STAGE_FOLDERS[newStatus].name);
    }

    if (eventType === 'jobDeleted') {
      if (!jobId) {
        context.res = { status: 200, body: "No job ID" };
        return;
      }

      const jobFolders = await ensureTable('JobFolders');
      let folderMapping;
      try {
        folderMapping = await jobFolders.getEntity('jobs', jobId);
      } catch (e) {
        context.log('No folder mapping for deleted job ' + jobId);
        context.res = { status: 200, body: "No folder mapping" };
        return;
      }

      try {
        await deleteSharePointItem(token, folderMapping.driveId, folderMapping.spFolderDriveId, context);
        context.log('Deleted SharePoint folder for job ' + jobId + ' (' + folderMapping.jobName + ')');
      } catch (e) {
        context.log('Failed to delete SharePoint folder: ' + e.message);
      }

      await jobFolders.deleteEntity('jobs', jobId);

      try {
        const syncedFiles = await ensureTable('SyncedFiles');
        const jtIndex = await ensureTable('SyncedFilesByJT');
        const filesToDelete = [];
        for await (const entity of syncedFiles.listEntities({ queryOptions: { filter: "PartitionKey eq '" + jobId + "'" } })) {
          filesToDelete.push(entity.rowKey);
        }
        for (const k of filesToDelete) await syncedFiles.deleteEntity(jobId, k);

        const indexToDelete = [];
        for await (const entity of jtIndex.listEntities({ queryOptions: { filter: "PartitionKey eq '" + jobId + "'" } })) {
          indexToDelete.push(entity.rowKey);
        }
        for (const k of indexToDelete) await jtIndex.deleteEntity(jobId, k);

        context.log('Cleaned up ' + filesToDelete.length + ' SyncedFiles and ' + indexToDelete.length + ' index records');
      } catch (e) {
        context.log('Error cleaning up SyncedFiles: ' + e.message);
      }
    }

    context.res = { status: 200, body: "Success" };

  } catch (err) {
    /* FIX 2: log the stack, the event type and the job id -- never a bare "n/a". */
    const detail = (err && (err.stack || err.message)) || String(err);
    context.log.error('JobTreadWebhook FAILED [' + eventType + ' / job ' + jobId + ']: ' + detail);
    context.res = { status: 500, body: detail };
  }
};

// ------------------------------------------------------------ sharepoint

async function resolveSPFolderId(token, driveId, jobFolderId, jtFolderPath, context) {
  if (!jtFolderPath) return jobFolderId;
  const segments = String(jtFolderPath).split('/').map(sanitizePathSegment).filter(Boolean);
  let currentParentId = jobFolderId;
  for (const segment of segments) {
    const children = await listAllChildren(token, driveId, currentParentId);
    let match = children.find(function(f) { return f.folder && f.name === segment; });
    if (!match) {
      context.log('Creating SharePoint subfolder: ' + segment);
      match = await createFolder(token, driveId, currentParentId, segment);
    }
    currentParentId = match.id;
  }
  return currentParentId;
}

/* FIX 4: page through children instead of truncating at Graph's default. */
async function listAllChildren(token, driveId, parentId) {
  const out = [];
  let url = 'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + parentId +
            '/children?$top=200&$select=id,name,folder,file,size';
  while (url) {
    const page = await httpGet(url, token);
    for (const item of (page.value || [])) out.push(item);
    url = page['@odata.nextLink'] || null;
  }
  return out;
}

/*
 * Daily log photos arrive from JobTread named "Photo 1", "Photo 2"... with NO
 * file extension, and the names repeat across every daily log on a job. The
 * old upload PUT to :/content with no conflictBehavior, which defaults to
 * REPLACE -- so every new log's "Photo 1" silently overwrote the previous
 * one. Roughly 42 SharePoint copies were destroyed this way. (The originals
 * survived in JobTread, which is the only reason this was recoverable.)
 *
 * Two fixes, both needed:
 *   - Sniff the real type from the first bytes and append an extension, so
 *     files preview instead of downloading as unknown blobs.
 *   - Pass conflictBehavior explicitly. fileCreated uses 'rename' so a
 *     collision becomes "Photo 1 1.jpg" instead of destroying the original.
 *     fileUpdated keeps 'replace', because there the overwrite is the point.
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

  /* Deliberately NOT sniffing the zip signature (50 4b 03 04): docx, xlsx and
   * pptx are all zips, and guessing ".zip" would be worse than no extension. */
  return null;
}

function ensureExtension(name, buffer, context) {
  const base = String(name || 'file');
  if (/\.[A-Za-z0-9]{1,6}$/.test(base)) return base;
  const ext = sniffExtension(buffer);
  if (!ext) return base;
  if (context) context.log('No extension on "' + base + '" -- detected ' + ext + ' from content');
  return base + ext;
}

/*
 * Returns { item, name }. `name` is what the file is ACTUALLY called in
 * SharePoint, which may differ from fileName if an extension was added or
 * Graph resolved a collision. Callers must record that name, and push it back
 * to JobTread, or the two sides drift -- and a later JobTread-side edit would
 * upload under the old name and create a duplicate.
 */
async function uploadFileToSharePoint(token, driveId, folderId, fileName, fileUrl, context, conflictBehavior) {
  const fileBuffer = await downloadFile(fileUrl);
  const uploadName = ensureExtension(fileName, fileBuffer, context);
  const behavior = conflictBehavior || 'replace';

  const uploadUrl = 'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + folderId +
                    ':/' + encodeURIComponent(uploadName) + ':/content' +
                    '?%40microsoft.graph.conflictBehavior=' + behavior;

  const item = await httpPutBuffer(uploadUrl, fileBuffer, token, context);
  const finalName = (item && item.name) || uploadName;

  if (finalName !== fileName) {
    context.log('Stored in SharePoint as "' + finalName + '" (JobTread calls it "' + fileName + '")');
  }
  return { item: item, name: finalName };
}

/* Rename a file in JobTread so both sides agree after an extension was added
 * or a collision was resolved. */
async function updateJTFileName(fileId, newName, context) {
  const result = await httpPostRaw('https://api.jobtread.com/pave', JSON.stringify({
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      updateFile: { $: { id: fileId, name: newName } }
    }
  }), context);
  if (!result || typeof result !== 'object' || result.errors) {
    throw new Error('updateJTFileName failed: ' + JSON.stringify(result).slice(0, 300));
  }
  return result;
}

async function deleteSharePointFileByName(token, driveId, folderId, fileName, context) {
  const children = await listAllChildren(token, driveId, folderId);
  const match = children.find(function(f) { return f.name === fileName; });
  if (!match) {
    context.log('File ' + fileName + ' not found in SharePoint, nothing to delete');
    return;
  }
  await deleteSharePointItem(token, driveId, match.id, context);
}

async function deleteSharePointItem(token, driveId, itemId, context) {
  return new Promise(function(resolve, reject) {
    const urlObj = new URL('https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + itemId);
    const req = https.request({
      hostname: urlObj.hostname,
      path: urlObj.pathname,
      method: 'DELETE',
      headers: { 'Authorization': 'Bearer ' + token }
    }, function(res) {
      let data = '';
      res.on('data', function(c) { data += c; });
      res.on('end', function() {
        if (res.statusCode >= 200 && res.statusCode < 300) resolve(data);
        else reject(new Error('Delete failed ' + res.statusCode + ': ' + data.slice(0, 300)));
      });
    });
    req.setTimeout(30000, function() { req.destroy(new Error('DELETE timed out')); });
    req.on('error', reject);
    req.end();
  });
}

async function createFolder(token, driveId, parentId, folderName) {
  const body = JSON.stringify({
    name: folderName,
    folder: {},
    "@microsoft.graph.conflictBehavior": "rename"
  });
  const result = await httpPost(
    'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + parentId + '/children',
    body,
    { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + token }
  );
  if (!result || !result.id) {
    throw new Error('createFolder returned no id for "' + folderName + '": ' + JSON.stringify(result));
  }
  return result;
}

/* Never fatal on its own: falls back to the item's plain webUrl. */
async function getShareLink(token, driveId, itemId, context, retries = 3) {
  for (let i = 0; i < retries; i++) {
    try {
      const data = await httpPost(
        'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + itemId + '/createLink',
        JSON.stringify({ type: "edit", scope: "organization" }),
        { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + token }
      );
      if (data && data.link && data.link.webUrl) return data.link.webUrl;
      if (context) context.log('getShareLink attempt ' + (i + 1) + ' returned no link');
    } catch (e) {
      if (context) context.log('getShareLink attempt ' + (i + 1) + ' failed: ' + e.message);
    }
    if (i < retries - 1) await new Promise(function(r) { setTimeout(r, 2000); });
  }
  const item = await httpGet('https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + itemId, token);
  if (!item || !item.webUrl) throw new Error('No share link and no webUrl for item ' + itemId);
  if (context) context.log('Fell back to direct webUrl');
  return item.webUrl;
}

async function renameItem(token, driveId, itemId, newName) {
  return await httpPatch(
    'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + itemId,
    JSON.stringify({ name: newName }),
    { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + token }
  );
}

async function moveFolder(token, driveId, itemId, newParentId, name) {
  return await httpPatch(
    'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + itemId,
    JSON.stringify({ parentReference: { id: newParentId }, name: name }),
    { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + token }
  );
}

async function getAccessToken() {
  const body = 'grant_type=client_credentials' +
    '&client_id=' + process.env.AZURE_CLIENT_ID +
    '&client_secret=' + encodeURIComponent(process.env.AZURE_CLIENT_SECRET) +
    '&scope=https%3A%2F%2Fgraph.microsoft.com%2F.default';
  const data = await httpPost(
    'https://login.microsoftonline.com/' + process.env.AZURE_TENANT_ID + '/oauth2/v2.0/token',
    body,
    { 'Content-Type': 'application/x-www-form-urlencoded' }
  );
  if (!data || !data.access_token) {
    throw new Error('Token request returned no access_token (check AZURE_CLIENT_SECRET expiry): ' + JSON.stringify(data));
  }
  return data.access_token;
}

async function getSiteId(token) {
  const data = await httpGet('https://graph.microsoft.com/v1.0/sites/purplepainting.sharepoint.com:/sites/purplepainting', token);
  if (!data || !data.id) throw new Error('Could not resolve site id: ' + JSON.stringify(data));
  return data.id;
}

async function getDriveId(token, siteId) {
  const data = await httpGet('https://graph.microsoft.com/v1.0/sites/' + siteId + '/drives', token);
  const drive = (data.value || []).find(function(d) { return d.name === 'Documents'; }) || (data.value || [])[0];
  if (!drive) throw new Error('No drive found on site ' + siteId);
  return drive.id;
}

/* getNamedFolderId() removed -- replaced by getStageFolderId() above. */

// ------------------------------------------------------------ jobtread

async function getFileInfo(fileId, context) {
  const result = await httpPostRaw('https://api.jobtread.com/pave', JSON.stringify({
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      file: {
        $: { id: fileId },
        id: {}, name: {}, url: {}, folder: {},
        fileTags: { nodes: { id: {}, name: {} } }
      }
    }
  }), context);
  return result && result.file;
}

async function getJobName(jobId, context) {
  const result = await httpPostRaw('https://api.jobtread.com/pave', JSON.stringify({
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      job: { $: { id: jobId }, name: {}, number: {} }
    }
  }), context);
  const job = result && result.job;
  if (!job) return null;
  return job.name || ('Job ' + job.number);
}

const JOB_WALK_BID_STAGE_FIELD = '22PWRx62raQg';  // Job Walk Bid Stage (autofilled)
const DIGITAL_BID_STAGE_FIELD = '22PWNYKhXZxr';   // Digital Takeoff Bid Stage (autofilled)
const PRODUCTION_STAGE_FIELD = '22PWsEy5ZSXL';    // Production Stage (autofilled)

async function getStageFields(jobId, context) {
  const result = await httpPostRaw('https://api.jobtread.com/pave', JSON.stringify({
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      job: {
        $: { id: jobId },
        customFieldValues: { $: { size: 50 }, nodes: { value: {}, customField: { id: {} } } }
      }
    }
  }), context);
  const nodes = (result && result.job && result.job.customFieldValues &&
                 result.job.customFieldValues.nodes) || [];
  const get = function (id) {
    const n = nodes.find(function (x) { return x.customField && x.customField.id === id; });
    return n ? n.value : null;
  };
  return {
    jobWalkBid: get(JOB_WALK_BID_STAGE_FIELD),
    digitalBid: get(DIGITAL_BID_STAGE_FIELD),
    production: get(PRODUCTION_STAGE_FIELD)
  };
}

async function postJobTreadComment(jobId, shareLink, context) {
  await httpPostRaw('https://api.jobtread.com/pave', JSON.stringify({
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      createComment: {
        $: {
          targetId: jobId,
          targetType: "job",
          message: 'OneDrive Folder: ' + shareLink,
          isPinned: true
        }
      }
    }
  }), context);
}

async function postJobTreadFile(jobId, shareLink, context) {
  await httpPostRaw('https://api.jobtread.com/pave', JSON.stringify({
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      updateJob: {
        $: {
          id: jobId,
          customFieldValues: { [ONEDRIVE_FOLDER_LINK_FIELD]: shareLink }
        }
      }
    }
  }), context);
}

// ------------------------------------------------------------ http

async function downloadFile(url) {
  return new Promise(function(resolve, reject) {
    const go = function(u, hops) {
      if (hops > 5) return reject(new Error('Too many redirects'));
      const req = https.get(u, function(res) {
        if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
          res.resume();
          return go(res.headers.location, hops + 1);
        }
        if (res.statusCode < 200 || res.statusCode >= 300) {
          res.resume();
          return reject(new Error('Download failed: HTTP ' + res.statusCode));
        }
        const chunks = [];
        res.on('data', function(c) { chunks.push(c); });
        res.on('end', function() { resolve(Buffer.concat(chunks)); });
        res.on('error', reject);
      });
      req.setTimeout(120000, function() { req.destroy(new Error('Download timed out')); });
      req.on('error', reject);
    };
    go(url, 0);
  });
}

async function httpPutBuffer(url, buffer, token, context) {
  return new Promise(function(resolve, reject) {
    const urlObj = new URL(url);
    const req = https.request({
      hostname: urlObj.hostname,
      path: urlObj.pathname + urlObj.search,
      method: 'PUT',
      headers: {
        'Authorization': 'Bearer ' + token,
        'Content-Type': 'application/octet-stream',
        'Content-Length': buffer.length
      }
    }, function(res) {
      let data = '';
      res.on('data', function(c) { data += c; });
      res.on('end', function() {
        context.log('SP upload status: ' + res.statusCode);
        if (res.statusCode < 200 || res.statusCode >= 300) {
          return reject(new Error('SP upload ' + res.statusCode + ': ' + String(data).slice(0, 300)));
        }
        try { resolve(JSON.parse(data)); } catch(e) { resolve({}); }
      });
    });
    req.setTimeout(180000, function() { req.destroy(new Error('SP upload timed out')); });
    req.on('error', reject);
    req.write(buffer);
    req.end();
  });
}

function httpPostRaw(url, body, context) {
  return new Promise(function(resolve, reject) {
    const urlObj = new URL(url);
    const req = https.request({
      hostname: urlObj.hostname,
      path: urlObj.pathname,
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) }
    }, function(res) {
      let data = '';
      res.on('data', function(c) { data += c; });
      res.on('end', function() {
        context.log('JT HTTP status: ' + res.statusCode);
        try { resolve(JSON.parse(data)); }
        catch(e) { reject(new Error('JT bad JSON (' + res.statusCode + '): ' + data.slice(0, 300))); }
      });
    });
    req.setTimeout(60000, function() { req.destroy(new Error('JT request timed out')); });
    req.on('error', reject);
    req.write(body);
    req.end();
  });
}

/*
 * FIX 2: status-checked, safely parsed, with a timeout. The old version parsed
 * inside the 'end' callback with no try/catch, so a non-JSON body threw
 * synchronously where reject() could not see it -- killing the invocation with
 * no error message at all.
 */
function httpGet(url, token) {
  return new Promise(function(resolve, reject) {
    const req = https.get(url, { headers: { 'Authorization': 'Bearer ' + token } }, function(res) {
      let data = '';
      res.on('data', function(c) { data += c; });
      res.on('end', function() {
        if (res.statusCode < 200 || res.statusCode >= 300) {
          return reject(new Error('GET ' + url + ' -> ' + res.statusCode + ': ' + data.slice(0, 300)));
        }
        try { resolve(JSON.parse(data)); }
        catch(e) { reject(new Error('GET ' + url + ' bad JSON (' + res.statusCode + '): ' + data.slice(0, 300))); }
      });
    });
    req.setTimeout(30000, function() { req.destroy(new Error('GET timed out: ' + url)); });
    req.on('error', reject);
  });
}

function httpPost(url, body, headers) {
  return new Promise(function(resolve, reject) {
    const urlObj = new URL(url);
    const req = https.request({
      hostname: urlObj.hostname,
      path: urlObj.pathname + urlObj.search,
      method: 'POST',
      headers: Object.assign({}, headers, { 'Content-Length': Buffer.byteLength(body) })
    }, function(res) {
      let data = '';
      res.on('data', function(c) { data += c; });
      res.on('end', function() {
        if (res.statusCode < 200 || res.statusCode >= 300) {
          return reject(new Error('POST ' + url + ' -> ' + res.statusCode + ': ' + String(data).slice(0, 300)));
        }
        try { resolve(JSON.parse(data)); } catch(e) { resolve(data); }
      });
    });
    req.setTimeout(30000, function() { req.destroy(new Error('POST timed out: ' + url)); });
    req.on('error', reject);
    req.write(body);
    req.end();
  });
}

function httpPatch(url, body, headers) {
  return new Promise(function(resolve, reject) {
    const urlObj = new URL(url);
    const req = https.request({
      hostname: urlObj.hostname,
      path: urlObj.pathname,
      method: 'PATCH',
      headers: Object.assign({}, headers, { 'Content-Length': Buffer.byteLength(body) })
    }, function(res) {
      let data = '';
      res.on('data', function(c) { data += c; });
      res.on('end', function() {
        if (res.statusCode < 200 || res.statusCode >= 300) {
          return reject(new Error('PATCH ' + url + ' -> ' + res.statusCode + ': ' + String(data).slice(0, 300)));
        }
        try { resolve(JSON.parse(data)); } catch(e) { resolve(data); }
      });
    });
    req.setTimeout(30000, function() { req.destroy(new Error('PATCH timed out: ' + url)); });
    req.on('error', reject);
    req.write(body);
    req.end();
  });
}