# Purple Painting integrations

Monorepo for Purple Painting's internal systems. See README.md for what deploys where.

## Rules

- Never push to `main`. A merge to `main` deploys to production. Work on a branch and open a PR for Kareem to merge.
- Never commit secrets (API keys, client secrets, connection strings, publish profiles). They live in Azure app settings or GitHub Actions secrets. List any new setting names in the PR description.
- Ask before adding any code that writes to JobTread or QuickBooks.
- One feature or screen per PR.

## Dashboard (`/dashboard`)

Read `dashboard/SPEC.md` (what to build) and `dashboard/README.md` (how the code is laid out) before working there. `dotnet build dashboard/Dashboard.csproj` must pass before opening a PR.

## Functions (`/functions`)

Azure Function App `jobtread` (Node 22). JobTread webhooks, SharePoint sync, vendor invoice processing.
