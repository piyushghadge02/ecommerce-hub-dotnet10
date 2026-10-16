using System.Text.Json.Serialization;
using ECommerce.Api.Infrastructure;

namespace ECommerce.Api.Models;

// ---------------------------------------------------------------- users ---
public sealed record UserDto(int Id, string Name, string Email);

// -------------------------------------------------------------- products ---
public sealed class ProductDto
{
    public string Id { get; init; } = "";
    public string Sku { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public double Price { get; init; }
    public string Category { get; init; } = "";
    public List<string> Tags { get; init; } = new();
    public Dictionary<string, object?> Attributes { get; init; } = new();
    public List<Dictionary<string, object?>> Variants { get; init; } = new();
    public bool Active { get; init; } = true;
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed class ProductCreateDto
{
    public string Sku { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public double Price { get; init; }
    public string Category { get; init; } = "";
    public List<string> Tags { get; init; } = new();
    public Dictionary<string, object?> Attributes { get; init; } = new();
    public List<Dictionary<string, object?>> Variants { get; init; } = new();
    public bool Active { get; init; } = true;
}

public sealed class ProductUpdateDto
{
    public string? Sku { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public double? Price { get; init; }
    public string? Category { get; init; }
    public List<string>? Tags { get; init; }
    public Dictionary<string, object?>? Attributes { get; init; }
    public List<Dictionary<string, object?>>? Variants { get; init; }
    public bool? Active { get; init; }
}

// ---------------------------------------------------------------- orders ---
public sealed record OrderItemCreateDto(string ProductId, int Quantity);
public sealed record OrderCreateDto(int UserId, List<OrderItemCreateDto> Items, string? IdempotencyKey);

public sealed class OrderItemDto
{
    public string ProductId { get; init; } = "";
    public string Title { get; init; } = "";
    public int Quantity { get; init; }
    [JsonConverter(typeof(DecimalAsStringConverter))]
    public decimal UnitPrice { get; init; }
    [JsonConverter(typeof(DecimalAsStringConverter))]
    public decimal LineTotal { get; init; }
}

public sealed record EsSyncDto(string Strategy, string Status);

public sealed class OrderCreateOutDto
{
    public int OrderId { get; init; }
    public int UserId { get; init; }
    public string Status { get; init; } = "";
    [JsonConverter(typeof(DecimalAsStringConverter))]
    public decimal TotalAmount { get; init; }
    public DateTimeOffset OrderDate { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public List<OrderItemDto> Items { get; init; } = new();
    public EsSyncDto EsSync { get; init; } = new("", "");
}

public sealed class OrderSyncDto
{
    public bool? EsInSync { get; init; }
    public DateTimeOffset PgUpdatedAt { get; init; }
    public DateTimeOffset? EsUpdatedAt { get; init; }
}

public sealed class OrderDetailOutDto
{
    public int OrderId { get; init; }
    public int UserId { get; init; }
    public UserDto Customer { get; init; } = new(0, "", "");
    public string Status { get; init; } = "";
    [JsonConverter(typeof(DecimalAsStringConverter))]
    public decimal TotalAmount { get; init; }
    public DateTimeOffset OrderDate { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public List<OrderItemDto> Items { get; init; } = new();
    public OrderSyncDto Sync { get; init; } = new();
}

public sealed record StatusUpdateDto(string Status);

public sealed class StatusUpdateOutDto
{
    public int OrderId { get; init; }
    public string Status { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    public EsSyncDto EsSync { get; init; } = new("", "");
}

// ---------------------------------------------------------------- search ---
public sealed class SearchRequestDto
{
    public string? Q { get; init; }
    public List<string>? Statuses { get; init; }
    public DateOnly? DateFrom { get; init; }
    public DateOnly? DateTo { get; init; }
    public double? MinTotal { get; init; }
    public double? MaxTotal { get; init; }
    public string SortBy { get; init; } = "order_date";
    public string SortDir { get; init; } = "desc";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

public sealed record SearchCustomerDto(int Id, string Name, string Email);

public sealed class SearchOrderRowDto
{
    public int OrderId { get; init; }
    public DateTimeOffset OrderDate { get; init; }
    public SearchCustomerDto Customer { get; init; } = new(0, "", "");
    public double TotalAmount { get; init; }
    public string Status { get; init; } = "";
}

public sealed class SearchAggsDto
{
    public double Revenue { get; init; }
    public Dictionary<string, int> ByStatus { get; init; } = new();
    public int TotalOrders { get; init; }
}

public sealed class SearchResponseDto
{
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }
    public List<SearchOrderRowDto> Orders { get; init; } = new();
    public SearchAggsDto Aggs { get; init; } = new();
}

// ----------------------------------------------------------------- admin ---
public sealed class HealthDto
{
    public string Status { get; init; } = "";
    public string Postgres { get; init; } = "";
    public string Mongodb { get; init; } = "";
    public string Elasticsearch { get; init; } = "";
    public string SyncStrategy { get; init; } = "";
}

public sealed class SyncStatusDto
{
    public int OutboxPending { get; init; }
    public int OutboxFailed { get; init; }
    public string? PollingWatermark { get; init; }
    public long? ElasticsearchDocs { get; init; }
}

public sealed record ReindexDto(int Indexed);

// --------------------------------------------------------------- entities ---
/// <summary>Internal PG row shapes (not serialized directly).</summary>
public sealed record DbUser(int Id, string Name, string Email);
public sealed record DbOrder(int Id, int UserId, DateTimeOffset OrderDate, string Status,
    decimal TotalAmount, DateTimeOffset UpdatedAt, string? IdempotencyKey);
public sealed record DbOrderItem(int Id, int OrderId, string ProductId, string Title,
    int Quantity, decimal UnitPrice);
public sealed record DbOutboxRow(int Id, int OrderId, string EventType, string PayloadJson,
    string Status, int Attempts, DateTimeOffset NextAttemptAt, string? LastError, DateTimeOffset CreatedAt);

/// <summary>Validated checkout line: (product_id, title snapshot, quantity, unit_price).</summary>
public sealed record OrderLine(string ProductId, string Title, int Quantity, decimal UnitPrice);

public sealed record OrderFull(DbOrder Order, DbUser User, List<DbOrderItem> Items);
public sealed record OutboxEvent(string EventType, string PayloadJson);
