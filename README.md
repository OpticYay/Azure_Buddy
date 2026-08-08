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
| `Jwt:SigningKey` | Random secret (32+ bytes) used to sign/verify JWT access tokens. Treat like a password - a leaked signing key lets anyone mint valid tokens for any user. |
| `Jwt:AccessTokenMinutes` / `Jwt:RefreshTokenDays` | Token lifetimes |
| `Identity:Password:*`, `Identity:Lockout:*` | Password complexity and lockout policy (see `Microsoft.AspNetCore.Identity.IdentityOptions` for all available keys) |
| `DataProtection:KeyPath` | Filesystem folder where the encryption keys protecting stored ADO PATs are kept. **Back this up** - losing it makes every stored PAT permanently undecryptable (users would need to re-enter them). In a multi-instance deployment this must be a *shared* location (mounted volume, or switch to `PersistKeysToDbContext`/Azure Blob storage), not local disk per instance. |
| `Ado:ApiVersion` | Azure DevOps REST API version (e.g. `7.1`) - the only ADO setting that's still global; org/project/PAT are per-user now (see below) |
| `Llm:Providers`, `Llm:Gemini:*`, `Llm:Ollama:*` | LLM provider fallback chain and per-provider settings (unchanged from the original n8n-ported design) |

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
container, and two named volumes - one for MySQL's data directory, one for the Data Protection keys
that encrypt stored ADO PATs (mounted so it survives `docker compose down`/container recreation;
without it every restart would generate fresh keys and every previously-stored PAT would become
unreadable). MySQL's host-published port defaults to 3307, not 3306, to avoid colliding with a MySQL
already running locally - override `MYSQL_PORT` in `.env` if that's also taken.

`Dockerfile` on its own (no compose) builds just the API image; see its comments for the full
`docker build`/`docker run` flow and why config is passed as environment variables rather than baked
into the image.

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

**Live chat** (`chat`, same conversational flow as the original n8n workflow, now authenticated and persisted):
- `POST /chat` — `{ sessionId?, message }` → `{ sessionId, reply }`. Omit `sessionId` to start a new session.

### Known shortcuts / deferred (v1)

- **Email verification / password reset** — not implemented. `IEmailSender` exists as an extension point (`NoOpEmailSender` is the only registered implementation today); wiring in a real provider (SendGrid, SMTP, etc.) later doesn't require touching `AuthController`.
- **SSO/OAuth login, role-based permissions** — out of scope per the task; single "authenticated user" role only.
- **Data Protection key storage** defaults to local filesystem, which only works for a single instance - see the `DataProtection:KeyPath` note above before deploying more than one instance.
