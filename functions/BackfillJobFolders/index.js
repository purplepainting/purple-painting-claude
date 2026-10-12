/*
 * BackfillJobFolders  --  HTTP-triggered reconciliation pass.
 *
 * Repairs the JobFolders table, which is the mapping BOTH sync directions
 * depend on:
 *   - JobTreadWebhook (fileCreated/fileUpdated/fileDeleted) bails out with
 *     "No folder mapping" when a job has no row.
 *   - SharePointSync builds jobFolderMap from this table; a folder with no
 *     row is invisible to resolveJobAndPath() and every file in it is skipped.
 *
 * For every job it will:
 *   1. Skip if a healthy JobFolders row + JT link already exist. "Healthy"
 *      now means the row's folder still EXISTS (2026-09-29, see below).
 *   2. ADOPT an existing SharePoint folder if one matches by normalized name
 *      (handles hand-made folders and slight naming drift like
 *      "125 Evans Ave - Hammond" vs "125 Evans Ave- Hammond").
 *   3. CREATE the folder only if no match is found -- in the stage folder the
 *      job's JobTread stage calls for (2026-09-29, see below).
 *   4. Write the JobFolders row.
 *   5. Backfill the JT custom field + pinned comment if missing, or if the
 *      old ones pointed at the dead folder.
 *
 * 2026-09-29 CHANGES
 *   - STALE ROWS. A row pointing at a deleted folder was counted healthy and
 *     skipped forever (1509 Shoreline Dr: row -> 01A5DCPRYW6KPUGJ47FZHYVJGZEXMAPICD,
 *     gone; 132 JT files never reached SharePoint). Every row's folder is now
 *     verified. A stale row is treated like a missing one and goes through
 *     adopt/create, and the JT link is replaced because it points at the
 *     dead folder too. Reported under "staleRepaired".
 *   - CREATE IN THE RIGHT STAGE. The create branch used Bids - Active for
 *     every job, so repairing a won or complete job misfiled it. It now uses
 *     the same decideStage() rules as JobTreadWebhook (all three stage
 *     fields). Jobs with no stage at all still go to Bids - Active.
 *
 * DRY RUN BY DEFAULT. Nothing is written unless ?apply=true.
 *
 *   GET /api/BackfillJobFolders                  -> report only
 *   GET /api/BackfillJobFolders?apply=true       -> perform repairs
 *   GET /api/BackfillJobFolders?limit=10&apply=true   -> repair first 10 only
 *   GET /api/BackfillJobFolders?jobId=22Pd8YxSus4t&apply=true  -> single job
 */

const https = require('https');
const { TableClient } = require('@azure/data-tables');

const JT_ORG_ID = '22PWNY9u7qZd';
const ONEDRIVE_FOLDER_LINK_FIELD = '22PYNFyRMxqa';

const JOB_WALK_BID_STAGE_FIELD = '22PWRx62raQg';  // Job Walk Bid Stage (autofilled)
const DIGITAL_BID_STAGE_FIELD = '22PWNYKhXZxr';   // Digital Takeoff Bid Stage (autofilled)
const PRODUCTION_STAGE_FIELD = '22PWsEy5ZSXL';    // Production Stage (autofilled)

/*
 * STAGE FOLDER RESOLUTION -- rewritten 2026-09-23 after the SharePoint
 * reorganisation.
 *
 * The old code assumed one root ('Deal Documents & Projects') holding all four
 * stage folders and resolved them by path. That root was renamed to
 * '01 Projects', the stage folders were split across two parents, and
 * 'Projects - Closed Lost' became 'Bids - Closed Lost'. Every lookup has
 * returned 404 since 2026-09-09, so this function has thrown on every run.
 *
 * Resolution order, most durable first:
 *   1. App Setting (SP_FOLDER_ID_DEAL etc) if set and still valid.
 *   2. The known item id below, if still valid.
 *   3. Name lookup under that stage's own parent.
 * Item IDs survive renames AND moves, so 1 and 2 cannot be broken by another
 * reorganisation. 3 exists only so a deleted-and-recreated folder resolves.
 *
 * Paths and ids verified live against the drive on 2026-09-23.
 * 2026-09-28: stage folders renamed to Bids - Active / Bids - Closed Lost /
 * Projects - Active / Projects - Complete (ids unchanged). App Settings
 * SP_FOLDER_ID_* are now set, so resolution goes through step 1.
 */
