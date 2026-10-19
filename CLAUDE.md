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

## Code conventions

- **Feature folders**, no namespaces (global namespace, like the existing code): `Auth/`, `Customers/`,
  `Projects/`, `Contractors/`, `Messaging/`, `Users/`, `Common/`, `Data/`.
- Endpoints live in static classes with an extension method (`app.MapCustomerEndpoints()`), using
  `MapGroup`, `TypedResults` / `Results<...>` return types, `.WithName()` and `.WithSummary()`.
- Requests are `sealed record`s with DataAnnotations (validated by `AddValidation()`); responses are
  positional records. Enums are serialized as strings.
- Lists return `PagedResult<T>` via `ToPagedResultAsync` (`Common/Paging.cs`).
- Phone numbers are normalized to E.164 with `PhoneNumbers.Normalize` (10 digits = US).
- Keep comment density low: comment the *why*, not the obvious.
- Avoid type names that clash with dependencies (that's why the entity is `ContactPhone`, not
  `PhoneNumber`, which Twilio uses; and `AppRoles`, not `Roles`, which `IdentityDbContext` has).

## Data (EF Core + PostgreSQL)

- `Data/AppDbContext.cs` (Identity DB context, schema version 3 for passkeys). Migrations are in
  `Data/Migrations`. Add one with `dotnet ef migrations add <Name> --output-dir Data/Migrations`.
  Don't rewrite migrations that have been pushed; add a new one.
- Entities: `Customer` and `Contractor` (one `ContactPhone` each), `Project` (status, `Types` list,
  `StartsAt`/`EndsAt` timeline, description), `SmsConsent` (opt-in/out history per phone, as proof of
  consent), `Message` (SMS log), `AppUser` (optionally linked to a customer/contractor).
- `ProjectType` is shared by project types and contractor trades. Enums are stored as text and lists as
  `text[]`.

## Auth

- **Staff** (roles Admin, Manager, VA): username + password via Identity's `/api/auth/login`, plus an
  authenticator app (Microsoft Authenticator). The `Staff` policy requires the role **and** the
  `two_factor_enabled` claim, added by `AppUserClaimsPrincipalFactory` so it survives token refresh.
- **Customers/contractors**: phone code via Twilio Verify (`/api/auth/phone/*`). Their login is created on
  first use, with `UserName` = E.164 phone, and linked to the matching customer/contractor. They can also
  use passkeys (`/api/auth/passkey/*`; the challenge is in a cookie, so CORS allows credentials) and Sign in
  with Apple (`/api/auth/apple`, linked once after a phone login).
- `/api/auth/register` is blocked (no self-registration). `GET /api/auth/me` returns roles, links and
  sign-in methods. Public login endpoints are rate limited (`AuthRateLimits`).
- Tokens are Identity's opaque bearer tokens (not JWTs).

## Integrations

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
- Staff management endpoints; customer/contractor portal endpoints.
- Sync between the database and monday.com (the database becomes the system of record).
- Apple sign-in doesn't check a nonce yet. Changing a customer's phone doesn't revoke existing
  passkeys/Apple links.
