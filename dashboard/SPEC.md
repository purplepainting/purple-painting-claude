# Purple Painting Dashboard: Build Spec

Source of truth for building the internal dashboard. Read this before working in `/dashboard`.
For how the code is laid out and how to add a module, see `dashboard/README.md`.

## 1. What this is

An internal web app for Purple Painting's team. It brings JobTread, QuickBooks Online and SharePoint together in one place. It **sits on top of** those systems and does not replace them:

- **JobTread** stays the system of record for jobs, schedules and job costing.
- **QuickBooks Online** stays the source of truth for company financials, payroll, bills and A/R.
- **SharePoint** (company team site) stays the document store. The dashboard links out to files. It never copies them, and it never uses anyone's personal OneDrive.

The dashboard is also **its own system of record** for data that exists in neither JobTread nor QuickBooks (commissions, bonuses, door-to-door activity, equipment). That data lives in the dashboard's own database.

The app is the first shell to build on and will change a lot. Keep it easy to modify: one place to register modules, shared styles and components, small focused files.

## 2. Current infrastructure (already live, don't recreate)

| Item | Value |
|---|---|
| Repo | `purplepainting/purple-painting-claude` (monorepo) |
| Dashboard code | `/dashboard` (.NET 10, Razor Pages) |
| Deploy | `.github/workflows/dashboard-deploy.yml`. Runs on merge to `main` when `dashboard/**` changes. Secret: `AZURE_WEBAPP_PUBLISH_PROFILE` |
| Web app | Azure App Service `PurpleDashboard`, Linux, B1, West US 3, resource group `JobTread-Integration`. Startup command: `dotnet Dashboard.dll` (set in Configuration, not in the repo) |
| URL | https://dashboard.purplepainting.net (managed cert, SNI SSL) |
| Auth | App Service Authentication (Easy Auth) with Entra ID app registration "Purple Dashboard Login", single-tenant, require authentication. The app receives only signed-in users. The current user is available from the `X-MS-CLIENT-PRINCIPAL-NAME` / `X-MS-CLIENT-PRINCIPAL` request headers and from `/.auth/me`. |
| Function app | `jobtread` (Node 22, Y1, Windows, Canada Central) in `/functions`, deployed by `functions-deploy.yml`. Already receives JobTread webhooks (`JobTreadWebhook`) and has a `JobTreadProxy`. |

## 3. Working rules

- **Never push to `main`.** A merge to `main` deploys to production. Always work on a branch and open a PR for Kareem to merge.
- **No secrets in the repo.** API keys, client secrets and connection strings go in Azure App Service → Environment variables (or Key Vault) and are read from configuration at runtime. List any new setting names in the PR description so Kareem can add them.
- `dotnet build` must pass before opening a PR.
- Before writing to JobTread or QuickBooks from the dashboard, ask first. Phase 1 is **read-only** for both. See 4a for how write-back works once approved.
- Keep each PR to one feature or screen. Smaller PRs are easier to review.

## 4. Architecture decisions (agreed)

