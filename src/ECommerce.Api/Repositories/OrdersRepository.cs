using Npgsql;
using ECommerce.Api.Models;

namespace ECommerce.Api.Repositories;

/// <summary>
/// PostgreSQL repository: orders, order items, outbox, sync_state.
/// <see cref="CreateOrderAsync"/> and <see cref="UpdateStatusAsync"/> run inside
/// ONE explicit transaction: any failure rolls everything back.
/// </summary>
public sealed class OrdersRepository(NpgsqlDataSource ds)
{
    public const string PollingWatermarkKey = "polling_watermark";
    public NpgsqlDataSource DataSource => ds;

    private static DbOrder ReadOrder(NpgsqlDataReader r) => new(
        r.GetInt32(0), r.GetInt32(1),
        new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc)),
        r.GetString(3), r.GetDecimal(4),
        new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc)),
        r.IsDBNull(6) ? null : r.GetString(6));

    private const string OrderCols =
        "id, user_id, order_date, status, total_amount, updated_at, idempotency_key";

    // ------------------------------------------------------------- write ---
    /// <summary>
    /// Insert order + items (+ optional outbox row) atomically.
    /// Returns (order, created); idempotent replay returns (existing, false).
    /// </summary>
    public async Task<(DbOrder order, bool created)> CreateOrderAsync(
        int userId, IReadOnlyList<OrderLine> lines, string? idempotencyKey,
        DateTimeOffset orderDate, DateTimeOffset updatedAt,
        Func<int, OutboxEvent>? outboxFactory, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                var existing = await FindByIdempotencyKeyAsync(conn, tx, idempotencyKey, ct);
                if (existing is not null)
                {
                    await tx.RollbackAsync(ct);
                    return (existing, false);
                }
            }

            var total = lines.Sum(l => l.Quantity * l.UnitPrice);
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO orders (user_id, order_date, status, total_amount, updated_at, idempotency_key) " +
                $"VALUES (@u, @od, 'PENDING', @t, @ua, @k) RETURNING {OrderCols}", conn, tx);
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("od", orderDate.UtcDateTime);
            cmd.Parameters.AddWithValue("t", total);
            cmd.Parameters.AddWithValue("ua", updatedAt.UtcDateTime);
            cmd.Parameters.AddWithValue("k", (object?)idempotencyKey ?? DBNull.Value);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var order = ReadOrder(r);
            await r.CloseAsync();

            foreach (var l in lines)
            {
                await using var ic = new NpgsqlCommand(
                    "INSERT INTO order_items (order_id, product_id, title, quantity, unit_price) " +
                    "VALUES (@o, @p, @t, @q, @up)", conn, tx);
                ic.Parameters.AddWithValue("o", order.Id);
                ic.Parameters.AddWithValue("p", l.ProductId);
                ic.Parameters.AddWithValue("t", l.Title);
                ic.Parameters.AddWithValue("q", l.Quantity);
                ic.Parameters.AddWithValue("up", l.UnitPrice);
                await ic.ExecuteNonQueryAsync(ct);
            }

            if (outboxFactory is not null)
            {
                var ev = outboxFactory(order.Id);
                await InsertOutboxRowAsync(conn, tx, order.Id, ev.EventType, ev.PayloadJson, ct);
            }

            await tx.CommitAsync(ct);
            return (order, true);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private static async Task InsertOutboxRowAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        int orderId, string eventType, string payloadJson, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO outbox (order_id, event_type, payload, status) VALUES (@o, @e, CAST(@p AS jsonb), 'pending')",
            conn, tx);
        cmd.Parameters.AddWithValue("o", orderId);
        cmd.Parameters.AddWithValue("e", eventType);
        cmd.Parameters.AddWithValue("p", payloadJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Dual-write fallback: outbox row in its own transaction (order already committed).</summary>
    public async Task InsertOutboxEventAsync(int orderId, string eventType, string payloadJson,
        CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await InsertOutboxRowAsync(conn, tx, orderId, eventType, payloadJson, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<DbOrder?> FindByIdempotencyKeyAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return await FindByIdempotencyKeyAsync(conn, null, key, ct);
    }

    private static async Task<DbOrder?> FindByIdempotencyKeyAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT {OrderCols} FROM orders WHERE idempotency_key = @k", conn, tx);
        cmd.Parameters.AddWithValue("k", key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadOrder(r) : null;
    }

    public async Task<DbOrder?> UpdateStatusAsync(int orderId, string status,
        Func<int, DateTimeOffset, OutboxEvent>? outboxFactory, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(
                $"UPDATE orders SET status = @s, updated_at = now() WHERE id = @id RETURNING {OrderCols}",
                conn, tx);
            cmd.Parameters.AddWithValue("s", status);
            cmd.Parameters.AddWithValue("id", orderId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) { await tx.RollbackAsync(ct); return null; }
            var order = ReadOrder(r);
            await r.CloseAsync();

            if (outboxFactory is not null)
            {
                var ev = outboxFactory(order.Id, order.UpdatedAt);
                await InsertOutboxRowAsync(conn, tx, order.Id, ev.EventType, ev.PayloadJson, ct);
            }
            await tx.CommitAsync(ct);
            return order;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // -------------------------------------------------------------- read ---
    public async Task<OrderFull?> GetFullAsync(int orderId, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {OrderCols} FROM orders WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", orderId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var order = ReadOrder(r);
        await r.CloseAsync();

        await using var uc = new NpgsqlCommand("SELECT id, name, email FROM users WHERE id = @u", conn);
        uc.Parameters.AddWithValue("u", order.UserId);
        await using var ur = await uc.ExecuteReaderAsync(ct);
        await ur.ReadAsync(ct);
        var user = new DbUser(ur.GetInt32(0), ur.GetString(1), ur.GetString(2));
        await ur.CloseAsync();

        var items = await GetItemsAsync(conn, orderId, ct);
        return new OrderFull(order, user, items);
    }

    private static async Task<List<DbOrderItem>> GetItemsAsync(
        NpgsqlConnection conn, int orderId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id, order_id, product_id, title, quantity, unit_price FROM order_items " +
            "WHERE order_id = @o ORDER BY id", conn);
        cmd.Parameters.AddWithValue("o", orderId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<DbOrderItem>();
        while (await r.ReadAsync(ct))
            items.Add(new DbOrderItem(r.GetInt32(0), r.GetInt32(1), r.GetString(2),
                r.GetString(3), r.GetInt32(4), r.GetDecimal(5)));
        return items;
    }

    /// <summary>Orders joined with user + items for ES document building.</summary>
    public async Task<List<OrderFull>> FetchForSyncAsync(DateTimeOffset? updatedAfter = null,
        int limit = 500, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var sql = "SELECT o.id, o.user_id, o.order_date, o.status, o.total_amount, o.updated_at, o.idempotency_key, " +
                  "u.id, u.name, u.email FROM orders o JOIN users u ON u.id = o.user_id " +
                  (updatedAfter.HasValue ? "WHERE o.updated_at > @after " : "") +
                  "ORDER BY o.updated_at ASC, o.id ASC LIMIT @n";
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (updatedAfter.HasValue) cmd.Parameters.AddWithValue("after", updatedAfter.Value.UtcDateTime);
        cmd.Parameters.AddWithValue("n", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<(DbOrder, DbUser)>();
        while (await r.ReadAsync(ct))
        {
            var order = new DbOrder(r.GetInt32(0), r.GetInt32(1),
                new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc)),
                r.GetString(3), r.GetDecimal(4),
                new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc)),
                r.IsDBNull(6) ? null : r.GetString(6));
            rows.Add((order, new DbUser(r.GetInt32(7), r.GetString(8), r.GetString(9))));
        }
        await r.CloseAsync();

        var result = new List<OrderFull>();
        foreach (var (order, user) in rows)
            result.Add(new OrderFull(order, user, await GetItemsAsync(conn, order.Id, ct)));
        return result;
    }

    // ------------------------------------------------------------- outbox ---
    public async Task<List<DbOutboxRow>> ClaimOutboxBatchAsync(int limit = 50,
        CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var rows = new List<DbOutboxRow>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, order_id, event_type, payload::text, status, attempts, next_attempt_at, last_error, created_at " +
            "FROM outbox WHERE status = 'pending' AND next_attempt_at <= now() " +
            "ORDER BY id ASC LIMIT @n FOR UPDATE SKIP LOCKED", conn, tx))
        {
            cmd.Parameters.AddWithValue("n", limit);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new DbOutboxRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3),
                    r.GetString(4), r.GetInt32(5),
                    new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc)),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc))));
            await r.CloseAsync();
        }
        if (rows.Count > 0)
        {
            await using var upd = new NpgsqlCommand(
                "UPDATE outbox SET status = 'processing' WHERE id = ANY(@ids)", conn, tx);
            upd.Parameters.AddWithValue("ids", rows.Select(x => x.Id).ToArray());
            await upd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return rows;
    }

    public async Task MarkOutboxDoneAsync(int outboxId, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE outbox SET status = 'done', last_error = NULL WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", outboxId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Pure backoff rule (unit-testable): after incrementing attempts,
    /// 'failed' at maxAttempts, else 'pending' with 2^attempts seconds delay.
    /// </summary>
    public static (string status, TimeSpan delay) ComputeBackoff(int attempts, int maxAttempts)
        => attempts >= maxAttempts
            ? ("failed", TimeSpan.Zero)
            : ("pending", TimeSpan.FromSeconds(Math.Pow(2, attempts)));

    public async Task MarkOutboxErrorAsync(int outboxId, string error, int maxAttempts,
        CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int attempts;
        await using (var cmd = new NpgsqlCommand(
            "UPDATE outbox SET attempts = attempts + 1 WHERE id = @id RETURNING attempts", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", outboxId);
            attempts = (int)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var (status, delay) = ComputeBackoff(attempts, maxAttempts);
        await using (var cmd2 = new NpgsqlCommand(
            "UPDATE outbox SET status = @s, last_error = @e, " +
            "next_attempt_at = CASE WHEN @s = 'pending' THEN now() + make_interval(secs => @d) ELSE next_attempt_at END " +
            "WHERE id = @id", conn, tx))
        {
            cmd2.Parameters.AddWithValue("s", status);
            cmd2.Parameters.AddWithValue("e", error.Length > 2000 ? error[..2000] : error);
            cmd2.Parameters.AddWithValue("d", delay.TotalSeconds);
            cmd2.Parameters.AddWithValue("id", outboxId);
            await cmd2.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<Dictionary<string, int>> CountOutboxByStatusAsync(CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT status, COUNT(*) FROM outbox GROUP BY status", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var dict = new Dictionary<string, int>();
        while (await r.ReadAsync(ct)) dict[r.GetString(0)] = (int)r.GetInt64(1);
        return dict;
    }

    // --------------------------------------------------------- sync_state ---
    public async Task<string?> GetSyncStateAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT value FROM sync_state WHERE key = @k", conn);
        cmd.Parameters.AddWithValue("k", key);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    public async Task SetSyncStateAsync(string key, string value, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO sync_state (key, value, updated_at) VALUES (@k, @v, now()) " +
            "ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()", conn);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("v", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DateTimeOffset?> GetPollingWatermarkAsync(CancellationToken ct = default)
    {
        var raw = await GetSyncStateAsync(PollingWatermarkKey, ct);
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTimeOffset.Parse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    public Task SetPollingWatermarkAsync(DateTimeOffset value, CancellationToken ct = default)
        => SetSyncStateAsync(PollingWatermarkKey, value.ToString("O"), ct);

    // -------------------------------------------------------------- misc ---
    public async Task<int> CountOrdersAsync(CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM orders", conn);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task DeleteAllDemoDataAsync(CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE outbox, order_items, orders, sync_state, users RESTART IDENTITY CASCADE", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
