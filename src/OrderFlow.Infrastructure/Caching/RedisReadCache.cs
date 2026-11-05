using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Catalog;
using StackExchange.Redis;

namespace OrderFlow.Infrastructure.Caching;

public sealed class RedisOptions
{
    public string ConnectionString { get; set; } = "";
    public int TimeoutMilliseconds { get; set; } = 250;
    public string? KeyPrefix { get; set; }
}

public sealed class RedisReadCache : IReadCache, IAsyncDisposable
{
    // A load that started before an invalidation holds an old generation, so its stale result is not stored.
    private const string FillScript = "if (redis.call('GET', KEYS[2]) or '') == ARGV[1] then redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3]) return 1 end return 0";
    private const string InvalidateScript = "redis.call('INCR', KEYS[2]) redis.call('PEXPIRE', KEYS[2], ARGV[1]) redis.call('DEL', KEYS[1]) return 1";
    private static readonly long GenerationTtlMilliseconds = (long)TimeSpan.FromHours(1).TotalMilliseconds;
    // Every entry written before a failed invalidation has expired after the longest catalog TTL.
    private static readonly TimeSpan PendingInvalidationLifetime = CatalogCache.Categories.MaxTtl;
    private static readonly TimeSpan CoalesceWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lazy<Task<ConnectionMultiplexer>> connection;
    private readonly string keyPrefix;
    private readonly ILogger<RedisReadCache> logger;
    private readonly Counter<long> hits;
    private readonly Counter<long> misses;
    private readonly Counter<long> failures;
    private readonly Histogram<double> duration;
    // One load per key per instance. Striped so that the set of gates stays bounded for any number of keys.
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly ConcurrentDictionary<string, long> pendingInvalidations = new();
    private readonly CancellationTokenSource shutdown = new();
    private int retryLoopRunning;
    private long lastFailureLogTimestamp;

