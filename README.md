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

`functions/ApplyOrganizeManifests` runs every 15 minutes. It applies the job-folder organizer's decisions from JSON files in SharePoint `Claude/Nightly Organize/Manifests`, then moves each manifest to `Manifests/Applied` with `<name>.results.json` beside it. With no manifest it does nothing and writes nothing. Only one run works at a time (a lock row in the SyncState table, 10 minute expiry; a second run exits). Each manifest is claimed by moving it into `Applied/processing` first; a manifest that can't be claimed is skipped, and one left there by a run that ran out of time is finished by the next run. To turn it off, disable the function in the portal.

```json
{
  "dryRun": true,
  "jtJobId": "<JobTread job id>",
  "job": "<exact SP job folder name>",
  "folderLink": "<JT OneDrive Folder Link>",
  "package": "<Post Packages folder name, or empty / null>",
  "moves":   [ { "expectedName": "", "expectedSize": 0, "destPath": "Photos/2 Progress", "newName": "", "social": "", "socialName": "" } ],
  "renames": [ { "folderPath": "", "oldName": "", "newName": "", "in": "job" } ],
  "bulkMoves": [ { "match": "*.jpeg|*.jpg|*.heic|*.png", "destPath": "Photos/1 Job Walk" },
                 { "match": "*.pdf", "destPath": "Plans & Docs/Bid Documents" } ],
  "brief":   { "file": "Post Brief - JT ###.docx", "title": "", "sub": "", "blurb": "",
               "did": [], "team": [], "tags": [], "personal": [], "photos": [], "ideas": [], "captions": [], "check": [] }
}
```

- `dryRun`: only `false` changes anything. `true` or a missing flag runs every check and writes the results JSON, with status `DRY RUN` for what would happen.
- `moves`: `from`, `size` and `toFolder` are accepted for `expectedName`, `expectedSize` and `destPath`. Other fields (`jtJobNumber`, `spFolderItemId`, `batchDate`, `source`, `notes`, `jtFileId`) are ignored. The file must be in the job root with that exact name and byte size. `destPath` is a folder inside the job (created if missing). `newName` defaults to `expectedName` and must keep the extension. `social` is `Before and After`, `Post-Worthy` or `Our Crew`; the file is copied to `Post Packages/<package>/<social>/<socialName>` (`socialName` defaults to `newName`).
- `jtJobId` (required) picks the JobFolders row, and the folder is opened by that row's SharePoint item id. `job` (the expected folder name) and `folderLink` are only cross-checks: names drift (JT `#` becomes `-`, trailing periods are stripped, jobs get renamed), so a mismatch adds a warning to the results JSON and the run goes on.
- `renames`: an in-place rename in `folderPath` (relative to the job, `""` for the root). Add `"in": "package"` to rename a file inside the package instead. The legacy photo folders can only be renumbered (`folderPath` `"Photos"`: `Job Walk` to `1 Job Walk`, `Progress` to `2 Progress`, `Completed` to `3 Completed`), and only when the numbered folder does not exist yet.
- `bulkMoves`: runs after `moves` and `renames`, on files still in the job root that no explicit row named (explicit rows always win). The first rule whose `match` fits the file name (case-insensitive globs, `*` and `?`, separated by `|`) moves it to `destPath`, keeping its name. Skipped: files over 100 MB, zips, and a name already taken in the destination. One results row per file: `BULK MOVED`, `DRY RUN` or `SKIP`. Same dryRun, lock, claim and 750 ms pacing as moves.
- `brief`: written as a Word file into the package root, replacing any earlier one. Empty sections are left out.
- Results statuses: `DONE`, `ALREADY DONE`, `BULK MOVED`, `DRY RUN`, `SKIP`, `NOT FOUND`, `ERROR`, `LEFT IN ROOT` (root files the manifest did not name).
- Always skipped: files over 100 MB (they never sync to JT), a move whose `newName` matches another file in the job root, any other rename of `Photos/Job Walk|Progress|Completed`, renaming package folders, folder names with `: * ? ' " < > | # % \ /`, and file names with `: * ? " < > | \ /`. `& $ , ( )` are fine in both.
- It never deletes anything and never calls JobTread. JT picks up moves and renames through SharePointSync (08:00 to 22:59 UTC).
- A manifest that errors (bad JSON, no `jtJobId`, no JobFolders row for it, or the folder no longer exists) is still moved to Applied with an `ERROR` row. Fix it and drop it in again; reruns report `ALREADY DONE` for finished work.