const STAGE_FOLDERS = {
  deal: {
    parent: '02 Estimates', name: 'Bids - Active',
    env: 'SP_FOLDER_ID_DEAL', id: '01A5DCPR5FRR55PDS5SJDIWGNS3GSL5JDK'
  },
  won: {
    parent: '01 Projects', name: 'Projects - Active',
    env: 'SP_FOLDER_ID_WON', id: '01A5DCPR5X7BUYUBZMEVGYKZ4OFSNL4BNK'
  },
  lost: {
    parent: '02 Estimates', name: 'Bids - Closed Lost',
    env: 'SP_FOLDER_ID_LOST', id: '01A5DCPR7SLSLBDLPTZJF2KWM62AQX4UBQ'
  },
  complete: {
    parent: '01 Projects', name: 'Projects - Complete',
    env: 'SP_FOLDER_ID_COMPLETE', id: '01A5DCPRYT3JXYJBRCMFAZCCWSQWQHBVKS'
  }
};

// Memoised for the life of the warm process. Safe: item ids survive renames
// and moves. Only a delete-and-recreate could stale it -- restart the app.
const _stageIdCache = new Map();

// ---------------------------------------------------------------- stage rules
// KEEP IN STEP WITH JobTreadWebhook.js -- same functions, same order.

function getTargetFolder(status) {
  if (!status) return null;
  const s = String(status).toLowerCase();

  if (s.includes('closed/won') || s.includes('gc awarded') || s.includes('deposit paid')) return 'won';
  if (s.includes('closed/lost') || s.includes('dormant')) return 'lost';
  /* DEAL stages before 'completed': "2) Completed: Job Walk" is a bid stage. */
  if (s.includes('requested')  || s.includes('bid invite') || s.includes('job walk') ||
      s.includes('takeoff')    || s.includes('estimate')   || s.includes('committed') ||
      s.includes('qualified')) return 'deal';

  if (s.includes('completed')) return 'complete';

  return null;
}

function decideStage(jobWalkBid, digitalBid, production) {
  const p = String(production || '').toLowerCase().trim();
  if (p.includes('completed') || p.includes('closed')) return 'complete';
  const bids = [getTargetFolder(jobWalkBid), getTargetFolder(digitalBid)];
  if (bids.includes('won')) return 'won';
  if (bids.includes('lost')) return 'lost';
  if (p) return 'won';
  return bids[0] || bids[1] || null;
}

// ---------------------------------------------------------------- helpers

function getStorageClient(tableName) {
  return TableClient.fromConnectionString(
    process.env.STORAGE_CONNECTION_STRING,
    tableName,
    { allowInsecureConnection: false }
  );
}

async function ensureTable(tableName) {
  const client = getStorageClient(tableName);
  try { await client.createTable(); } catch (e) { /* already exists */ }
  return client;
}

/*
 * Fixed version of the sanitizer. The original called .trim(), which strips
 * whitespace but NOT a trailing period -- and SharePoint rejects any item
 * name ending in a period. That is what killed "26 Celine Dr.",
 * "1308 Bath St." and "1147 Coast Village Rd.".
 */
