using System.Collections.Concurrent;
using Authagonal.Core.Clustering;
using Authagonal.SqlProvider.Sql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Authagonal.SqlProvider.Clustering;

/// <summary>
/// Cross-node event bus backed by an append-only table — the SQL counterpart to the Azure table-log
/// and DynamoDB log buses. <see cref="PublishAsync"/> writes a row (pk = topic, sk = time-ordered id);
/// every node polls for rows past its per-topic high-water cursor and dispatches to local subscribers.
/// No peer discovery. Delivery is at-least-once and unordered; handlers must be idempotent. Old rows
/// are pruned opportunistically.
/// <para>
/// PostgreSQL's LISTEN/NOTIFY would give lower latency, but it needs a dedicated long-lived connection
/// per node and drops messages a disconnected listener missed. Polling a durable log keeps the
/// delivery guarantee identical to the other backends and keeps SQLite working with the same code.
/// </para>
/// </summary>
public sealed class SqlClusterEventBus(SqlTable table, TimeSpan pollInterval, ILogger<SqlClusterEventBus> logger)
    : IClusterEventBus, IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Topic> _topics = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _pollCount;

    public async Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var row = new SqlRow(topic, NewRowKey(DateTimeOffset.UtcNow));
        row.PutS("payload", Convert.ToBase64String(payload.Span));
        await table.PutAsync(row, ct).ConfigureAwait(false);
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

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(pollInterval);
        while (await SafeWaitAsync(timer, ct).ConfigureAwait(false))
        {
            foreach (var topic in _topics.Values)
            {
                topic.Cursor ??= NewRowKey(DateTimeOffset.UtcNow);
                try { await DrainTopicAsync(topic, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Cluster event poll failed for topic {Topic}", topic.Name);
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

        var filter = SqlKeyFilter.Partition(topic.Name) with { SkAfter = cursor };
        await foreach (var row in table.QueryAsync(filter, ct).ConfigureAwait(false))
        {
            if (string.CompareOrdinal(row.Sk, max) > 0) max = row.Sk;

            ReadOnlyMemory<byte> payload = Convert.FromBase64String(row.GetStr("payload"));
            foreach (var sub in topic.Handlers.Keys)
            {
                try { await sub.Handler(payload, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Cluster event handler failed for topic {Topic}", topic.Name);
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
            try
            {
                var filter = SqlKeyFilter.Partition(topic.Name) with { SkBefore = cutoff };
                await foreach (var row in table.QueryAsync(filter, ct).ConfigureAwait(false))
                {
                    try { await table.DeleteAsync(row.Pk, row.Sk, ct).ConfigureAwait(false); }
                    catch (Exception) { /* already gone */ }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Cluster event prune failed for topic {Topic}", topic.Name);
            }
        }
    }

    // sk sorts ascending by time so "sk > cursor" yields only newer events; the guid suffix breaks ties
    // when two nodes publish in the same tick.
    private static string NewRowKey(DateTimeOffset t) => $"{t.UtcTicks:D19}-{Guid.NewGuid():N}";

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
