# Purple Painting integrations

| Folder | Deploys to | Workflow |
|---|---|---|
| functions/ | Function App `jobtread` (Y1, Windows, Canada Central) | .github/workflows/functions-deploy.yml |
| dashboard/ | Web App PurpleDashboard (B1, Linux, West US 3) | .github/workflows/dashboard-deploy.yml |

Each workflow only runs when files in its own folder change.

Secrets live in Azure (Function App > Environment variables), never in this repo.
The deploy credentials live in GitHub > Settings > Secrets and variables > Actions:
AZURE_FUNCTIONAPP_PUBLISH_PROFILE for functions/ and AZURE_WEBAPP_PUBLISH_PROFILE
for dashboard/.

Rollback = revert the commit and push. The old FunctionName-YYYY-MM-DD-ROLLBACK.js
convention is no longer needed once history is in git.