function sanitizeFolderName(name) {
  return String(name || '')
    .replace(/[:\\/*?'"<>|#%]/g, '-')
    .replace(/^[.\s]+/, '')
    .replace(/[.\s]+$/, '')
    .trim();
}

/*
 * Loose key for matching an existing folder to a job. Case-insensitive,
 * punctuation-insensitive, whitespace-collapsed -- so a folder someone
 * created by hand still gets adopted rather than duplicated.
 */
function normalizeForMatch(name) {
  return String(name || '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, ' ')
    .trim();
}

// ---------------------------------------------------------------- http

function httpGet(url, token) {
  return new Promise(function (resolve, reject) {
    const req = https.get(url, { headers: { Authorization: 'Bearer ' + token } }, function (res) {
      let data = '';
      res.on('data', function (c) { data += c; });
      res.on('end', function () {
        if (res.statusCode < 200 || res.statusCode >= 300) {
          return reject(new Error('GET ' + url + ' -> ' + res.statusCode + ': ' + data.slice(0, 400)));
        }
        try { resolve(JSON.parse(data)); }
        catch (e) { reject(new Error('GET ' + url + ' bad JSON: ' + data.slice(0, 400))); }
      });
    });
    req.setTimeout(30000, function () { req.destroy(new Error('GET timed out: ' + url)); });
    req.on('error', reject);
  });
}

function httpPost(url, body, headers) {
  return new Promise(function (resolve, reject) {
    const u = new URL(url);
    const options = {
      hostname: u.hostname,
      path: u.pathname + u.search,
      method: 'POST',
      headers: Object.assign({}, headers, { 'Content-Length': Buffer.byteLength(body) })
    };
    const req = https.request(options, function (res) {
      let data = '';
      res.on('data', function (c) { data += c; });
      res.on('end', function () {
        let parsed = null;
        try { parsed = JSON.parse(data); } catch (e) { parsed = data; }
        if (res.statusCode < 200 || res.statusCode >= 300) {
          return reject(new Error('POST ' + url + ' -> ' + res.statusCode + ': ' + String(data).slice(0, 400)));
        }
        resolve(parsed);
      });
    });
    req.setTimeout(30000, function () { req.destroy(new Error('POST timed out: ' + url)); });
    req.on('error', reject);
    req.write(body);
    req.end();
  });
}

function jtQuery(payload, context) {
  return new Promise(function (resolve, reject) {
    const body = JSON.stringify(payload);
    const options = {
      hostname: 'api.jobtread.com',
      path: '/pave',
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) }
    };
    const req = https.request(options, function (res) {
      let data = '';
      res.on('data', function (c) { data += c; });
      res.on('end', function () {
        try { resolve(JSON.parse(data)); }
        catch (e) { reject(new Error('JT bad JSON (' + res.statusCode + '): ' + data.slice(0, 400))); }
      });
    });
    req.setTimeout(30000, function () { req.destroy(new Error('JT request timed out')); });
    req.on('error', reject);
    req.write(body);
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
    body,
    { 'Content-Type': 'application/x-www-form-urlencoded' }
  );
  if (!data || !data.access_token) {
    throw new Error('Token request returned no access_token: ' + JSON.stringify(data));
  }
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
  const data = await httpGet('https://graph.microsoft.com/v1.0/sites/' + siteId + '/drives', token);
  const drive = (data.value || []).find(function (d) { return d.name === 'Documents'; }) || data.value[0];
  return drive.id;
}

/* Confirm an id still points at a live folder. A stale id is worse than none
 * -- it silently writes into the wrong place. */
async function verifyFolderId(token, driveId, itemId) {
  try {
    const item = await httpGet(
      'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + itemId +
      '?$select=id,name,folder,deleted', token
    );
    return item && item.id && item.folder && !item.deleted ? item : null;
  } catch (e) { return null; }
}

/* Page through a named parent under the drive root and find one child. */
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
 * Resolve one stage folder to an item id: App Setting, then the known id,
 * then a name lookup under that stage's own parent.
 */
async function getStageFolderId(token, driveId, stageKey, context) {
  const stage = STAGE_FOLDERS[stageKey];
  if (!stage) throw new Error('Unknown stage key "' + stageKey + '"');

  const cacheKey = driveId + '|' + stageKey;
  if (_stageIdCache.has(cacheKey)) return _stageIdCache.get(cacheKey);

  const candidates = [];
  if (process.env[stage.env]) candidates.push({ id: process.env[stage.env], from: stage.env });
  if (stage.id) candidates.push({ id: stage.id, from: 'built-in id' });

  for (const c of candidates) {
    const item = await verifyFolderId(token, driveId, c.id);
    if (item) {
      context.log('Stage "' + stageKey + '" -> ' + item.name + ' (via ' + c.from + ')');
      _stageIdCache.set(cacheKey, c.id);
      return c.id;
    }
    context.log('Stage "' + stageKey + '": ' + c.from + ' no longer resolves, trying next');
  }

  const found = await findChildByName(token, driveId, stage.parent, stage.name);
  if (!found) {
    throw new Error(
      'Stage folder "' + stage.name + '" not found under "' + stage.parent + '". ' +
      'The SharePoint layout changed again -- update STAGE_FOLDERS, or set ' +
      stage.env + ' to the folder id in App Settings.'
    );
  }
  context.log('Stage "' + stageKey + '" -> resolved by name at ' +
              stage.parent + '/' + stage.name + ' (id ' + found.id + ')');
  _stageIdCache.set(cacheKey, found.id);
  return found.id;
}

// Pages through every child. The original code used a single unpaged
// /children call, which silently truncates at 200 items -- Deal Documents
// already holds far more than that.
async function listAllChildren(token, driveId, parentId) {
  const out = [];
  let url = 'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + parentId +
            '/children?$top=200&$select=id,name,folder,webUrl';
  while (url) {
    const page = await httpGet(url, token);
    for (const item of (page.value || [])) out.push(item);
    url = page['@odata.nextLink'] || null;
  }
  return out;
}

async function createFolder(token, driveId, parentId, folderName) {
  const body = JSON.stringify({
    name: folderName,
    folder: {},
    '@microsoft.graph.conflictBehavior': 'fail'
  });
  const result = await httpPost(
    'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + parentId + '/children',
    body,
    { 'Content-Type': 'application/json', Authorization: 'Bearer ' + token }
  );
  if (!result || !result.id) {
    throw new Error('createFolder returned no id for "' + folderName + '": ' + JSON.stringify(result));
  }
  return result;
}

async function getShareLink(token, driveId, itemId, context) {
  for (let i = 0; i < 3; i++) {
    try {
      const data = await httpPost(
        'https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + itemId + '/createLink',
        JSON.stringify({ type: 'edit', scope: 'organization' }),
        { 'Content-Type': 'application/json', Authorization: 'Bearer ' + token }
      );
      if (data && data.link && data.link.webUrl) return data.link.webUrl;
    } catch (e) {
      context.log('  createLink attempt ' + (i + 1) + ' failed: ' + e.message);
    }
    if (i < 2) await new Promise(function (r) { setTimeout(r, 2000); });
  }
  // Fallback: the item's own webUrl needs no sharing API at all.
  const item = await httpGet('https://graph.microsoft.com/v1.0/drives/' + driveId + '/items/' + itemId, token);
  if (!item || !item.webUrl) throw new Error('No share link and no webUrl for item ' + itemId);
  context.log('  fell back to direct webUrl');
  return item.webUrl;
}

// ---------------------------------------------------------------- jobtread

async function fetchAllJobs(context) {
  const jobs = [];
  let page = null;
  let guard = 0;

  const wanted = [ONEDRIVE_FOLDER_LINK_FIELD, JOB_WALK_BID_STAGE_FIELD,
                  DIGITAL_BID_STAGE_FIELD, PRODUCTION_STAGE_FIELD];

  while (guard++ < 100) {
    const args = { size: 100, sortBy: [{ field: 'createdAt', order: 'desc' }] };
    if (page) args.page = page;

    const payload = {
      query: {
        $: { grantKey: process.env.JOBTREAD_API_KEY },
        organization: {
          $: { id: JT_ORG_ID },
          jobs: {
            $: args,
            nextPage: {},
            nodes: {
              id: {},
              name: {},
              number: {},
              createdAt: {},
              closedOn: {},
              customFieldValues: {
                $: { size: 10, where: [['customField', 'id'], 'in', wanted] },
                nodes: { value: {}, customField: { id: {} } }
              }
            }
          }
        }
      }
    };

    const res = await jtQuery(payload, context);
    const conn = res && res.organization && res.organization.jobs;
    if (!conn) throw new Error('Unexpected JT response: ' + JSON.stringify(res).slice(0, 400));

    for (const node of (conn.nodes || [])) {
      const cfv = (node.customFieldValues && node.customFieldValues.nodes) || [];
      const get = function (id) {
        const n = cfv.find(function (x) { return x.customField && x.customField.id === id; });
        return n ? n.value : null;
      };
      jobs.push({
        id: node.id,
        name: node.name || ('Job ' + node.number),
        createdAt: node.createdAt,
        closedOn: node.closedOn,
        link: get(ONEDRIVE_FOLDER_LINK_FIELD) || null,
        stage: decideStage(get(JOB_WALK_BID_STAGE_FIELD), get(DIGITAL_BID_STAGE_FIELD),
                           get(PRODUCTION_STAGE_FIELD))
      });
    }

    page = conn.nextPage;
    if (!page) break;
  }

  return jobs;
}

async function setJobLink(jobId, shareLink, context) {
  return jtQuery({
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      updateJob: {
        $: { id: jobId, customFieldValues: { [ONEDRIVE_FOLDER_LINK_FIELD]: shareLink } }
      }
    }
  }, context);
}

async function postPinnedComment(jobId, shareLink, context) {
  return jtQuery({
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      createComment: {
        $: {
          targetId: jobId,
          targetType: 'job',
          message: 'OneDrive Folder: ' + shareLink,
          isPinned: true
        }
      }
    }
  }, context);
}

async function jobHasFolderComment(jobId, context) {
  const res = await jtQuery({
    query: {
      $: { grantKey: process.env.JOBTREAD_API_KEY },
      job: {
        $: { id: jobId },
        comments: { $: { size: 25 }, nodes: { message: {} } }
      }
    }
  }, context);
  const nodes = (res && res.job && res.job.comments && res.job.comments.nodes) || [];
  return nodes.some(function (c) {
    return typeof c.message === 'string' && c.message.indexOf('OneDrive Folder:') !== -1;
  });
}

// ---------------------------------------------------------------- main

module.exports = async function (context, req) {
  const apply = String((req.query && req.query.apply) || '') === 'true';
  const onlyJobId = (req.query && req.query.jobId) || null;
  const limit = parseInt((req.query && req.query.limit) || '0', 10) || 0;

  const report = {
    mode: apply ? 'APPLY' : 'DRY RUN',
    scanned: 0,
    healthy: 0,
    adopted: [],
    created: [],
    rowWrittenOnly: [],
    staleRepaired: [],
    linkBackfilled: [],
    failed: [],
    startedAt: new Date().toISOString()
  };

  try {
    context.log('BackfillJobFolders starting -- ' + report.mode);

    const token = await getAccessToken();
    const siteId = await getSiteId(token);
    const driveId = await getDriveId(token, siteId);
    const jobFolders = await ensureTable('JobFolders');

    // Existing rows, keyed by JT job id.
    const existingRows = new Map();
    for await (const entity of jobFolders.listEntities()) {
      existingRows.set(entity.rowKey, entity);
    }
    context.log('JobFolders currently holds ' + existingRows.size + ' rows');

    // Index every folder under all four stage parents, by normalized name,
    // so an existing folder gets adopted instead of duplicated. Also keep a
    // set of every live folder id, so a row's folder can be verified cheaply.
    const stageParentIds = {};
    const folderIndex = new Map();
    const liveFolderIds = new Set();
    for (const key of Object.keys(STAGE_FOLDERS)) {
      const stageName = STAGE_FOLDERS[key].name;
      try {
        const parentId = await getStageFolderId(token, driveId, key, context);
        stageParentIds[key] = parentId;
        const children = await listAllChildren(token, driveId, parentId);
        let count = 0;
        for (const child of children) {
          if (!child.folder) continue;
          liveFolderIds.add(child.id);
          const norm = normalizeForMatch(child.name);
          if (!folderIndex.has(norm)) {
            folderIndex.set(norm, { id: child.id, name: child.name, stage: stageName });
            count++;
          }
        }
        context.log('Indexed ' + count + ' folders under "' + stageName + '"');
      } catch (e) {
        context.log('Could not index "' + stageName + '": ' + e.message);
      }
    }

    if (!stageParentIds.deal) {
      throw new Error('Cannot resolve the "Bids - Active" parent -- aborting rather than guessing');
    }

    let jobs = await fetchAllJobs(context);
    context.log('Fetched ' + jobs.length + ' jobs from JobTread');

    if (onlyJobId) jobs = jobs.filter(function (j) { return j.id === onlyJobId; });

    let touched = 0;

    for (const job of jobs) {
      report.scanned++;

      const row = existingRows.get(job.id);
      let hasRow = !!(row && row.spFolderDriveId);
      const hasLink = !!job.link;

      /*
       * STALE ROW CHECK. The row's folder is usually directly under a stage
       * folder (fast path: liveFolderIds). If not, ask Graph -- it may have
       * been moved somewhere legitimate. Only a folder Graph cannot find is
       * stale.
       */
      let stale = false;
      if (hasRow && !liveFolderIds.has(row.spFolderDriveId)) {
        const live = await verifyFolderId(token, row.driveId || driveId, row.spFolderDriveId);
        if (!live) {
          stale = true;
          hasRow = false;
          context.log('STALE row for ' + job.name + ' (' + job.id + '): folder ' +
                      row.spFolderDriveId + ' no longer exists');
        }
      }

      if (hasRow && hasLink) { report.healthy++; continue; }

      if (limit && touched >= limit) continue;
      touched++;

      const label = job.name + ' (' + job.id + ')';
      context.log('Repairing ' + label + ' -- row:' + hasRow + ' link:' + hasLink + (stale ? ' STALE' : ''));

      try {
        let folderId = hasRow ? row.spFolderDriveId : null;
        let action = 'row-only';
        const targetStage = job.stage || 'deal';

        if (!folderId) {
          const match = folderIndex.get(normalizeForMatch(job.name));
          if (match) {
            folderId = match.id;
            action = 'adopt';
            context.log('  adopting existing folder "' + match.name + '" in ' + match.stage);
          } else {
            const safeName = sanitizeFolderName(job.name);
            if (!safeName) throw new Error('Job name sanitizes to empty string');
            if (!stageParentIds[targetStage]) {
              throw new Error('Cannot resolve stage folder "' + targetStage + '" -- not creating');
            }
            action = 'create';
            context.log('  no match -- will create "' + safeName + '" in ' + STAGE_FOLDERS[targetStage].name);
            if (apply) {
              const created = await createFolder(token, driveId, stageParentIds[targetStage], safeName);
              folderId = created.id;
              folderIndex.set(normalizeForMatch(safeName), {
                id: created.id, name: safeName, stage: STAGE_FOLDERS[targetStage].name
              });
            }
          }
        }

        if (!apply) {
          const entry = { job: job.name, jobId: job.id, action: action };
          if (action === 'create') entry.stageFolder = STAGE_FOLDERS[targetStage].name;
          if (stale) {
            entry.staleFolderId = row.spFolderDriveId;
            report.staleRepaired.push(entry);
          } else if (action === 'adopt') report.adopted.push(entry);
          else if (action === 'create') report.created.push(entry);
          else report.rowWrittenOnly.push(entry);
          continue;
        }

        // A stale row's link points at the dead folder too -- always replace it.
        const needNewLink = !job.link || stale;
        const shareLink = needNewLink ? await getShareLink(token, driveId, folderId, context) : job.link;

        await jobFolders.upsertEntity({
          partitionKey: 'jobs',
          rowKey: job.id,
          jobName: sanitizeFolderName(job.name),
          spFolderDriveId: folderId,
          spFolderUrl: shareLink,
          driveId: driveId
        }, 'Replace');
        context.log('  wrote JobFolders row');

        if (needNewLink) {
          await setJobLink(job.id, shareLink, context);
          report.linkBackfilled.push({ job: job.name, jobId: job.id });
          context.log('  set custom field');

          // A stale job's existing pinned comment has the dead link, so post a fresh one.
          if (stale || !(await jobHasFolderComment(job.id, context))) {
            await postPinnedComment(job.id, shareLink, context);
            context.log('  posted pinned comment');
          }
        }

        const entry = { job: job.name, jobId: job.id, folderId: folderId, action: action };
        if (stale) {
          entry.staleFolderId = row.spFolderDriveId;
          report.staleRepaired.push(entry);
        } else if (action === 'adopt') report.adopted.push(entry);
        else if (action === 'create') report.created.push(entry);
        else report.rowWrittenOnly.push(entry);

      } catch (e) {
        context.log.error('  FAILED ' + label + ': ' + (e.stack || e.message));
        report.failed.push({ job: job.name, jobId: job.id, error: e.message });
      }
    }

    report.finishedAt = new Date().toISOString();
    report.summary =
      report.scanned + ' scanned, ' + report.healthy + ' already healthy, ' +
      report.staleRepaired.length + ' stale rows, ' +
      report.adopted.length + ' adopted, ' + report.created.length + ' created, ' +
      report.rowWrittenOnly.length + ' row-only, ' +
      report.linkBackfilled.length + ' links set, ' + report.failed.length + ' failed';

    context.log(report.summary);
    context.res = { status: 200, headers: { 'Content-Type': 'application/json' }, body: report };

  } catch (err) {
    const detail = (err && (err.stack || err.message)) || String(err);
    context.log.error('BackfillJobFolders aborted: ' + detail);
    report.fatal = detail;
    context.res = { status: 500, headers: { 'Content-Type': 'application/json' }, body: report };
  }
};