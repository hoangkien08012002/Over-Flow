using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderFlow.Application.Catalog;
using OrderFlow.Application.Inventory;
using OrderFlow.Domain.Catalog;
using OrderFlow.Domain.Inventory;
using OrderFlow.Domain.Ordering;
using OrderFlow.Infrastructure.Identity;
using OrderFlow.Infrastructure.Persistence;
using StackExchange.Redis;
using Xunit;

namespace OrderFlow.IntegrationTests;

public sealed class CatalogCacheTests
{
    private const string Hits = "orderflow.cache.hits";
    private const string Misses = "orderflow.cache.misses";
    private const string Failures = "orderflow.cache.failures";
    private static readonly Guid ShippingMethodId = Guid.Parse("5b6e892f-486b-4471-9d84-4d33b13cbe01");
    private static readonly int RedisPort = int.Parse(Environment.GetEnvironmentVariable("ORDERFLOW_REDIS_PORT") ?? "6379");
    private static readonly string RedisEndpoint = $"127.0.0.1:{RedisPort}";

    [Fact]
    public Task AC_06_first_read_misses_second_hits_and_product_update_invalidates() => WithCatalogAsync(async (context, ct) =>
    {
        await using var factory = context.Factory(RedisEndpoint);
        using var metrics = new CacheMetrics(factory.Services);
        using var client = factory.CreateClient();
        var url = $"/api/v1/products/{context.ProductId}";

        Assert.Equal("Widget", (await ReadAsync(client, url, ct)).GetProperty("name").GetString());
        Assert.Equal(1, metrics.Count(Misses, "product"));
        Assert.Equal(0, metrics.Count(Hits, "product"));
        var ttl = await context.Redis.GetDatabase().KeyTimeToLiveAsync(context.Key(CatalogCache.ProductKey(context.ProductId)));
        Assert.InRange(ttl!.Value, CatalogCache.Product.MinTtl - TimeSpan.FromSeconds(5), CatalogCache.Product.MaxTtl);

        await context.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Products SET Name = 'Changed only in SQL' WHERE Id = {context.ProductId}", ct);
        await context.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Inventories SET OnHand = 7 WHERE ProductId = {context.ProductId}", ct);
        var cached = await ReadAsync(client, url, ct);
        Assert.Equal("Widget", cached.GetProperty("name").GetString());
        Assert.Equal(1, metrics.Count(Misses, "product"));
        Assert.Equal(1, metrics.Count(Hits, "product"));
        // FR-CAT-003 reports availability from SQL Server, including on a cache hit.
        Assert.Equal(7, cached.GetProperty("available").GetInt32());
        Assert.True(metrics.DurationRecorded);

        var freshVersion = await context.Db.Products.AsNoTracking().Where(x => x.Id == context.ProductId).Select(x => x.RowVersion).SingleAsync(ct);
        using var admin = await context.AdminClientAsync(factory, ct);
        var update = await admin.PutAsJsonAsync(url, ProductBody(context.CategoryId, "CACHE-1", "Renamed", Convert.ToBase64String(freshVersion)), ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("Renamed", (await ReadAsync(client, url, ct)).GetProperty("name").GetString());
        Assert.Equal(2, metrics.Count(Misses, "product"));

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{url}/activation", new { isActive = false }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(url, ct)).StatusCode);
        // Staff filled the shared entry above; it must not expose the inactive product publicly.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url, ct)).StatusCode);
    });

    [Fact]
    public Task AC_06_category_mutation_invalidates_category_list_and_its_products() => WithCatalogAsync(async (context, ct) =>
    {
        await using var factory = context.Factory(RedisEndpoint);
        using var metrics = new CacheMetrics(factory.Services);
        using var client = factory.CreateClient();
        using var admin = await context.AdminClientAsync(factory, ct);
        var productUrl = $"/api/v1/products/{context.ProductId}";
        var categoryUrl = $"/api/v1/categories/{context.CategoryId}";

        Assert.Single((await ReadAsync(client, "/api/v1/categories", ct)).EnumerateArray());
        await ReadAsync(client, "/api/v1/categories", ct);
        Assert.Equal(1, metrics.Count(Misses, "categories"));
        Assert.Equal(1, metrics.Count(Hits, "categories"));
        Assert.Equal("Cached", (await ReadAsync(client, productUrl, ct)).GetProperty("categoryName").GetString());

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(categoryUrl, new { name = "Renamed", slug = "cached", isActive = true }, ct)).StatusCode);
        Assert.Equal("Renamed", (await ReadAsync(client, "/api/v1/categories", ct)).EnumerateArray().Single().GetProperty("name").GetString());
        Assert.Equal("Renamed", (await ReadAsync(client, productUrl, ct)).GetProperty("categoryName").GetString());

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/categories", new { name = "Second", slug = "second", isActive = true }, ct)).StatusCode);
        Assert.Equal(2, (await ReadAsync(client, "/api/v1/categories", ct)).GetArrayLength());

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{categoryUrl}/activation", new { isActive = false }, ct)).StatusCode);
        Assert.Equal("Second", (await ReadAsync(client, "/api/v1/categories", ct)).EnumerateArray().Single().GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(productUrl, ct)).StatusCode);
        Assert.Equal(2, (await ReadAsync(admin, "/api/v1/categories?includeInactive=true", ct)).GetArrayLength());
    });

    [Fact]
    public Task Redis_endpoint_named_localhost_connects_to_an_ipv4_only_server() => WithCatalogAsync(async (context, ct) =>
    {
        await using var factory = context.Factory($"localhost:{RedisPort}");
        using var metrics = new CacheMetrics(factory.Services);
        using var client = factory.CreateClient();
        var url = $"/api/v1/products/{context.ProductId}";

        await ReadAsync(client, url, ct);
        await ReadAsync(client, url, ct);
        Assert.Equal(0, metrics.Count(Failures, "product"));
        Assert.Equal(1, metrics.Count(Hits, "product"));
    });

    [Fact]
    public Task AC_05_redis_outage_falls_back_to_sql_for_reads_and_mutations() => WithCatalogAsync(async (context, ct) =>
    {
        await using var factory = context.Factory($"127.0.0.1:{ClosedPort()}");
        using var metrics = new CacheMetrics(factory.Services);
        using var client = factory.CreateClient();
        using var admin = await context.AdminClientAsync(factory, ct);
        var url = $"/api/v1/products/{context.ProductId}";

        Assert.Equal("Widget", (await ReadAsync(client, url, ct)).GetProperty("name").GetString());
        Assert.Equal("Cached", (await ReadAsync(client, "/api/v1/categories", ct)).EnumerateArray().Single().GetProperty("name").GetString());
        Assert.True(metrics.Count(Failures, "product") > 0);
        Assert.True(metrics.Count(Failures, "categories") > 0);
        Assert.Equal(0, metrics.Count(Hits, "product"));

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(url, ProductBody(context.CategoryId, "CACHE-1", "Written during outage"), ct)).StatusCode);
        Assert.Equal("Written during outage", (await ReadAsync(client, url, ct)).GetProperty("name").GetString());
    });

    [Fact]
    public Task Failed_invalidation_is_retried_after_redis_recovers() => WithCatalogAsync(async (context, ct) =>
    {
        using var gate = new TcpGate(RedisPort);
        await using var factory = context.Factory($"127.0.0.1:{gate.Port}");
        using var client = factory.CreateClient();
        using var admin = await context.AdminClientAsync(factory, ct);
        var url = $"/api/v1/products/{context.ProductId}";
        var key = context.Key(CatalogCache.ProductKey(context.ProductId));
        await ReadAsync(client, url, ct);
        Assert.True(await context.Redis.GetDatabase().KeyExistsAsync(key));

        gate.Close();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(url, ProductBody(context.CategoryId, "CACHE-1", "After outage"), ct)).StatusCode);
        Assert.True(await context.Redis.GetDatabase().KeyExistsAsync(key), "The eviction must have failed for this scenario to hold.");
        Assert.Equal("After outage", (await ReadAsync(client, url, ct)).GetProperty("name").GetString());

        gate.Open();
        var deadline = Stopwatch.StartNew();
        while (await context.Redis.GetDatabase().KeyExistsAsync(key) && deadline.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(100, ct);
        Assert.False(await context.Redis.GetDatabase().KeyExistsAsync(key));
        Assert.Equal("After outage", (await ReadAsync(client, url, ct)).GetProperty("name").GetString());
    });

    [Fact]
    public Task Invalidation_during_a_load_prevents_storing_the_stale_result() => WithCatalogAsync(async (context, ct) =>
    {
        await using var factory = context.Factory(RedisEndpoint);
        var cache = factory.Services.GetRequiredService<IReadCache>();
        var key = $"race:{Guid.NewGuid()}:v1";
        var loading = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var staleRead = cache.GetOrLoadAsync(CatalogCache.Product, key, async _ =>
        {
            loading.SetResult();
            await release.Task;
            return "old";
        }, ct);
        await loading.Task;
        await cache.InvalidateAsync([key]);
        release.SetResult();

        Assert.Equal("old", await staleRead);
        Assert.False(await context.Redis.GetDatabase().KeyExistsAsync(context.Key(key)));
        Assert.Equal("new", await cache.GetOrLoadAsync(CatalogCache.Product, key, _ => Task.FromResult<string?>("new"), ct));
        Assert.Equal("new", await cache.GetOrLoadAsync(CatalogCache.Product, key, _ => Task.FromResult<string?>("unexpected load"), ct));
    });

    [Fact]
    public Task Concurrent_misses_on_one_key_load_once() => WithCatalogAsync(async (context, ct) =>
    {
        await using var factory = context.Factory(RedisEndpoint);
        var cache = factory.Services.GetRequiredService<IReadCache>();
        var key = $"hot:{Guid.NewGuid()}:v1";
        var loads = 0;
        var reads = Enumerable.Range(0, 50).Select(_ => Task.Run(() => cache.GetOrLoadAsync(CatalogCache.Product, key, async _ =>
        {
            Interlocked.Increment(ref loads);
            await Task.Delay(100, ct);
            return "value";
        }, ct), ct)).ToArray();

        Assert.All(await Task.WhenAll(reads), x => Assert.Equal("value", x));
        Assert.Equal(1, loads);
    });

    [Fact]
    public Task Corrupt_cache_entry_is_treated_as_a_miss_and_replaced() => WithCatalogAsync(async (context, ct) =>
    {
        await using var factory = context.Factory(RedisEndpoint);
        using var metrics = new CacheMetrics(factory.Services);
        using var client = factory.CreateClient();
        var key = context.Key(CatalogCache.ProductKey(context.ProductId));
        await context.Redis.GetDatabase().StringSetAsync(key, "{not json");

        Assert.Equal("Widget", (await ReadAsync(client, $"/api/v1/products/{context.ProductId}", ct)).GetProperty("name").GetString());
        Assert.True(metrics.Count(Failures, "product") > 0);
        Assert.Contains("Widget", (await context.Redis.GetDatabase().StringGetAsync(key)).ToString());
    });

    [Fact]
    public Task BR_cached_data_never_approves_inventory_or_checkout() => WithCatalogAsync(async (context, ct) =>
    {
        foreach (var service in new[] { typeof(InventoryService), typeof(CheckoutService) })
            Assert.DoesNotContain(service.GetConstructors().SelectMany(x => x.GetParameters()), x => x.ParameterType == typeof(IReadCache));

        await using var factory = context.Factory(RedisEndpoint);
        using var anonymous = factory.CreateClient();
        var url = $"/api/v1/products/{context.ProductId}";
        // SQL says the product is inactive; the poisoned entry says active at a price of 1.
        var version = await context.Db.Products.AsNoTracking().Where(x => x.Id == context.ProductId).Select(x => x.RowVersion).SingleAsync(ct);
        await context.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Products SET IsActive = 0 WHERE Id = {context.ProductId}", ct);
        var poison = new CachedProduct(context.ProductId, context.CategoryId, "Cached", true, "CACHE-1", "Poisoned", null, 1m, "VND", true, Convert.ToBase64String(version));
        await context.Redis.GetDatabase().StringSetAsync(context.Key(CatalogCache.ProductKey(context.ProductId)), JsonSerializer.Serialize(poison, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("Poisoned", (await ReadAsync(anonymous, url, ct)).GetProperty("name").GetString());

        using var customer = await context.CustomerClientAsync(factory, ct);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync("/api/v1/cart/items", new { productId = context.ProductId, quantity = 1 }, ct)).StatusCode);

        await context.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Products SET IsActive = 1 WHERE Id = {context.ProductId}", ct);
        await context.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Inventories SET OnHand = 5 WHERE ProductId = {context.ProductId}", ct);
        var add = await customer.PostAsJsonAsync("/api/v1/cart/items", new { productId = context.ProductId, quantity = 2 }, ct);
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        using var cart = JsonDocument.Parse(await add.Content.ReadAsStringAsync(ct));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new CheckoutRequest(cart.RootElement.GetProperty("id").GetGuid(), ShippingMethodId, "COD", null,
                new DeliveryAddress("Test Customer", "0900000000", "Line 1", "Ward", "District", "Province", "VN")))
        };
        request.Headers.Add("Idempotency-Key", "cache-poison-checkout");
        var order = await customer.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Accepted, order.StatusCode);
        using var body = JsonDocument.Parse(await order.Content.ReadAsStringAsync(ct));
        Assert.Equal(20.50m, body.RootElement.GetProperty("subtotal").GetDecimal());

        await using var scope = factory.Services.CreateAsyncScope();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
        var reserve = await inventory.ReserveAsync(body.RootElement.GetProperty("id").GetGuid(), context.ProductId, 6, ct);
        Assert.Equal(ReserveOutcome.InsufficientStock, reserve.Outcome);
    });

    // Sequential TestServer samples; see docs/CATALOG_PERFORMANCE.md. Reports timings without a brittle assertion.
    [Fact]
    public Task Phase7_catalog_cache_benchmark() => WithCatalogAsync(async (context, ct) =>
    {
        const int warmup = 5, samples = 30;
        var now = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).ToArray();
        for (var index = 0; index < ids.Length; index++)
        {
            context.Db.Products.Add(Product.Create(ids[index], context.CategoryId, $"BENCH-{index:0000}", $"Benchmark product {index:0000}", null, index + 1m, "VND", now));
            if (index % 2 == 0) context.Db.Inventories.Add(Inventory.Create(Guid.NewGuid(), ids[index], 5, now));
        }
        await context.Db.SaveChangesAsync(ct);

        await using (var factory = context.Factory(RedisEndpoint))
        {
            using var client = factory.CreateClient();
            // Warm the process and connection on products outside the measured sets.
            for (var i = 0; i < warmup; i++) await SampleAsync(client, $"/api/v1/products/{ids[500 + i]}", ct);
            var cold = new List<double>();
            for (var i = 0; i < samples; i++) cold.Add(await SampleAsync(client, $"/api/v1/products/{ids[i]}", ct));
            var warm = new List<double>();
            for (var i = 0; i < samples; i++) warm.Add(await SampleAsync(client, $"/api/v1/products/{ids[0]}", ct));
            Report("cold", cold);
            Report("warm", warm);
        }
        await using (var factory = context.Factory($"127.0.0.1:{ClosedPort()}"))
        {
            using var client = factory.CreateClient();
            for (var i = 0; i < warmup; i++) await SampleAsync(client, $"/api/v1/products/{ids[i]}", ct);
            var unavailable = new List<double>();
            for (var i = 0; i < samples; i++) unavailable.Add(await SampleAsync(client, $"/api/v1/products/{ids[0]}", ct));
            Report("redis-unavailable", unavailable);
        }

        static void Report(string scenario, List<double> values)
        {
            values.Sort();
            Console.WriteLine($"CACHE_BENCHMARK scenario={scenario} products=1000 samples={values.Count} p95_ms={values[(int)Math.Ceiling(values.Count * 0.95) - 1]:F1} median_ms={values[values.Count / 2]:F1} processors={Environment.ProcessorCount}");
        }
    });

    private static async Task<double> SampleAsync(HttpClient client, string url, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var response = await client.GetAsync(url, ct);
        watch.Stop();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return watch.Elapsed.TotalMilliseconds;
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.RootElement.Clone();
    }

    private static object ProductBody(Guid categoryId, string sku, string name, string? version = null) =>
        new { categoryId, sku, name, description = "A product", price = 10.25m, currencyCode = "VND", isActive = true, version };

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> RedisIsReachableAsync(CancellationToken ct)
    {
        using var client = new TcpClient();
        try { await client.ConnectAsync(IPAddress.Loopback, RedisPort, ct); return true; }
        catch (SocketException) { return false; }
    }

    private static async Task WithCatalogAsync(Func<CacheContext, CancellationToken, Task> test)
    {
        var ct = TestContext.Current.CancellationToken;
        var password = ReadPassword();
        if (string.IsNullOrWhiteSpace(password)) { Assert.Skip("SQL Server password is not configured."); return; }
        if (!await RedisIsReachableAsync(ct)) { Assert.Skip("Redis is not reachable."); return; }
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = $"127.0.0.1,{Environment.GetEnvironmentVariable("ORDERFLOW_SQL_PORT") ?? "1433"}",
            InitialCatalog = $"OrderFlow_Phase7_{Guid.NewGuid():N}", UserID = "sa", Password = password, TrustServerCertificate = true
        }.ConnectionString;
        await using var db = new OrderFlowDbContext(new DbContextOptionsBuilder<OrderFlowDbContext>().UseSqlServer(connection).Options);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(RedisEndpoint);
        var migrated = false;
        try
        {
            await db.Database.MigrateAsync(ct);
            migrated = true;
            var now = DateTimeOffset.UtcNow;
            var category = Category.Create(Guid.NewGuid(), "Cached", "cached", null, now);
            var product = Product.Create(Guid.NewGuid(), category.Id, "CACHE-1", "Widget", "A product", 10.25m, "VND", now);
            db.Categories.Add(category);
            db.Products.Add(product);
            db.Inventories.Add(Inventory.Create(Guid.NewGuid(), product.Id, 0, now));
            await db.SaveChangesAsync(ct);
            await test(new CacheContext(connection, $"test-{Guid.NewGuid():N}:", db, redis, category.Id, product.Id), ct);
        }
        finally { if (migrated) await db.Database.EnsureDeletedAsync(CancellationToken.None); }
    }

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

    private sealed record CacheContext(string Connection, string KeyPrefix, OrderFlowDbContext Db, ConnectionMultiplexer Redis, Guid CategoryId, Guid ProductId)
    {
        public string Key(string key) => KeyPrefix + key;
        public CacheFactory Factory(string redis) => new(Connection, redis, KeyPrefix);

        public async Task<HttpClient> AdminClientAsync(CacheFactory factory, CancellationToken ct)
        {
            var email = $"admin-{Guid.NewGuid():N}@example.invalid";
            await RegisterAsync(factory, email, ct);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                Assert.True((await manager.AddToRoleAsync((await manager.FindByEmailAsync(email))!, "Admin")).Succeeded);
            }
            return await LoginAsync(factory, email, ct);
        }

        public async Task<HttpClient> CustomerClientAsync(CacheFactory factory, CancellationToken ct)
        {
            var email = $"customer-{Guid.NewGuid():N}@example.invalid";
            await RegisterAsync(factory, email, ct);
            return await LoginAsync(factory, email, ct);
        }

        private static async Task RegisterAsync(CacheFactory factory, string email, CancellationToken ct)
        {
            using var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "StrongPassword123!", firstName = "Cache", lastName = "Test" }, ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        private static async Task<HttpClient> LoginAsync(CacheFactory factory, string email, CancellationToken ct)
        {
            var client = factory.CreateClient();
            var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "StrongPassword123!" }, ct);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync(ct));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("accessToken").GetString());
            return client;
        }
    }

    private sealed class CacheFactory(string connection, string redis, string keyPrefix) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OrderFlow"] = connection,
                ["Redis:ConnectionString"] = redis, ["Redis:KeyPrefix"] = keyPrefix,
                ["Jwt:Issuer"] = "orderflow-tests", ["Jwt:Audience"] = "orderflow-api",
                ["Jwt:SigningKey"] = "TEST_ONLY_orderflow_integration_signing_key_32_bytes_min",
                ["Jwt:AccessTokenMinutes"] = "15", ["Jwt:RefreshTokenDays"] = "30"
            }));
        }
    }

    // Counts only this host's instruments, so that parallel test classes do not affect the totals.
    private sealed class CacheMetrics : IDisposable
    {
        private readonly ConcurrentDictionary<string, long> counts = new();
        private readonly MeterListener listener = new();
        public bool DurationRecorded { get; private set; }

        public CacheMetrics(IServiceProvider services)
        {
            var meterFactory = services.GetRequiredService<IMeterFactory>();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "OrderFlow.Cache" && ReferenceEquals(instrument.Meter.Scope, meterFactory)) meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "cache") counts.AddOrUpdate($"{instrument.Name}|{tag.Value}", value, (_, current) => current + value);
            });
            listener.SetMeasurementEventCallback<double>((_, _, _, _) => DurationRecorded = true);
            listener.Start();
            // The cache singleton creates its meter on first resolution.
            services.GetRequiredService<IReadCache>();
        }

        public long Count(string name, string cache) => counts.GetValueOrDefault($"{name}|{cache}");
        public void Dispose() => listener.Dispose();
    }

    // A loopback forwarder that simulates a Redis outage and recovery for one client without touching the shared server.
    private sealed class TcpGate : IDisposable
    {
        private readonly int targetPort;
        private readonly ConcurrentBag<TcpClient> clients = [];
        private TcpListener? listener;
        public int Port { get; }

        public TcpGate(int targetPort)
        {
            this.targetPort = targetPort;
            Port = ClosedPort();
            Open();
        }

        public void Open()
        {
            var current = new TcpListener(IPAddress.Loopback, Port);
            current.Start();
            listener = current;
            _ = AcceptAsync(current);
        }

        public void Close()
        {
            listener?.Stop();
            listener = null;
            while (clients.TryTake(out var client)) client.Dispose();
        }

        public void Dispose() => Close();

        private async Task AcceptAsync(TcpListener current)
        {
            try
            {
                while (true)
                {
                    var inbound = await current.AcceptTcpClientAsync();
                    var outbound = new TcpClient();
                    await outbound.ConnectAsync(IPAddress.Loopback, targetPort);
                    clients.Add(inbound);
                    clients.Add(outbound);
                    _ = PumpAsync(inbound, outbound);
                    _ = PumpAsync(outbound, inbound);
                }
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException) { }
        }

        private static async Task PumpAsync(TcpClient from, TcpClient to)
        {
            try { await from.GetStream().CopyToAsync(to.GetStream()); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
            finally { from.Dispose(); to.Dispose(); }
        }
    }
}
