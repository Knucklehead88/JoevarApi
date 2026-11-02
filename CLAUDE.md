# joevarApi

Backend for Joevar, a renovation/construction business. ASP.NET Core Minimal APIs on **.NET 10**.

## Branches

- **`master`**: SMS + monday.com integration only. **Pushing to `master` auto-deploys to Render**
  (`render.yaml`). Twilio inbound SMS is logged on monday.com customer boards, and staff text customers by
  posting `@CustomerName message` updates on monday.
- **`backend-app`**: turns this into the full backend: PostgreSQL via EF Core, Identity auth, CRUD for
  customers/projects/contractors. It will deploy to **Azure** (App Service + Azure Database for PostgreSQL
  Flexible Server); there's no deploy pipeline yet. It still contains the SMS/monday code from `master`.
  `render.yaml` is removed here. No background-job framework (no Hangfire) for now.

The Vue 3 + Pinia + Vuetify frontend is a separate project, **joevarWeb** (sibling folder). Its `CLAUDE.md`
documents this API's contract for the frontend: **when endpoints or request/response shapes change, update
`joevarWeb/CLAUDE.md` too.**

## Working with the user

- **Let the user review changes before you commit.** Summarize what changed, then wait for an explicit
  "commit". Keep unrelated changes in separate commits.
- **Don't push without being asked.** Remember that `master` deploys to production.
- Ask before decisions that are the user's to make; otherwise pick sensible defaults and say so.
- If something can't be verified locally (no DB, no Twilio/Apple credentials), say what was and wasn't tested.

## Running locally (Windows or Linux)

Requires the .NET 10 SDK and Docker.

```bash
docker compose up -d db          # PostgreSQL 17 on localhost:5432 (joevar / joevar_dev)
dotnet tool restore              # installs dotnet-ef from dotnet-tools.json
dotnet ef database update        # apply migrations
dotnet run --launch-profile http # http://localhost:5282, docs at /scalar, OpenAPI at /openapi/v1.json
dotnet run -- create-admin <username> <password>   # one-time: first Admin user
```

`appsettings.Development.json` holds the local connection string, CORS origin (`http://localhost:5173`)
and passkey domain (`localhost`). Real secrets come from environment variables (`.env.example` lists them;
`docker compose` reads `.env`). Never commit secrets.

Locally, Twilio secrets live in **user secrets** (`UserSecretsId` 885f587e-11fc-4189-bdb2-e921fed59ee0,
the same on every machine): `dotnet user-secrets set "Twilio:AccountSid" …` (also `Twilio:AuthToken`,
`Twilio:VerifyServiceSid`). Each machine has its own database and secrets.

Both dev machines already use port 5432 (Windows: a local PostgreSQL 16 service; Linux: the snap
`postgresql`), so the DB runs as the container `joevar-db` on **5433** and every `dotnet` command gets
`ConnectionStrings__Default='Host=localhost;Port=5433;Database=joevar;Username=joevar;Password=joevar_dev'`.
The Linux checkout keeps an uncommitted edit of `appsettings.Development.json` to 5433; never commit it,
nor the untracked `.vscode/`.

