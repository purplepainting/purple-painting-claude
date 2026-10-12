const https = require('https');
const { TableClient } = require('@azure/data-tables');

const SP_SYNC_TAG_ID = '22PYNSeQCN7F';
const JT_ORG_ID = '22PWNY9u7qZd';

/*
 * Added 2026-09-09. BackfillJobFiles has had this cap since it was written;
 * SharePointSync never got it, which is what let a 313MB plan set stall the
 * timer on 7/27. downloadFile() buffers the whole body into memory, so one
 * oversized file takes the function down and every later file in the delta
 * with it. Anything over the cap is logged and skipped -- deliberately NOT
 * recorded in SyncedFiles, so it gets reconsidered if the cap is ever raised.
 */
const MAX_BYTES = 100 * 1024 * 1024;

function tooBig(item) {
  const n = Number(item && item.size);
  return Number.isFinite(n) && n > MAX_BYTES;
}

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

module.exports = async function (context, myTimer) {
  context.log('SharePointSync (delta) timer fired');

  /*
   * 2026-09-28: time budget. The Function App kills a run at 5 minutes
   * (seen 9/28 09:29: 300035 ms, "Delta returned 815 changed items" during
   * the Toyota upload). A killed run never stores the new delta link, so the
   * next run re-reads the same backlog -- and everything behind it (renames,
   * moves) waits hours. Worse, a kill in the middle of an upload creates the
   * JT file but never writes its tracking row, so the next run uploads it
   * again: a duplicate in JobTread.
   * Stop starting new items after RUN_BUDGET_MS, leave the delta link where
   * it was, and let the next run (a minute later) carry on. Items already
   * done have tracking rows and are skipped cheaply on the re-read.
   */
  const RUN_STARTED = Date.now();
  const RUN_BUDGET_MS = 3.5 * 60 * 1000;
  let outOfTime = false;

  try {
    const token = await getAccessToken();
    const siteId = await getSiteId(token);
    const driveId = await getDriveId(token, siteId);
    const jobFolders = await ensureTable('JobFolders');
    const syncedFiles = await ensureTable('SyncedFiles');
    const syncState = await ensureTable('SyncState');
    const jtIndex = await ensureTable('SyncedFilesByJT');

    const jobs = [];
    for await (const entity of jobFolders.listEntities()) {
      jobs.push(entity);
    }
    context.log('Tracking ' + jobs.length + ' job folders');

    const jobFolderMap = new Map();
    for (const job of jobs) {
      jobFolderMap.set(job.spFolderDriveId, job);
    }

    let storedLink = null;
    try {
      const state = await syncState.getEntity('state', 'deltaLink');
      storedLink = state.value;
    } catch (e) {
      context.log('No stored delta link, seeding from latest');
    }

    let deltaUrl;
    if (storedLink) {
      deltaUrl = storedLink;
    } else {
      deltaUrl = 'https://graph.microsoft.com/v1.0/drives/' + driveId + '/root/delta?token=latest';
      const seedResult = await httpGet(deltaUrl, token);
      const seedLink = seedResult['@odata.deltaLink'];
      if (seedLink) {
        await syncState.upsertEntity({ partitionKey: 'state', rowKey: 'deltaLink', value: seedLink }, 'Replace');
        context.log('Seeded delta link, no changes processed this run');
      }
      context.res = { status: 200, body: 'Seeded' };
      return;
    }

    let allChanges = [];
    let nextLink = deltaUrl;
    let finalDeltaLink = null;

    while (nextLink) {
      const page = await httpGet(nextLink, token);
      const items = page.value || [];
      allChanges = allChanges.concat(items);

      if (page['@odata.nextLink']) {
        nextLink = page['@odata.nextLink'];
      } else if (page['@odata.deltaLink']) {
        finalDeltaLink = page['@odata.deltaLink'];
        nextLink = null;
      } else {
        nextLink = null;
      }
    }

    context.log('Delta returned ' + allChanges.length + ' changed items');

    const parentCache = new Map();

    async function resolveJobAndPath(item) {
      let currentId = item.parentReference && item.parentReference.id;
      const pathSegments = [];

      let depth = 0;
      while (currentId && depth < 20) {
        if (jobFolderMap.has(currentId)) {
          return { job: jobFolderMap.get(currentId), folderPath: pathSegments.reverse().join('/') };
        }

        let parentInfo = parentCache.get(currentId);
        if (!parentInfo) {
          try {
            const data = await httpGet(
              'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + currentId,
              token
            );
            parentInfo = { name: data.name, parentId: data.parentReference && data.parentReference.id };
            parentCache.set(currentId, parentInfo);
          } catch (e) {
            return null;
          }
        }

        pathSegments.push(parentInfo.name);
        currentId = parentInfo.parentId;
        depth++;
      }

      return null;
    }

    async function getQuickXorHash(item) {
      const fromDelta = item.file && item.file.hashes && item.file.hashes.quickXorHash;
      if (fromDelta) {
        return fromDelta;
      }
      try {
        const data = await httpGet(
          'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + item.id + '?$select=file',
          token
        );
        return data.file && data.file.hashes && data.file.hashes.quickXorHash;
      } catch (e) {
        return null;
      }
    }

    /*
     * 2026-09-25: folder moves and renames. Graph delta reports a moved or
     * renamed folder as ONE item -- the folder -- and not its descendants,
     * so every file under it kept its old folder path in JobTread (seen on
     * SBCC Cosmetology: 99 files stranded after "Pre-Con Jobwalk Photos" ->
     * "Photos/Job Walk"). When a folder INSIDE a job folder changes, list its
     * descendants and run each one through the normal per-file path/name
     * check below. Files already in step cost one table read and nothing
     * else.
     *
     * Budget: at most FOLDER_EXPAND_BUDGET descendant files per run, so one
     * huge move cannot run the timer past its time limit. Folders not
     * finished are kept in SyncState ('pendingFolders') and resumed next run.
     */
    const FOLDER_EXPAND_BUDGET = 150;
    let expandBudget = FOLDER_EXPAND_BUDGET;
    const queue = allChanges.slice();
    const queuedIds = new Set(queue.map(function (i) { return i.id; }));
    const pendingFolders = [];

    let storedPending = [];
    try {
      const p = await syncState.getEntity('state', 'pendingFolders');
      storedPending = JSON.parse(p.value || '[]');
    } catch (e) { /* none */ }
    for (const fid of storedPending) {
      queue.push({ id: fid, folder: {}, __resume: true });
    }

    async function expandFolder(folderId) {
      const stack = [folderId];
      while (stack.length) {
        const current = stack.pop();
        if (expandBudget <= 0) {
          pendingFolders.push(current);
          continue;
        }
        let url = 'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + current +
                  '/children?$top=200&$select=id,name,folder,file,size,parentReference';
        while (url) {
          const page = await httpGet(url, token);
          for (const child of (page.value || [])) {
            if (child.folder) { stack.push(child.id); continue; }
            if (queuedIds.has(child.id)) continue;
            queuedIds.add(child.id);
            queue.push(child);
            expandBudget--;
          }
          url = page['@odata.nextLink'] || null;
        }
      }
    }

    for (let qi = 0; qi < queue.length; qi++) {
      if (Date.now() - RUN_STARTED > RUN_BUDGET_MS) {
        outOfTime = true;
        context.log.warn('Time budget reached after ' + qi + ' of ' + queue.length +
                         ' items -- stopping cleanly; delta link NOT advanced, next run resumes');
        break;
      }
      const item = queue[qi];
      try {
        if (item.root) {
          continue;
        }
        if (item.folder) {
          if (item.deleted) continue;
          if (jobFolderMap.has(item.id)) continue; // the job folder itself (stage moves): relative paths unchanged
          if (!item.__resume) {
            const where = await resolveJobAndPath(item);
            if (!where) continue; // not inside a job folder
          }
          try {
            await expandFolder(item.id);
            context.log('Expanded changed folder ' + (item.name || item.id) + ' -- queue now ' + queue.length + ' items');
          } catch (e) {
            context.log.error('Could not expand folder ' + (item.name || item.id) + ': ' + e.message);
            pendingFolders.push(item.id);
          }
          continue;
        }

        const resolved = await resolveJobAndPath(item);
        if (!resolved) {
          continue;
        }

        const job = resolved.job;
        const jobId = job.rowKey;
        const folderPath = resolved.folderPath;

        if (item.deleted) {
          context.log('Detected deletion in SharePoint: ' + (item.name || item.id) + ' (job: ' + job.jobName + ')');
          let tracked;
          try {
            tracked = await syncedFiles.getEntity(jobId, item.id);
          } catch (e) {
            continue;
          }
          /*
           * 2026-09-25: a blank jtFileId used to fall back to tracked.rowKey,
           * which is the SHAREPOINT item id -- so deleteJTFile was called with
           * an id JobTread has never heard of. Refuse instead. Deletion is the
           * one change we do not auto-repair: a wrong name+size match here
           * would delete the wrong JobTread file, and that cannot be undone.
           */
          if (!tracked.jtFileId) {
            context.log.warn('NOT DELETING in JT: ' + (tracked.fileName || item.id) + ' (job: ' + job.jobName +
                             ') was deleted in SharePoint but its tracking row has no jtFileId. ' +
                             'Delete the JobTread copy by hand if intended.');
            continue;
          }
          try {
            const jtFileId = tracked.jtFileId;
            await deleteJTFile(jtFileId, context);
            await syncedFiles.deleteEntity(jobId, item.id);
            try {
              await jtIndex.deleteEntity(jobId, jtFileId);
            } catch (e) { /* may not exist */ }
            context.log('Deleted ' + tracked.fileName + ' from JT (job: ' + job.jobName + ')');
          } catch (e) {
            context.log('Failed to delete from JT: ' + e.message);
          }
          continue;
        }

        const fileName = item.name;

        if (tooBig(item)) {
          context.log('SKIPPING oversized file ' + fileName + ' (' +
                      Math.round(Number(item.size) / 1048576) + 'MB > ' +
                      Math.round(MAX_BYTES / 1048576) + 'MB cap) on job ' + job.jobName +
                      ' -- move it manually or raise MAX_BYTES');
          continue;
        }

        let existingRecord = null;
        try {
          existingRecord = await syncedFiles.getEntity(jobId, item.id);
        } catch (e) { }

        if (!existingRecord) {
          context.log('New file from SharePoint: ' + fileName + ' (job: ' + job.jobName + (folderPath ? ', folder: ' + folderPath : '') + ')');
          try {
            const downloadUrl = item['@microsoft.graph.downloadUrl'] || await getDownloadUrl(token, driveId, item.id);
            const contentHash = await getQuickXorHash(item);
            const createdFile = await uploadFileToJT(jobId, fileName, downloadUrl, folderPath, context);
            let jtFileId = createdFile && createdFile.createFile && createdFile.createFile.createdFile && createdFile.createFile.createdFile.id;

            /*
             * 2026-09-25: this is where the June blank rows came from. The
             * upload succeeded, the id was not in the reply, and the row was
             * written with jtFileId '' under a "Synced" log line. Now: say so
             * loudly, then try to recover the id immediately by name+size.
             * The row is still written either way -- without it the next
             * delta would treat the file as new and upload a second copy.
             */
            if (!jtFileId) {
              context.log.error('NO JT FILE ID returned for ' + fileName + ' (job: ' + job.jobName +
                                '). createFile reply: ' + JSON.stringify(createdFile).slice(0, 500));
              const found = await resolveJTFileByNameAndSize(jobId, fileName, item.size, item.id, jtIndex, context);
              if (found.id) {
                jtFileId = found.id;
                context.log('Recovered JT file id ' + jtFileId + ' for ' + fileName + ' by name+size');
              } else {
                context.log.error('Could not recover JT file id for ' + fileName + ': ' + found.reason +
                                  '. Row written with blank jtFileId; later changes will retry the repair.');
              }
            }

            await syncedFiles.upsertEntity({
              partitionKey: jobId,
              rowKey: item.id,
              fileName,
              origin: 'sharepoint',
              jtFileId: jtFileId || '',
              spFolderPath: folderPath || '',
              contentHash: contentHash || '',
              syncedAt: new Date().toISOString()
            }, 'Replace');

            if (jtFileId) {
              await jtIndex.upsertEntity({
                partitionKey: jobId,
                rowKey: jtFileId,
                spItemId: item.id
              }, 'Replace');
            }

            context.log((jtFileId ? 'Synced new file to JT: ' : 'Uploaded to JT but UNPAIRED: ') + fileName);
          } catch (e) {
            context.log('Failed to sync new file ' + fileName + ': ' + e.message);
          }
        } else {
          const previousPath = existingRecord.spFolderPath || '';
          const previousName = existingRecord.fileName || '';
          const pathChanged = previousPath !== folderPath;
          const nameChanged = !!previousName && previousName !== fileName;

          /*
           * 2026-09-25: repair rows with no jtFileId (the June-era bug;
           * IMG_4670 on St. Rafael's is the known example) instead of skipping
           * them. The JT file is looked up on THIS job by the name the row
           * recorded at upload time (SharePoint may since have renamed it) and
           * the exact byte size. Exactly one match, not already paired with a
           * different SharePoint item, or nothing: ambiguous matches are
           * refused and logged, never guessed.
           */
          if (!existingRecord.jtFileId) {
            const lookupName = previousName || fileName;
            const found = await resolveJTFileByNameAndSize(jobId, lookupName, item.size, item.id, jtIndex, context);
            if (!found.id) {
              context.log.warn('SKIPPING ' + fileName + ' (job: ' + job.jobName + ') -- tracking row has no ' +
                               'jtFileId and repair was refused: ' + found.reason);
              continue;
            }

            /*
             * Seed the content hash if the old row never stored one. Without
             * this, the content-hash branch below sees '' !== current hash and
             * "replaces" the JT file -- a re-upload plus a delete of the
             * original -- for a file whose content never changed.
             */
            const seededHash = existingRecord.contentHash || (await getQuickXorHash(item)) || '';
            const repaired = Object.assign({}, existingRecord, {
              jtFileId: found.id,
              contentHash: seededHash,
              repairedAt: new Date().toISOString()
            });
            await syncedFiles.upsertEntity(repaired, 'Replace');
            await jtIndex.upsertEntity({
              partitionKey: jobId,
              rowKey: found.id,
              spItemId: item.id
            }, 'Replace');
            existingRecord = repaired;
            context.log('REPAIRED tracking row for ' + fileName + ' (job: ' + job.jobName + '): paired with JT file ' +
                        found.id + ' ("' + lookupName + '", ' + item.size + 'b)');
          }

          if (existingRecord.jtFileId && (pathChanged || nameChanged)) {
            const changes = [];
            const fields = {};
            if (pathChanged) {
              fields.folder = folderPath;
              changes.push('folder "' + previousPath + '" -> "' + folderPath + '"');
            }
            if (nameChanged) {
              fields.name = fileName;
              changes.push('name "' + previousName + '" -> "' + fileName + '"');
            }
            context.log('Changed in SharePoint: ' + changes.join(', ') + ' (job: ' + job.jobName + ')');
            try {
              await updateJTFile(existingRecord.jtFileId, fields, context);
              const updated = Object.assign({}, existingRecord, {
                spFolderPath: folderPath || '',
                fileName: fileName,
                syncedAt: new Date().toISOString()
              });
              await syncedFiles.upsertEntity(updated, 'Replace');
              /* Keep the local copy current: the content-hash branch below
               * builds from existingRecord, and a stale one would write the
               * old name and folder straight back. */
              existingRecord = updated;
              context.log('Applied in JobTread and verified: ' + fileName);
            } catch (e) {
              context.log('FAILED to apply change in JobTread for ' + fileName + ': ' + e.message);
              continue;
            }
          }

          if (existingRecord.jtFileId && existingRecord.origin === 'sharepoint') {
            const newHash = await getQuickXorHash(item);
            const previousHash = existingRecord.contentHash || '';

            if (newHash && newHash !== previousHash) {
              context.log('Content changed in SharePoint: ' + fileName + ', replacing JT file');
              try {
                const downloadUrl = item['@microsoft.graph.downloadUrl'] || await getDownloadUrl(token, driveId, item.id);
                const replaceResult = await replaceJTFileContent(jobId, existingRecord.jtFileId, fileName, folderPath, downloadUrl, context);
                const newJtFileId = replaceResult && replaceResult.createFile && replaceResult.createFile.createdFile && replaceResult.createFile.createdFile.id;

                await syncedFiles.upsertEntity(
                  Object.assign({}, existingRecord, {
                    contentHash: newHash,
                    fileName,
                    jtFileId: newJtFileId || existingRecord.jtFileId,
                    syncedAt: new Date().toISOString()
                  }),
                  'Replace'
                );

                if (newJtFileId) {
                  try {
                    await jtIndex.deleteEntity(jobId, existingRecord.jtFileId);
                  } catch (e) { /* may not exist */ }
                  await jtIndex.upsertEntity({
                    partitionKey: jobId,
                    rowKey: newJtFileId,
                    spItemId: item.id
                  }, 'Replace');
                }

                context.log('Updated tracking for replaced file: ' + fileName);
              } catch (e) {
                context.log('Failed to replace JT file for ' + fileName + ': ' + e.message);
              }
            }
          }
        }
      } catch (e) {
        context.log('Error processing delta item: ' + e.message);
      }
    }

    try {
      await syncState.upsertEntity({
        partitionKey: 'state', rowKey: 'pendingFolders',
        value: JSON.stringify(Array.from(new Set(outOfTime ? storedPending.concat(pendingFolders) : pendingFolders)))
      }, 'Replace');
      if (pendingFolders.length) {
        context.log('Folder expansion budget reached; ' + pendingFolders.length + ' folder(s) carried to next run');
      }
    } catch (e) {
      context.log.error('Could not save pendingFolders: ' + e.message);
    }

    if (finalDeltaLink && !outOfTime) {
      await syncState.upsertEntity({ partitionKey: 'state', rowKey: 'deltaLink', value: finalDeltaLink }, 'Replace');
      context.log('Stored new delta link');
    }

    context.log('SharePointSync (delta) complete');
  } catch (err) {
    context.log.error('SharePointSync error:', err.message);
  }
};

