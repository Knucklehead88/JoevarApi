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
  confirmation), `Message` (SMS log), `ContractorApplication` (the website's contractor registration: email, full name, company, `Professions` (ProjectType[] stored as text[] of names), `State`, `City`, `Licenses` (text[]); public `POST /api/contractor-applications`, shares the contact rate limit; state must be one of the 50 in `UsCityDirectory.States` (no DC), city must be in that state per countriesnow.space (`UsCityDirectory`, typed HttpClient, cached a day per state; accepted as typed when the service is down), licenses one of `ContractorApplicationEndpoints.Licenses`; saved, emailed to the office and acknowledged to the applicant; no endpoint to read them yet), `KitchenBooking` (Kitchen Design page's "Schedule your free blueprint": name, email, E.164 phone, `AppointmentDate` (date), `AppointmentTime` ("09:00"…, slots in `KitchenBookingEndpoints.TimeSlots`), `Selections` (text[] of "Category: Option"), notes; public `POST /api/kitchen-bookings`, date at least 5 days (`LeadDays`) after Chicago's today, shares the contact rate limit, saved then emailed to the office and a "we received your request" copy with the selections to the customer via `OfficeEmailSender.SendToCustomerAsync`, Reply-To the office), `ContactMessage` (website Contact form, via the public
  `POST /api/contact`, rate limited to 5 per hour per IP; saved, then emailed by `OfficeEmailSender`; no
  endpoint to read them yet), `AppUser`
  (optionally linked to a customer/contractor).
- `ProjectType` is shared by project types and contractor trades. Enums are stored as text and lists as
  `text[]`.

## Auth

- **Staff** (roles Admin, Manager, VA): username + password via Identity's `/api/auth/login`, plus an
  authenticator app (Microsoft Authenticator). The `Staff` policy requires the role **and** the
  `two_factor_enabled` claim, added by `AppUserClaimsPrincipalFactory` so it survives token refresh.
- **Customers/contractors**: code texted to the phone on file via Twilio Verify (`/api/auth/phone/*`).
  They must give both their email and phone number. If both belong to the same customer or contractor,
  they sign in. If neither is on file, a new customer signs up (created after the code is verified, with
  the email as a placeholder name; contractors are still added by staff). If only one is on file, or they
  belong to different people, start/verify return 409 `EmailPhoneMismatch` and no code is sent. Their login
  is created on first use, with `UserName` = E.164 phone, and linked to the matching customer/contractor;
  each sign-in saves the email they used on the login (`/me` returns it as `email`). They can also
  use passkeys (`/api/auth/passkey/*`; the challenge is in a cookie, so CORS allows credentials) and Sign in
  with Apple (`/api/auth/apple`, linked once after a phone login).
- **Sign in / sign up with Google** (`Auth/GoogleLogin.cs`, `POST /api/auth/google` with `{ accessToken }`
  from the frontend's GIS token client, `Google:ClientId`). `GoogleTokenValidator` (typed HttpClient) checks
  the token with Google: `tokeninfo` must show our client ID as `aud`, `userinfo` gives sub/name/email and
  the email must be verified. **One account per person**, matched in order: the login linked to that Google
  `sub`; the single login whose `NormalizedEmail` matches; the customer/contractor with that email (their
  existing login, or a new `google-{sub}` login linked to them); otherwise a new Customer (Google name, email,
  no phone) and login. Each new match is linked (provider "Google"). Several matches: 409
  `GoogleEmailAmbiguous`.
- Phone sign-in reuses a customer's/contractor's existing login (e.g. created by Google) before creating one
  named after the phone. If the email belongs to a Google-linked login and the phone isn't on file with it,
  start/verify return 409 `EmailUsesGoogle`: a texted code proves the phone, not the email, so a new phone is
  never attached to someone's Google account (prevents takeover by typing their email).
- Note the email saved on a phone login is whatever was typed at sign-in, not verified by us.
- `/api/auth/register` is blocked (customers sign up through the phone login above). `GET /api/auth/me` returns roles, links and
  sign-in methods. Public login endpoints are rate limited (`AuthRateLimits`).
- Tokens are Identity's opaque bearer tokens (not JWTs).

## Integrations

- **Email** (`Common/OfficeEmailSender.cs`, MailKit): Contact form messages, kitchen bookings and contractor registrations go out through Gmail SMTP
  (`smtp.gmail.com:587`, STARTTLS) logged in to a Google Workspace mailbox with an **app password**. Settings
  `Email:Username`/`Password`/`From`/`To` (+ `Host`, `Port`, `FromName`), user secrets locally. Saved first,
  then sent with the visitor as Reply-To; a send failure or missing settings is only logged (the visitor
  still gets 204). `System.Security.Cryptography.Pkcs` is referenced directly to override the vulnerable
  10.0.0 that MailKit pulls in.
- **Google**: `Google:ClientId` (user secret locally) = the web client ID from the Google Cloud project; see `Auth/GoogleLogin.cs`.
- **Twilio**: inbound SMS webhook `/api/sms/inbound` (signature-validated), opt-in `/api/sms/opt-in`,
  Verify for login codes. US A2P 10DLC registration was rejected once; never message numbers without
  consent (TCPA / carrier rules). Bulk contractor texting must be opt-in only.
- **monday.com** (`MondayClient.cs`): customer boards = any board without "Ticket" in its name that has a
  column titled "Phone". Inbound texts are posted as `@Handle message` replies. Updates starting with
  `@Handle` are texted to the customer (`/api/monday/webhook`, `?token=` secret). The API token's user
  must own a board to register webhooks there; otherwise add the webhook in monday by hand.

## Known gaps / next steps

- Azure deployment (GitHub Actions, App Service, Postgres Flexible Server, forwarded client IPs for rate
  limiting, shared parent domain for frontend + API so passkeys work).
- A staff endpoint to list Contact form messages (they're emailed and saved, but not readable in the app).
- Staff management endpoints (for now: `create-admin` per person); customer/contractor portal endpoints.
- Sync between the database and monday.com (the database becomes the system of record).
- Apple sign-in doesn't check a nonce yet. Changing a customer's phone doesn't revoke existing
  passkeys/Apple links.
