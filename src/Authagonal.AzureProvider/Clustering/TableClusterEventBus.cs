using System.Collections.Concurrent;
using Authagonal.Core.Clustering;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.AzureProvider.Clustering;

/// <summary>
/// Cross-node event bus backed by an append-only Azure Table log. <see cref="PublishAsync"/> appends
/// a row (PartitionKey = topic); every node polls for rows past its per-topic high-water cursor and
/// dispatches them to local subscribers. No peer discovery or per-replica addressing — works on ACA
/// and AKS alike. Delivery is at-least-once and unordered; handlers must be idempotent. Old rows are
/// pruned opportunistically.
/// </summary>
public sealed class TableClusterEventBus : IClusterEventBus, IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly TableClient _table;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<TableClusterEventBus> _logger;
    private readonly ConcurrentDictionary<string, Topic> _topics = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _pollCount;
    private volatile bool _tableReady;

    public TableClusterEventBus(TableClient table, TimeSpan pollInterval, ILogger<TableClusterEventBus> logger)
    {
        _table = table;
        _pollInterval = pollInterval;
        _logger = logger;
    }

    public async Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);
        var entity = new ClusterEventEntity
        {
            PartitionKey = topic,
            RowKey = NewRowKey(DateTimeOffset.UtcNow),
            Payload = Convert.ToBase64String(payload.Span),
        };
        await _table.AddEntityAsync(entity, ct).ConfigureAwait(false);
    }

    public IDisposable Subscribe(string topic, Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler)
    {
        var t = _topics.GetOrAdd(topic, name => new Topic(name));
        var sub = new Subscription(t, handler);
        t.Handlers[sub] = 0;
        return sub;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Only deliver events published from now on — don't replay the whole log at boot.
        var start = NewRowKey(DateTimeOffset.UtcNow);
        foreach (var t in _topics.Values)
            t.Cursor ??= start;
        _loop = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* shutting down */ }
        }
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (_tableReady) return;
        try { await _table.CreateIfNotExistsAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Event table ensure failed"); }
        _tableReady = true;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        await EnsureTableAsync(ct).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_pollInterval);
        while (await SafeWaitAsync(timer, ct).ConfigureAwait(false))
        {
            foreach (var topic in _topics.Values)
            {
                topic.Cursor ??= NewRowKey(DateTimeOffset.UtcNow);
                try { await DrainTopicAsync(topic, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Cluster event poll failed for topic {Topic}", topic.Name);
                }
            }

            if (Interlocked.Increment(ref _pollCount) % 20 == 0)
                await PruneAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task DrainTopicAsync(Topic topic, CancellationToken ct)
    {
        var cursor = topic.Cursor!;
        var max = cursor;
        var filter = $"PartitionKey eq '{Escape(topic.Name)}' and RowKey gt '{cursor}'";

        await foreach (var e in _table.QueryAsync<ClusterEventEntity>(filter, cancellationToken: ct).ConfigureAwait(false))
        {
            if (string.CompareOrdinal(e.RowKey, max) > 0) max = e.RowKey;

            ReadOnlyMemory<byte> payload = Convert.FromBase64String(e.Payload);
            foreach (var sub in topic.Handlers.Keys)
            {
                try { await sub.Handler(payload, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Cluster event handler failed for topic {Topic}", topic.Name);
                }
            }
        }

        topic.Cursor = max;
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        var cutoff = NewRowKey(DateTimeOffset.UtcNow - Retention);
        foreach (var topic in _topics.Values)
        {
            var filter = $"PartitionKey eq '{Escape(topic.Name)}' and RowKey lt '{cutoff}'";
            try
            {
                await foreach (var e in _table.QueryAsync<ClusterEventEntity>(filter, cancellationToken: ct).ConfigureAwait(false))
                {
                    try { await _table.DeleteEntityAsync(e.PartitionKey, e.RowKey, ETag.All, ct).ConfigureAwait(false); }
                    catch (RequestFailedException) { /* already gone */ }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Cluster event prune failed for topic {Topic}", topic.Name);
            }
        }
    }

    // RowKey sorts ascending by time so "gt cursor" yields only newer events; the guid suffix
    // breaks ties when two nodes publish in the same tick.
    private static string NewRowKey(DateTimeOffset t) => $"{t.UtcTicks:D19}-{Guid.NewGuid():N}";

    private static string Escape(string s) => s.Replace("'", "''");

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }

    private sealed class Topic(string name)
    {
        public string Name => name;
        public string? Cursor;
        public ConcurrentDictionary<Subscription, byte> Handlers { get; } = new();
    }

    private sealed class Subscription(Topic topic, Func<ReadOnlyMemory<byte>, CancellationToken, Task> handler) : IDisposable
    {
        public Func<ReadOnlyMemory<byte>, CancellationToken, Task> Handler => handler;
        public void Dispose() => topic.Handlers.TryRemove(this, out _);
    }
}

internal sealed class ClusterEventEntity : ITableEntity
{
    public string PartitionKey { get; set; } = default!;
    public string RowKey { get; set; } = default!;
    public string Payload { get; set; } = default!;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
}