/*
 * Pave returns HTTP 200 for application-level failures, and httpPostRaw
 * resolves the raw string when the body will not parse. The old
 * updateJTFileFolder inspected neither, so a failed folder change logged
 * "Updated JT folder for X" exactly like a successful one.
 */
function assertJTOk(result, label) {
  if (result === null || result === undefined) {
    throw new Error(label + ': empty response from JobTread');
  }
  if (typeof result !== 'object') {
    throw new Error(label + ': non-JSON response: ' + String(result).slice(0, 300));
  }
  if (result.errors) throw new Error(label + ': ' + JSON.stringify(result.errors).slice(0, 300));
  if (result.error) throw new Error(label + ': ' + JSON.stringify(result.error).slice(0, 300));
  return result;
}

/* Read a file back from JobTread so a change can be confirmed rather than assumed. */
async function readJTFile(fileId, context) {
  const result = await httpPostRaw('https://api.jobtread.com/pave', JSON.stringify({
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      file: { $: { id: fileId }, id: {}, name: {}, folder: {} }
    }
  }), context);
  assertJTOk(result, 'readJTFile');
  return result.file || null;
}

/*
 * Added 2026-09-25. Finds the JobTread file a blank-jtFileId tracking row
 * should point at. Returns { id } on a single unambiguous match, otherwise
 * { id: null, reason }. Never guesses:
 *   - candidates are files on THIS job whose name equals `name` exactly and
 *     whose byte size equals `size` exactly
 *   - zero candidates            -> refuse
 *   - more than one candidate    -> refuse, even if all but one are already
 *                                   paired (a duplicate upload is exactly the
 *                                   case where "the unpaired one" can be wrong)
 *   - the one candidate is already paired with a DIFFERENT SharePoint item
 *                                -> refuse
 *   - the name query returns a full page (100) -> refuse; we did not see
 *     every candidate, so "exactly one" cannot be claimed
 */
