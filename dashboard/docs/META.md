# Facebook & Instagram ads (Meta Marketing API)

The dashboard reads Facebook and Instagram ad results (spend, impressions, reach, clicks, link clicks, leads) through
the Meta Marketing API and shows them on **Sales & Marketing > Ads & lead sources**. It never changes campaigns,
budgets or ads. It's one shared company connection: someone connects it once on **Connections**.

API version: `MetaOptions.GraphVersion` in `Integrations/Meta/MetaOptions.cs` (currently **v26.0**, released
July 29, 2026). Meta retires Marketing API versions on a schedule. When the
[changelog](https://developers.facebook.com/docs/graph-api/changelog) lists a newer version, change that one constant.

## One-time setup (Meta developer app)

Do this signed in to Facebook as the person who runs Purple Painting's ads. That person needs access to the ad account
in Meta Business Settings (business.facebook.com > Settings > Accounts > Ad accounts).

1. Go to https://developers.facebook.com, click **My Apps** > **Create app**. If asked, register as a developer first.
2. Use case: **Other**. App type: **Business**. Name it "Purple Dashboard", enter a contact email, and pick the
   Purple Painting business portfolio. Create the app.
3. In the app dashboard, under **Add products** (or **Use cases**), add:
   - **Marketing API**
   - **Facebook Login for Business**
4. **Facebook Login for Business > Settings**:
   - **Valid OAuth Redirect URIs**: exactly `https://dashboard.purplepainting.net/connections/meta/callback`
   - Leave **Client OAuth login** and **Web OAuth login** on. **Enforce HTTPS** on.
5. **App settings > Basic**:
   - **App domains**: `dashboard.purplepainting.net`
   - **Privacy Policy URL**: required to save the page. Any page on purplepainting.net with a privacy policy works.
   - **Category**: Business and pages.
   - Copy the **App ID**, then click **Show** next to **App secret** and copy it.
6. **App roles > Roles**: the person who will click Connect must be listed as an **Administrator** (the creator
   already is). Add anyone else who might reconnect later as Administrator or Developer.
7. **App Review > Permissions and features**: `ads_read` should show **Standard access** (Business apps get standard
   access automatically). Nothing to submit. See "Do we need App Review?" below.
8. In Azure: PurpleDashboard > Settings > Environment variables > App settings, add:
   - `META_APP_ID`: the App ID
   - `META_APP_SECRET`: the App secret
9. Apply and restart. On the dashboard open **Connections** > **Facebook & Instagram ads** > **Connect Facebook**,
   approve on Facebook, then pick the Purple Painting ad account in the card and click **Save**. If there's only one
   active ad account, it's picked for you.

Optional settings:
- `META_REDIRECT_URI`: only if the site address changes (must match step 4 exactly).
- `META_LOGIN_CONFIG_ID`: set this only if Facebook's dialog says the app must use a Login for Business configuration.
  Create one under **Facebook Login for Business > Configurations** (token type **User access token**, permission
  `ads_read`, the Purple Painting ad account as an asset) and put its configuration ID here. The dashboard then sends
  `config_id` instead of `scope=ads_read`.

### Do we need App Review?

No, as long as only people with a role on the app connect it and they read ad accounts they already have access to
(our own ad account). Meta's Marketing API authorization docs say an app that only manages your own ad account only
needs **standard access** to `ads_read`. Business apps get standard access automatically, and it works for anyone
with a role on the app (step 6). **Advanced access**, which needs App Review and Business Verification, is only needed
to read ad accounts of people who don't have a role on the app (for example clients' accounts). That doesn't apply here.

The app can stay in **Development** mode, because standard access works for people with an app role.
The Marketing API "development" access tier has low rate limits. That's fine: the dashboard makes three insights
calls a day.

If Meta shows error **#200 / #10 (permission)** or **#274**, the connected Facebook user doesn't have access to that ad
account. Add them in Business Settings > Accounts > Ad accounts (View performance is enough). In Development mode it
can also help to add the ad account under the app's **App settings > Advanced > Advertising accounts**.

