using System.Text.Json;
using ECommerce.Api.Config;
using ECommerce.Api.Infrastructure;
using ECommerce.Api.Models;
using ECommerce.Api.Repositories;

namespace ECommerce.Api.Services;

/// <summary>
/// Checkout and order-status business logic. Write-path discipline:
/// checkout validates against the CURRENT MongoDB catalog (server prices win)
/// and persists order + items inside ONE PostgreSQL transaction. Elasticsearch
/// is synchronized only AFTER the PG transaction commits.
/// </summary>
public sealed class OrderService(
    OrdersRepository orders,
    UsersRepository users,
    ProductsRepository products,
    SearchRepository search,
    AppOptions options)
{
    public static readonly string[] Statuses = ["PENDING", "PROCESSING", "SHIPPED"];

    private readonly SyncService _sync = new(orders, search, options);

    private static Dictionary<string, object?> BuildEsDoc(
        int orderId, DbUser user, IReadOnlyList<OrderLine> lines,
        string status, DateTimeOffset orderDate, DateTimeOffset updatedAt)
    {
        static string Iso(DateTimeOffset dto) => Money.TruncateToSecond(dto).ToString("O");
        var total = lines.Sum(l => l.Quantity * l.UnitPrice);
        return new Dictionary<string, object?>
        {
            ["order_id"] = (long)orderId,
            ["order_date"] = Iso(orderDate),
            ["status"] = status,
            ["total_amount"] = (double)total,
            ["updated_at"] = Iso(updatedAt),
            ["customer"] = new Dictionary<string, object?>
            {
                ["id"] = (long)user.Id, ["name"] = user.Name, ["email"] = user.Email,
            },
            ["items"] = lines.Select(l => (object?)new Dictionary<string, object?>
            {
                ["product_id"] = l.ProductId,
                ["title"] = l.Title,
                ["quantity"] = l.Quantity,
                ["unit_price"] = (double)l.UnitPrice,
            }).ToList(),
        };
    }

    private async Task<List<OrderLine>> ValidateLinesAsync(
        List<OrderItemCreateDto> items, CancellationToken ct)
    {
        var lines = new List<OrderLine>();
        foreach (var item in items)
        {
            var product = await products.GetAsync(item.ProductId, ct);
            if (product is null)
                throw new ApiException(422, $"product not found: {item.ProductId}");
            if (!product.Active)
                throw new ApiException(422, $"product is inactive: {item.ProductId}");
            lines.Add(new OrderLine(item.ProductId, product.Title, item.Quantity,
                Money.FromDouble(product.Price)));
        }
        return lines;
    }

    private static OrderCreateOutDto ToCreateOut(DbOrder order, DbUser user,
        List<DbOrderItem> items, EsSyncDto esSync) => new()
        {
            OrderId = order.Id,
            UserId = order.UserId,
            Status = order.Status,
            TotalAmount = order.TotalAmount,
            OrderDate = order.OrderDate,
            UpdatedAt = order.UpdatedAt,
            Items = items.Select(i => new OrderItemDto
            {
                ProductId = i.ProductId,
                Title = i.Title,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                LineTotal = Money.Quantize(i.UnitPrice * i.Quantity),
            }).ToList(),
            EsSync = esSync,
        };

    public async Task<(OrderCreateOutDto dto, int statusCode)> PlaceOrderAsync(
        OrderCreateDto payload, CancellationToken ct = default)
    {
        var user = await users.GetAsync(payload.UserId, ct);
        if (user is null)
            throw new ApiException(400, $"unknown user_id: {payload.UserId}");

        var lines = await ValidateLinesAsync(payload.Items, ct);
        var now = DateTimeOffset.UtcNow;
        var strategy = options.NormalizedStrategy;

        // Idempotency replay: return the existing order without inserting.
        if (!string.IsNullOrEmpty(payload.IdempotencyKey))
        {
            var existing = await orders.FindByIdempotencyKeyAsync(payload.IdempotencyKey, ct);
            if (existing is not null)
            {
                var full = (await orders.GetFullAsync(existing.Id, ct))!;
                EsOrderDoc? doc = null;
                try { doc = await search.GetOrderDocAsync(options.EsIndex, existing.Id, ct); }
                catch { /* ES unreachable: report queued */ }
                return (ToCreateOut(full.Order, full.User, full.Items,
                    new EsSyncDto(strategy, doc is not null ? "synced" : "queued")), 200);
            }
        }

        Dictionary<string, object?> MakeDoc(int orderId) =>
            BuildEsDoc(orderId, user, lines, "PENDING", now, now);

        var (order, created) = await orders.CreateOrderAsync(
            payload.UserId, lines, payload.IdempotencyKey, now, now,
            strategy == "outbox"
                ? oid => new OutboxEvent("order.created", JsonSerializer.Serialize(MakeDoc(oid)))
                : null,
            ct);
        if (!created) // lost a race with another request using the same key
        {
            var full = (await orders.GetFullAsync(order.Id, ct))!;
            return (ToCreateOut(full.Order, full.User, full.Items,
                new EsSyncDto(strategy, "queued")), 200);
        }

        var esSync = await _sync.SyncAfterCommitAsync(
            order.Id, "order.created", MakeDoc(order.Id), ct);

        var created2 = (await orders.GetFullAsync(order.Id, ct))!;
        return (ToCreateOut(created2.Order, created2.User, created2.Items, esSync), 201);
    }

    public async Task<OrderDetailOutDto> GetDetailAsync(int orderId, CancellationToken ct = default)
    {
        var full = await orders.GetFullAsync(orderId, ct);
        if (full is null) throw new ApiException(404, $"order not found: {orderId}");
        var (order, user, items) = full;

        bool? esInSync;
        DateTimeOffset? esUpdatedAt = null;
        try
        {
            var esDoc = await search.GetOrderDocAsync(options.EsIndex, orderId, ct);
            if (esDoc is null)
            {
                esInSync = false;
            }
            else
            {
                esUpdatedAt = esDoc.UpdatedAt;
                esInSync = Money.TruncateToSecond(esDoc.UpdatedAt) == Money.TruncateToSecond(order.UpdatedAt)
                    && esDoc.Status == order.Status
                    && esDoc.TotalAmount == (double)order.TotalAmount;
            }
        }
        catch (EsUnavailableException)
        {
            esInSync = null; // ES unreachable: unknown, not "out of sync"
        }

        return new OrderDetailOutDto
        {
            OrderId = order.Id,
            UserId = order.UserId,
            Customer = new UserDto(user.Id, user.Name, user.Email),
            Status = order.Status,
            TotalAmount = order.TotalAmount,
            OrderDate = order.OrderDate,
            UpdatedAt = order.UpdatedAt,
            Items = items.Select(i => new OrderItemDto
            {
                ProductId = i.ProductId,
                Title = i.Title,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                LineTotal = Money.Quantize(i.UnitPrice * i.Quantity),
            }).ToList(),
            Sync = new OrderSyncDto
            {
                EsInSync = esInSync,
                PgUpdatedAt = order.UpdatedAt,
                EsUpdatedAt = esUpdatedAt,
            },
        };
    }

    public async Task<StatusUpdateOutDto> UpdateStatusAsync(int orderId, string status,
        CancellationToken ct = default)
    {
        if (!Statuses.Contains(status))
            throw new ApiException(422, $"invalid status: {status}");

        var full = await orders.GetFullAsync(orderId, ct);
        if (full is null) throw new ApiException(404, $"order not found: {orderId}");
        var (existing, user, items) = full;
        var lines = items.Select(i => new OrderLine(i.ProductId, i.Title, i.Quantity,
            Money.Quantize(i.UnitPrice))).ToList();
        var strategy = options.NormalizedStrategy;

        var order = (await orders.UpdateStatusAsync(orderId, status,
            strategy == "outbox"
                ? (oid, updatedAt) => new OutboxEvent("order.status_updated",
                    JsonSerializer.Serialize(BuildEsDoc(oid, user, lines, status,
                        existing.OrderDate, updatedAt)))
                : null,
            ct))!;

        var doc = BuildEsDoc(order.Id, user, lines, status, existing.OrderDate, order.UpdatedAt);
        var esSync = await _sync.SyncAfterCommitAsync(order.Id, "order.status_updated", doc, ct);

        return new StatusUpdateOutDto
        {
            OrderId = order.Id,
            Status = order.Status,
            UpdatedAt = order.UpdatedAt,
            EsSync = esSync,
        };
    }
}
