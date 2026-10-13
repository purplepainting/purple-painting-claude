// Public terms and privacy page for the Purple Dashboard (Intuit asks for a privacy policy and EULA link
// when registering the dashboard's QuickBooks app). Static page, no data. URL: /api/dashboard-terms
const page = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Purple Dashboard: Privacy Policy and Terms</title>
<style>
  body { font: 16px/1.6 -apple-system, "Segoe UI", Roboto, sans-serif; color: #1f1a26; background: #faf8fc; margin: 0; }
  main { max-width: 720px; margin: 0 auto; padding: 32px 16px 48px; }
  h1 { font-size: 26px; color: #4c1d95; margin: 0 0 4px; }
  h2 { font-size: 17px; margin: 24px 0 4px; }
  p { margin: 0 0 10px; }
  .muted { color: #6b6475; font-size: 14px; }
</style></head>
<body><main>
<h1>Purple Dashboard: Privacy Policy and Terms</h1>
<p class="muted">Purple Painting · Last updated October 13, 2026</p>
<p>Purple Dashboard is an internal tool used only by Purple Painting staff. It is not offered to the public or to other businesses.</p>
<h2>What it accesses</h2>
<p>With an authorized Purple Painting administrator's permission, the dashboard reads Purple Painting's own data from QuickBooks Online (accounts, reports, invoices, bills and related records), JobTread, Google Ads and Google Business Profile (ad results and reviews), and Meta / Facebook and Instagram ads (ad results for Purple Painting's own ad account). It uses that data only to show reports to Purple Painting staff. The dashboard does not change data in QuickBooks, Google or Meta, and it does not collect data from members of the public.</p>
<h2>How data is kept</h2>
<p>Connection tokens are stored encrypted in Purple Painting's private Microsoft Azure account. Access to the dashboard requires a Purple Painting Microsoft work account. Data is not sold, shared with third parties, or used for advertising.</p>
<h2>Disconnecting</h2>
<p>An administrator can disconnect QuickBooks, Google or Facebook at any time from the dashboard's Connections page, or from the service itself (QuickBooks: Settings &gt; Apps; Google: your Google Account's third-party access; Facebook: Settings &gt; Business integrations). Disconnecting stops all access.</p>
<h2>Data deletion</h2>
<p>To have data the dashboard keeps from a connected service deleted, disconnect it on the Connections page (stored connection tokens are deleted) or email kareem@purplepainting.net and it will be deleted within 30 days.</p>
<h2>Terms of use</h2>
<p>The dashboard is provided for Purple Painting's internal business use only, as is, without warranty. Only authorized Purple Painting staff may use it.</p>
<h2>Contact</h2>
<p>kareem@purplepainting.net</p>
</main></body></html>`;

module.exports = async function (context) {
  context.res = {
    status: 200,
    headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "public, max-age=3600" },
    body: page,
  };
};
