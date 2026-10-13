# Purple Painting Dashboard

Internal web app at https://dashboard.purplepainting.net. What to build and why is in
[SPEC.md](SPEC.md). This file covers how the code is laid out and how to change it.

## Run it locally

Needs the .NET 10 SDK.

```
cd dashboard
dotnet run
```

Locally there's no Entra sign-in in front of the app, so you're shown as "Local Developer".

To see real JobTread data locally, set the grant key first: `dotnet user-secrets set JOBTREAD_API_KEY <key>`
(never put it in a file in the repo). Or point at saved sample responses with
`JobTread__FixtureDirectory=/path/to/folder dotnet run`, where the folder has one `{query name}.json`
per query (`financials-months`, `projects-list`, `projects-activity`, `projects-detail`, `schedule-week`, `people-staff`, `labor-entries`, `projects-closed`, `sales-leads`, and for pushes `schedule-job-tasks`, `schedule-current`, `schedule-update`, `schedule-create`, `schedule-delete`).

**QuickBooks locally:** `QuickBooks__FixtureDirectory=/path` serves saved reports (`ProfitAndLoss.json`, `query-{Entity}.json`) instead of calling Intuit.

**Google locally:** `Google__FixtureDirectory=/path` serves saved Google Ads / Business Profile responses (file list in `docs/GOOGLE.md`), and Connect Google connects straight away as the sample account (needs the local database below).
**Meta (Facebook/Instagram ads) locally:** `Meta__FixtureDirectory=/path` serves saved Graph responses (`adaccounts.json`, `insights-account.json`, `insights-campaign.json`, `insights-platform.json`) instead of calling Meta. See `docs/META.md`.

**Database locally:** without a connection string the app runs with no database (Employees is read only).
For a local SQLite file: `ConnectionStrings__Dashboard="Data Source=/tmp/dashboard.db" dotnet run`. It's created on first run.

## Layout

| Path | What it is |
|---|---|
| `Program.cs` | App startup: services, routes, `/health` |
| `Modules/ModuleRegistry.cs` | **The list of modules.** The left menu comes from here, in order; the first one is the home page |
| `Auth/CurrentUser.cs` | The signed-in user, from Easy Auth headers. Inject `CurrentUser` anywhere |
| `Pages/ComingSoon.cshtml` | Placeholder page for any module that isn't built yet |
| `Pages/Shared/_Layout.cshtml` | Left menu, top bar and page frame used by every page |
| `Data/DashboardDb.cs` | The dashboard database (EF Core): tables and the `DatabaseStatus` pages check before saving |
| `Data/DatabaseSetup.cs` | Picks Azure SQL or SQLite from the connection string; applies migrations at startup |
| `Data/Migrations/` | Schema history. Add one with `dotnet ef migrations add <Name> -o Data/Migrations` |
| `Integrations/QuickBooks/` | QuickBooks Online connection (read only). Setup: `docs/QUICKBOOKS.md` |
| `Integrations/Google/` | Google connection (read only): one company Google account for Google Ads (`googleAds:searchStream`) and Business Profile reviews. Setup: `docs/GOOGLE.md` |
| `Modules/Sales/GoogleAdsService.cs` | Google Ads by month and campaign for Sales & Marketing; `SalesService.MergeApiSpend` puts it (and Meta, via `MetaAdsService.SpendSource`) in Ads → revenue without double counting. Jobs are attributed by the customer's JobTread Lead Source; `SalesService.ChannelFor` maps Lead Source values to spend channels, `JobTreadLeadsService` counts new accounts by Lead Source |
| `Modules/Customers/GoogleReviewsService.cs`, `ReviewMatcher.cs` | Google reviews for Customers, matched to customers by name (display only) |
| `Integrations/Meta/` | Facebook/Instagram ads via the Meta Marketing API (read only; API version constant in `MetaOptions`). Setup: `docs/META.md`. Connections card: `ViewComponents/MetaConnectionViewComponent.cs`; Sales section: `Modules/Sales/MetaAdsService.cs`, `Pages/Sales/_MetaAds.cshtml` |
| `Modules/Financials/` | Financials: QuickBooks report parsing, account rules (which account counts as wages, burden, job cost, overhead), JobTread vs QuickBooks by month |
| `Pages/Connections/` | Connections page: JobTread status, your own JobTread key (for pushes), connect QuickBooks, connect Google (pick the Ads account and Business Profile location) |
| `Modules/Changes/` | Change log page data: `ChangeLogReader` (filters, paging), `ChangeFormatter` (one summary formatter per area; add one when a new area starts logging), `PersonNames` (email → name) |
| `Modules/People/TeamDirectory.cs` | Everyone's crew level and PM color (Employees page, then settings, then JobTread role) |
| `ViewComponents/SideNavViewComponent.cs` | Builds the left menu tiles and their numbers |
| `Pages/Shared/_Icon.cshtml` | Line icons used on menu tiles |
| `wwwroot/css/site.css` | All styles. Colors, fonts and sizes are CSS variables at the top |
| `wwwroot/js/site.js` | Small optional scripts (auto-submit filters, load expanded rows) |
| `Integrations/JobTread/` | The JobTread client. Every JobTread call goes through `IJobTreadClient` |
| `Modules/Projects/` | Projects data: models, preset views, `ProjectsService` (JobTread queries) |
| `Modules/Scheduling/` | Schedule: `ScheduleService` (read tasks), `SchedulePushService` (write changes to JobTread), `LaborCostService` (actual and estimated labor), job colors and crew levels |
| `Modules/People/` | `PeopleService` (staff and roles from JobTread), PM colors |
| `Modules/Hiring/` | Hiring (Employees → Hiring): `HiringService` (candidates, interview days, bookings, files), `TextTemplates` (English/Spanish texts), `InterviewSheet` (sheet answers), `HiringLists` (stages, positions, tools, checklists, SharePoint links) |
| `Pages/Employees/Interviews/` | Hiring pages: candidates and days (`Index`), one day (`Day`), one candidate (`Candidate`), interview sheet (`Sheet`) |
| `wwwroot/js/hiring.js` | Hiring: copy buttons, checkboxes that save in place |
| `Modules/Search/` | `SearchService`: global search across the module services' cached data. Page and JSON at `Pages/Search/` (`/search?q=`, `/search?handler=suggest&q=`), top bar box in `_Layout` + `wwwroot/js/search.js` |
| `wwwroot/js/schedule-editor.js` | The schedule editor (draft, drag/tap, review & push) |
| `Infrastructure/` | Shared helpers: `LocalClock` (Pacific time), `Format` (money, %, dates) |
| `docs/JOBTREAD.md` | How our JobTread data is structured and how to query it |