async function resolveJTFileByNameAndSize(jobId, name, size, spItemId, jtIndex, context) {
  const wantSize = Number(size);
  if (!name) return { id: null, reason: 'no file name to match on' };
  if (!Number.isFinite(wantSize) || wantSize <= 0) return { id: null, reason: 'no usable size on the SharePoint item' };

  let result;
  try {
    result = await httpPostRaw('https://api.jobtread.com/pave', JSON.stringify({
      query: {
        $: { grantKey: process.env.JOBTREAD_API_KEY },
        job: {
          $: { id: jobId },
          files: {
            $: { size: 100, where: ['name', name] },
            nodes: { id: {}, name: {}, size: {} }
          }
        }
      }
    }), context);
    assertJTOk(result, 'resolveJTFileByNameAndSize');
  } catch (e) {
    return { id: null, reason: 'JobTread lookup failed: ' + e.message };
  }

  const nodes = (result.job && result.job.files && result.job.files.nodes) || [];
  if (nodes.length >= 100) {
    return { id: null, reason: '100+ JT files named "' + name + '" on this job; cannot prove a unique match' };
  }

  const candidates = nodes.filter(function (f) {
    return f.name === name && Number(f.size) === wantSize;
  });

  if (candidates.length === 0) {
    return { id: null, reason: 'no JT file on this job named "' + name + '" at ' + wantSize + 'b' +
                              (nodes.length ? ' (' + nodes.length + ' by name, none at that size)' : '') };
  }
  if (candidates.length > 1) {
    return { id: null, reason: 'AMBIGUOUS: ' + candidates.length + ' JT files named "' + name + '" at ' +
                              wantSize + 'b (' + candidates.map(function (c) { return c.id; }).join(', ') + ')' };
  }

  const match = candidates[0];
  try {
    const claimed = await jtIndex.getEntity(jobId, match.id);
    if (claimed && claimed.spItemId && claimed.spItemId !== spItemId) {
      return { id: null, reason: 'JT file ' + match.id + ' is already paired with SharePoint item ' + claimed.spItemId };
    }
  } catch (e) {
    /* not in the index -- unclaimed, which is what we want */
  }

  return { id: match.id };
}

