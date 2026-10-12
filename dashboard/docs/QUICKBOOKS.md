# QuickBooks connection

The dashboard reads QuickBooks Online (Accounting API) for Financials. It never writes to QuickBooks.
One shared connection: someone connects it once on **Connections** (link at the bottom of the menu).

## One-time setup (Intuit developer app)

1. Go to https://developer.intuit.com and sign in with the QuickBooks admin login.
2. Dashboard > **Create an app** > **QuickBooks Online and Payments**. Name it "Purple Dashboard". Scope: **Accounting**.
3. In the app, open **Keys & credentials** > **Production** (Intuit asks a few questions about the app first: it's an internal app for our own company).
4. Under **Redirect URIs** add exactly: `https://dashboard.purplepainting.net/quickbooks/callback`
5. Copy the **Client ID** and **Client Secret** into Azure: PurpleDashboard > Settings > Environment variables > App settings:
   - `QBO_CLIENT_ID`
   - `QBO_CLIENT_SECRET`
6. Apply and restart, then on the dashboard open **Connections** > **Connect QuickBooks** and pick the Purple Painting company.

Optional settings: `QBO_REDIRECT_URI` (if the site address changes), `QBO_ENVIRONMENT=sandbox` (Intuit test company),
`QBO_EXTRA_SCOPES` (extra scopes, space separated, e.g. payroll scopes once Intuit approves them).

## How it works

- `Integrations/QuickBooks/QuickBooksClient.cs`: OAuth (connect, callback, refresh), `QueryAsync` (QuickBooks query language)
  and `ReportAsync` (e.g. `ProfitAndLoss`). Access tokens (1 hour) are kept in memory; the refresh token (100 days,
  replaced on every refresh) is saved encrypted in the `QuickBooksConnections` table. If nobody uses QuickBooks for
  100 days, reconnect on Connections.
- Encryption uses ASP.NET Data Protection; on Azure App Service the keys are kept in `/home/ASP.NET/DataProtection-Keys`
  (persistent). If the keys are ever lost, the page says to reconnect.

## Pay rates and pay stubs

Intuit only gives payroll data (compensation, pay stubs) to apps it approves. To ask: open a ticket with Intuit
developer support for the app above, requesting the payroll compensation / payslip read scopes for our own company.
If approved, add the scopes to `QBO_EXTRA_SCOPES`, reconnect, and the dashboard can fill QB base pay on Employees from
the latest pay stub (not built until access is granted).

# Per-person JobTread keys

Schedule pushes are sent with the pushing person's own JobTread grant key (Connections > Your JobTread account), so
JobTread records the change as them. Keys are checked with JobTread (must be the Purple Painting organization) and
saved encrypted in `JobTreadUserKeys`, per Entra user. Reading pages still uses the shared `JOBTREAD_API_KEY`.