    public RedisReadCache(IOptions<RedisOptions> options, IMeterFactory meterFactory, ILogger<RedisReadCache> logger)
    {
        var settings = options.Value;
        keyPrefix = settings.KeyPrefix ?? "";
        this.logger = logger;
        var configuration = ConfigurationOptions.Parse(settings.ConnectionString);
        configuration.AbortOnConnectFail = false;
        configuration.ConnectRetry = 1;
        // Windows spends about 2 seconds on ::1 before trying IPv4 when "localhost" reaches an IPv4-only server.
        configuration.ConnectTimeout = Math.Max(settings.TimeoutMilliseconds, 3000);
        configuration.SyncTimeout = settings.TimeoutMilliseconds;
        configuration.AsyncTimeout = settings.TimeoutMilliseconds;
        // Fail immediately while disconnected instead of queueing, so that an outage costs reads no extra latency.
        configuration.BacklogPolicy = BacklogPolicy.FailFast;
        connection = new Lazy<Task<ConnectionMultiplexer>>(() => ConnectionMultiplexer.ConnectAsync(configuration));

        var meter = meterFactory.Create("OrderFlow.Cache");
        hits = meter.CreateCounter<long>("orderflow.cache.hits");
        misses = meter.CreateCounter<long>("orderflow.cache.misses");
        failures = meter.CreateCounter<long>("orderflow.cache.failures");
        duration = meter.CreateHistogram<double>("orderflow.cache.operation.duration", "ms");
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var multiplexer = await connection.Value.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            await multiplexer.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException) { return false; }
    }

    public async Task<T?> GetOrLoadAsync<T>(CacheEntryPolicy policy, string key, Func<CancellationToken, Task<T?>> load, CancellationToken cancellationToken) where T : class
    {
        // This instance knows the stored value is stale until the retry loop removes it.
        if (pendingInvalidations.ContainsKey(key)) return await load(cancellationToken);

        var first = await ReadAsync<T>(policy, key);
        if (first.Status == ReadStatus.Hit) return first.Value;
        if (first.Status == ReadStatus.Unavailable) return await load(cancellationToken);

        var gate = gates[(uint)key.GetHashCode() % gates.Length];
        // A waiter that times out loads without the gate, so one slow query cannot stall other keys in its stripe.
        var entered = await gate.WaitAsync(CoalesceWait, cancellationToken);
        try
        {
            var current = await ReadAsync<T>(policy, key);
            if (current.Status == ReadStatus.Hit) return current.Value;
            if (current.Status == ReadStatus.Unavailable) return await load(cancellationToken);

            misses.Add(1, new KeyValuePair<string, object?>("cache", policy.Name));
            var loaded = await load(cancellationToken);
            if (loaded is not null) await FillAsync(policy, key, current.Generation, loaded);
            return loaded;
        }
        finally { if (entered) gate.Release(); }
    }

    public async Task InvalidateAsync(IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0) return;
        try { await EvictAsync(keys); }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            RecordFailure("invalidate", "invalidate", exception);
            var failedAt = Stopwatch.GetTimestamp();
            foreach (var key in keys) pendingInvalidations[key] = failedAt;
            StartRetryLoop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync();
        if (connection.IsValueCreated) await (await connection.Value).DisposeAsync();
        shutdown.Dispose();
        foreach (var gate in gates) gate.Dispose();
    }

    private async Task<ReadResult<T>> ReadAsync<T>(CacheEntryPolicy policy, string key) where T : class
    {
        var started = Stopwatch.GetTimestamp();
        RedisValue[] values;
        try
        {
            var database = (await connection.Value).GetDatabase();
            values = await database.StringGetAsync([keyPrefix + key, GenerationKey(key)]);
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            RecordFailure(policy.Name, "get", exception);
            return new ReadResult<T>(ReadStatus.Unavailable, null, RedisValue.Null);
        }
        finally { RecordDuration(policy.Name, "get", started); }

        if (values[0].IsNull) return new ReadResult<T>(ReadStatus.Missing, null, values[1]);
        try
        {
            var value = JsonSerializer.Deserialize<T>(values[0].ToString(), Json);
            if (value is null) return new ReadResult<T>(ReadStatus.Missing, null, values[1]);
            hits.Add(1, new KeyValuePair<string, object?>("cache", policy.Name));
            return new ReadResult<T>(ReadStatus.Hit, value, values[1]);
        }
        catch (JsonException exception)
        {
            // An unreadable entry is a miss; the fill that follows replaces it.
            RecordFailure(policy.Name, "deserialize", exception);
            return new ReadResult<T>(ReadStatus.Missing, null, values[1]);
        }
    }

    private async Task FillAsync<T>(CacheEntryPolicy policy, string key, RedisValue generation, T value) where T : class
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var ttl = policy.MinTtl + (policy.MaxTtl - policy.MinTtl) * Random.Shared.NextDouble();
            var database = (await connection.Value).GetDatabase();
            await database.ScriptEvaluateAsync(FillScript, [keyPrefix + key, GenerationKey(key)],
                [generation.IsNull ? "" : generation, JsonSerializer.Serialize(value, Json), (long)ttl.TotalMilliseconds]);
        }
        catch (Exception exception) when (IsCacheFailure(exception)) { RecordFailure(policy.Name, "fill", exception); }
        finally { RecordDuration(policy.Name, "fill", started); }
    }

    private async Task EvictAsync(IReadOnlyCollection<string> keys)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var database = (await connection.Value).GetDatabase();
            await Task.WhenAll(keys.Select(key => database.ScriptEvaluateAsync(InvalidateScript, [keyPrefix + key, GenerationKey(key)], [GenerationTtlMilliseconds])));
        }
        finally { RecordDuration("invalidate", "invalidate", started); }
    }

    private void StartRetryLoop()
    {
        if (Interlocked.Exchange(ref retryLoopRunning, 1) == 0) _ = RetryPendingInvalidationsAsync();
    }

    // Best effort: pending keys are lost if the process stops, and the TTL then bounds staleness.
    private async Task RetryPendingInvalidationsAsync()
    {
        var delay = TimeSpan.FromSeconds(1);
        try
        {
            while (!pendingInvalidations.IsEmpty)
            {
                await Task.Delay(delay, shutdown.Token);
                var snapshot = pendingInvalidations.ToArray();
                try
                {
                    await EvictAsync(snapshot.Select(x => x.Key).ToArray());
                    foreach (var entry in snapshot) pendingInvalidations.TryRemove(entry);
                    delay = TimeSpan.FromSeconds(1);
                }
                catch (Exception exception) when (IsCacheFailure(exception))
                {
                    RecordFailure("invalidate", "invalidate", exception);
                    foreach (var entry in snapshot.Where(x => Stopwatch.GetElapsedTime(x.Value) > PendingInvalidationLifetime)) pendingInvalidations.TryRemove(entry);
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
                }
            }
        }
        catch (OperationCanceledException) { return; }
        finally { Volatile.Write(ref retryLoopRunning, 0); }
        // A key added after the loop saw an empty set needs its own loop.
        if (!pendingInvalidations.IsEmpty) StartRetryLoop();
    }

    private string GenerationKey(string key) => $"{keyPrefix}{key}:gen";

    private void RecordDuration(string cache, string operation, long started) => duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        new KeyValuePair<string, object?>("cache", cache), new KeyValuePair<string, object?>("operation", operation));

    private void RecordFailure(string cache, string operation, Exception exception)
    {
        failures.Add(1, new KeyValuePair<string, object?>("cache", cache), new KeyValuePair<string, object?>("operation", operation));
        // An outage fails every read; the counter records each one and the log stays readable.
        var last = Interlocked.Read(ref lastFailureLogTimestamp);
        if (last != 0 && Stopwatch.GetElapsedTime(last) < FailureLogInterval) return;
        if (Interlocked.CompareExchange(ref lastFailureLogTimestamp, Stopwatch.GetTimestamp(), last) != last) return;
        logger.LogWarning("Cache {Operation} failed with {ExceptionType}; catalog reads are served from SQL Server.", operation, exception.GetType().Name);
    }

    private static bool IsCacheFailure(Exception exception) => exception is RedisException or TimeoutException or ObjectDisposedException;

    private enum ReadStatus { Hit, Missing, Unavailable }
    private readonly record struct ReadResult<T>(ReadStatus Status, T? Value, RedisValue Generation) where T : class;
}