/*
 * Applies a folder change, a name change, or both, in one mutation, then
 * reads the file back and verifies it actually took.
 *
 * Renames were previously not synced at all. That left a trap: JobTread kept
 * the old name, and a later JobTread-side edit re-uploaded under that old
 * name, producing a second copy in SharePoint.
 */
async function updateJTFile(fileId, fields, context) {
  const args = { id: fileId };
  if (Object.prototype.hasOwnProperty.call(fields, 'folder')) args.folder = fields.folder || null;
  if (Object.prototype.hasOwnProperty.call(fields, 'name')) args.name = fields.name;

  const result = await httpPostRaw('https://api.jobtread.com/pave', JSON.stringify({
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      updateFile: { $: args }
    }
  }), context);
  assertJTOk(result, 'updateJTFile');

  const after = await readJTFile(fileId, context);
  if (!after) throw new Error('updateJTFile: file ' + fileId + ' not readable after update');

  if (Object.prototype.hasOwnProperty.call(fields, 'name') && after.name !== fields.name) {
    throw new Error('updateJTFile: name did not take -- JobTread still reports "' +
                    after.name + '", expected "' + fields.name + '"');
  }
  if (Object.prototype.hasOwnProperty.call(fields, 'folder')) {
    const want = fields.folder || '';
    const got = after.folder || '';
    if (want !== got) {
      throw new Error('updateJTFile: folder did not take -- JobTread still reports "' +
                      got + '", expected "' + want + '"');
    }
  }
  return after;
}

