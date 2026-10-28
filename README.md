# OrderFlow

OrderFlow is a .NET 10 modular-monolith backend for reliable e-commerce order processing. Phases 1–5 cover solution foundation, persistence, identity, catalog, and inventory concurrency.

## Prerequisites

- Visual Studio 2026 with the ASP.NET and web development workload, or the .NET 10 SDK for command-line use.
- Docker Desktop with the Linux engine, for local SQL Server, Redis, and Kafka.

## Restore, build, and test

```sh
dotnet restore OrderFlow.sln
dotnet build OrderFlow.sln --no-restore
dotnet test --solution OrderFlow.sln --no-build
```

The solution contains `Domain`, `Application`, `Infrastructure`, `Api`, and `Worker`, plus unit and integration test projects. Domain is independent; Application references Domain; Infrastructure implements Application ports; API and Worker are composition roots. The Phase 1 architecture tests enforce the runtime project-reference graph.

In Visual Studio 2026, open `OrderFlow.sln`, restore NuGet packages, select `OrderFlow.Api` as the startup project, and run its `OrderFlow.Api` launch profile. Configure the SQL connection and JWT environment variables described below before starting the API. The Worker is a separate executable and can be selected as another startup project when needed.

Phase 2 adds Category, Product, Inventory, and immutable InventoryAdjustment domain behavior, an Identity-aware `OrderFlowDbContext`, SQL Server mappings, constraints, rowversion concurrency, and the initial migration. Phase 4 adds catalog endpoints. Phase 5 adds audited adjustment, SQL-backed reservations, low-stock outbox records, and an expiry job interface. Checkout and Kafka processing follow in later phases.

## Local dependencies

Copy `.env.example` to `.env`, then replace the SQL password placeholder with a unique local password. In PowerShell:

```powershell
Copy-Item .env.example .env
docker compose --env-file .env config
docker compose --env-file .env up -d
docker compose --env-file .env ps
docker compose --env-file .env down
```

From the host, SQL Server is at `localhost:1433`, Redis at `localhost:6379`, and Kafka at `localhost:9092` unless the ports are changed in `.env`. Containers can reach them at `sqlserver:1433`, `redis:6379`, and `kafka:19092`. Compose binds the exposed ports to loopback for local development. SQL Server and Redis use named volumes; Kafka data is ephemeral in this Phase 1 setup. Do not use the development password or plaintext Kafka listener in production.

Phase 3 provides registration, login, refresh, logout, and protected authorization policy checks. Cache adapters and Kafka handlers are implemented in later phases.

## Phase 3 authentication and Swagger

The API expects SQL Server and JWT settings from configuration or environment variables. No signing key is committed. For local PowerShell use, start SQL Server with the Compose instructions above, set `MSSQL_SA_PASSWORD` from your `.env`, then configure the API and migrate its schema:

Install or update the EF Core CLI tool to the EF Core 10 version used by the project with `dotnet tool update --global dotnet-ef --version 10.0.12` before running the migration command.

```powershell
$env:MSSQL_SA_PASSWORD = (Get-Content .env | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' } | Select-Object -First 1).Split('=', 2)[1]
$env:Jwt__Issuer = "OrderFlow.Local"
$env:Jwt__Audience = "OrderFlow.Api"
$env:Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$env:ORDERFLOW_SQL_CONNECTION = "Server=localhost,1433;Database=OrderFlow;User ID=sa;Password=$env:MSSQL_SA_PASSWORD;TrustServerCertificate=True;Encrypt=False"
$env:ConnectionStrings__OrderFlow = $env:ORDERFLOW_SQL_CONNECTION
dotnet ef database update --project src/OrderFlow.Infrastructure --startup-project src/OrderFlow.Api
dotnet run --project src/OrderFlow.Api
```

The signing key must contain at least 32 UTF-8 bytes. Production startup rejects missing issuer/audience/key and token lifetimes outside the supported range. Defaults are 15-minute access tokens and 30-day refresh tokens. Identity requires passwords of at least 12 characters with uppercase, lowercase, digit, and non-alphanumeric characters; five consecutive failed logins lock an account for 15 minutes. Set `Jwt__AccessTokenMinutes` and `Jwt__RefreshTokenDays` to override lifetimes.

Open `https://localhost:53374/swagger` or `http://localhost:53375/swagger` (the launch profile URLs; otherwise use the URL printed by `dotnet run`). In Swagger, use these steps:

1. Call `POST /api/v1/auth/register` with a unique email, strong password, first name, and last name.
2. Call `POST /api/v1/auth/login` with that email and password. Copy `accessToken` and `refreshToken` from the response.
3. Click **Authorize** and enter the access token. The Bearer scheme is configured in OpenAPI. Staff roles can access the catalog and inventory routes documented in `docs/API_CONTRACTS.md`.
4. Call `POST /api/v1/auth/refresh` with the current `refreshToken`; replace both copied tokens with the returned pair.
5. Call `POST /api/v1/auth/logout` with the refresh token to revoke it. Reusing a rotated refresh token revokes the active token family.

Registration assigns only the Customer role. Support, InventoryManager, and Admin are seeded roles for controlled staff provisioning; public registration cannot choose a role. API requests return `X-Correlation-Id`, including failures.

Phase 5 reservations expire after 15 minutes by default. Set `Inventory__ReservationMinutes` to a value from 1 to 1440 to change the timeout. The expiry job interface is available for the Worker; automatic scheduling is added in Phase 9. Inventory mutations use SQL Server and do not require Redis or Kafka.
