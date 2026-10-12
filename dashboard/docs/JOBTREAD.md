# JobTread reference

How Purple Painting's JobTread account is structured, and how the dashboard reads it.
Add to this file whenever a module starts using a new part of JobTread.

Code: `Integrations/JobTread/` (client, ids, JSON helpers). Field ids live in `JobTreadIds.cs`.

## Connecting

- API: `POST https://api.jobtread.com/pave` with body `{ "query": { "$": { "grantKey": "…" }, …query } }`.
- The response is the query's top-level fields directly (e.g. `{ "organization": { … } }`). Query errors come back as **plain text**, not JSON.
- Grant key: Azure setting `JOBTREAD_API_KEY`. Same name and value as the `jobtread` function app.
- Organization id: `22PWNY9u7qZd`.
- Job link for people: `https://app.jobtread.com/jobs/{jobId}`.
- The Claude JobTread connector can run the same queries, which is the quickest way to try one out.

## Gotchas

- **"Request Entity Too Large" (413) means the query's *largest possible result* is too big**, not the request. JobTread multiplies `size` by every nested list's `size` (e.g. 25 tasks × 25 assignees fails, 25 × 10 works, even when the real data is tiny). Keep nested `size`s tight, page with `nextPage`, or group/sum on the JobTread side instead of pulling raw rows.
- **Lists return 10 items unless you pass `size`** (max 100). Always set `size`, and follow `nextPage` (pass it back as `page`) when there can be more. Grouped results (`group`) are paged the same way.
- `where` on a related field uses a path array: `[["document", "id"], null]`, `[["job", "id"], "in", [...]]`.
- Not every relation can be filtered on. `costItems` can't filter on `["document", "type"]` ("document is not queryable"); go through `documents` instead.
- Aggregates: `"billed": { "_": "documents", "$": { "where": … }, "sum": { "$": ["price"] } }` returns `{ "sum": 123.45 }`, or `null` when nothing matches.
- Group by: `"$": { "group": { "by": [["costCode", "id"], ["costCode", "name"]], "aggs": { "cost": { "sum": "cost" } } } }, "withValues": {}`. Each row in `withValues` has the group-by path filled in plus the agg names.
- Schema exploration: `{ "schema": { "$": { "path": "job" } } }` lists a type's fields.

## Jobs

Open jobs: `organization.jobs` with `where: ["closedOn", null]`. Around 150 open at a time; bids and production jobs are both "open".

| Field | Meaning |
|---|---|
| `number`, `name` | Job number (e.g. "362") and name |
| `location.address`, `location.account.name` | Site address and customer |
| `projectedPrice` | Contract: total of approved customer orders |
| `actualCost` | Costs to date: time entries + approved and pending vendor bills (verified to the cent) |
| `projectedCost` | JobTread's projected final cost (not used yet) |
| `costItems` where `document.id` is null | The job budget (copied from the estimate). **Not used for numbers**: Purple Painting doesn't maintain it. The dashboard only uses it to list the cost codes on a job |
| `closedOn` | Set when the job is closed |
| `startTask`, `endTask` | Usually empty in our account. Use scheduled task dates instead (below) |

### Job custom fields

Read through `customFieldValues { value, customField { id } }`. Filter to the ids you need with `where: [["customField","id"], "in", [...]]` and `size: 25`.

| Id | Name | Values |
|---|---|---|
| `22PWsEy5ZSXL` | Production Stage (autofilled) | Pre-Construction / Color Submittals, Scheduled, In Progress, On Hold - Phasing, Completed, Closed. Empty for bids |
| `22PWsEVVW4aj` | Job Type | Residential - Standard Home, Residential - Custom House, Commercial, Prevailing Wage, Property Management / Production |
| `22PXCNYAFP5P` | Project Manager | Jose Morales, Abraham Gaspar |
| `22PXCPYP4HNM` | Sales Person | Kendall Martin, Peter Tommaso, Lyric Nguyen, Alicia Ochoa, Abraham Gaspar, Kareem Mustafa, Conor Sanders |
| `22PWsDkAPRYB` | How Did Bid Come In | Received Digital Bid Invite, Requested Job Walk, Partner Work Order, **Internal Job** (Office Work, Time Off, Shop Labor: hidden from Projects) |
| `22PWRx62raQg` | Job Walk Bid Stage (autofilled) | 1) Requested: Proposal … 6) Deposit Paid: Closed/Won, 7) Dormant, Closed/Lost |
| `22PWNYKhXZxr` | Digital Takeoff Bid Stage (autofilled) | 1) Received: Bid Invite … 6) Closed/Won - GC Awarded Contract, 7) Dormant, Closed/Lost |
| `22PXKWVJQgxh` | Pricing Struction | Production, Standard, Custom Homes, Prevailing Wage |
| `22PY6Y7snTvN` | Proposal Due Date | date |
| `22PYNFyRMxqa` | OneDrive Folder Link | URL of the job's **SharePoint** folder (name is historical) |
| `22PdhDrXpi9P` | Claude Chat Link | URL |