async function replaceJTFileContent(jobId, oldFileId, fileName, folderPath, fileUrl, context) {
  const ext = fileName.split('.').pop().toLowerCase();
  const typeMap = {
    'pdf': 'application/pdf',
    'jpg': 'image/jpeg', 'jpeg': 'image/jpeg',
    'png': 'image/png', 'gif': 'image/gif',
    'doc': 'application/msword',
    'docx': 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    'xls': 'application/vnd.ms-excel',
    'xlsx': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    'mp4': 'video/mp4', 'mov': 'video/quicktime',
    'heic': 'image/heic'
  };
  const contentType = typeMap[ext] || 'application/octet-stream';

  const fileBuffer = await downloadFile(fileUrl);
  const fileSize = fileBuffer.length;

  const uploadPayload = {
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      createUploadRequest: {
        $: {
          organizationId: JT_ORG_ID,
          size: fileSize,
          type: contentType
        },
        createdUploadRequest: {
          id: {},
          url: {},
          method: {},
          headers: {}
        }
      }
    }
  };

  const uploadResult = await httpPostRaw(
    'https://api.jobtread.com/pave',
    JSON.stringify(uploadPayload),
    context
  );

  const uploadRequest = uploadResult && uploadResult.createUploadRequest && uploadResult.createUploadRequest.createdUploadRequest;
  if (!uploadRequest || !uploadRequest.id) {
    throw new Error('Failed to create upload request for content replace: ' + JSON.stringify(uploadResult));
  }

  await uploadBytesToUrl(uploadRequest.url, uploadRequest.method, uploadRequest.headers, fileBuffer, contentType, context);

  const createFileParams = {
    targetId: jobId,
    targetType: 'job',
    name: fileName,
    uploadRequestId: uploadRequest.id,
    fileTagIds: [SP_SYNC_TAG_ID]
  };
  if (folderPath) {
    createFileParams.folder = folderPath;
  }

  const filePayload = {
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      createFile: {
        $: createFileParams,
        createdFile: { id: {}, name: {} }
      }
    }
  };

  const fileResult = await httpPostRaw(
    'https://api.jobtread.com/pave',
    JSON.stringify(filePayload),
    context
  );

  try {
    await deleteJTFile(oldFileId, context);
  } catch (e) {
    context.log('Could not delete old JT file after replace: ' + e.message);
  }

  context.log('JT file content replaced: ' + JSON.stringify(fileResult));
  return fileResult;
}