- **Azure only.** No AWS.
- **Event-driven updates.** JobTread and QuickBooks webhooks push changes, instead of polling. Webhooks are received by the **function app**, which writes into the dashboard database. The dashboard reads from its database, so pages stay fast and API rate limits are respected.
- **Own database** for (a) cached copies of JobTread and QuickBooks data that the dashboard displays, and (b) dashboard-only data (commissions, bonuses, sales activity, equipment, saved views).
- **Main screen = launchpad of tiles.** There's no single summary page.
- **Each module opens on its main list**, with filters on top. **Saved filter views** (like JobTread's) are used only where they make sense: Projects and Vendors for now.
- **No Documents tile.** Show a link to the relevant SharePoint folder next to a job or customer.

### 4a. Editing and write-back (agreed direction, built feature by feature)

Kareem wants to make changes in the dashboard and have them land in JobTread and/or QuickBooks. **Scheduling is the first and most important case** (see module 2).

- **Data owned by JobTread or QuickBooks:** an edit in the dashboard is sent to that system's API (JobTread Pave, QuickBooks Online API). The dashboard's cached copy is updated from the result, and again by the webhook that follows. The dashboard never keeps its own diverging version of JobTread/QuickBooks data.
- **Data that exists only in the dashboard:** saved straight to the dashboard database.
- **Data that lives in both** (e.g. a customer's phone number): the owning system wins. Writes go to the owner first; on failure, nothing is changed locally and the user sees the error.
- All JobTread and QuickBooks calls go through one integration client per system (read and write methods in one place), so write-back can be added per field without touching pages much.
- Every write-back is recorded in an audit table (who, what, when, old value, new value, result).
- Each write-back feature is proposed and approved before it's built (see rule in section 3).

### Stack (confirmed in the first dashboard PR)
- ASP.NET Core (.NET 10) with **Razor Pages** for server-rendered screens, plus small vanilla JS where interactivity is needed. No SPA framework.
- **Azure SQL Database** (the free serverless offer is enough to start) with **EF Core** and migrations. A separate DB from anything the functions use today, unless sharing is clearly better.
- Function app and dashboard share the DB connection string via environment variables.

## 5. Visual design (from the approved mockups)

Mockups: https://claude.ai/artifact/DyfCdU6XShw45GyDAZyeJ9 (Kareem can export or screenshot them). Match this look:

| Token | Value |
|---|---|
| Header / nav background | `#2E1248` (deep purple), white text, `#D9CCE8` secondary text |
| Primary accent | `#6B2FA3` (buttons, active chips, key numbers) |
| Dark accent / links | `#4A1F73` |
| Page background | `#F7F6FA` |
| Card | white, 1px border `#E3DDEB`, 14px radius |
| Text | `#1C1726`; muted `#5E566B` |
| Warning / over budget / owed | `#9A3412` text, `#FDEBD8` chip background |
| Good / within range | `#1E5631` text, `#E6F2EA` chip background |
| Fonts | Plus Jakarta Sans (UI), IBM Plex Mono (numbers and money) |

These tokens live as CSS variables at the top of `dashboard/wwwroot/css/site.css`. Change them there, not in individual pages.

Patterns: a sticky top bar with a "← Dashboard" back link and the module name; pill-shaped saved-view buttons; tables inside white cards that scroll horizontally on small screens; status chips; minimum touch target of 44px. Must work on a phone (crews and salespeople will use it in the field).

Phone checklist (iPhone Safari and Android Chrome), already handled in the shell; keep it true for every new screen:
- Layout fits at 360px wide with no sideways page scroll. Wide tables scroll inside their card (`.table-wrap`).
- Touch targets at least 44px. Hover effects only on devices that have hover.
- Form fields at least 16px text so iPhone doesn't zoom on tap.
- Content stays clear of the notch and home bar (safe-area insets).
- Installable to the home screen (manifest + icons); opens full-screen with the purple status bar.
- Test new screens at phone width before opening a PR.

## 6. Modules, in build order

### Launchpad (home)
- Greeting with the signed-in user's name, and a search box (global search comes later; it can be non-functional at first).
- 8 tiles in this order. Each tile shows its name, a one-line description, its data source and one key number:
  1. Projects · 2. Scheduling · 3. Financials · 4. Customers · 5. Sales and marketing · 6. Vendors and payables · 7. Employees · 8. Equipment and sundries

### 1. Projects (JobTread)
- Default view: **all active jobs**.
- Saved views as pills (e.g. All active, Starting this week, Over budget, Public works) plus "+ Save current view". Saved views are stored per user in the dashboard DB.
- Filters: status, job type, project manager, text search (name/address).
- Table columns: job, customer, stage (chip), dates, contract amount, budget used (bar + %, warning color over 100%), link to SharePoint folder.
- **Expandable job rows.** Each job is a row (a card on phones) that expands in place when tapped; tap again to collapse. It must work by touch.
  - **Collapsed row (the bar you tap)** also shows:
    - a **% complete ring** (a small donut like QuickBooks Projects), with the number in the middle. **% complete = costs to date ÷ budgeted cost** (capped at 100% on the ring; over 100% shows the warning color).
    - **billed %** (billed to date ÷ contract) in small text next to the ring. If billed % trails % complete by more than 10 points, flag it ("Under-billed: send a pay app").
    - **profit %** = (billed to date − costs to date) ÷ billed to date, using JobTread billings and costs. Green at or above the target margin, warning color below the floor or negative, no color in between. Starting values: **target 35%, floor 25%**, stored as settings (later per job type, since public works runs lower) so they can be changed without a code change.
  - **Expanded panel:**
    - **Costs by cost code:** one line per JobTread cost code on the job with budget, actual cost to date, remaining, and % of budget used (bar, warning color over 100%).
    - **List of all cost codes on the job**, including codes with budget but no costs yet.
    - Links to the job in JobTread and its SharePoint folder.
  - Data: the job's budget, cost items and cost codes from JobTread, plus its billings and actual costs. Load the expanded detail when the row opens so the list stays fast.

### 2. Scheduling (JobTread)
- Week view by default (Day / Week / Month toggle), with prev/next/today.
- Rows = crews (plus an "Unassigned" row); job blocks span the days they cover.
- Color-coded by job type (interior / exterior / commercial-public works) using colors that differ in lightness, not hue alone.
- Built from JobTread job dates and crew assignments.
- **First write-back feature (Kareem's top priority for editing):** build and change the schedule here (drag a job to another crew or day, set start/end, assign crews), then push it to JobTread. JobTread stays the system of record; the dashboard is the editing surface. Plan it as:
  1. Read-only schedule view from JobTread data.
  2. Draft edits in the dashboard (stored in the dashboard DB as a pending schedule), so a week can be built before anything changes in JobTread.
  3. "Push to JobTread" sends the changes through the JobTread integration client, shows what will change before sending, and records each change in the audit table.
  - How crews and schedule items are represented in JobTread (job dates, tasks, assignees) must be confirmed with Kareem before step 2.

### 3. Financials (hybrid; large module with tabs)
Tabs: **Company · Job P&L · Reconciliation · Bonuses & distributions · Commissions**
- **Company:** P&L, balance sheet and cash position from QuickBooks. KPI cards for revenue YTD, net profit YTD, cash on hand and open A/R.
- **Job P&L:** per-job profit from JobTread, with burden rates applied.
- **Reconciliation (key feature):** month-by-month, job-by-job comparison of JobTread job profit against QuickBooks profit. **QuickBooks is the source of truth.** Columns: job, revenue, JobTread profit, QuickBooks profit, gap, flag. A persistent gap means burden rates in JobTread need adjusting. Include a month total row with a plain-English verdict (e.g. "JobTread running ~4% optimistic").
- **Bonuses & distributions:** dashboard-owned data. Depends on Employees.
- **Commissions:** dashboard-owned. Records who sold each job, commission rules, and payouts.

### 4. Customers (JobTread + QuickBooks + reviews)
- List on the left (search; filters: All / Owes money / Review to request), detail on the right (stacks on mobile).
- Detail: contact info, lifetime revenue, outstanding balance (QuickBooks), the customer's jobs (JobTread), links to JobTread and SharePoint.
- **Reviews:** dashboard-owned review status per job (requested / received + platform + rating / ask later), with a "Request review" button. The button only records the request in Phase 1.

### 5. Sales and marketing
- **Ads → revenue:** spend per channel (Google Ads, Facebook/Instagram, referrals/signs), leads, jobs won and revenue, with return on spend. Ad platform integration comes later; Phase 1 can use manually entered monthly spend.
- **Door-to-door and reps:** dashboard-owned. A "Log activity" form (rep, date, doors knocked, leads, notes), totals for doors, leads and sold, and a rep table (doors, leads, sold, sold $).
- **Open issue:** the attribution method (which ad produced which lead/job) is undecided. Don't invent one. Leave a clear placeholder.

### 6. Vendors and payables (QuickBooks)
- Bills, due dates and vendor balances. Saved views (e.g. Due in 14 days, Overdue).

### 7. Employees (QuickBooks payroll + JobTread assignments)
- One profile per person.
- **Open issue:** identity matching ("Jose in QuickBooks" = "Jose in JobTread"). Propose a mapping table in the dashboard DB with a simple admin screen to link records manually. Don't attempt fuzzy auto-matching without approval.

### 8. Equipment and sundries (dashboard-owned)
- Fully custom: items (sprayers, ladders, vehicles, supplies), status, location or assigned crew, low/out-of-stock flags.

## 7. Phased plan

**Phase 1: Foundation + Projects (first PRs)**
1. App shell: layout, top bar, design tokens, fonts, launchpad with all 8 tiles. Tiles for unbuilt modules show "Coming soon". Show the signed-in user's name from Easy Auth headers.
2. Database: Azure SQL + EF Core + first migration (users and saved views). List the env var names Kareem needs to add.
3. JobTread data path: decide with Kareem between (a) reading live through the existing `JobTreadProxy` function, or (b) having the function app sync jobs into the DB on webhook events. (b) is the agreed long-term direction; (a) is acceptable as a first step.
4. Projects screen with filters and saved views.

**Phase 2:** Scheduling, then Customers (needs QuickBooks access: an Intuit developer app with OAuth2 and token storage).
**Phase 3:** Financials (Company → Job P&L → Reconciliation → Commissions/Bonuses).
**Phase 4:** Sales and marketing, Vendors, Employees, Equipment.
**Later:** a Claude assistant panel via the Anthropic API that answers questions from dashboard data. Not in scope yet.

## 8. Open questions to raise with Kareem (don't guess)
- Marketing attribution method.
- Employee identity matching approach.
- Commission and bonus rules (amounts, triggers, who is eligible).
- Which job types and crews exist in JobTread, and how crews are represented there.
- QuickBooks API access: Intuit developer app setup and where OAuth tokens are stored.
- Projects: confirm the starting margin target (35%) and floor (25%) against real job history, and whether public works needs its own numbers.
- Beyond scheduling (the first write-back), which fields should be editable from the dashboard, and in which system (JobTread or QuickBooks) each edit lands.