## Common changes

**Rename, reorder or re-describe a menu tile:** edit its line in `Modules/ModuleRegistry.cs`.

**Add a new module:** add a line to `ModuleRegistry.All`. It shows up in the left menu
straight away with a "Coming soon" page.

**Build out a module** (Projects as the example):
1. Create `Pages/Projects/Index.cshtml` starting with `@page "/projects"` (the module's key).
   A literal route like this takes priority over the Coming soon page.
2. In the page, set `ViewData["ModuleName"] = "Projects";` for the top bar title.
3. In `ModuleRegistry.cs`, set the module's status to `ModuleStatus.Live`.

**Add a menu tile key number:** implement `ITileMetric` for the module and register it in `Program.cs`
(see `ActiveProjectsMetric`).

**Add or change a Projects view pill:** edit `Modules/Projects/ProjectViews.cs`.

**Change the profit % colors:** edit the stops in `Modules/Projects/ProfitScale.cs`.

**Crew levels and PM colors:** set per person on the Employees page. `Crew:Foremen` / `Crew:Helpers` and `ProjectManagers:Colors` in `appsettings.json` are only the defaults for anyone not set there. Pages get both from `TeamDirectory.GetAsync()` (`team.LevelOf(name)`, `team.PmStyle(name)`).

**Add a table:** add the class and `DbSet` in `Data/DashboardDb.cs`, then `dotnet ef migrations add <Name> -o Data/Migrations` and commit the migration. It's applied on the next deploy. Check `DatabaseStatus.Available` before writing.

**Change colors or fonts:** edit the variables in `:root` at the top of `wwwroot/css/site.css`.

**Phones:** every screen must work on iPhone and Android. Follow the phone checklist in SPEC.md section 5; the shared styles already handle most of it.

**Shared UI pieces** already styled in `site.css`: `.card`, `.chip` (`.warn`, `.good`, `.active`),
`.button` (`.secondary`), and `.table-wrap` + `table.data` for tables that scroll on phones.

## Live updates from JobTread

The function app's `JobTreadWebhook` writes every JobTread event to a `DashboardEvents` table; the `DashboardChanges`
function returns new ones. The dashboard (`Integrations/JobTread/JobTreadChangeWatcher.cs`) polls it every 20 seconds
and drops only the cached data a change touches (`Apply` there maps event kinds to areas; `Infrastructure/CacheSignals.cs`).
Setting: `JOBTREAD_CHANGES_URL` = the DashboardChanges function URL with its `?code=` key.

How long JobTread data is kept (`Infrastructure/JobTreadFreshness.cs`): 30 minutes per area while the feed is answering
**and** every event kind that area depends on has come through in the last 7 days (projects: job, task, timeEntry,
document; schedule: task, job; labor: timeEntry). Otherwise 2 minutes, so an event type the webhook isn't subscribed to
never leaves stale data. Without the setting, everything is 2 minutes. `/health` shows `jobTreadCache`: the event kinds
seen and the current minutes per area. QuickBooks data isn't signalled and keeps its own 10-minute cache.

## Settings and secrets

Never commit secrets. Settings go in Azure: PurpleDashboard > Settings > Environment variables,
and are read with `builder.Configuration`. List any new setting names in the PR description.

The database connection string is under PurpleDashboard > Settings > Environment variables > **Connection strings**, name `Dashboard`, type SQLAzure.

## Deploy

Merging to `main` with changes under `dashboard/` deploys automatically
(`.github/workflows/dashboard-deploy.yml`). Always work on a branch and open a PR.