`business_management` isn't requested: `/me/adaccounts` lists the ad accounts the user can see with `ads_read` alone.

## The 60-day token

Facebook Login gives a short-lived token, which the dashboard swaps for a **long-lived user token (about 60 days)**,
stored encrypted (ASP.NET Data Protection) in the `MetaConnections` table. Meta doesn't let a server renew this token
without the person signing in again. So the Connections card shows the expiry date, and when fewer than **10 days**
are left it says to **Reconnect**. The Sales page says so too. Reconnecting takes a minute and keeps the ad account
already picked. If the token expires, the Facebook/Instagram section shows a "Reconnect" message and the ads view falls
back to hand-entered spend.

(A later option that never expires: a Business Manager **system user** token with `ads_read`. That means another
setting and someone pasting a token, so it isn't built.)

## What the dashboard reads

All calls are `GET` (plus a report job if needed) with `appsecret_proof`:

- `/me?fields=id,name` and `/me/adaccounts` (picker on Connections, cached 10 minutes)
- `/{act_id}/insights` with `time_increment=monthly` and `time_range` = the 1st of the month 11 months ago through
  today, three times:
  - `level=account`: monthly totals (true unique reach per month)
  - `level=campaign`: per campaign per month
  - `level=account&breakdowns=publisher_platform`: Facebook vs Instagram (vs Audience Network / Messenger)
- Fields: `spend, impressions, reach, clicks, inline_link_clicks, actions`. Every page is followed (`paging.next`).
  If Meta answers "reduce the amount of data", the same request runs as an async report job (`POST /{act}/insights` >
  poll `/{report_run_id}` > read its rows). That only creates a report. It never changes the account.
- **Leads**: from `actions`. Meta's `lead` action is its total of all leads (instant forms + website pixel), so it's
  used when present. Otherwise `onsite_conversion.lead_grouped` + `offsite_conversion.fb_pixel_lead` are added.
  Leads are Meta's count, not JobTread jobs.
- Results are cached until the next **5am Pacific**. **↻ Refresh** in the section drops them. Connecting, disconnecting
  or picking another ad account also clears them.

## Ads → revenue

Meta spend is fed into the existing ads view as the channel **Facebook/Instagram**, next to hand-entered spend rows.
For every month the API covers (the last 12), Meta's number **replaces** a hand-entered row for the same channel and
month (Facebook/Instagram, Facebook, Instagram, Meta, Facebook Ads…). Both are never counted. The page notes which
manual rows weren't counted, and marks them "Meta API used" in the Spend entered table. Older months still use
hand-entered rows.

## Nextdoor

Nextdoor has no self-serve ads reporting API for small advertisers. Its Ads API is only open to approved partners.
Keep entering Nextdoor spend by hand each month (Enter monthly spend on the same tab).

## Local development

`Meta__FixtureDirectory=/path` (Development only) serves saved Graph responses instead of calling Meta:
`adaccounts.json`, `insights-account.json`, `insights-campaign.json`, `insights-platform.json` (each `{ "data": [...] }`
as Meta returns them). Without a saved connection in the database it pretends someone connected and picked the first
ad account. Connecting isn't possible in fixture mode.

## Code

- `Integrations/Meta/MetaOptions.cs`: settings, API version constant
- `Integrations/Meta/MetaClient.cs`: OAuth (code > long-lived token), ad accounts, insights (paging, async fallback,
  cache), fixtures
- `Data/MetaConnection.cs`: the saved connection (`MetaConnections` table, migration `MetaConnection`)
- `Pages/Connections/Meta.cshtml.cs` (connect, pick account, disconnect) and `MetaCallback.cshtml.cs`
  (`/connections/meta/callback`). Card: `ViewComponents/MetaConnectionViewComponent.cs` +
  `Pages/Shared/Components/MetaConnection/Default.cshtml`
- `Modules/Sales/MetaAdsService.cs`: report parsing, lead counting, merge with hand-entered spend.
  Page section: `Pages/Sales/_MetaAds.cshtml`, note: `_MetaSpendNote.cshtml`
