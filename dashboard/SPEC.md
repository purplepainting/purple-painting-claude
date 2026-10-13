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

### 4b. Users & access (built)

Settings → **Users & access** (`/settings/access`, admins only). Everyone who opens the dashboard is recorded on their first request (table `UserAccess`: email, name, role, overrides, first/last seen; last seen is written at most every 5 minutes) and starts as **View only**, which shows nothing sensitive (no financials, pay, bonuses, commissions, sales, vendors, company connections or change log). An admin picks each person's role and can tick or untick single permissions on top of it (per-person overrides). Every change is in the change log (area "access", e.g. "Changed Sam's role Office → Supervisor").

Roles (owner-approved, Oct 2026; the table is `Auth/AccessRoles.cs`):

| Permission | Admin | Office | Supervisor | View only |
|---|---|---|---|---|
| projects | view, edit | view, edit | view | view |
| scheduling (edit = push to JobTread) | view, edit | view, edit | view, edit | view |
| financials (Financials pages incl. job by job and overhead) | view, edit | view | view | — |
| bonuses (view = everyone's bonuses; edit = approve/pay; rate = effort ratings) | all | view | rate only | — |
| commissions | view, edit | view | — | — |
| sales (door-to-door) | view, edit | view | — | — |
| sales.ads (ads & lead sources, ad spend) | view, edit | view | — | — |
| customers | view, edit | view, edit | view | view |
| vendors | view, edit | view, edit | — | — |
| employees (list, levels, colors, notes) | view, edit | view, edit | view | view |
| employees.pay (QB base pay, JobTread rates, per-person labor cost) | view, edit | view | — | — |
| hiring | view, edit | view, edit | view, edit | view |
| equipment | view, edit | view, edit | view, edit | view |
| changelog | view | view | — | — |
| connections (company QuickBooks/Google/Facebook) | view, edit | view | — | — |
| settings, settings.bonuses (bonus rules), access | all | — | — | — |

Keys are `area.view` / `area.edit` (`employees.pay.view`, `bonuses.rate`, `settings.bonuses` …); an edit key brings its view key with it. Everyone can open Settings, search, and Connections for their **own** JobTread sign-in (needed to push the schedule).

- **Bootstrap admins:** emails in the `ADMIN_EMAILS` setting (comma separated; default `kareem@purplepainting.net` in appsettings.json) are always Admin and can't be changed or locked out. An admin also can't remove their own access to the page.
- **Database down:** ADMIN_EMAILS are Admin, everyone else View only (nothing can be saved anyway); Settings says so.
- **Enforcement (server side, default deny):** `Auth/AccessFilter.cs` runs before every page handler. Each page needs its folder's view permission (`AccessMap.Pages`), and every POST handler needs the permission listed for it (`AccessMap.Handlers`). A page or POST handler not in the map works for admins only and logs a warning (also listed at startup). Denied: 403 page "You don't have access to X. Ask an admin if you need it." (JSON `{error}` for fetch calls).
- **What's hidden:** menu tiles and their numbers, Settings tiles, Save buttons and edit forms (each page's `CanEdit` = database up **and** the edit permission), the schedule editor, pay columns and per-person labor cost without `employees.pay.view`, pay fields without `employees.pay.edit` (a pay change in a POST is refused), Sales tabs by permission, company connection cards, and global search groups (Jobs need projects or scheduling; Customers, People, Equipment and Vendors need their area).
- **Locally:** `?devuser=someone@purplepainting.net` (a cookie) or the `Dev:UserEmail` setting acts as that person; Development only, Easy Auth headers always win. `appsettings.Development.json` makes `dev@localhost` an admin.

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
- "/" opens the Projects list.
- **Global search (built):** a search box in the top bar of every page (phones: a search icon that opens a full-width box). Results drop down as you type, grouped: Jobs (open + closed in the last 12 months; name, number, customer, address; opens the job timeline, with a JobTread link), Customers, People (opens their Employees row), Equipment & sundries, and Vendors (only when QuickBooks bills are already cached). Up to 6 per group; ↑/↓ and Enter pick a result, Enter alone opens the full results page (`/search?q=`), Esc closes, "/" on the keyboard focuses search. Reads only cached data; a source that fails, isn't connected or takes over 2 seconds is skipped (`Modules/Search/SearchService.cs`).

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
- **Built (week view):** each job on screen gets a different color (hues spread evenly around the wheel, greens left out because crew chips are green, for the number of jobs that week, alternating pastel and mid tones when there are more than 4; day view uses the week's colors); crew chips are colored by level on one green scale (Foreman dark green, Painter medium green, Helper light green); management chips are purple by JobTread role (Admin dark, Office medium, Sales light), PMs in their own PM color, anyone else gray and listed foreman first; PM tags are colored per PM and sit under the job type; job numbers are not shown. Each job/day shows labor cost and hours (actual from time entries up to today, "est." after: crew × scheduled hours × burdened rate) and each job shows a week total of both. By person shows the same per painter per day, with a week total per person (the money only with `employees.pay.view`, see 4b; others see hours). Past days show **what actually happened**: who clocked in with hours, scheduled people who didn't clock in crossed out, unscheduled people dashed. Day view groups tasks into one card per job, most people first. **Crew and Supervisor schedules are separate** (toggle): a task with only supervisors on it (PM job starts, QC, material runs) is on the Supervisors schedule; tasks with painters (or unassigned) are on Crew.
- **Schedule editor (built):** "Edit schedule" turns the week into a draft: drag a box to another day (dates shift, the date in the task name updates), drag or tap a painter to move just them (or to "Not scheduled" to take them off), "+ Add day" offers a new day (copy of the job's nearest day) or one of the job's unfinished tasks already in JobTread from 2 weeks back to 4 months ahead, moved onto that day (a normal update, so the conflict check and linked tasks apply), "+ New task", "+ Add job" (active jobs not on the schedule), tap a box to edit name/dates/times/crew. A day added in the draft can be removed before pushing. **Delete** tries a real delete in JobTread; if JobTread's key isn't allowed to delete, it falls back to renaming the task "DELETE – …", moving it to that week's Sunday and clearing its crew (no notification), for someone to delete in JobTread. Marked tasks show crossed out in red. The push result says which happened. A bar counts pending changes; "Review & push" lists every change (before → after, linked tasks noted) and sends them to JobTread. Each change is checked against JobTread first and skipped if someone changed it there since loading. JobTread moves dependent tasks and notifies the crew (its defaults). Every push is logged with who and what. The draft survives a reload (this browser only). Anyone with `scheduling.edit` (see 4b) can push, once they sign in with JobTread on Connections (or paste their own key); pushes go to JobTread as them, so JobTread shows who made the change.
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
  - **Built: monthly bonuses** (`/financials/bonuses`, Oct 2026; replaces the per-job approve/mark-paid first pass). Rules are edited on **Settings → Bonus rules** (`/settings/bonuses`, permission `settings.bonuses`), saved in the dashboard database (`DashboardSettings` "bonus-rules"; the `Bonuses` app settings are only the defaults), every save change-logged before → after. The page's "How bonuses are calculated" card shows the live rules.
    - **Per job, per month** (JobTread, read only): income = customer invoices by `issueDate` (approved + pending, net of retention), costs = time entries by `startedAt` + vendor bills by `issueDate`, profit = income − costs. One grouped request per month (`bonus-month-yyyy-MM`; invoices, bills, time by person), paged per alias, cached like Projects.
    - **Pools:** 10% of the month's profit to supervisors, 10% to the field. A losing month makes the pools **negative**: each person's share comes off their year.
    - **Field split:** 25% by share of hours on the job that month, 75% by share of effort score (average of every rater's 1–10 score for the month; not rated = no effort part; nobody rated = effort part by hours). Cap (default): nobody gets more than their share of hours, the excess stays with the company (other modes: effort part capped at a multiple of hours share, or none). **Supervisors' split:** by supervisors' hours on the job that month; none → the job's PM. Formulas in one place: `Modules/Financials/BonusRules.cs` (`BonusSplit`).
    - **Eligibility:** probation (QuickBooks hire date + 90 days, checked at the end of the month), left (QuickBooks release date / inactive), or excluded by hand for a month (reason, change-logged). Shares are worked out as if eligible and **kept by the company** (setting: or redistributed). "Hire date unknown" counts as eligible.
    - **Effort ratings** (Effort & eligibility tab, permission `bonuses.rate`): one score per month, person and rater; pre-filled from last month's average (first month: the last spreadsheet round, embedded `effort-last-round.json`; no uploads). Spread of 5+ flagged. Read-only once the month has paid & locked bonuses.
    - **Clawbacks** on a job (cost not in JobTread → 10% + 10% of it, or bonus dollars from a pool): charged to the people on the job by their year-to-date shares of it. Editable, deletable, change-logged.
    - **Yearly ledger per person** (calendar year): every job-month line, plus and minus, plus admin adjustments (reason required). Owed = max(0, opening + YTD net − 10% retention − paid this year): losses lower later payouts, nobody pays cash back. Retention is released in the **year-end settlement, paid with January** (setting: or when each job closes); admins can release early. A negative year-end balance carries into next year (setting). Optional rule: hold payment for a month when QuickBooks net profit for it is negative.
    - **Monthly reconcile** (main view): pick a month → per person YTD earned, losses, adjustments, retention, paid, owed (opens to that month's jobs and the year by job), pool totals, the company's QuickBooks net profit for the month next to the bonus total. **Payment reconcile** right below: month M is paid with month M+1's bonus run; per person due / recorded / record payment (amount = what's left, stays on the row with an inline confirmation) with Delete. **Payroll checks:** "Bonus Only" paychecks in QuickBooks dated in M+1 on the bonus account (6114 Bonus Payroll; setting), gross, matched by QuickBooks employee id then name: Check found (matches / differs) → Record from payroll → **Paid & locked** (per person or "Lock month"). A locked month is a stored snapshot; later changes to it (JobTread edits, ratings, rules) flow into the next open month as an automatic adjustment. Admin unlock with reason.
    - **Jobs tab:** each job opens to Month · Income · Costs · Profit · pools · clawbacks · kept by company · each person, notes (probation, PM fallback, unrated), clawbacks with Add / Edit / Delete.
    - **Employees:** each person's row shows their bonus year by month (earned, losses, net, YTD, held, paid with check #, owed) and status chips (needs `bonuses.view`).
    - Old per-job approvals: paid ones became payments (one per person, dated when marked paid) on first load; approved-but-unpaid ones are listed as history. Nothing is written to JobTread or QuickBooks.
    - **Open with Kareem:** Jul 27–31, 2026 isn't covered (spreadsheet round ended Jul 26, monthly starts Aug); supervisor pool by hours vs the spreadsheet's "5% per supervisor per week"; whether people can see their own bonus.
- **Commissions:** dashboard-owned. Records who sold each job, commission rules, and payouts.

### 4. Customers (JobTread + QuickBooks + reviews)
- List on the left (search; filters: All / Owes money / Review to request), detail on the right (stacks on mobile).
- Detail: contact info, lifetime revenue, outstanding balance (QuickBooks), the customer's jobs (JobTread), links to JobTread and SharePoint.
- **Reviews:** dashboard-owned review status **per customer, not per job** (Kareem: "we only want 1 review per customer, even if they have multiple jobs"): JobTread's customer field "Left Review" is the only place a left review is marked (the dashboard reads it, never writes it); the dashboard tracks the request: requested (and where we asked) / ask later / not asking.
- **Built (first pass):** `/customers`: JobTread jobs (open + closed in the last 12 months) grouped by JobTread customer account, QuickBooks balance and contact info matched by name (QuickBooks customers who owe but have no recent job also show). Filters All / Owes money / Review to request. Each job: stage, contract, billed, unpaid, JobTread and SharePoint links. Tile: customers owing.
- **Built: reviews per customer** (CustomerReviews table, keyed by JobTread account id). One review form per customer in the expanded customer (status, platform, stars, notes, Save) and a status chip on the customer row. A customer is a **review to request** when they have at least one completed job, JobTread's "Left Review" customer field isn't "Left a Review", and they have no review status in the dashboard. Customers marked "Left a Review" in JobTread show a "Left a review (JobTread)" chip; a dashboard status, when there is one, shows instead. The KPI and filter count customers. Saving doesn't change JobTread's "Left Review" field (that would be a JobTread write; not approved yet). The earlier per-job JobReviews table is kept but no longer used (its rows have only a job id).
- **Built: Google reviews (read only, `docs/GOOGLE.md`):** from the Business Profile (v4 reviews) once Google is connected and a location is picked. KPI "Google rating" (average, count, matched); a **Google reviews** pill with the rating, a recent reviews feed (stars, reviewer, date, text, our reply, matched customer) and a "Not matched to a customer" list. Reviewers are matched to customers **by name only** (normalized; exact or close, and only when exactly one customer fits; `Modules/Customers/ReviewMatcher.cs`). A matched customer shows "Google review ★N" and its review text, and doesn't count as "Review to request". Display only: JobTread's "Left Review" stays the record (the customer's detail says to update it in JobTread by hand); nothing is written to JobTread or Google. Replying to reviews isn't built (to decide). Cached 3 hours; Refresh clears it.

### 5. Sales and marketing
**Built (first pass):** `/sales` with three tabs.
- **Commissions:** salesperson from each JobTread job; a rule per person (percent of money collected, contract, or job profit; CommissionRules table); earned per job, payments recorded (CommissionPayments), owed. Collected = invoices minus unpaid (before retention). Rates aren't known yet: everyone shows "No rate set" until entered. Tile: commissions owed.
- **Door-to-door:** log form (date, rep, doors, leads, sold, sold $, notes; SalesActivities), totals and by-rep table for 30/90 days or YTD.
- **Ads & lead sources:** monthly spend per channel from the ad APIs, QuickBooks (booked) or entered by hand (AdSpends), next to jobs and contract value by the **customer account's Lead Source** in JobTread (see attribution below).
- **Built: attribution by JobTread Lead Source (read only).** The website form (SquarespaceWebhook) sets the customer account's "Lead Source" from the ad click id (gclid → Google Ad, fbclid → Meta, ndclid → NextDoor, else Website Form); others are set by hand. "Ads → revenue by lead source" groups sold jobs by that field, mapped to spend channels in one place (`SalesService.ChannelFor`): Google Ad / Google / Google Ads → Google Ads, Meta / Facebook / Instagram → Facebook/Instagram, NextDoor → Nextdoor, others as written. Jobs whose customer has no Lead Source fall back to "How bid came in", in rows labelled "No lead source (bid: …)" at the bottom. **JT leads** = new JobTread customer accounts created in the period (whole months, same as spend) by Lead Source (one grouped query, `docs/JOBTREAD.md`), shown next to platform-reported leads with cost per JT lead and close rate (new accounts in the period with a sold job ÷ JT leads). A note gives how many new accounts have no Lead Source. Cached like projects.
- **Ads → revenue:** spend per channel (Google Ads, Facebook/Instagram, referrals/signs), leads, jobs won and revenue, with return on spend. Ad platform integration comes later; Phase 1 can use manually entered monthly spend.
- **Built: Google Ads (read only, `docs/GOOGLE.md`):** once Google is connected on Connections, the Ads tab shows a Google Ads section from the Google Ads API (campaign × month, last 12 months): spend, clicks, leads (= Google Ads conversions), cost per lead and calls from ads for the period, a by-month table (12 months) and a by-campaign table for the period (period picker adds "Last 12 months"). Google Ads spend and leads feed the "Ads → revenue by lead source" table under the channel `Google Ads` (JobTread Lead Source "Google Ad"/"Google" maps to it); other channels stay manual. If a hand-entered Google Ads row exists for a month the API covers, the API figure is used and the row is marked "Not counted" (no double counting). Cached until 5am; Refresh clears it. Never changes ads.
- **Built: Facebook & Instagram ads (Meta Marketing API, read only).** One shared connection on Connections (Facebook Login, `ads_read`, long-lived ~60-day token stored encrypted in `MetaConnections`; the card shows the expiry and asks for a reconnect under 10 days; ad account picker from `/me/adaccounts`). The Ads & lead sources tab gets a "Facebook & Instagram ads" section: KPIs for the period (spend, link clicks, Meta-reported leads, cost per lead), by campaign, Facebook vs Instagram (`publisher_platform`), and the last 12 months by month (spend, impressions, reach, clicks, link clicks, leads, cost per lead). Meta spend feeds the ads view as the "Facebook/Instagram" channel: for months the API covers, it replaces a hand-entered row for the same channel and month (noted on the page, never both). Cached until 5am, Refresh link. Setup and details: `docs/META.md`. Settings `META_APP_ID`, `META_APP_SECRET`.
- **Nextdoor:** no self-serve ads API; its spend comes from QuickBooks (6410, vendor Nextdoor) below.
- **Built: ad spend booked in QuickBooks (read only, `Modules/Sales/QuickBooksAdSpendService.cs`).** One account lookup and one General Ledger report for the ad accounts over the last 12 months (cached until 5am like all QuickBooks data). Accounts: setting `Sales:AdAccounts` (comma separated, default `6410,6420,6430,6440`; sub-accounts are included). Channels, mapped in one place (`QuickBooksAdSpendService.ChannelOf`): **6420** Google Advertising Lead Gen → Google Ads (Google:AdsChannel); **6440** Website SEO → "Website SEO"; **6430** Printing Marketing Materials → "Printing"; **6410** Other Advertising Lead Gen split by vendor (or memo when there's no payee): Facebook/Meta/Instagram → Facebook/Instagram, Nextdoor → Nextdoor, Yelp, Angi/HomeAdvisor, Thumbtack, Houzz, Porch, Bark, BBB → that name, Google → Google Ads (booked to the wrong account), anything else → "Other advertising". 6460/6470/6480/6490 aren't ad spend and aren't read.
- **Spend source per channel and month** (`SalesService.MergeApiSpend`): the platform API (Google Ads, Meta) when connected and covering that month → else QuickBooks booked (channel-months with bookings) → else hand-entered rows. Never two: a hand-entered row under a source is marked "Not counted · API/QuickBooks: $X"; QuickBooks figures under an API are shown only in the reconcile. Lead source rows get an "API" or "QB" chip.
- **Platform vs QuickBooks** (Ads tab, `SalesService.Reconcile`): per channel and month in the period, platform reported · booked in QuickBooks · difference (platform − QuickBooks), flagged when over $25 and over 5%; channels without a platform show QuickBooks as the source ("no API"/"not connected"). The current month is marked "so far". A collapsed list explains common causes (billing timing, card charges booked to the wrong account, not categorized yet, credits, tax) and another lists the QuickBooks lines in the period (date, account, vendor, channel). Refresh link drops cached QuickBooks data.
- **Not ad spend:** channels in setting `Sales:NonAdChannels` (default `Website SEO,Printing`; `-` counts everything) are left out of the Ad spend total and the lead-source table and show in the reconcile as "not ad spend".
- **Manual spend** is collapsed under "Add spend not in QuickBooks" (open by default only when neither QuickBooks nor an ad API is connected). No uploads.
- **Door-to-door and reps:** dashboard-owned. A "Log activity" form (rep, date, doors knocked, leads, notes), totals for doors, leads and sold, and a rep table (doors, leads, sold, sold $).
- **Attribution:** by the customer account's JobTread Lead Source (above). Open: whether "Google" (picked by hand) means Google Ads or organic Google/Business Profile; it's counted as Google Ads for now. Leads that never become a JobTread account (e.g. calls not entered) aren't in JT leads.

### 6. Vendors and payables (QuickBooks)
- Bills, due dates and vendor balances. Saved views (e.g. Due in 14 days, Overdue).
- **Built (first pass):** `/vendors`: open QuickBooks bills (Balance > 0) with views Due in 14 days (incl. overdue), Overdue, All open, By vendor; search; KPI totals; tile = owed in the next 14 days. Read only.

### 7. Employees (QuickBooks payroll + JobTread assignments)
- One profile per person.
- **Identity matching (solved):** every JobTread membership carries `qboEmployeeId`, so JobTread and QuickBooks people match by id. A manual override in the dashboard DB covers any missing link.
- **Per-employee settings live here** (dashboard DB): crew level (Foreman / Painter / Helper / Supervisor; suggested from QuickBooks base rate: Helper $16–22, Painter $23–28, Foreman $29+, confirmable/overridable) and a color for each PM. The schedule and Projects PM tags read from these profiles; anyone not set falls back to the `Crew` and `ProjectManagers` settings in appsettings.json, then to the JobTread role (non-field staff = Supervisor). (Projects' foreman pick still uses highest pay.)
- **Built:** `/employees` lists everyone internal in JobTread, grouped Crew / Supervisors & office, with level chip (`*` = default, not set yet), PM color tag, hours this week and a flag when no QuickBooks employee is linked. Opening a row shows level, PM color (supervisors), notes, and jobs worked in the last 30 days (from time entries). Saving needs the database; without it the page is read only. Every save and every schedule push is written to the `ChangeLog` table (who, what, before/after, result).
- **Built (v2):** management shows its JobTread role (Admin, PM, Office, Sales) instead of "Supervisor"; the schedule toggle is Crew / Management. Each row shows QuickBooks base pay (QB, entered on the person for now) and the JobTread burdened rate (JT, their regular time entry type); the expanded row lists all JobTread rate types. When QB pay points to a different level than the one set, the row says so ("Pay says Foreman"). Pay shows only with `employees.pay.view` and is changed only with `employees.pay.edit` (see 4b).
- **QuickBooks pay automatically:** Intuit only gives pay rates and pay stubs to apps it approves for payroll access. Once the dashboard's Intuit app is approved, QB pay fills in from the latest pay stub. Until then it's typed in.
- **Built: change log** (`/changes`, "Change log" in the menu footer next to Connections): every entry in the `ChangeLog` table, newest first. Each row: when (relative, exact Pacific time on hover), who (name from the email: their JobTread link on Connections, then a JobTread staff name that fits the email, else the email), area chip, a plain-English summary per area (schedule pushes with before → after days, times and crew; employee saves; bonuses; commissions; sundries; equipment; reviews; connections) and result (Done / Conflict / Failed). Opening a row shows the details as a before/after table and a key/value list. Filters: area, person, last 7 / 30 / 90 days, search (target and details, sundry names); 50 per page with Older / Newer. Read only.
- **Later:** certifications.

#### Employees → Hiring (built, first pass)
No upload buttons anywhere on the dashboard (Kareem): candidates are added on the page; there is no file upload or spreadsheet import.
`/employees/interviews` (page title "Hiring", "Hiring" pill on the Employees page). Replaces the Interviews spreadsheets, the paper interview sheets and the scanned docs: everything up to the trial day is kept here. Follows "How We Run Hiring Interviews" (Oct 2026). Dashboard-owned data only (tables HiringCandidates, HiringEvents, InterviewDays, InterviewBookings, CandidateDocuments; migration `Hiring`); nothing is written to JobTread or QuickBooks.
- **Candidates** (also the list of past applicants): name, phone, position (Helper / Painter / Foreman / Sales Rep), language (English / Spanish / Both), source (Craigslist / Indeed / Referral / Other), experience. Stages: Texted → Booked → Confirmed → Interviewed → Trial day → Hired, plus Not hired / No-show / Withdrew. Stage pills with counts (In progress, each stage, All), search by name, phone, position, language, stage or date (interviewed or interview day, e.g. "10/14"), the interview day each person came to, rating and file count. Quick add needs only name, phone, position and language; a phone number already in the list opens that person instead of adding a duplicate.
- **Interview days:** date (defaults to next Wednesday), location (default Topa Topa Brewing patio, 120 Santa Barbara St), window 11:00–2:00, 15-minute slots. The day view lists the slots: book someone already in the list or add someone new in the slot, move, take off the schedule (back to Texted, No-show, Withdrew). Open slots are highlighted; English speakers are suggested for early slots and Spanish speakers for later ones (sorted, not enforced). A "Spreadsheet view" keeps the old columns (Name, Time, Phone #, ENG Y/N, Conf., Position/Experience).
- **Reminders:** per booking "Reminder sent (day before)" and "Confirmed (day of)" checkboxes (Confirmed moves the stage). After 10:00am on interview day (or once the day has passed) anyone not confirmed is flagged "Not confirmed — remove?" with one-click No-show or back to Texted.
- **Texts:** Invite, Position, Times, Location, Expectations, Reminder, Day-of confirmation, Jobsite Supervisor invite, Trial day invite, Required tools and an optional "Not this time", in English and Spanish, filled in with the day's date, time and place (`Modules/Hiring/TextTemplates.cs`, not editable on the page yet). Each person gets their language; Both gets English then Spanish in one message. **Copy** buttons plus an "Open Google Voice" link: texts go out from Google Voice, which doesn't open from `sms:` links, so there's no Text button.
- **Interview sheet** (phone/tablet form): the Interview Questions sheet (date, name, phone, city, position, bilingual, desired pay, 90-day start rate, years 1–10+, companies, skills, transportation, basic tools, payroll only, felony, part-time/weekends, hobbies, ever fired and why), interviewer (defaults to the signed-in user), 1–5 stars, notes and "points covered" (payroll only; 90-day probation rate; sick & holiday pay after probation; tools). Sales Rep candidates get the sales talking points instead. Saving moves the stage to Interviewed. Answers are stored as JSON on the candidate.
- **Trial day and hire:** invite to a trial day (date, jobsite supervisor, notes) → Trial day; shows the Trial Day Info requirements and the required tools for their position (Helper → Painter → Foreman, each adding to the one below) with a copy-ready list; trial hours worked. Hire → Hired with the onboarding checklist (W4, ID photo, direct deposit form, signed 90-day probation agreement, company policy, onboarding time to SHOP LABOR "onboarding", Required Tools doc emailed, added to JobTread, added to QuickBooks payroll; the last two are ticked after doing it there). Not hired with a reason.
- **Files:** removed from the page (no uploads, per Kareem). The CandidateDocuments table stays; any existing files still show on the candidate and can be viewed or deleted.
- **History:** every stage change, booking, upload and note with who and when; all changes are also written to the ChangeLog (area "hiring").
- **Documents box:** links to Interview Questions.pdf, Trial Day Information, Trial Day Timesheet, Sales Development Rep Interview Points and the Interview Spreadsheets folder. The Office Manager Task Manual is left out (it still has passwords in it).
- **Later:** import the historic Interview spreadsheets (their columns map to name, slot time, phone, language, confirmed, position/experience and the interview day); editable templates; open questions from the process doc (what the rating scale means, who sits in, how a trial day is scheduled and paid).

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
