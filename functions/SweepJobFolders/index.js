/*
 * SweepJobFolders -- timer-triggered self-healing pass.
 *
 * WHY
 * jobCreated webhooks will fail occasionally no matter how fast the code is.
 * On 2026-09-23 the MOXI Museum job returned HTTP 499 ("A task was canceled")
 * after 8.7 seconds -- JobTread hung up before the function finished. The
 * folder never got made, and nothing noticed until someone went looking.
 *
 * Rather than chase each cause (timeouts, Graph throttling, transient 500s),
 * this reconciles state on a schedule. Any job missing a folder, a JobFolders
 * row, or a OneDrive link gets repaired within the hour.
 *
 * It calls BackfillJobFolders directly rather than reimplementing it, so
 * there is exactly one copy of the repair logic. That function is idempotent:
 * healthy jobs are skipped, existing folders are adopted rather than
 * duplicated, and existing links are left alone.
 *
 * Schedule lives in function.json. Default is hourly on the hour.
 */

const backfill = require('../BackfillJobFolders/index.js');

module.exports = async function (context, myTimer) {
  const started = Date.now();

  if (myTimer && myTimer.isPastDue) {
    context.log('SweepJobFolders is running late -- the host was likely asleep');
  }

  context.log('SweepJobFolders starting');

  /*
   * BackfillJobFolders is an HTTP handler: (context, req). It reads
   * req.query and writes its report to context.res. A minimal req object is
   * all it needs, and context.res is simply ignored on a timer trigger.
   */
  const fakeReq = { query: { apply: 'true' }, body: null };

  try {
    await backfill(context, fakeReq);

    const report = context.res && context.res.body;

    if (!report) {
      context.log.error('SweepJobFolders: backfill returned no report');
      return;
    }

    if (report.fatal) {
      context.log.error('SweepJobFolders: backfill aborted -- ' + report.fatal);
      return;
    }

    const repaired =
      (report.adopted ? report.adopted.length : 0) +
      (report.created ? report.created.length : 0) +
      (report.rowWrittenOnly ? report.rowWrittenOnly.length : 0);

    const failed = report.failed ? report.failed.length : 0;

    // Quiet when there is nothing to say; loud when there is.
    if (repaired === 0 && failed === 0) {
      context.log('SweepJobFolders: nothing to repair (' +
                  report.scanned + ' scanned, ' + report.healthy + ' healthy) in ' +
                  (Date.now() - started) + 'ms');
      return;
    }

    context.log.warn('SweepJobFolders REPAIRED ' + repaired + ' job(s): ' + report.summary);

    for (const j of (report.created || [])) {
      context.log.warn('  created folder for "' + j.job + '" (' + j.jobId +
                       ') -- its jobCreated webhook did not complete');
    }
    for (const j of (report.adopted || [])) {
      context.log.warn('  adopted existing folder for "' + j.job + '" (' + j.jobId + ')');
    }
    for (const j of (report.rowWrittenOnly || [])) {
      context.log.warn('  wrote missing JobFolders row for "' + j.job + '" (' + j.jobId + ')');
    }
    for (const j of (report.failed || [])) {
      context.log.error('  FAILED to repair "' + j.job + '" (' + j.jobId + '): ' + j.error);
    }

  } catch (err) {
    const detail = (err && (err.stack || err.message)) || String(err);
    context.log.error('SweepJobFolders threw: ' + detail);
  } finally {
    // Never leave an HTTP response object hanging around on a timer context.
    context.res = undefined;
  }
};