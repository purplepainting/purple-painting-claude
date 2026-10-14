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

## Organize manifests

`functions/ApplyOrganizeManifests` runs every 15 minutes. It applies the job-folder organizer's decisions from JSON files in SharePoint `Claude/Nightly Organize/Manifests`, then moves each manifest to `Manifests/Applied` with `<name>.results.json` beside it. With no manifest it does nothing and writes nothing. To turn it off, disable the function in the portal.

```json
{
  "dryRun": true,
  "job": "<exact SP job folder name>",
  "folderLink": "<JT OneDrive Folder Link>",
  "package": "<Post Packages folder name, or empty>",
  "moves":   [ { "expectedName": "", "expectedSize": 0, "destPath": "Photos/2 Progress", "newName": "", "social": "", "socialName": "" } ],
  "renames": [ { "folderPath": "", "oldName": "", "newName": "", "in": "job" } ],
  "brief":   { "file": "Post Brief - JT ###.docx", "title": "", "sub": "", "blurb": "",
               "did": [], "team": [], "tags": [], "personal": [], "photos": [], "ideas": [], "captions": [], "check": [] }
}
```

- `dryRun`: only `false` changes anything. `true` or a missing flag runs every check and writes the results JSON, with status `DRY RUN` for what would happen.
- `moves`: the file must be in the job root with that exact name and byte size. `destPath` is a folder inside the job (created if missing). `newName` defaults to `expectedName` and must keep the extension. `social` is `Before and After`, `Post-Worthy` or `Our Crew`; the file is copied to `Post Packages/<package>/<social>/<socialName>` (`socialName` defaults to `newName`).
- `job` must match exactly one JobFolders row, and the folder is opened by that row's SharePoint item id. `folderLink` is only a cross-check: if it differs from the row's stored link, the results JSON gets a warning and the run goes on.
- `renames`: an in-place rename in `folderPath` (relative to the job, `""` for the root). Add `"in": "package"` to rename a file inside the package instead. The legacy photo folders can only be renumbered (`folderPath` `"Photos"`: `Job Walk` to `1 Job Walk`, `Progress` to `2 Progress`, `Completed` to `3 Completed`), and only when the numbered folder does not exist yet.
- `brief`: written as a Word file into the package root, replacing any earlier one. Empty sections are left out.
- Results statuses: `DONE`, `ALREADY DONE`, `DRY RUN`, `SKIP`, `NOT FOUND`, `ERROR`, `LEFT IN ROOT` (root files the manifest did not name).
- Always skipped: files over 100 MB (they never sync to JT), a move whose `newName` matches another file in the job root, any other rename of `Photos/Job Walk|Progress|Completed`, renaming package folders, folder names with `: * ? ' " < > | # % \ /`, and file names with `: * ? " < > | \ /`. `& $ , ( )` are fine in both.
- It never deletes anything and never calls JobTread. JT picks up moves and renames through SharePointSync (08:00 to 22:59 UTC).
- A manifest that errors (bad JSON, no or ambiguous JobFolders row, folder renamed or gone) is still moved to Applied with an `ERROR` row. Fix it and drop it in again; reruns report `ALREADY DONE` for finished work.
