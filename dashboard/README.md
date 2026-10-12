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
per query (`projects-list`, `projects-activity`, `projects-detail`, `schedule-week`).

## Layout

| Path | What it is |
|---|---|
| `Program.cs` | App startup: services, routes, `/health` |
| `Modules/ModuleRegistry.cs` | **The list of modules.** The left menu comes from here, in order; the first one is the home page |
| `Auth/CurrentUser.cs` | The signed-in user, from Easy Auth headers. Inject `CurrentUser` anywhere |
| `Pages/ComingSoon.cshtml` | Placeholder page for any module that isn't built yet |
| `Pages/Shared/_Layout.cshtml` | Left menu, top bar and page frame used by every page |
| `ViewComponents/SideNavViewComponent.cs` | Builds the left menu tiles and their numbers |
| `Pages/Shared/_Icon.cshtml` | Line icons used on menu tiles |
| `wwwroot/css/site.css` | All styles. Colors, fonts and sizes are CSS variables at the top |
| `wwwroot/js/site.js` | Small optional scripts (auto-submit filters, load expanded rows) |
| `Integrations/JobTread/` | The JobTread client. Every JobTread call goes through `IJobTreadClient` |
| `Modules/Projects/` | Projects data: models, preset views, `ProjectsService` (JobTread queries) |
| `Modules/Scheduling/` | Schedule data: `ScheduleService` (JobTread tasks), job type colors |
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

**Change colors or fonts:** edit the variables in `:root` at the top of `wwwroot/css/site.css`.

**Phones:** every screen must work on iPhone and Android. Follow the phone checklist in SPEC.md section 5; the shared styles already handle most of it.

**Shared UI pieces** already styled in `site.css`: `.card`, `.chip` (`.warn`, `.good`, `.active`),
`.button` (`.secondary`), and `.table-wrap` + `table.data` for tables that scroll on phones.

## Settings and secrets

Never commit secrets. Settings go in Azure: PurpleDashboard > Settings > Environment variables,
and are read with `builder.Configuration`. List any new setting names in the PR description.

## Deploy

Merging to `main` with changes under `dashboard/` deploys automatically
(`.github/workflows/dashboard-deploy.yml`). Always work on a branch and open a PR.