async function deleteJTFile(fileId, context) {
  const payload = {
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      deleteFile: {
        $: { id: fileId }
      }
    }
  };
  const result = await httpPostRaw('https://api.jobtread.com/pave', JSON.stringify(payload), context);
  return result;
}

async function getDownloadUrl(token, driveId, itemId) {
  const data = await httpGet(
    'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + itemId,
    token
  );
  return data['@microsoft.graph.downloadUrl'];
}

async function uploadFileToJT(jobId, fileName, fileUrl, folderPath, context) {
  const ext = fileName.split('.').pop().toLowerCase();
  const typeMap = {
    'pdf': 'application/pdf',
    'jpg': 'image/jpeg', 'jpeg': 'image/jpeg',
    'png': 'image/png', 'gif': 'image/gif',
    'doc': 'application/msword',
    'docx': 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    'xls': 'application/vnd.ms-excel',
    'xlsx': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    'mp4': 'video/mp4', 'mov': 'video/quicktime',
    'heic': 'image/heic'
  };
  const contentType = typeMap[ext] || 'application/octet-stream';

  const fileBuffer = await downloadFile(fileUrl);
  const fileSize = fileBuffer.length;
  context.log('Downloaded ' + fileName + ', size: ' + fileSize + ' bytes');

  const uploadPayload = {
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      createUploadRequest: {
        $: {
          organizationId: JT_ORG_ID,
          size: fileSize,
          type: contentType
        },
        createdUploadRequest: {
          id: {},
          url: {},
          method: {},
          headers: {}
        }
      }
    }
  };

  const uploadResult = await httpPostRaw(
    'https://api.jobtread.com/pave',
    JSON.stringify(uploadPayload),
    context
  );

  const uploadRequest = uploadResult && uploadResult.createUploadRequest && uploadResult.createUploadRequest.createdUploadRequest;
  if (!uploadRequest || !uploadRequest.id) {
    throw new Error('Failed to create upload request: ' + JSON.stringify(uploadResult));
  }

  await uploadBytesToUrl(uploadRequest.url, uploadRequest.method, uploadRequest.headers, fileBuffer, contentType, context);

  const createFileParams = {
    targetId: jobId,
    targetType: 'job',
    name: fileName,
    uploadRequestId: uploadRequest.id,
    fileTagIds: [SP_SYNC_TAG_ID]
  };
  if (folderPath) {
    createFileParams.folder = folderPath;
  }

  const filePayload = {
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      createFile: {
        $: createFileParams,
        createdFile: { id: {}, name: {} }
      }
    }
  };

  const fileResult = await httpPostRaw(
    'https://api.jobtread.com/pave',
    JSON.stringify(filePayload),
    context
  );

  context.log('JT file create result: ' + JSON.stringify(fileResult));
  return fileResult;
}

