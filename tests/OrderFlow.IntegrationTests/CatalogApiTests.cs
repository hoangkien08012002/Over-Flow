using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderFlow.Infrastructure.Identity;
using OrderFlow.Infrastructure.Persistence;
using OrderFlow.Domain.Catalog;
using OrderFlow.Domain.Inventory;
using Xunit;

namespace OrderFlow.IntegrationTests;

public sealed class CatalogApiTests
{
    [Fact]
    public async Task FR_CAT_001_002_003_004_catalog_admin_public_validation_and_concurrency()
    {
        var password = ReadPassword();
        if (string.IsNullOrWhiteSpace(password)) { Assert.Skip("SQL Server password is not configured."); return; }
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = $"127.0.0.1,{Environment.GetEnvironmentVariable("ORDERFLOW_SQL_PORT") ?? "1433"}",
            InitialCatalog = $"OrderFlow_Phase4_{Guid.NewGuid():N}", UserID = "sa", Password = password, TrustServerCertificate = true
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<OrderFlowDbContext>().UseSqlServer(connection).Options;
        await using var db = new OrderFlowDbContext(options);
        var migrated = false;
        try
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            migrated = true;
            await using var factory = new CatalogFactory(connection);
            using var publicClient = factory.CreateClient();
            using var adminClient = factory.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await publicClient.PostAsJsonAsync("/api/v1/categories", new { name = "Hardware", slug = "hardware", isActive = true }, TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await publicClient.PostAsJsonAsync("/api/v1/products", new { }, TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await publicClient.PostAsJsonAsync("/api/v1/auth/register", new { email = "catalog-admin@example.com", password = "StrongPassword123!", firstName = "Catalog", lastName = "Admin" }, TestContext.Current.CancellationToken)).StatusCode);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await manager.FindByEmailAsync("catalog-admin@example.com");
                Assert.True((await manager.AddToRoleAsync(user!, "Admin")).Succeeded);
            }
            var login = await adminClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "catalog-admin@example.com", password = "StrongPassword123!" }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using (var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
                adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("accessToken").GetString());

            var category = await adminClient.PostAsJsonAsync("/api/v1/categories", new { name = "Hardware", slug = "hardware", isActive = true }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, category.StatusCode);
            var categoryId = (await category.Content.ReadFromJsonAsync<CategoryDto>(TestContext.Current.CancellationToken))!.Id;
            Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PostAsJsonAsync("/api/v1/categories", new { name = "Other", slug = "hardware", isActive = true }, TestContext.Current.CancellationToken)).StatusCode);
            var missingCategory = await adminClient.PostAsJsonAsync("/api/v1/products", Product(Guid.NewGuid(), "P-MISSING"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, missingCategory.StatusCode);
            var product = await adminClient.PostAsJsonAsync("/api/v1/products", Product(categoryId, "P-001"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, product.StatusCode);
            var created = (await product.Content.ReadFromJsonAsync<ProductDto>(TestContext.Current.CancellationToken))!;
            Assert.Equal(0, created.Available);
            Assert.Equal(HttpStatusCode.Conflict, (await adminClient.PostAsJsonAsync("/api/v1/products", Product(categoryId, "P-001"), TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await publicClient.GetAsync("/api/v1/products?sort=unknown", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await publicClient.GetAsync("/api/v1/products?minPrice=20&maxPrice=10", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await publicClient.GetAsync("/api/v1/products?pageSize=101", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Single((await publicClient.GetFromJsonAsync<PageDto>("/api/v1/products?pageSize=1&search=Widget", TestContext.Current.CancellationToken))!.Items);
            Assert.Empty((await publicClient.GetFromJsonAsync<PageDto>("/api/v1/products?isAvailable=true", TestContext.Current.CancellationToken))!.Items);

            var update = await adminClient.PutAsJsonAsync($"/api/v1/products/{created.Id}", Product(categoryId, "P-001", "Updated", created.Version), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            var stale = await adminClient.PutAsJsonAsync($"/api/v1/products/{created.Id}", Product(categoryId, "P-001", "Stale", created.Version), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Contains("CONCURRENCY_CONFLICT", await stale.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var updated = (await update.Content.ReadFromJsonAsync<ProductDto>(TestContext.Current.CancellationToken))!;
            Assert.Equal(HttpStatusCode.OK, (await adminClient.PutAsJsonAsync($"/api/v1/products/{created.Id}/activation", new { isActive = false, version = updated.Version }, TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await publicClient.GetAsync($"/api/v1/products/{created.Id}", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync($"/api/v1/products/{created.Id}", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Empty((await publicClient.GetFromJsonAsync<PageDto>("/api/v1/products", TestContext.Current.CancellationToken))!.Items);
            Assert.Single((await adminClient.GetFromJsonAsync<PageDto>("/api/v1/products?includeInactive=true", TestContext.Current.CancellationToken))!.Items);

            var raceCategory = await adminClient.PostAsJsonAsync("/api/v1/categories", new { name = "Race", slug = "race", isActive = true }, TestContext.Current.CancellationToken);
            var raceCategoryId = (await raceCategory.Content.ReadFromJsonAsync<CategoryDto>(TestContext.Current.CancellationToken))!.Id;
            await using (var deactivation = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Categories SET IsActive = 0 WHERE Id = {raceCategoryId}", TestContext.Current.CancellationToken);
                var competingCreate = adminClient.PostAsJsonAsync("/api/v1/products", Product(raceCategoryId, "P-RACE"), TestContext.Current.CancellationToken);
                await Task.Delay(200, TestContext.Current.CancellationToken);
                Assert.False(competingCreate.IsCompleted);
                await deactivation.CommitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.BadRequest, (await competingCreate).StatusCode);
            }
            Assert.False(await db.Products.AsNoTracking().AnyAsync(x => x.Sku == "P-RACE", TestContext.Current.CancellationToken));
        }
        finally { if (migrated) await db.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Phase4_catalog_query_performance_sample()
    {
        var password = ReadPassword();
        if (string.IsNullOrWhiteSpace(password)) { Assert.Skip("SQL Server password is not configured."); return; }
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = $"127.0.0.1,{Environment.GetEnvironmentVariable("ORDERFLOW_SQL_PORT") ?? "1433"}",
            InitialCatalog = $"OrderFlow_Phase4Perf_{Guid.NewGuid():N}", UserID = "sa", Password = password, TrustServerCertificate = true
        }.ConnectionString;
        await using var db = new OrderFlowDbContext(new DbContextOptionsBuilder<OrderFlowDbContext>().UseSqlServer(connection).Options);
        var migrated = false;
        try
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            migrated = true;
            var category = Category.Create(Guid.NewGuid(), "Benchmark", "benchmark", null, DateTimeOffset.UtcNow);
            db.Categories.Add(category);
            var actor = new ApplicationUser
            {
                Id = Guid.NewGuid(), UserName = "catalog-benchmark", NormalizedUserName = "CATALOG-BENCHMARK",
                Email = "catalog-benchmark@example.invalid", NormalizedEmail = "CATALOG-BENCHMARK@EXAMPLE.INVALID",
                FirstName = "Catalog", LastName = "Benchmark", CreatedAtUtc = DateTimeOffset.UtcNow
            };
            db.Users.Add(actor);
            var firstId = Guid.Empty;
            for (var index = 0; index < 1000; index++)
            {
                var product = OrderFlow.Domain.Catalog.Product.Create(Guid.NewGuid(), category.Id, $"BENCH-{index:0000}", $"Benchmark product {index:0000}", null, index + 1m, "VND", DateTimeOffset.UtcNow);
                if (index == 0) firstId = product.Id;
                db.Products.Add(product);
                if (index % 2 == 0)
                {
                    var inventory = Inventory.Create(Guid.NewGuid(), product.Id, 5, DateTimeOffset.UtcNow);
                    db.Inventories.Add(inventory);
                    db.InventoryAdjustments.Add(inventory.Adjust(50, "BENCHMARK_SEED", null, actor.Id, "phase4-benchmark", DateTimeOffset.UtcNow));
                }
            }
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await using var factory = new CatalogFactory(connection);
            using var client = factory.CreateClient();
            var routes = new[] { "/api/v1/products?search=Benchmark&page=1&pageSize=20&sort=price", $"/api/v1/products/{firstId}" };
            foreach (var route in routes)
            {
                for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(route, TestContext.Current.CancellationToken)).StatusCode);
                var samples = new List<double>();
                for (var i = 0; i < 30; i++)
                {
                    var watch = Stopwatch.StartNew();
                    using var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
                    watch.Stop();
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                }
                samples.Sort();
                Console.WriteLine($"CATALOG_BENCHMARK route={route} products=1000 warmup=5 samples=30 p95_ms={samples[28]:F1} median_ms={samples[15]:F1} processors={Environment.ProcessorCount}");
            }
        }
        finally { if (migrated) await db.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken); }
    }

    private static object Product(Guid categoryId, string sku, string name = "Widget", string? version = null) => new { categoryId, sku, name, description = "A product", price = 10.25m, currencyCode = "VND", isActive = true, version };
    private static string? ReadPassword()
    {
        var configured = Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var file = Path.Combine(directory.FullName, ".env");
            if (File.Exists(file)) return File.ReadLines(file).FirstOrDefault(x => x.StartsWith("MSSQL_SA_PASSWORD=", StringComparison.Ordinal))?.Split('=', 2)[1];
        }
        return null;
    }
    private sealed record CategoryDto(Guid Id);
    private sealed record ProductDto(Guid Id, int Available, string Version);
    private sealed record PageDto(ProductDto[] Items, int Page, int PageSize, int TotalCount, int TotalPages);
    private sealed class CatalogFactory(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OrderFlow"] = connection,
                ["Jwt:Issuer"] = "orderflow-tests", ["Jwt:Audience"] = "orderflow-api",
                ["Jwt:SigningKey"] = "TEST_ONLY_orderflow_integration_signing_key_32_bytes_min",
                ["Jwt:AccessTokenMinutes"] = "15", ["Jwt:RefreshTokenDays"] = "30"
            }));
        }
    }
}
