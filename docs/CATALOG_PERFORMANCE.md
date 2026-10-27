# Phase 4 catalog query measurement

Measured on 2026-10-22 with .NET 10, SQL Server 2022 in local Docker, and ASP.NET Core `TestServer` (in-process HTTP; no network hop). Host: Windows, Intel Core i5-9300H 2.40 GHz, 8 logical processors, 15.9 GiB RAM.

The `Phase4_catalog_query_performance_sample` integration test migrates an empty database and seeds 1,000 active products in one active category. Half the products have inventory records with 50 units on hand; the rest have no inventory record. It then makes five warm-up requests followed by 30 sequential measured requests per route. Time is measured around the complete `HttpClient.GetAsync` response. The p95 uses nearest rank (29th sorted sample of 30). No Redis cache is used.

| Route | Median | p95 |
| --- | ---: | ---: |
| `GET /api/v1/products?search=Benchmark&page=1&pageSize=20&sort=price` | 10.3 ms | 18.2 ms |
| `GET /api/v1/products/{id}` | 3.6 ms | 5.1 ms |

These figures establish a local query baseline for the phase 4 gate. They include EF Core, SQL Server, serialization, and TestServer overhead. They do not measure a deployed network path or concurrent load. The uncached detail result is below the SRS local target of 500 ms for this workload. Phase 7 will measure cached detail separately.

To repeat, start a local SQL Server with the repository Docker Compose configuration, set `ORDERFLOW_SQL_PORT` to its published port, then run:

```powershell
.\tests\OrderFlow.IntegrationTests\bin\Debug\net10.0\OrderFlow.IntegrationTests.exe -method OrderFlow.IntegrationTests.CatalogApiTests.Phase4_catalog_query_performance_sample -showLiveOutput -reporter verbose
```

Results vary with local Docker load and host activity. The test creates and deletes its own database and does not enforce a brittle timing assertion.