/*
 * Rewritten 2026-09-09. The old version followed only 301/302, never checked
 * the status code (a 404 body resolved as a valid zero-length Buffer and got
 * uploaded to JobTread as an empty file), had no redirect limit, no timeout,
 * and no size ceiling. It now aborts the moment MAX_BYTES is exceeded, so an
 * oversized file costs one dropped request instead of the whole timer run.
 */
async function downloadFile(url, maxBytes) {
  const cap = maxBytes || MAX_BYTES;
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
        const declared = parseInt(res.headers['content-length'] || '0', 10);
        if (declared && declared > cap) {
          res.destroy();
          return reject(new Error('OVERSIZE:' + declared));
        }
        const chunks = [];
        let total = 0;
        res.on('data', function(chunk) {
          total += chunk.length;
          if (total > cap) {
            res.destroy();
            return reject(new Error('OVERSIZE:' + total));
          }
          chunks.push(chunk);
        });
        res.on('end', function() { resolve(Buffer.concat(chunks)); });
        res.on('error', reject);
      });
      req.setTimeout(120000, function() { req.destroy(new Error('Download timed out')); });
      req.on('error', reject);
    };
    go(url, 0);
  });
}

async function uploadBytesToUrl(url, method, headers, buffer, contentType, context) {
  return new Promise(function(resolve, reject) {
    const urlObj = new URL(url);
    const parsedHeaders = headers ? (typeof headers === 'string' ? JSON.parse(headers) : headers) : {};
    const options = {
      hostname: urlObj.hostname,
      path: urlObj.pathname + urlObj.search,
      method: method || 'PUT',
      headers: Object.assign({}, parsedHeaders, {
        'Content-Type': contentType,
        'Content-Length': buffer.length
      })
    };
    const req = https.request(options, function(res) {
      let data = '';
      res.on('data', function(chunk) { data += chunk; });
      res.on('end', function() {
        context.log('GCS upload status: ' + res.statusCode);
        resolve(data);
      });
    });
    req.on('error', reject);
    req.write(buffer);
    req.end();
  });
}

