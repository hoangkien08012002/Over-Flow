# OrderFlow

OrderFlow is a .NET 10 modular-monolith backend for reliable e-commerce order processing. This repository currently contains the Phase 1 solution foundation.

## Prerequisites

- .NET 10 SDK.
- Docker Desktop with the Linux engine, for local SQL Server, Redis, and Kafka.

## Restore, build, and test

```sh
dotnet restore OrderFlow.sln
dotnet build OrderFlow.sln --no-restore
dotnet test OrderFlow.sln --no-build
```

The solution contains `Domain`, `Application`, `Infrastructure`, `Api`, and `Worker`, plus unit and integration test projects. Domain is independent; Application references Domain; Infrastructure implements Application ports; API and Worker are composition roots. The Phase 1 architecture tests enforce the runtime project-reference graph.

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

The API and Worker currently only start their hosts. Business endpoints, database mappings, cache adapters, and Kafka handlers are implemented in later phases.