On Linux (Ubuntu, folder `~/Projects/JoevarApi`) the SDK is Ubuntu's `dotnet-sdk-10.0` package
(`/usr/bin/dotnet`); plain `dotnet` works everywhere. (The `dotnet` snap used before was removed on
2026-10-21 after its SDK went missing; don't reinstall it.)

## Code conventions

- **Feature folders**, no namespaces (global namespace, like the existing code): `Auth/`, `Customers/`,
  `Projects/`, `Contractors/`, `Contact/`, `Kitchen/`, `ContractorApplications/`, `Messaging/`, `Users/`, `Common/`, `Data/`.
- Endpoints live in static classes with an extension method (`app.MapCustomerEndpoints()`), using
  `MapGroup`, `TypedResults` / `Results<...>` return types, `.WithName()` and `.WithSummary()`.
- Requests are `sealed record`s with DataAnnotations (validated by `AddValidation()`); responses are
  positional records. Enums are serialized as strings.
- Lists return `PagedResult<T>` via `ToPagedResultAsync` (`Common/Paging.cs`).
- Phone numbers are normalized to E.164 with `PhoneNumbers.Normalize` (10 digits = US). Known problem:
  a Romanian local number like `0755 576 000` becomes `+10755576000`; `+40 …` works. Ask whether
  customers are in Romania, the US or both before changing it.
- Keep comment density low: comment the *why*, not the obvious.
- Avoid type names that clash with dependencies (that's why the entity is `ContactPhone`, not
  `PhoneNumber`, which Twilio uses; and `AppRoles`, not `Roles`, which `IdentityDbContext` has).

## Data (EF Core + PostgreSQL)

- `Data/AppDbContext.cs` (Identity DB context, schema version 3 for passkeys). Migrations are in
  `Data/Migrations`. Add one with `dotnet ef migrations add <Name> --output-dir Data/Migrations`.
  Don't rewrite migrations that have been pushed; add a new one.
- Entities: `Customer` and `Contractor` (one `ContactPhone` each), `Project` (status, `Types` list,
  `StartsAt`/`EndsAt` timeline, description), `SmsConsent` (opt-in/out history as proof of consent: E.164
  `PhoneNumber`, optional link to a customer's/contractor's `ContactPhone`, wording in `Evidence` +
  `EvidenceVersion`, `SourceUrl`, `IpAddress`, `RecordedAt`; `POST /api/sms/opt-in` records one before texting the
  confirmation), `Message` (SMS log), `ContractorApplication` (the website's contractor registration: email, full name, company, `Professions` (ProjectType[] stored as text[] of names), `StreetAddress` and `ZipCode` (required, ZIP `12345` or `12345-6789`; rows from before 2026-10-29 have empty values), `State`, `City`, `Licenses` (text[]); public `POST /api/contractor-applications`, shares the contact rate limit; state must be one of the 50 in `UsCityDirectory.States` (no DC), city must be in that state per countriesnow.space (`UsCityDirectory`, typed HttpClient, cached a day per state; accepted as typed when the service is down), licenses one of `ContractorApplicationEndpoints.Licenses`; saved, emailed to the office and acknowledged to the applicant; no endpoint to read them yet), `KitchenBooking` (Kitchen Design page's "Schedule your free blueprint": name, email, E.164 phone, the visit's address (`StreetAddress`, `City`, `State`, `ZipCode`, required and checked like contractor registrations; empty on bookings before 2026-11-03; in both emails and the staff list), `AppointmentDate` (date), `AppointmentTime` ("09:00"…, slots in `KitchenBookingEndpoints.TimeSlots`), `Selections` (text[] of "Category: Option"), notes; public `POST /api/kitchen-bookings`, date at least 5 days (`LeadDays`) after Chicago's today, shares the contact rate limit, saved then emailed to the office and a "we received your request" copy with the selections to the customer via `OfficeEmailSender.SendToCustomerAsync`, Reply-To the office; `Status` Pending → Approved/Declined with `DecidedAt`/`DecidedBy` (user name), set by the staff endpoints in `Kitchen/KitchenBookingStaffEndpoints.cs`: `GET /api/kitchen-bookings` (status, from/to dates, paging), `POST .../{id}/approve` (optional new date/time; not in the past, LeadDays doesn't apply) and `.../{id}/decline` (optional message), both emailing the customer after saving; 409 when already decided), `ContactMessage` (website Contact form, via the public
  `POST /api/contact`, rate limited to 5 per hour per IP; saved, then emailed by `OfficeEmailSender`; no
  endpoint to read them yet), `AppUser`
  (optionally linked to a customer/contractor).
- `ProjectType` is shared by project types and contractor trades. Enums are stored as text and lists as
  `text[]`.

## Auth

- **Staff** (roles Admin, Manager, VA): username + password via Identity's `/api/auth/login`, plus an
  authenticator app (Microsoft Authenticator). The `Staff` policy requires the role **and** the
  `two_factor_enabled` claim, added by `AppUserClaimsPrincipalFactory` so it survives token refresh.
- **Customers/contractors**, any of these (the frontend's `/login`: Phone and Username tabs, then the providers):
  - **Phone** (`Auth/PhoneLoginEndpoints.cs`, `/api/auth/phone/start` + `/verify`, both `{ name, phone }` (+ `code`)):
    Twilio Verify texts a code; the code proves the phone, so it signs in whoever has that number on file (their
    existing login, e.g. created by Google, else a new one with `UserName` = E.164 phone). A number not on file signs
    up a new customer with that name (contractors are still added by staff). The name is otherwise ignored, except
    as the login's `DisplayName` when it has none. No email is asked for (until 2026-11-03 it was email + phone).
  - **Email + password** (`Auth/PasswordSignup.cs`): `POST /api/auth/signup` `{ email, password }` creates a
    Customer (name = the email until they change it) and a login (`UserName` = the email until they pick one in their
    profile; Identity's default password rules; an email already on any login/customer/contractor is a 400 field
    error) and signs in. `POST /api/auth/password/login` `{ email, password }` signs in the one login with that email
    and a password (Identity's lockout applies); staff accounts get 401 `RequiresTwoFactor` (they use `/api/auth/login`
    with their username + authenticator). The email isn't verified: it's saved unconfirmed on the login only, **not**
    on the customer.
  - **Google** (`Auth/GoogleLogin.cs`, `POST /api/auth/google` `{ accessToken }` from the frontend's GIS token
    client, `Google:ClientId`). `GoogleTokenValidator` (typed HttpClient) checks it with Google: `tokeninfo` must
    show our client ID as `aud`, `userinfo` gives sub/name/email, and the email must be verified.
  - **Microsoft** (`Auth/MicrosoftLogin.cs`, `POST /api/auth/microsoft` `{ idToken }`, `Microsoft:ClientId`):
    personal and work/school accounts. The frontend gets an ID token in a popup (auth code + PKCE, no library);
    `MicrosoftTokenValidator` checks the signature (keys from the `common` v2.0 metadata), `aud` = our client ID,
    lifetime, and that `iss` is the token's own tenant (`tid`). Only personal accounts' emails (tid
    `9188040d-…`) count as verified; a work/school account's email is set by its org's admin. No nonce check
    server-side (the frontend checks it). App registration "Joevar web" (client ID
    `ed5f075c-139f-4ffb-ab63-e799375efdd6`, object ID `f1ce24fa-…`, audience `AzureADandPersonalMicrosoftAccount`,
    created 2026-11-03 with `az ad app create` in the joevar83@outlook.com directory; SPA redirect URIs
    `/auth/microsoft.html` on localhost:5173, joevar.com, www.joevar.com and the azurestaticapps.net host).
  - Passkeys (`/api/auth/passkey/*`; the challenge is in a cookie, so CORS allows credentials) and Sign in with
    Apple (`/api/auth/apple`, linked once after another sign-in).
- **One account per person** for Google and Microsoft (`Auth/ExternalAccounts.cs`): the login already linked to
  that account (`sub`); else, only with a **verified** email: the single login with that email *and*
  `EmailConfirmed`, then the customer/contractor with that email (their login, or a new `google-{sub}` /
  `microsoft-{sub}` one); else a new Customer (name, the email only if verified) and login. Each new match is
  linked (provider "Google"/"Microsoft"). Several matches: 409 `GoogleEmailAmbiguous`/`MicrosoftEmailAmbiguous`.
  An unverified email that's already on file anywhere: 409 `MicrosoftEmailInUse`. Unverified emails never join an
  existing account and stay off customer records, so nobody can pre-register someone's email (password sign-up or
  a work account) and later receive their Google/Microsoft sign-in.
- Older phone logins still carry the email typed at sign-in back then (unverified, `EmailConfirmed` false).
- Identity's `/api/auth/register` is blocked. `GET /api/auth/me` returns roles, links and sign-in methods. Public
  login endpoints (phone, signup, Google, Microsoft, Apple, passkeys) are rate limited (`AuthRateLimits`, 10 per
  15 minutes per IP); Identity's `/login` relies on its lockout (5 failures).
- Tokens are Identity's opaque bearer tokens (not JWTs).

## Integrations

- **Email** (`Common/OfficeEmailSender.cs`, MailKit): Contact form messages, kitchen bookings and contractor registrations go out through Gmail SMTP
  (`smtp.gmail.com:587`, STARTTLS) logged in to a Google Workspace mailbox with an **app password**. Settings
  `Email:Username`/`Password`/`From`/`To` (+ `Host`, `Port`, `FromName`, default "Joevar"), user secrets locally.
  Every email is sent as plain text plus HTML: the logo on top (`Common/joevar-logo.png`, an embedded resource
  attached inline as `cid:`, rendered from the frontend's `JoevarLogo` at 2x) and the same text in a white card. Saved first,
  then sent with the visitor as Reply-To; a send failure or missing settings is only logged (the visitor
  still gets 204). `System.Security.Cryptography.Pkcs` is referenced directly to override the vulnerable
  10.0.0 that MailKit pulls in.
- **Google**: `Google:ClientId` (user secret locally) = the web client ID from the Google Cloud project; see `Auth/GoogleLogin.cs`.
- **Microsoft**: `Microsoft:ClientId` (user secret locally; App Service setting via `infra/deploy.sh`) = the app
  registration's client ID (see Auth above).
- **Twilio**: inbound SMS webhook `/api/sms/inbound` (signature-validated), opt-in `/api/sms/opt-in`,
  Verify for login codes. US A2P 10DLC registration was rejected once; never message numbers without
  consent (TCPA / carrier rules). Bulk contractor texting must be opt-in only.
- **monday.com** (`MondayClient.cs`): customer boards = any board without "Ticket" in its name that has a
  column titled "Phone". Inbound texts are posted as `@Handle message` replies. Updates starting with
  `@Handle` are texted to the customer (`/api/monday/webhook`, `?token=` secret). The API token's user
  must own a board to register webhooks there; otherwise add the webhook in monday by hand.

## Azure (production)

- `infra/main.bicep`, lowest tiers at the user's request: App Service Linux **F1 (free)** `joevar-api` (.NET 10,
  HTTPS only, forwarded headers on; sleeps after ~20 idle minutes so the first request is slow and live visitors reset,
  60 CPU minutes a day, no custom domain: switch `apiPlanSku` to `B1`, ~$13/month, for api.joevar.com) and
  PostgreSQL 17 Flexible Server **B1ms** `joevar-pg` (32 GB, auto-grow off, database `joevar`, 7-day backups,
  firewall: Azure services only) and Static Web App `joevar-web` (Free), all in Central US (Iowa). North Central US
  (Illinois) was the plan, but this trial subscription has 0 App Service quota there and in the other US regions
  tested (eastus, eastus2, southcentralus, westus2); `az deployment group validate` shows it before creating
  anything. Resource group `joevar-rg`. The subscription is a Free Trial (quota
  `FreeTrial_2014-09-01`, spending limit on): $0 while the database is in the free account's 12 months (750 h B1ms +
  32 GB), then ~$16/month. Upgrade it to Pay-As-You-Go within the trial's 30 days, or Azure disables everything. Subscription "Azure subscription 1" (Azure account
  joevar83@outlook.com) has a $50 monthly budget `joevar-monthly` that emails office@joevar.com at 80% of actual
  spend and when the month's forecast passes $50 (created with `az rest`, not in Bicep).
