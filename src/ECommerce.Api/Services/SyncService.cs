using System.Text.Json;
using ECommerce.Api.Config;
using ECommerce.Api.Models;
using ECommerce.Api.Repositories;
using Microsoft.Extensions.Logging;

namespace ECommerce.Api.Services;

/// <summary>
/// Synchronization strategies: dual-write, transactional outbox, polling.
/// All strategies upsert ES documents with the deterministic id `order-{id}`,
/// so retries and replays are idempotent. MongoDB is never on the sync path.
/// </summary>
public sealed class SyncService(
    OrdersRepository orders,
    SearchRepository search,
    AppOptions options,
    ILogger<SyncService>? log = null)
{
    public const int RepairLoopSeconds = 10;
    public const int OutboxBatchSize = 50;
    public const int PollingBatchSize = 500;

    private async Task PushOrderDocAsync(Dictionary<string, object?> doc, CancellationToken ct)
        => await search.UpsertOrderDocAsync(options.EsIndex, doc, ct);

    /// <summary>
    /// Synchronize a committed order to ES per SYNC_STRATEGY. Never throws:
    /// ES failures become outbox rows / pending_retry, never fake rollbacks.
    /// </summary>
    public async Task<EsSyncDto> SyncAfterCommitAsync(int orderId, string eventType,
        Dictionary<string, object?> doc, CancellationToken ct = default)
    {
        var strategy = options.NormalizedStrategy;
        if (strategy is "outbox" or "polling")
            return new EsSyncDto(strategy, "queued");

        // dual_write: index immediately; on failure enqueue a repair outbox row
        // in a SEPARATE transaction (the order itself stays committed).
        try
        {
            await PushOrderDocAsync(doc, ct);
            return new EsSyncDto(strategy, "synced");
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "dual-write to ES failed; enqueueing outbox repair for order {OrderId}", orderId);
            await orders.InsertOutboxEventAsync(orderId, eventType,
                JsonSerializer.Serialize(doc), ct);
            return new EsSyncDto(strategy, "pending_retry");
        }
    }

    /// <summary>Drain due outbox rows once. Returns (processed, failed) counts.</summary>
    public async Task<(int processed, int failed)> ProcessOutboxOnceAsync(CancellationToken ct = default)
    {
        var batch = await orders.ClaimOutboxBatchAsync(OutboxBatchSize, ct);
        var processed = 0; var failed = 0;
        foreach (var row in batch)
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(row.PayloadJson)!;
                await PushOrderDocAsync(dict, ct);
                await orders.MarkOutboxDoneAsync(row.Id, ct);
                processed++;
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "outbox event {OutboxId} failed", row.Id);
                await orders.MarkOutboxErrorAsync(row.Id, ex.Message, options.OutboxMaxAttempts, ct);
                failed++;
            }
        }
        return (processed, failed);
    }

    /// <summary>One polling pass: upsert orders newer than the watermark, advance it.</summary>
    public async Task<(int synced, string? watermark)> RunPollingCycleAsync(CancellationToken ct = default)
    {
        var watermark = await orders.GetPollingWatermarkAsync(ct);
        var rows = await orders.FetchForSyncAsync(
            watermark ?? new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PollingBatchSize, ct);
        if (rows.Count == 0)
            return (0, watermark?.ToString("O"));

        var maxUpdated = watermark;
        var synced = 0;
        foreach (var entry in rows)
        {
            // Raises on ES failure -> watermark is NOT advanced (at-least-once).
            await PushOrderDocAsync(
                SearchRepository.BuildOrderDoc(entry.Order, entry.User, entry.Items), ct);
            synced++;
            if (maxUpdated is null || entry.Order.UpdatedAt > maxUpdated)
                maxUpdated = entry.Order.UpdatedAt;
        }
        await orders.SetPollingWatermarkAsync(maxUpdated!.Value, ct);
        return (synced, maxUpdated.Value.ToString("O"));
    }

    /// <summary>Full rebuild of the ES index from PostgreSQL (snapshots only, never Mongo).</summary>
    public async Task<int> ReindexAllAsync(CancellationToken ct = default)
    {
        if (!await search.EnsureIndexAsync(options.EsIndex, ct))
            throw new EsUnavailableException("elasticsearch unreachable");
        var rows = await orders.FetchForSyncAsync(null, 100_000, ct);
        const int batchSize = 100;
        var count = 0;
        for (var i = 0; i < rows.Count; i += batchSize)
        {
            var batch = rows.Skip(i).Take(batchSize).ToList();
            await Task.WhenAll(batch.Select(e =>
                PushOrderDocAsync(SearchRepository.BuildOrderDoc(e.Order, e.User, e.Items), ct)));
            count += batch.Count;
        }
        await search.RefreshAsync(options.EsIndex, ct);
        return count;
    }
}

/// <summary>
/// Background worker started per SYNC_STRATEGY:
/// dual_write -> repair loop every 10s; outbox -> outbox worker;
/// polling -> polling worker. No-op when <see cref="AppOptions.StartWorkers"/> is false.
/// </summary>
public sealed class SyncWorker(
    OrdersRepository orders,
    SearchRepository search,
    AppOptions options,
    ILogger<SyncWorker>? log = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.StartWorkers) return;
        var sync = new SyncService(orders, search, options, null);
        var strategy = options.NormalizedStrategy;
        var (interval, mode) = strategy switch
        {
            "outbox" => (TimeSpan.FromSeconds(options.OutboxPollSeconds), "outbox"),
            "polling" => (TimeSpan.FromSeconds(options.PollingIntervalSeconds), "polling"),
            _ => (TimeSpan.FromSeconds(SyncService.RepairLoopSeconds), "repair"),
        };
        log?.LogInformation("sync worker started: {Mode} every {Interval}s", mode, interval.TotalSeconds);
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (mode == "polling")
                    await sync.RunPollingCycleAsync(stoppingToken);
                else
                    await sync.ProcessOutboxOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "sync worker cycle failed ({Mode})", mode);
            }
        }
    }
}