async function getAccessToken() {
  const tenantId = process.env.AZURE_TENANT_ID;
  const clientId = process.env.AZURE_CLIENT_ID;
  const clientSecret = process.env.AZURE_CLIENT_SECRET;
  const body = 'grant_type=client_credentials&client_id=' + clientId + '&client_secret=' + encodeURIComponent(clientSecret) + '&scope=https%3A%2F%2Fgraph.microsoft.com%2F.default';
  const data = await httpPost('https://login.microsoftonline.com/' + tenantId + '/oauth2/v2.0/token', body, {
    'Content-Type': 'application/x-www-form-urlencoded'
  });
  return data.access_token;
}

async function getSiteId(token) {
  const data = await httpGet(
    'https://graph.microsoft.com/v1.0/sites/purplepainting.sharepoint.com:/sites/purplepainting',
    token
  );
  return data.id;
}

async function getDriveId(token, siteId) {
  const data = await httpGet(
    'https://graph.microsoft.com/v1.0/sites/' + siteId + '/drives',
    token
  );
  const drive = data.value.find(function(d) { return d.name === 'Documents'; }) || data.value[0];
  return drive.id;
}

function httpPostRaw(url, body, context) {
  return new Promise(function(resolve, reject) {
    const urlObj = new URL(url);
    const options = {
      hostname: urlObj.hostname,
      path: urlObj.pathname,
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(body)
      }
    };
    const req = https.request(options, function(res) {
      let data = '';
      res.on('data', function(chunk) { data += chunk; });
      res.on('end', function() {
        try { resolve(JSON.parse(data)); } catch(e) { resolve(data); }
      });
    });
    req.on('error', reject);
    req.write(body);
    req.end();
  });
}

function httpGet(url, token) {
  return new Promise(function(resolve, reject) {
    https.get(url, { headers: { 'Authorization': 'Bearer ' + token } }, function(res) {
      let data = '';
      res.on('data', function(chunk) { data += chunk; });
      res.on('end', function() {
        try { resolve(JSON.parse(data)); } catch(e) { reject(new Error('Failed to parse response: ' + data)); }
      });
    }).on('error', reject);
  });
}

function httpPost(url, body, headers) {
  return new Promise(function(resolve, reject) {
    const urlObj = new URL(url);
    const options = {
      hostname: urlObj.hostname,
      path: urlObj.pathname + urlObj.search,
      method: 'POST',
      headers: Object.assign({}, headers, { 'Content-Length': Buffer.byteLength(body) })
    };
    const req = https.request(options, function(res) {
      let data = '';
      res.on('data', function(chunk) { data += chunk; });
      res.on('end', function() { try { resolve(JSON.parse(data)); } catch(e) { resolve(data); } });
    });
    req.on('error', reject);
    req.write(body);
    req.end();
  });
}