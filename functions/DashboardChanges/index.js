/*
 * DashboardChanges: JobTread events newer than ?since=<epoch ms>, newest first (max 200), for the dashboard
 * to refresh its caches. Written by JobTreadWebhook (see recordDashboardEvent). Function key required.
 * Rows older than 7 days are cleared out occasionally.
 * ?seen=1 instead returns { seen: { eventType: latest epoch ms } } for the last 7 days, so the dashboard
 * knows which kinds of changes JobTread is actually sending (and how long it can safely cache each area).
 */
const { TableClient, odata } = require('@azure/data-tables');

const TABLE = 'DashboardEvents';
const REVERSE_TICKS = 9999999999999;

function key(ms) { return String(REVERSE_TICKS - ms).padStart(13, '0'); }

module.exports = async function (context, req) {
  const since = Number(req.query.since) || Date.now() - 10 * 60 * 1000;
  const table = TableClient.fromConnectionString(process.env.STORAGE_CONNECTION_STRING, TABLE);

  if (req.query.seen) {
    const seen = {};
    try {
      const rows = table.listEntities({ queryOptions: { filter: odata`PartitionKey eq 'e' and RowKey lt ${key(Date.now() - 7 * 24 * 3600 * 1000)}`, select: ['type', 'at'] } });
      for await (const r of rows) {
        const at = Number(r.at);
        if (r.type && !(seen[r.type] >= at)) seen[r.type] = at;
      }
    } catch (err) {
      if (err.statusCode !== 404) context.log.warn('DashboardChanges seen read failed: ' + (err && err.message));
    }
    context.res = { status: 200, headers: { 'Content-Type': 'application/json' }, body: { now: Date.now(), seen } };
    return;
  }
  const events = [];
  try {
    // Newer events have smaller row keys.
    const rows = table.listEntities({ queryOptions: { filter: odata`PartitionKey eq 'e' and RowKey lt ${key(since)}` } });
    for await (const r of rows) {
      events.push({ type: r.type, jobId: r.jobId || null, at: Number(r.at) });
      if (events.length >= 200) break;
    }

    if (Math.random() < 0.05) {
      const old = table.listEntities({ queryOptions: { filter: odata`PartitionKey eq 'e' and RowKey gt ${key(Date.now() - 7 * 24 * 3600 * 1000)}` } });
      let n = 0;
      for await (const r of old) { await table.deleteEntity(r.partitionKey, r.rowKey); if (++n >= 200) break; }
    }
  } catch (err) {
    if (err.statusCode !== 404) context.log.warn('DashboardChanges read failed: ' + (err && err.message));
  }
  context.res = { status: 200, headers: { 'Content-Type': 'application/json' }, body: { now: Date.now(), events } };
};