- Custom domains (2026-10-25): https://joevar.com and https://www.joevar.com on the Static Web App (Namecheap DNS:
  ALIAS @ and CNAME www to the azurestaticapps.net host; free managed certificates). The API keeps its azurewebsites.net
  address (F1 can't have a custom domain); CORS already allows joevar.com and www. `Passkeys__ServerDomain` is still
  the azurestaticapps.net host until passkeys get a shared parent domain (api.joevar.com needs B1).
- Created 2026-10-25: API https://joevar-api.azurewebsites.net, site https://calm-meadow-08a1f4310.3.azurestaticapps.net,
  database server `joevar-pg.postgres.database.azure.com`.
- `infra/deploy.sh` creates or updates them (re-runnable): copies the `Twilio:*`, `Email:*`, `Google:*`, `Microsoft:*` and `Push:*` user
  secrets into App Service settings through a private temp file (never printed), generates a new database password
  each run (the connection string follows), and with `gh` signed in sets the GitHub secrets/variables for both repos.
  Bicep replaces all app settings on each run, so add settings there or in the script, not only in the portal.
- `.github/workflows/deploy-api.yml`: push to `backend-app` → `dotnet publish` → App Service (publish profile secret
  `AZURE_API_PUBLISH_PROFILE`, variable `AZURE_API_APP_NAME`). Outside Development the API applies pending migrations
  at startup (one instance, so no race).
- Staff logins in Azure: set the App Service setting `Seed__StaffPassword`, restart, then remove it (or SSH in and
  run `dotnet joevarApi.dll create-admin <name> <password>` in `/home/site/wwwroot`).
- Passkeys need the site and API on one parent domain, and Apple sign-in a registered HTTPS domain: both wait for the
  custom domain (app.joevar.com + api.joevar.com or joevar.com). The default site URL is random
  (`*.azurestaticapps.net`); CORS and `Passkeys__ServerDomain` follow it.

## Known gaps / next steps

- Custom domain for the Azure deployment (shared parent domain for frontend + API so passkeys work).
- Live visitors (`Visitors/VisitorPresenceEndpoints.cs`): public `POST /api/visitors/ping` `{ visitorId, path }`
  (own rate limit, 120 per minute per IP; id 8-64 letters/digits/dashes; at most 10,000 tracked), staff
  `GET /api/visitors/live` → `{ total, pages }` for pings within 60 s. In memory only: resets on restart and is per
  instance (with several App Service instances, move it to the database or a cache).
- `Auth/StaffSeed.cs`: in Development, startup creates the Admins joe, cristi and silviu if missing, with the password
  from the `Seed:StaffPassword` user secret (skipped when it isn't set; existing users untouched).
- **Profile** (`Auth/ProfileEndpoints.cs`, signed-in user): `PUT /api/auth/me/profile` (name, email, and `userName` for
  non-staff: 3-50 letters/digits/`._-` starting with a letter, not `google-`/`microsoft-`, unique; omitted = unchanged), `PUT
  /api/auth/me/preferences` (`{ systemTheme?, darkTheme? }`, only the given ones change: `AppUser.SystemTheme`, follow
  the device, default true; `AppUser.DarkTheme`, the picked theme, default true; both in `/me`), `POST /api/auth/me/phone/start` + `/verify` (Twilio
  Verify code to the new number). Name and phone changes also go to the linked Customer/Contractor and ContactPhone;
  the email only while it's verified (`EmailConfirmed`; Google/Microsoft matching trusts those records). A linked
  login must keep an email, and one on file for someone else gets a 400
  field error. A changed phone detaches the old `SmsConsents` (`ContactPhoneId` = null; they keep their `PhoneNumber` as
  proof), so consent doesn't carry over to the new number. A login named after its phone is renamed with it.
- **Web push** (`Notifications/`, package `Lib.Net.Http.WebPush` 3.3.1, MIT, chosen over `WebPush` because it uses
  .NET's own crypto, not the old Portable.BouncyCastle): VAPID keys in `Push:PublicKey`/`Push:PrivateKey`/`Push:Subject`
  (user secrets; App Service settings via `infra/deploy.sh`; generated 2026-10-29, push is off without them).
  `WebPushSubscription` rows belong to a user (`UserId`) or a kitchen booking (`KitchenBookingId`), cascade-deleted with
  them. Staff alerts go only to users with a staff role and 2FA on; a booking's decision goes to its own device and to logins that proved
  its phone or email (`PhoneNumberConfirmed`/`EmailConfirmed`), never on an unproven match. A device on file twice gets one notification. `GET /api/push/public-key` (anyone),
  `PUT`/`DELETE /api/push/subscription` (any signed-in user; delete also removes that device's booking subscriptions), and `push` on `KitchenBookingRequest`. Endpoints must be https
  on fcm.googleapis.com, push.services.mozilla.com, push.apple.com or notify.windows.com (no posting to arbitrary
  URLs); an unusable one on a booking is ignored. `StaffAlertInterceptor` (an EF `SaveChangesInterceptor`) queues a
  staff alert whenever a `KitchenBooking`, `ContractorApplication` or `Customer` is added, from any code path (also
  staff-created customers); approve/decline queue the customer's ("confirmed", "rescheduled" when moved, "about your
  request" when declined). `PushQueue` (bounded channel) → `PushSender` (BackgroundService) sends, logs
  "delivered to N device(s)", and deletes subscriptions the push service answers 404/410 for. Tested against FCM with
  made-up endpoints and real keys (headless Firefox can't subscribe to Mozilla's push service).
- `GET /api/contractor-applications` (staff) lists registrations, newest first; there's no approve/convert-to-contractor
  step yet.
- Gotcha: `dotnet ef migrations add` gives a new non-null string column (like `KitchenBookings.Status`) a default of
  `""`, which breaks reading existing rows as an enum; set the migration's `defaultValue` (here `"Pending"`).
- A staff endpoint to list Contact form messages (they're emailed and saved, but not readable in the app).
- Staff management endpoints (for now: `create-admin` per person); customer/contractor portal endpoints.
- Sync between the database and monday.com (the database becomes the system of record).
- Apple sign-in doesn't check a nonce yet. Changing a customer's phone doesn't revoke existing
  passkeys/Apple links.
