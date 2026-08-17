# Azure_Buddy

Chatbot that seamlessly integrates with Azure DevOps and helps manage Work Items and CRUDs related to them.

## What's in this repo

- [`N8N chatbot files/QA Azure Buddy (Jul 26 at 23_30_31).json`](N8N%20chatbot%20files/QA%20Azure%20Buddy%20%28Jul%2026%20at%2023_30_31%29.json) — an [n8n](https://n8n.io/) workflow export implementing the chatbot. It exposes a chat trigger backed by an AI agent (Google Gemini, with a local Ollama model as fallback) that can create, search, update, and link Azure DevOps work items via the Azure DevOps REST API.

## Setup

1. Import the workflow JSON into your n8n instance (Workflows → Import from File).
2. Configure the following credentials in n8n:
   - **Azure DevOps** (HTTP Basic Auth, used by the `httpRequest` nodes) — a Personal Access Token (PAT) with Work Items read/write scope.
   - **Google Gemini (PaLM) API** — for the primary chat model.
   - **Ollama** — optional, used as a local fallback chat model.
3. Set the following environment variables on the n8n instance:
   - `ADO_ORG` — your Azure DevOps organization name.
   - `ADO_PROJECT` — your Azure DevOps project name.
   - `ADO_API_VERSION` — Azure DevOps REST API version (e.g. `7.1`).
4. Activate the workflow and send a message to the chat trigger to interact with the bot.

## Notes

- No secrets are stored in the workflow export; credentials are referenced by n8n credential ID and must be configured separately in your own n8n instance.

## .NET Backend (`AzureBuddy.Api`)

`src/` contains a .NET 8 port of the n8n workflow above, extended with multi-user auth, per-user Azure
DevOps configuration, and persisted chat history backed by MySQL.

### Projects

| Project | Purpose |
|---|---|
| `AzureBuddy.Api` | ASP.NET Core Web API host - controllers, DI wiring, `Program.cs` |
| `AzureBuddy.Core` | All business logic: LLM providers, ADO client, intent routing/agent, auth, settings, chat history services |
| `AzureBuddy.Data` | EF Core `DbContext`, entities, migrations (MySQL) |
| `AzureBuddy.Tests` | Unit tests |

### Configuration

Copy `src/AzureBuddy.Api/appsettings.Example.json` to `appsettings.json` (permanently gitignored -
your real values never risk being committed) and fill in the placeholders below, or override via
environment variables / `dotnet user-secrets` locally:

| Key | What it is |
|---|---|
| `ConnectionStrings:DefaultConnection` | MySQL connection string, e.g. `server=localhost;port=3306;database=azurebuddy;user=azurebuddy;password=...` |
| `Jwt:SigningKey` | Random secret (32+ bytes) used to sign/verify JWT access tokens. Treat like a password - a leaked signing key lets anyone mint valid tokens for any user. Validated at startup (`[Required, MinLength(32)]` via `ValidateOnStart()`) so a missing/too-short key fails loudly immediately instead of the first time a token is minted. |
| `Jwt:AccessTokenMinutes` / `Jwt:RefreshTokenDays` | Token lifetimes |
| `Cors:AllowedOrigins` | JSON array of origins the browser is allowed to call this API from, e.g. `["https://app.example.com"]`. **Required outside Development** - the app fails fast at startup if this is missing in a non-Development environment, rather than silently falling back to allowing only `localhost:4200`. |
| `Admin:Emails` | JSON array of emails granted the `Admin` role on every startup, if a matching user already exists (register the account first, then restart the app). |
| `Identity:Password:*`, `Identity:Lockout:*` | Password complexity and lockout policy (see `Microsoft.AspNetCore.Identity.IdentityOptions` for all available keys) |
| `DataProtection:KeyPath` | Filesystem folder where the encryption keys protecting stored ADO PATs/LLM API keys are kept, used only when `Redis:Configuration` is blank. **Back this up** - losing it makes every stored PAT/API key permanently undecryptable (users would need to re-enter them). In a multi-instance deployment without Redis this must be a *shared* location (mounted volume), not local disk per instance - or better, configure Redis instead (see "Horizontal scaling" below). |
| `Ado:ApiVersion` | Azure DevOps REST API version (e.g. `7.1`) - the only ADO setting that's still global; org/project/PAT are per-user now (see below) |
| `Llm:Providers`, `Llm:Gemini:*`, `Llm:Ollama:*` | LLM provider fallback chain and per-provider settings (unchanged from the original n8n-ported design) |
| `Redis:Configuration` | StackExchange.Redis connection string, e.g. `localhost:6379`. Leave blank to run entirely in-memory/per-instance - the app works unmodified single-instance with zero Redis. Required to run more than one API replica (see "Horizontal scaling" below). |
| `Redis:InstanceName` | Key prefix for everything this app writes to Redis, so one Redis instance can safely be shared with other apps. Defaults to `azurebuddy:`. |
| `Redis:ChatWindowTtlHours` | How long a cached chat conversation window survives in Redis with no activity before falling back to re-reading recent history from MySQL. Defaults to `24`. |
| `Network:KnownProxies` | JSON array of trusted reverse-proxy/load-balancer IPs, e.g. `["10.0.0.5"]`. Empty by default, meaning `X-Forwarded-For` is ignored and the auth rate limiter partitions by the direct connection's own IP. Only add an entry here for a proxy you control - trusting the wrong one lets a client spoof their own rate-limit partition. |

### Database

Apply migrations before first run:

```bash
dotnet tool install --global dotnet-ef
dotnet ef database update --project src/AzureBuddy.Data --startup-project src/AzureBuddy.Api
```

### Running with Docker

```bash
cp .env.example .env   # fill in JWT_SIGNING_KEY at minimum
docker compose up -d --build
dotnet ef database update --project src/AzureBuddy.Data --startup-project src/AzureBuddy.Api \
  --connection "server=localhost;port=3307;database=azurebuddy;user=azurebuddy;password=azurebuddy"
```

API is then reachable at `http://localhost:8080`. `docker-compose.yml` wires up the API, a MySQL
container, a Redis container, and three named volumes - one for MySQL's data directory, one for
Redis's AOF persistence file, one for the Data Protection keys that encrypt stored ADO PATs/LLM API
keys (mounted so it survives `docker compose down`/container recreation; without it every restart
would generate fresh keys and every previously-stored PAT would become unreadable - though with Redis
configured, as it is by default in this compose file, keys live in Redis instead and this volume goes
unused). MySQL's host-published port defaults to 3307, not 3306, to avoid colliding with a MySQL
already running locally - override `MYSQL_PORT` in `.env` if that's also taken.

`Dockerfile` on its own (no compose) builds just the API image; see its comments for the full
`docker build`/`docker run` flow and why config is passed as environment variables rather than baked
into the image.

### Horizontal scaling

The app runs unmodified as a single instance with zero Redis configured - `Redis:Configuration` blank
is the default in `appsettings.Example.json`, and everything (chat history, Data Protection keys, LLM
settings hot-reload) falls back to today's in-memory/per-instance behavior. Scaling beyond one replica
requires Redis, because a per-process `ConcurrentDictionary`/local file system can't be shared across
processes: a second replica behind a load balancer would otherwise silently lose conversational
context, be unable to decrypt PATs the first replica encrypted, and never see an admin's saved LLM
settings change.

**Try it locally**: `docker compose --profile scale up -d --build` starts `mysql`, `redis`, `api`
(port 8080), and `api2` (port 8081) - two replicas of the same image, both pointed at the same MySQL
database and the same Redis instance. Start a chat session against `:8080`, send a follow-up message
against `:8081`, and the second reply is generated with full context from the first - the same
behavior `TwoReplicaChatContinuityTests` proves in the test suite. `api2` is behind a `scale` compose
profile (not started by a plain `docker compose up`) since a normal single-instance dev/demo setup
doesn't need it.

**Migrating an existing single-instance deployment to Redis**: setting `Redis:Configuration` on an
already-running deployment does NOT automatically move Data Protection keys already sitting on disk -
a fresh Redis-backed key ring starts empty, and every PAT/API key encrypted under the old filesystem
key ring becomes unreadable the moment the switch flips. Migrate the key ring first:

1. Take note of the filesystem path from `DataProtection:KeyPath` (defaults to `DataProtection-Keys`
   relative to the API's working directory).
2. With Redis reachable and before switching `Redis:Configuration` on for the live app, run
   `DataProtectionKeyImporter.ImportAsync(keyDirectoryPath, multiplexer, $"{instanceName}DataProtection-Keys")`
   (see `AzureBuddy.Core.Caching.DataProtectionKeyImporter`) - a small console/script invocation is
   enough, it's idempotent so re-running it after a partial failure is safe.
3. Set `Redis:Configuration` (and restart the app) once the import has run. Existing PATs/API keys now
   decrypt correctly, and every replica going forward shares this same key ring.

### Endpoints

**Auth** (`api/auth`, no token required):
- `POST /api/auth/register` — `{ email, password, displayName }` → tokens
- `POST /api/auth/login` — `{ email, password }` → tokens
- `POST /api/auth/refresh` — `{ refreshToken }` → new tokens (rotates the refresh token)
- `POST /api/auth/logout` — `{ refreshToken }` → revokes it

All other endpoints require `Authorization: Bearer <accessToken>`.

**ADO settings** (`api/settings/ado`, per authenticated user):
- `GET /api/settings/ado` — masked config (no raw PAT)
- `PUT /api/settings/ado` — `{ organizationUrl, defaultProject, personalAccessToken }`
- `DELETE /api/settings/ado`
- `POST /api/settings/ado/test-connection` — validates the stored PAT against ADO

**Chat history** (`api/chats`, per authenticated user):
- `GET /api/chats?page=&pageSize=` — paginated session list
- `GET /api/chats/{sessionId}` — full message history
- `POST /api/chats` — `{ title? }` → new session
- `POST /api/chats/{sessionId}/messages` — multipart form: `role`, `content`, `workItemId?`, `screenshot?` (file). Screenshots are forwarded straight to Azure DevOps as a work-item attachment and never written to disk here - only the resulting ADO URL is stored.
- `DELETE /api/chats/{sessionId}`

**Live chat** (`api/chat`, same conversational flow as the original n8n workflow, now authenticated and persisted):
- `POST /api/chat` — `{ sessionId?, message }` → `{ sessionId, reply }`. Omit `sessionId` to start a new session. Rate-limited per authenticated user (see `RateLimiterPolicies.Chat`) since every call makes at least one billed LLM request.

**Health** (no token required):
- `GET /health/live` — always 200 once the process is up; runs no dependency checks. For an
  orchestrator's liveness probe - failing it triggers a container restart, so it must never fail for a
  reason a restart can't fix.
- `GET /health/ready` — 200 only if MySQL (and Redis, when configured) are reachable. For readiness/load
  balancer checks - failing it should stop traffic to this replica without restarting it.

### Known shortcuts / deferred (v1)

- **Email verification / password reset** — not implemented. `IEmailSender` exists as an extension point (`NoOpEmailSender` is the only registered implementation today); wiring in a real provider (SendGrid, SMTP, etc.) later doesn't require touching `AuthController`.
- **SSO/OAuth login, role-based permissions** — out of scope per the task; single "authenticated user" role only.
- **Data Protection key storage** defaults to local filesystem, which only works for a single instance - see the `DataProtection:KeyPath` note above before deploying more than one instance.