## Money on a job

| Number | How it's read |
|---|---|
| Approved contract | `job.projectedPrice` = approved customer orders (proposal + approved change orders). The dashboard's contract adds pending change orders on production jobs |
| Pending change orders | `documents` where `type = customerOrder`, `status = pending`, sum `price`. On a bid this is the proposal waiting on the customer |
| Unsent change orders | `documents` where `type = customerOrder`, `status = draft`, `name like "%Change Order%"`. The name filter skips old draft proposals |
| Invoiced (net) | `documents` where `type = customerInvoice` and `status in [approved, pending]`, sum `price`. **This is after retention is held back** |
| Unpaid | `documents` where `type = customerInvoice`, `status in [approved, pending]`, sum `balance` |
| Retention held | `job.retainageCostItem.documentCostItems` where `document.type = customerInvoice`, `document.status in [approved, pending]`, sum `price` (negative; flip the sign). `job.defaultRetainagePercentage` is the rate (e.g. 0.1) |
| Billed (earned) | Invoiced (net) + retention held. Used for the billed ring and left to bill |
| Costs to date | `job.actualCost` |

Pay apps (`isPaymentApplication = true`) carry a negative "Retainage" line linked to the job's `retainageCostItem`, so each invoice's `price` is net of retention.

Change orders are `customerOrder` documents named "Change Order". A job's estimate can also hold unselected option lines (`isSelected = false`); they're in the budget but not the contract (First Presbyterian: $32k budget vs $12k contract).

Document types seen: `customerOrder` (proposal, change orders), `customerInvoice`, `vendorBill`. Statuses: `draft`, `pending`, `approved`, `denied`. Draft and denied are ignored everywhere.

### Costs by cost code (Projects expanded row)

Three grouped queries on the job, merged by cost code id:

- Codes on the job: `costItems` where `document.id` is null, group by `costCode` (amounts ignored).
- Labor: `timeEntries`, group by `costItem.costCode` → sum `cost`, sum `minutes`.
- Bills: `documents` where `type = vendorBill`, `status in [approved, pending]`, then each bill's `costItems` grouped by `costCode` → sum `cost`.

Labor + bills per code adds up to `job.actualCost`. Time entries always have a cost item; some cost items have no cost code ("No cost code" row).

Cost codes are numbered by area, e.g. 1000 Interior, 1300 Interior Ceilings, 6100 Exterior Body, 6300 Exterior Fascia, 7100 Pressure Washing, 8100 T&M – Interior, 8400 T&M – Materials & Supplies.

## Schedule

The schedule is `tasks` (with `isToDo = false`). There's no crew object in JobTread:

- Production is scheduled as **one task per job per day** (e.g. "Mon, 10.05 — Patch & Prep Ext."), often under a group task (`isGroup = true`) for the phase.
- **Painters are assigned per task** through `assignedMemberships { user { name } }`. A "crew" is whoever is assigned that day.
- Tasks without a job are office/admin items (payroll, payables, HR). `taskType { name, color }` labels them (e.g. "Admin - Payroll 💸", "Project - Production 🖌️", "Project - SOW 📋").
- Fields: `startDate`, `endDate`, `startTime`, `endTime`, `progress` (0–1), `completed`.

Projects list activity (one call per 100 jobs, three grouped aliases on `organization`):

- `dates`: `tasks` where `isToDo = false`, `isGroup = false`, `job.id in [...]`, grouped by `job.id` → `min startDate`, `max endDate`.
- `upcoming`: same, plus `startDate >= today` → `min startDate` (next scheduled day).
- `lastTime`: `timeEntries` where `job.id in [...]`, grouped by `job.id` → `max startedAt` (UTC timestamp; convert to Pacific).

Where-clause note: a single condition is passed bare (`"where": [["job","id"], "in", [...]]`), not wrapped in another array.

## Time entries

`timeEntries` fields: `startedAt`, `endedAt` (UTC timestamps), `minutes`, `cost`, `hourlyRate`, `isApproved`, `type`, `user`, `job`, `costItem`, `notes`.

## People and roles

- `organization.memberships` where `isInternal = true`: staff. `role.name` is one of Admin, Project Manager, Salesperson, Office Administrator, Field Employee. There is **no foreman role**; every painter is "Field Employee".
- **Link to QuickBooks:** each membership has `qboEmployeeId`, the QuickBooks employee id. Every internal member has one, so JobTread ↔ QuickBooks people match by id, not by name (names differ: e.g. "Alejandra Guzman" in JobTread is "Alejandra De Jesus" in QuickBooks, "Moises Gonzalez" is "Gonzales"). A user's display name belongs to their own JobTread login and can't be changed through the API (`updateMembership` has no name field).
- Crew on a job: `job.timeEntries` grouped by `user.id`, `user.name` → sum `minutes`, max `hourlyRate`, max `startedAt`. `hourlyRate` is the cost rate on each entry.
- Foreman (dashboard rule): highest `hourlyRate` among Field Employees on the job, ties by hours.
