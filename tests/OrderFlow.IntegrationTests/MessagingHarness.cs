using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Messaging;
using OrderFlow.Domain.Catalog;
using OrderFlow.Domain.Inventory;
using OrderFlow.Domain.Ordering;
using OrderFlow.Infrastructure.Identity;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;
using OrderFlow.Worker;
using Xunit;

namespace OrderFlow.IntegrationTests;

// One isolated SQL database and Kafka topic prefix per test, driven through the real Worker composition.
internal sealed class MessagingHarness : IAsyncDisposable
{
    private static readonly Guid ShippingMethodId = Guid.Parse("5b6e892f-486b-4471-9d84-4d33b13cbe01");
    private readonly string topicPrefix = $"t{Guid.NewGuid():N}.";
    private readonly string kafkaBootstrap = $"127.0.0.1:{Environment.GetEnvironmentVariable("ORDERFLOW_KAFKA_PORT") ?? "9092"}";
    private bool kafkaUsed;

    private MessagingHarness(string connection, DbContextOptions<OrderFlowDbContext> options)
    {
        Connection = connection;
        Options = options;
    }

    public string Connection { get; }
    public DbContextOptions<OrderFlowDbContext> Options { get; }
    public Guid UserId { get; private set; }
    public Guid CategoryId { get; private set; }
    public void ActAs(Guid userId) => UserId = userId;
    public string KafkaBootstrap => kafkaBootstrap;
    public string Topic(string name) => topicPrefix + name;
    public OrderFlowDbContext NewDb() => new(Options);
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<MessagingHarness?> CreateAsync(bool requireKafka = false)
    {
        var password = Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, ".env");
                if (File.Exists(path)) { password = File.ReadLines(path).FirstOrDefault(x => x.StartsWith("MSSQL_SA_PASSWORD=", StringComparison.Ordinal))?.Split('=', 2)[1]; break; }
            }
        }
        if (string.IsNullOrEmpty(password)) { Assert.Skip("SQL Server password is not configured."); return null; }
        var connection = new SqlConnectionStringBuilder { DataSource = $"127.0.0.1,{Environment.GetEnvironmentVariable("ORDERFLOW_SQL_PORT") ?? "1433"}",
            InitialCatalog = $"OrderFlow_Messaging_{Guid.NewGuid():N}", UserID = "sa", Password = password, TrustServerCertificate = true }.ConnectionString;
        var harness = new MessagingHarness(connection, new DbContextOptionsBuilder<OrderFlowDbContext>().UseSqlServer(connection).Options);
        if (requireKafka && !harness.KafkaIsReachable()) { Assert.Skip("Kafka is not reachable."); return null; }
        await using var db = harness.NewDb();
        await db.Database.MigrateAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "messaging", NormalizedUserName = "MESSAGING",
            Email = "messaging@example.invalid", NormalizedEmail = "MESSAGING@EXAMPLE.INVALID", FirstName = "Message", LastName = "Tester", CreatedAtUtc = now };
        var category = Category.Create(Guid.NewGuid(), "Messaging", "messaging", null, now);
        db.Users.Add(user);
        db.Categories.Add(category);
        await db.SaveChangesAsync(Ct);
        harness.UserId = user.Id;
        harness.CategoryId = category.Id;
        return harness;
    }

    public async Task<Guid> SeedProductAsync(decimal price, int onHand)
    {
        await using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        var product = Product.Create(Guid.NewGuid(), CategoryId, $"M-{Guid.NewGuid():N}"[..20], "Messaging product", null, price, "VND", now);
        var inventory = Inventory.Create(Guid.NewGuid(), product.Id, 0, now);
        var adjustment = inventory.Adjust(onHand, "SEED", null, UserId, "messaging-seed", now);
        db.Products.Add(product);
        db.Inventories.Add(inventory);
        db.InventoryAdjustments.Add(adjustment);
        await db.SaveChangesAsync(Ct);
        return product.Id;
    }

    public async Task<Coupon> SeedCouponAsync(string code, int? totalLimit = 1, int? perUserLimit = 1)
    {
        await using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        var coupon = Coupon.Create(Guid.NewGuid(), code, CouponDiscountType.Fixed, 5m, 0m, now.AddDays(-1), now.AddDays(1), totalLimit, perUserLimit, now);
        db.Coupons.Add(coupon);
        await db.SaveChangesAsync(Ct);
        return coupon;
    }

    public async Task<(int UsageCount, int ActiveUsages, int ReleasedUsages)> CouponStateAsync(Guid couponId)
    {
        await using var db = NewDb();
        return ((await db.Coupons.AsNoTracking().SingleAsync(x => x.Id == couponId, Ct)).UsageCount,
            await db.CouponUsages.CountAsync(x => x.CouponId == couponId && x.ReleasedAtUtc == null, Ct),
            await db.CouponUsages.CountAsync(x => x.CouponId == couponId && x.ReleasedAtUtc != null, Ct));
    }

    public async Task<Guid> CheckoutAsync(Guid productId, int quantity, string paymentMethod, string correlationId = "messaging-test", string? couponCode = null)
    {
        await using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        var cart = await db.Carts.Include(x => x.Items).SingleOrDefaultAsync(x => x.UserId == UserId, Ct);
        if (cart is null) { cart = new Cart(UserId, now); db.Carts.Add(cart); }
        cart.Items.Add(new CartItem(cart.Id, productId, quantity, now));
        await db.SaveChangesAsync(Ct);
        var result = await NewCheckout(db).CheckoutAsync(UserId, $"key-{Guid.NewGuid():N}", new CheckoutRequest(cart.Id, ShippingMethodId, paymentMethod, couponCode,
            new DeliveryAddress("Buyer", "0900000000", "Line 1", "Ward", "District", "Province", "VN")), correlationId, Ct);
        Assert.Equal(202, result.StatusCode);
        return await db.Orders.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).Where(x => x.UserId == UserId).Select(x => x.Id).FirstAsync(Ct);
    }

    public async Task<Guid> CheckoutMultipleAsync(params (Guid ProductId, int Quantity)[] lines)
    {
        await using (var db = NewDb())
        {
            var now = DateTimeOffset.UtcNow;
            var cart = new Cart(UserId, now);
            db.Carts.Add(cart);
            foreach (var line in lines.Skip(1)) cart.Items.Add(new CartItem(cart.Id, line.ProductId, line.Quantity, now));
            await db.SaveChangesAsync(Ct);
        }
        return await CheckoutAsync(lines[0].ProductId, lines[0].Quantity, "CardSimulation");
    }

    public static CheckoutService NewCheckout(OrderFlowDbContext db) => new(db, TimeProvider.System,
        Microsoft.Extensions.Options.Options.Create(new OrderingOptions()), new InventoryService(db, TimeProvider.System, Microsoft.Extensions.Options.Options.Create(new InventoryOptions())));

    // A built but unstarted host gives the Worker's real services without its background loops.
    public IHost BuildWorker(Action<IServiceCollection>? services = null, Dictionary<string, string?>? settings = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = "Development" });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OrderFlow"] = Connection,
            ["Kafka:BootstrapServers"] = kafkaBootstrap,
            ["Kafka:TopicPrefix"] = topicPrefix,
            ["Kafka:ConsumerGroupPrefix"] = topicPrefix,
            ["Kafka:TopicPartitions"] = "1",
            ["Outbox:PollIntervalMilliseconds"] = "100",
            ["Outbox:BaseRetryMilliseconds"] = "100",
            ["Outbox:MaxRetrySeconds"] = "1",
            ["Consumers:MaxAttempts"] = "3",
            ["Consumers:BaseRetryMilliseconds"] = "10",
            ["Consumers:MaxRetryMilliseconds"] = "100",
            ["Consumers:ReplayPollSeconds"] = "1"
        });
        if (settings is not null) builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddOrderFlowWorker(builder.Configuration);
        services?.Invoke(builder.Services);
        return builder.Build();
    }

    public async Task<IHost> StartWorkerAsync(Action<IServiceCollection>? services = null, Dictionary<string, string?>? settings = null)
    {
        kafkaUsed = true;
        var host = BuildWorker(services, settings);
        await host.StartAsync(Ct);
        return host;
    }

    // Applies one committed outbox event to one consumer exactly as the Kafka runner does, with a fresh scope for each delivery.
    public static async Task<bool> DeliverAsync(IHost worker, string consumerName, EventEnvelope envelope)
    {
        var definition = EventConsumers.All.Single(x => x.Name == consumerName);
        await using var scope = worker.Services.CreateAsyncScope();
        var handler = (IEventHandler)scope.ServiceProvider.GetRequiredService(definition.HandlerType);
        return await scope.ServiceProvider.GetRequiredService<InboxProcessor>().ProcessAsync(definition.Name, handler, envelope, Ct);
    }

    public async Task<EventEnvelope> OutboxEventAsync(Guid aggregateId, string eventType)
    {
        await using var db = NewDb();
        var row = await db.OutboxMessages.AsNoTracking().OrderByDescending(x => x.OccurredAtUtc).FirstAsync(x => x.AggregateId == aggregateId && x.EventType == eventType, Ct);
        return EventSerializer.Parse(row.Payload);
    }

    public async Task<T> WaitForAsync<T>(Func<OrderFlowDbContext, Task<T>> read, Func<T, bool> done, string description, int timeoutSeconds = 60)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            await using var db = NewDb();
            var value = await read(db);
            if (done(value)) return value;
            if (DateTimeOffset.UtcNow > deadline) Assert.Fail($"Timed out waiting for {description}; last value: {value}");
            await Task.Delay(200, Ct);
        }
    }

    public Task<OrderStatus> WaitForStatusAsync(Guid orderId, OrderStatus status) => WaitForAsync(
        db => db.Orders.AsNoTracking().Where(x => x.Id == orderId).Select(x => x.Status).SingleAsync(Ct), x => x == status, $"order status {status}");

    public async Task<(int OnHand, int Reserved)> StockAsync(Guid productId)
    {
        await using var db = NewDb();
        var row = await db.Inventories.AsNoTracking().SingleAsync(x => x.ProductId == productId, Ct);
        return (row.OnHand, row.Reserved);
    }

    public async Task<IReadOnlyList<ReservationStatus>> ReservationsAsync(Guid orderId)
    {
        await using var db = NewDb();
        return await db.InventoryReservations.AsNoTracking().Where(x => x.OrderId == orderId).Select(x => x.Status).ToListAsync(Ct);
    }

    public async Task<string> ReadDeadLetterAsync(string sourceTopic)
    {
        using var consumer = new ConsumerBuilder<string?, string>(new ConsumerConfig { BootstrapServers = kafkaBootstrap, BrokerAddressFamily = BrokerAddressFamily.V4, GroupId = $"{topicPrefix}dlq-check",
            AutoOffsetReset = AutoOffsetReset.Earliest, EnableAutoCommit = false }).Build();
        consumer.Subscribe(Topic(EventTopics.DeadLetter(sourceTopic)));
        var result = await Task.Run(() => consumer.Consume(TimeSpan.FromSeconds(30)), Ct);
        consumer.Close();
        Assert.NotNull(result);
        return result.Message.Value;
    }

    public async Task ProduceRawAsync(string topic, string key, string value)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = kafkaBootstrap, BrokerAddressFamily = BrokerAddressFamily.V4 }).Build();
        await producer.ProduceAsync(Topic(topic), new Message<string, string> { Key = key, Value = value }, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (kafkaUsed) await DeleteTopicsAsync();
        await using var db = NewDb();
        await db.Database.EnsureDeletedAsync(CancellationToken.None);
    }

    private bool KafkaIsReachable()
    {
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafkaBootstrap, BrokerAddressFamily = BrokerAddressFamily.V4 }).SetLogHandler((_, _) => { }).SetErrorHandler((_, _) => { }).Build();
            return admin.GetMetadata(TimeSpan.FromSeconds(15)).Brokers.Count > 0;
        }
        catch (KafkaException) { return false; }
    }

    private async Task DeleteTopicsAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafkaBootstrap, BrokerAddressFamily = BrokerAddressFamily.V4 }).Build();
        var topics = EventTopics.All.SelectMany(x => new[] { Topic(x), Topic(EventTopics.DeadLetter(x)) }).ToList();
        try { await admin.DeleteTopicsAsync(topics, new DeleteTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(10) }); }
        catch (DeleteTopicsException) { /* Topics live only as long as the disposable local broker. */ }
        catch (KafkaException) { }
    }
}

internal sealed class RecordingProducer : IEventProducer
{
    private readonly List<OutgoingMessage> messages = [];
    public Func<OutgoingMessage, int, Exception?> Failure { get; set; } = (_, _) => null;
    public IReadOnlyList<OutgoingMessage> Messages { get { lock (messages) return messages.ToArray(); } }

    public Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        int count;
        lock (messages) { messages.Add(message); count = messages.Count(x => x.Headers["eventId"] == message.Headers["eventId"]); }
        return Failure(message, count) is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }
}
