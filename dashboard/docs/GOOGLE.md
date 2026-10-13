# Google connection (Google Ads + Business Profile reviews)

The dashboard reads two things from the company's Google account, with **one** "Connect Google":

- **Google Ads**: spend, impressions, clicks, conversions (leads) and calls per campaign per month, on
  **Sales & Marketing > Ads & lead sources**. Google Ads spend also feeds the "Ads → revenue" table.
- **Google Business Profile**: star rating, review count and every review, on **Customers** (the "Google reviews" pill),
  with reviewers matched to customers by name.

It is **read only**: it never creates or changes ads, campaigns, budgets, the Business Profile, or review replies, and it
never writes to JobTread (a matched review does not change JobTread's "Left Review" field; update that by hand).

Setup takes about 20 minutes of clicking, plus waiting for Google to approve two requests (step 5 and step 6).
Do every step signed in as the Google account that owns the Ads account and the Business Profile.

## 1. Create a Google Cloud project

1. Go to https://console.cloud.google.com and sign in with that Google account.
2. Top bar > project picker > **New project**. Name: `Purple Dashboard`. Create, then make sure it's selected in the top bar.

## 2. Turn on the APIs

Left menu > **APIs & Services** > **Library**. Search for each of these and click **Enable**:

| API | Used for |
|---|---|
| **Google Ads API** | Ads spend, clicks, conversions |
| **My Business Account Management API** | Finding the Business Profile account |
| **My Business Business Information API** | Listing the business location(s) |
| **Google My Business API** | Reviews (the v4 API; it only shows up in the Library once step 6 is approved, so come back and enable it then) |

If a page later says an API "isn't turned on in the Google Cloud project", it's one of these.

## 3. OAuth consent screen

Left menu > **APIs & Services** > **OAuth consent screen** (newer consoles call it **Google Auth Platform** > Branding / Audience).

1. **User type**:
   - If the Google account is a **Google Workspace** account (a company domain managed in Google Workspace), choose **Internal**. Nothing else to do for publishing.
   - Otherwise (a Gmail address, or a Google account made with a non-Google email), choose **External**.
2. App name `Purple Dashboard`, support email = your address, developer contact = your address. Save.
3. **Scopes** (Data Access): add these three:
   - `https://www.googleapis.com/auth/adwords`
   - `https://www.googleapis.com/auth/business.manage`
   - `openid` and `.../auth/userinfo.email` (shown as "email")
4. **External only**:
   - **Test users** (Audience): add the owner's Google address.
   - Then **Publish app** (Audience > Publishing status > **In production**). While an External app is in "Testing",
     Google expires the connection every 7 days. You don't need Google's verification for this (it's only us): when
     connecting, Google shows "Google hasn't verified this app"; click **Advanced** > **Go to Purple Dashboard (unsafe)**.

## 4. OAuth client (the keys the dashboard uses)

1. **APIs & Services** > **Credentials** > **Create credentials** > **OAuth client ID**.
2. Application type: **Web application**. Name: `Purple Dashboard`.
3. **Authorized redirect URIs** > Add URI, exactly:
   `https://dashboard.purplepainting.net/connections/google/callback`
4. Create. Copy the **Client ID** and **Client secret** (needed in step 7).

## 5. Google Ads developer token

Google only gives API tokens to **manager** (MCC) accounts.

1. If you don't have one: go to https://ads.google.com/home/tools/manager-accounts/ and create a manager account with the
   same Google login, then link the Purple Painting Ads account to it (manager account > Accounts > + > Link existing account;
   accept the invitation inside the Purple Painting Ads account under Admin > Access and security > Managers).
2. In the **manager** account: **Admin** (or Tools) > **API Center**. Fill in the form and accept the terms. You get a
   **developer token** with **Test** access. Copy it (step 7).
3. On the same page, **Apply for Basic access**. Say it's an internal reporting dashboard for our own account, read only
   (reports only, no campaign management). Google usually answers within a few business days by email.
   Until Basic access is approved, the Connections page says the token "only has test access" and Ads won't load.
4. Note the manager account's id (top right, `123-456-7890`). Optional setting in step 7.

The developer token is tied to the Cloud project the first time it's used: use it only with the OAuth client from step 4.

## 6. Business Profile API access

New Cloud projects get **0** Business Profile API requests a minute until Google approves them.

1. Find the project number: Cloud console > **Dashboard** (or project settings) > **Project number**.
2. Fill in the request form: https://developers.google.com/my-business/content/prereqs (link "request access" /
   "Business Profile API contact form"), choose **Application for Basic API Access**, and give the project number, the
   business's website and the Business Profile. Use the same Google login that manages the profile.
3. Google answers by email (often 1-2 weeks). When approved, enable the **Google My Business API** (step 2 table).
   Until then the Connections page says "Google hasn't approved Business Profile API access for this project yet".

## 7. Settings in Azure

Azure portal > **PurpleDashboard** > Settings > **Environment variables** > App settings > **+ Add**:

| Name | Value |
|---|---|
| `GOOGLE_CLIENT_ID` | Client ID from step 4 |
| `GOOGLE_CLIENT_SECRET` | Client secret from step 4 |
| `GOOGLE_ADS_DEVELOPER_TOKEN` | Developer token from step 5 |
| `GOOGLE_ADS_LOGIN_CUSTOMER_ID` | Optional. The manager account id from step 5, digits only (e.g. `1234567890`). Only needed if the Ads account is reachable only through the manager; the dashboard normally works this out itself |

Optional: `GOOGLE_REDIRECT_URI` if the site address ever changes (must match step 4), and `Google:AdsChannel` (Azure name
`Google__AdsChannel`) if JobTread's "How bid came in" uses a different name than `Google Ads`.

**Apply**, then restart the app.

## 8. Connect

1. Dashboard > **Connections** (bottom of the menu) > Google card > **Connect Google**.
2. Sign in with the owner Google account and **tick every box** on Google's page (Google Ads and Business Profile).
3. Back on Connections, pick the **Google Ads account** and the **Business Profile location**, then press the "Use this…" buttons.
4. The checklist on the card shows anything still missing (developer token, Basic access, Business Profile approval).
   Ads and reviews each start working as soon as their part is approved; you don't need to reconnect.

You can connect before steps 5 and 6 are approved; come back and press **Check again** once Google emails you.

## How it works

- `Integrations/Google/GoogleClient.cs`: OAuth (authorization code, `access_type=offline`, `prompt=consent`, state cookie
  checked in `Pages/Connections/GoogleCallback`), Google Ads `googleAds:searchStream` (GAQL), Business Profile account and
  location lists, v4 reviews (paged). Access tokens (1 hour) stay in memory; the refresh token is stored encrypted
  (ASP.NET Data Protection, like QuickBooks) in the `GoogleConnections` table with the connected email, scopes,
  and the picked Ads account and location.
- **API version:** `GoogleClient.AdsApiVersion` (currently `v25`). Google retires each Ads API version about a year after release
  and emails developers first; bump the constant then (release notes: https://developers.google.com/google-ads/api/docs/release-notes).
- **Ads query:** campaign × month for the first day of the month 11 months ago through today: `metrics.cost_micros`,
  `impressions`, `clicks`, `conversions`, `phone_calls` (dropped automatically if Google rejects it). Leads = conversions;
  cost per lead = cost ÷ conversions.
- **No double counting:** in the Ads → revenue table, Google Ads spend comes from the API for the months it covers. A hand-entered
  row for the same month with a Google Ads channel name ("Google Ads", "Google", "AdWords") isn't counted and is marked
  "Not counted · API" under Spend entered. Other channels (Facebook, Google LSA, mailers…) stay manual.
- **Review matching** (`Modules/Customers/ReviewMatcher.cs`): names are normalized (case, accents, punctuation, "the",
  "family", "Mr."…); couples ("John & Mary Smith") match either person. A review matches only when exactly one customer fits:
  exact name, or close (same words in another order, middle name left out, or one typo in a name of 8+ letters). Reviewers
  with one name, anonymous reviewers and anything ambiguous are listed under "Not matched". A matched customer shows
  "Google review ★N" and no longer counts as "Review to request".
- **Caching:** Ads data until the next 5am (Pacific), reviews for 3 hours, account/location lists for 1 hour. **Refresh** on
  Sales & Marketing (ads) or Customers (reviews) clears them; "Check again" on Connections clears everything.

## Fixtures (local development)

With `ASPNETCORE_ENVIRONMENT=Development` and `Google__FixtureDirectory=/path`, the dashboard reads JSON files instead of
calling Google, and **Connect Google** connects straight away (needs a local database). Files (Google's own response shapes):

| File | Google call |
|---|---|
| `userinfo.json` | `{ "email": "…" }`, the connected account |
| `ads-listAccessibleCustomers.json` | `customers:listAccessibleCustomers` |
| `ads-customer-{id}.json` | searchStream `FROM customer` for each accessible id |
| `ads-customer_client-{managerId}.json` | searchStream `FROM customer_client` for manager accounts |
| `ads-campaign-months.json` | searchStream campaign × month report (`ads-campaign-months-nocalls.json` if phone calls are rejected) |
| `business-accounts.json` | Account Management `accounts` |
| `business-locations-{accountId}.json` | Business Information `accounts/{id}/locations` |
| `business-reviews.json`, `business-reviews-{pageToken}.json` | v4 reviews, first page and each next page |

A file whose top level is Google's error shape (`{"error": {"code": 429, …}}`) is treated as that error, so the
"not approved yet" messages can be tried locally.
