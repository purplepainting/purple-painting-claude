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
- **Navigation = a left menu of module tiles** (icon, name, key number), always visible on desktop and behind a menu button on phones. **The home page is the Projects list.** (Changed from the original launchpad-of-tiles design at Kareem's request.)
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

Patterns: a left menu of module tiles and a sticky top bar with the module name (menu button on phones); pill-shaped saved-view buttons; tables inside white cards that scroll horizontally on small screens; status chips; minimum touch target of 44px. Must work on a phone (crews and salespeople will use it in the field).

Phone checklist (iPhone Safari and Android Chrome), already handled in the shell; keep it true for every new screen:
- Layout fits at 360px wide with no sideways page scroll. Wide tables scroll inside their card (`.table-wrap`).
- Touch targets at least 44px. Hover effects only on devices that have hover.
- Form fields at least 16px text so iPhone doesn't zoom on tap.
- Content stays clear of the notch and home bar (safe-area insets).
- Installable to the home screen (manifest + icons); opens full-screen with the purple status bar.
- Test new screens at phone width before opening a PR.

## 6. Modules, in build order

### Left menu and home page
- 8 module tiles in this order, each with its name, a short line (key-number label, or "Coming soon") and its key number:
  1. Projects · 2. Scheduling · 3. Financials · 4. Customers · 5. Sales & Marketing · 6. Vendors & Payables · 7. Employees · 8. Equipment & Sundries
- The signed-in user's name at the bottom of the menu.
- "/" opens the Projects list. Global search comes later.

### 1. Projects (JobTread)
- Default view: **all active jobs** = Pre-Construction, Scheduled, In Progress, On Hold, **plus Completed jobs that still have money to bill or collect** (left to bill, unpaid invoices, or retention held). A completed job leaves the active list once it's fully billed and paid. Sorted **most active first**: nearest clock-in or scheduled day to today. Jobs in Pre-Construction / Color Submittals haven't started, so they sort to the bottom. Each row shows that activity ("Today", "Next Wed 10/14", "Last time Oct 6").
- Saved views as pills (e.g. All active, Starting this week, Over budget, Public works) plus "+ Save current view". Saved views are stored per user in the dashboard DB.
- Filters: status, job type, project manager, text search (name/address).
- **The JobTread budget is not used** (Purple Painting doesn't maintain it).
- **Main line (collapsed) stays basic:** billed ring (billed ÷ contract); `#number Name` with the client and **PM** and the total contract under it; **costs / billed** on the right; then the **profit % pill**. Job type (Residential, Custom Home, Commercial, Public Works, Property Mgmt) is in the dropdown. A small orange dot marks money waiting on us (unsent change orders, or a finished job not fully billed/paid).
- **Profit % = (billed − costs) ÷ billed**, where **billed includes retention held** (earned, collected at the end). Wherever billed includes retention it shows in **orange**, with the retention amount on hover and in Job details. "At full contract" (contract − costs) shows in Job details for reference. Pill color is one purple scale: **darker purple = more profit (40%+ deepest), lighter purple toward 10%, then pink to red at 0%, solid red when negative.** Stops live in `Modules/Projects/ProfitScale.cs`.
- **Dropdown (expanded)** opens on **Costs** (costs by cost code). Buttons switch to **Crew** and **Job details**; links to JobTread and SharePoint sit beside them. Flags show above the buttons. Job details holds everything else: flags, job type, address, stage, PM, activity, scheduled dates; contract (approved · pending), billed (incl. retention held), left to bill, unpaid, costs, profit $ (billed − costs, plus at full contract), cash (invoiced − costs); links to JobTread and SharePoint; then costs by cost code.
- **Change orders:** contract = approved orders + pending change orders (usually agreed, e.g. T&M). Unsent change orders (drafts named "Change Order") get a flag. On bids, a pending order is the proposal ("Proposal pending").
- View pills include "Costs ahead of billing", "Left to bill" and "Done, money owed". Rows show **Unpaid** (invoice balances) and completed jobs with money outstanding get a "Done, $X to bill or collect" chip.
- **Crew** (in the dropdown, loaded when the row opens): everyone who clocked time on the job, with hours and last day worked, most hours first. **Foreman = the highest-paid field person on the job** (JobTread has no foreman field), ties broken by hours; supervisors (any internal role other than Field Employee: admins, PMs, office, sales) are listed separately and never picked. Pay rates are used only for this and never shown. Salesperson and PM show in the dropdown details.
- **Costs by cost code** (bottom of the dropdown, loaded when the row opens): labor, bills, total, share of the job's costs, and hours per JobTread cost code, including codes on the estimate with no costs yet (grayed out).

### 2. Scheduling (JobTread)
- Week view by default (Day / Week / Month toggle), with prev/next/today. Day, Week and Month are built.
- **Built (month view, read only):** Monday–Sunday grid of the weeks overlapping the month, prev/next month, same Crew/Management toggle. Each day lists its jobs as chips in the job's color (most people first, up to 4 then "+N more"); a job opens its timeline, a date opens the Day view. On phones each day shows a colored dot per job, and tapping a day lists its jobs under the grid. No labor costs or editing.
- JobTread has no crew object (see docs/JOBTREAD.md): each job day is a task with painters assigned. So the week grid has two row modes: **by job** (default; each cell shows the day's task and who's on it) and **by person** (each cell shows the job they're on), plus an "Unassigned" row. On phones the week shows as a day-by-day list with times, people and a map link.
- Color-coded by job type (interior / exterior / commercial-public works) using colors that differ in lightness, not hue alone.
- Built from JobTread job dates and crew assignments.
- **Built (week view):** each job on screen gets a different color (hues spread evenly around the wheel, greens left out because crew chips are green, for the number of jobs that week, alternating pastel and mid tones when there are more than 4; day view uses the week's colors); crew chips are colored by level on one green scale (Foreman dark green, Painter medium green, Helper light green); management chips are purple by JobTread role (Admin dark, Office medium, Sales light), PMs in their own PM color, anyone else gray and listed foreman first; PM tags are colored per PM and sit under the job type; job numbers are not shown. Each job/day shows labor cost and hours (actual from time entries up to today, "est." after: crew × scheduled hours × burdened rate) and each job shows a week total of both. By person shows the same per painter per day, with a week total per person (visible to everyone with dashboard access for now; can be limited by role once Employees exists). Past days show **what actually happened**: who clocked in with hours, scheduled people who didn't clock in crossed out, unscheduled people dashed. Day view groups tasks into one card per job, most people first. **Crew and Supervisor schedules are separate** (toggle): a task with only supervisors on it (PM job starts, QC, material runs) is on the Supervisors schedule; tasks with painters (or unassigned) are on Crew.
- **Schedule editor (built):** "Edit schedule" turns the week into a draft: drag a box to another day (dates shift, the date in the task name updates), drag or tap a painter to move just them (or to "Not scheduled" to take them off), "+ Add day" offers a new day (copy of the job's nearest day) or one of the job's unfinished tasks already in JobTread from 2 weeks back to 4 months ahead, moved onto that day (a normal update, so the conflict check and linked tasks apply), "+ New task", "+ Add job" (active jobs not on the schedule), tap a box to edit name/dates/times/crew. A day added in the draft can be removed before pushing. **Delete** tries a real delete in JobTread; if JobTread's key isn't allowed to delete, it falls back to renaming the task "DELETE – …", moving it to that week's Sunday and clearing its crew (no notification), for someone to delete in JobTread. Marked tasks show crossed out in red. The push result says which happened. A bar counts pending changes; "Review & push" lists every change (before → after, linked tasks noted) and sends them to JobTread. Each change is checked against JobTread first and skipped if someone changed it there since loading. JobTread moves dependent tasks and notifies the crew (its defaults). Every push is logged with who and what. The draft survives a reload (this browser only). Everyone with dashboard access can push, once they sign in with JobTread on Connections (or paste their own key); pushes go to JobTread as them, so JobTread shows who made the change.
- **Built: job timeline** (`/scheduling/job/{id}`, tap a job name on the schedule): every task on the job by phase (JobTread group tasks), bars on a date axis with a today line, colored done / in progress / upcoming / past its dates and not marked done, crew chips by level, linked-task count; dates, tasks done and crew-days at the top. Read only for now; dragging on the timeline (same draft-then-push) is a later step.
- **First write-back feature (Kareem's top priority for editing):** build and change the schedule here (drag a job to another crew or day, set start/end, assign crews), then push it to JobTread. JobTread stays the system of record; the dashboard is the editing surface. Plan it as:
  1. Read-only schedule view from JobTread data.
  2. Draft edits in the dashboard (stored in the dashboard DB as a pending schedule), so a week can be built before anything changes in JobTread.
  3. "Push to JobTread" sends the changes through the JobTread integration client, shows what will change before sending, and records each change in the audit table.
  - How crews and schedule items are represented in JobTread (job dates, tasks, assignees) must be confirmed with Kareem before step 2.

### 3. Financials (hybrid; large module with tabs)
**Built (first pass, Oct 2026):** `/financials` with three tabs, for any period (this month, last month, last 3 months, since JobTread (May 2026), YTD, last year; accrual or cash):
- **JobTread vs QuickBooks:** revenue (JT invoices, net of retention, vs QB income), field labor (JT time entries at burdened rates vs QB field wages + burden), job costs (JT vendor bills vs QB materials/jobsite; job bills are entered in JobTread and pushed to QuickBooks, so these should match), gross profit, cost per hour; by month; and **"Where things are off"**: month gaps in job costs or revenue, field payroll booked under Expenses, labor burden under Expenses, no employer payroll tax account, uncategorized, deleted accounts with activity, unnumbered accounts.
- **Labor burden:** QB field wages by account + burden items (workers' comp 5510, sick & holiday 5180) = real field labor cost; per JobTread clocked hour and % of wages; vs JobTread's average burdened rate; gap per hour by month. QuickBooks has no employer payroll tax or benefits accounts (likely inside wages); noted on the page.
- **Overhead:** everything outside job costs by category (office & management payroll, rent, vehicles & fuel, insurance, marketing, commissions, bonuses, software, professional, bank, interest, ...), % of revenue, per field hour, per month; each category opens to its accounts.
- Accounts are sorted by QuickBooks account number (`Modules/Financials/AccountRules.cs`; override any prefix with settings `Financials:Accounts:{prefix}` = `Kind|Category`).
Tabs: **Company · Job P&L · Reconciliation · Bonuses & distributions · Commissions**
- **Company:** P&L, balance sheet and cash position from QuickBooks. KPI cards for revenue YTD, net profit YTD, cash on hand and open A/R.
- **Job P&L:** per-job profit from JobTread, with burden rates applied.
- **Built (first pass): Job by job** (`/financials/jobs`): each job's lifetime JobTread figures (invoices net of retention, costs to date) vs QuickBooks P&L by customer/job (all dates; labor = field wages + burden via account rules). QuickBooks columns are matched to jobs by job name, job number, or customer when the customer has one job. Shows gaps over $1,000 and 10% of revenue, JobTread-only and QuickBooks-only rows, and QuickBooks labor/job costs not assigned to any customer/job ("Not Specified").
- **Reconciliation (key feature):** month-by-month, job-by-job comparison of JobTread job profit against QuickBooks profit. **QuickBooks is the source of truth.** Columns: job, revenue, JobTread profit, QuickBooks profit, gap, flag. A persistent gap means burden rates in JobTread need adjusting. Include a month total row with a plain-English verdict (e.g. "JobTread running ~4% optimistic").
- **Bonuses & distributions:** dashboard-owned data. Depends on Employees.
  - **Built (first pass):** `/financials/bonuses`. Per job: 10% of profit to supervisors and 10% to the field (settings `Bonuses:SupervisorPercent` / `FieldPercent`), profit = billed incl. retention − costs (JobTread). Each pool split by hours on the job (supervisors who clocked time; if none, all to the job's PM). **Ready** when the job is Completed and fully billed and collected; **Coming** shows pools at today's profit for jobs still going (and what it's waiting on). Approve locks the amounts (BonusPayouts table), then Mark paid. By-person totals (ready, approved, paid this year). Paid this year is checked against QuickBooks 6114 Bonus Payroll. Every action is logged.
  - **To confirm with Kareem:** profit as the base, split by hours, and "completed and paid" as the trigger.
- **Commissions:** dashboard-owned. Records who sold each job, commission rules, and payouts.

### 4. Customers (JobTread + QuickBooks + reviews)
- List on the left (search; filters: All / Owes money / Review to request), detail on the right (stacks on mobile).
- Detail: contact info, lifetime revenue, outstanding balance (QuickBooks), the customer's jobs (JobTread), links to JobTread and SharePoint.
- **Reviews:** dashboard-owned review status **per customer, not per job** (Kareem: "we only want 1 review per customer, even if they have multiple jobs"): requested / received + platform + rating / ask later / not asking. Saving only records the status in Phase 1.
- **Built (first pass):** `/customers`: JobTread jobs (open + closed in the last 12 months) grouped by JobTread customer account, QuickBooks balance and contact info matched by name (QuickBooks customers who owe but have no recent job also show). Filters All / Owes money / Review to request. Each job: stage, contract, billed, unpaid, JobTread and SharePoint links. Tile: customers owing.
- **Built: reviews per customer** (CustomerReviews table, keyed by JobTread account id). One review form per customer in the expanded customer (status, platform, stars, notes, Save) and a status chip on the customer row. A customer is a **review to request** when they have at least one completed job, JobTread's "Left Review" customer field isn't "Left a Review", and they have no review status in the dashboard. Customers marked "Left a Review" in JobTread show a "Left a review (JobTread)" chip; a dashboard status, when there is one, shows instead. The KPI and filter count customers. Saving doesn't change JobTread's "Left Review" field (that would be a JobTread write; not approved yet). The earlier per-job JobReviews table is kept but no longer used (its rows have only a job id).

### 5. Sales and marketing
**Built (first pass):** `/sales` with three tabs.
- **Commissions:** salesperson from each JobTread job; a rule per person (percent of money collected, contract, or job profit; CommissionRules table); earned per job, payments recorded (CommissionPayments), owed. Collected = invoices minus unpaid (before retention). Rates aren't known yet: everyone shows "No rate set" until entered. Tile: commissions owed.
- **Door-to-door:** log form (date, rep, doors, leads, sold, sold $, notes; SalesActivities), totals and by-rep table for 30/90 days or YTD.
- **Ads & lead sources:** monthly spend per channel entered by hand (AdSpends) next to jobs and contract value by JobTread "How bid came in" (same names line up). Not attribution (still undecided).
- **Ads → revenue:** spend per channel (Google Ads, Facebook/Instagram, referrals/signs), leads, jobs won and revenue, with return on spend. Ad platform integration comes later; Phase 1 can use manually entered monthly spend.
- **Door-to-door and reps:** dashboard-owned. A "Log activity" form (rep, date, doors knocked, leads, notes), totals for doors, leads and sold, and a rep table (doors, leads, sold, sold $).
- **Open issue:** the attribution method (which ad produced which lead/job) is undecided. Don't invent one. Leave a clear placeholder.

### 6. Vendors and payables (QuickBooks)
- Bills, due dates and vendor balances. Saved views (e.g. Due in 14 days, Overdue).
- **Built (first pass):** `/vendors`: open QuickBooks bills (Balance > 0) with views Due in 14 days (incl. overdue), Overdue, All open, By vendor; search; KPI totals; tile = owed in the next 14 days. Read only.

### 7. Employees (QuickBooks payroll + JobTread assignments)
- One profile per person.
- **Identity matching (solved):** every JobTread membership carries `qboEmployeeId`, so JobTread and QuickBooks people match by id. A manual override in the dashboard DB covers any missing link.
- **Per-employee settings live here** (dashboard DB): crew level (Foreman / Painter / Helper / Supervisor; suggested from QuickBooks base rate: Helper $16–22, Painter $23–28, Foreman $29+, confirmable/overridable) and a color for each PM. The schedule and Projects PM tags read from these profiles; anyone not set falls back to the `Crew` and `ProjectManagers` settings in appsettings.json, then to the JobTread role (non-field staff = Supervisor). (Projects' foreman pick still uses highest pay.)
- **Built:** `/employees` lists everyone internal in JobTread, grouped Crew / Supervisors & office, with level chip (`*` = default, not set yet), PM color tag, hours this week and a flag when no QuickBooks employee is linked. Opening a row shows level, PM color (supervisors), notes, and jobs worked in the last 30 days (from time entries). Saving needs the database; without it the page is read only. Every save and every schedule push is written to the `ChangeLog` table (who, what, before/after, result).
- **Built (v2):** management shows its JobTread role (Admin, PM, Office, Sales) instead of "Supervisor"; the schedule toggle is Crew / Management. Each row shows QuickBooks base pay (QB, entered on the person for now) and the JobTread burdened rate (JT, their regular time entry type); the expanded row lists all JobTread rate types. When QB pay points to a different level than the one set, the row says so ("Pay says Foreman"). Pay is visible to everyone with dashboard access (management only, per Kareem).
- **QuickBooks pay automatically:** Intuit only gives pay rates and pay stubs to apps it approves for payroll access. Once the dashboard's Intuit app is approved, QB pay fills in from the latest pay stub. Until then it's typed in.
- **Built: change log** (`/changes`, "Change log" in the menu footer next to Connections): every entry in the `ChangeLog` table, newest first. Each row: when (relative, exact Pacific time on hover), who (name from the email: their JobTread link on Connections, then a JobTread staff name that fits the email, else the email), area chip, a plain-English summary per area (schedule pushes with before → after days, times and crew; employee saves; bonuses; commissions; sundries; equipment; reviews; connections) and result (Done / Conflict / Failed). Opening a row shows the details as a before/after table and a key/value list. Filters: area, person, last 7 / 30 / 90 days, search (target and details, sundry names); 50 per page with Older / Newer. Read only.
- **Later:** certifications.

### 8. Equipment and sundries (dashboard-owned)
**Built (first pass):** `/equipment` with three tabs.
- **Sundries:** shop stock (tape, plastic, caulk, sandpaper, rollers...) with unit, average cost, reorder point and order quantity, vendor/SKU. Bulk order in (adds stock, updates average cost), issue to a job (takes stock out at average cost, charged to that job), physical count correction. Reorder list for items at or below their reorder point (tile: low or out of stock). Every movement logged (InventoryMovements).
- **Equipment:** each piece (sprayers, ladders, scaffold, vehicles...) with tag, value, location (shop or a job), who has it, condition (Good / Needs service / Out of service), last serviced, and history (moves, condition, service).
- **Starting list:** when a tab is empty, "Load starting list" adds the 76 pieces from the SharePoint equipment list (09 Operations/Equipment, "Equipment List - CURRENT") or the 31 sundries from the April 2025 bulk sundry order (`Modules/Equipment/starting-list.json`). Job sites in the sheet are matched to open JobTread jobs by name or address; trucks and trailers go in "with"; unmatched sites stay at the shop with the sheet location in notes. Sundry counts start at 0.
- **Charged to jobs:** sundry cost per job for a period, with undo. Not pushed to JobTread: that would be a JobTread write (needs Kareem's approval).
- Fully custom: items (sprayers, ladders, vehicles, supplies), status, location or assigned crew, low/out-of-stock flags.

## 7. Phased plan

**Phase 1: Foundation + Projects (first PRs)**
1. App shell: layout, top bar, design tokens, fonts, launchpad with all 8 tiles. Tiles for unbuilt modules show "Coming soon". Show the signed-in user's name from Easy Auth headers.
2. Database: Azure SQL + EF Core + first migration (users and saved views). List the env var names Kareem needs to add.
3. JobTread data path: decide with Kareem between (a) reading live through the existing `JobTreadProxy` function, or (b) having the function app sync jobs into the DB on webhook events. (b) is the agreed long-term direction; (a) is acceptable as a first step.
   **Decided (first step):** the dashboard reads JobTread live from its own server code (`IJobTreadClient`, grant key in the `JOBTREAD_API_KEY` setting), with a 2-minute cache and a Refresh link. Not through `JobTreadProxy`, which is anonymous and only lists jobs. Moving to (b) later only changes the services in `Modules/`, not the pages.
4. Projects screen with filters and saved views.

**Phase 2:** Scheduling, then Customers (needs QuickBooks access: an Intuit developer app with OAuth2 and token storage).
**Phase 3:** Financials (Company → Job P&L → Reconciliation → Commissions/Bonuses).
**Phase 4:** Sales and marketing, Vendors, Employees, Equipment.
**Later:** a Claude assistant panel via the Anthropic API that answers questions from dashboard data. Not in scope yet.

## 8. Open questions to raise with Kareem (don't guess)
- Marketing attribution method.
- Commission and bonus rules (amounts, triggers, who is eligible).
- Which job types and crews exist in JobTread, and how crews are represented there.
- QuickBooks API access: Intuit developer app setup and where OAuth tokens are stored.
- Projects: a work-based % complete (e.g. costs ÷ contract × 75%) was considered and set aside for now in favor of billing + costs-vs-billed.
- Beyond scheduling (the first write-back), which fields should be editable from the dashboard, and in which system (JobTread or QuickBooks) each edit lands.
