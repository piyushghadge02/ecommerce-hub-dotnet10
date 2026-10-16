using System.Text.Json;
using System.Text.Json.Nodes;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using ECommerce.Api.Infrastructure;
using ECommerce.Api.Models;
using EsHttpMethod = Elastic.Transport.HttpMethod;

namespace ECommerce.Api.Repositories;

/// <summary>Thrown when Elasticsearch cannot be reached (as opposed to a 404).</summary>
public sealed class EsUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Thin async gateway over the official Elasticsearch client (low-level API),
/// so the query/mapping JSON stays byte-identical to the reference implementation.
/// </summary>
public interface IEsGateway
{
    Task<bool> PingAsync(CancellationToken ct = default);
    Task<bool> IndexExistsAsync(string index, CancellationToken ct = default);
    Task CreateIndexAsync(string index, string mappingJson, CancellationToken ct = default);
    Task DeleteIndexAsync(string index, CancellationToken ct = default);
    Task IndexDocumentAsync(string index, string id, string docJson, CancellationToken ct = default);
    /// <summary>Returns the _source JSON, null on 404. Throws <see cref="EsUnavailableException"/> when unreachable.</summary>
    Task<string?> GetDocumentAsync(string index, string id, CancellationToken ct = default);
    Task<long?> CountAsync(string index, CancellationToken ct = default);
    Task<string> SearchAsync(string index, string bodyJson, CancellationToken ct = default);
    Task RefreshAsync(string index, CancellationToken ct = default);
}

public sealed class EsGateway : IEsGateway
{
    private readonly ElasticsearchClient _client;

    public EsGateway(string url)
    {
        var settings = new ElasticsearchClientSettings(new Uri(url))
            .RequestTimeout(TimeSpan.FromSeconds(5))
            .DisableDirectStreaming();
        _client = new ElasticsearchClient(settings);
    }

    private static EsUnavailableException Unavailable(Exception ex)
        => new("elasticsearch unreachable", ex);

    private async Task<StringResponse> RawAsync(EsHttpMethod method, string path,
        PostData? data, CancellationToken ct)
    {
        try
        {
            return await _client.Transport.RequestAsync<StringResponse>(method, path, data, null, ct);
        }
        catch (Exception ex) { throw Unavailable(ex); }
    }

    private static void EnsureOk(StringResponse resp, string op)
    {
        if (!resp.ApiCallDetails.HasSuccessfulStatusCode)
            throw new EsUnavailableException(
                $"{op} failed: {(int?)resp.ApiCallDetails.HttpStatusCode}");
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await RawAsync(EsHttpMethod.HEAD, "/", null, ct);
            return resp.ApiCallDetails.HasSuccessfulStatusCode;
        }
        catch (EsUnavailableException) { return false; }
    }

    public async Task<bool> IndexExistsAsync(string index, CancellationToken ct = default)
    {
        var resp = await RawAsync(EsHttpMethod.HEAD, "/" + Uri.EscapeDataString(index), null, ct);
        if (resp.ApiCallDetails.HttpStatusCode == 404) return false;
        EnsureOk(resp, "index exists check");
        return true;
    }

    public async Task CreateIndexAsync(string index, string mappingJson, CancellationToken ct = default)
    {
        var resp = await RawAsync(EsHttpMethod.PUT, "/" + Uri.EscapeDataString(index),
            PostData.String(mappingJson), ct);
        EnsureOk(resp, "create index");
    }

    public async Task DeleteIndexAsync(string index, CancellationToken ct = default)
    {
        var resp = await RawAsync(EsHttpMethod.DELETE, "/" + Uri.EscapeDataString(index), null, ct);
        if (resp.ApiCallDetails.HttpStatusCode == 404) return;
        EnsureOk(resp, "delete index");
    }

    public async Task IndexDocumentAsync(string index, string id, string docJson, CancellationToken ct = default)
    {
        var resp = await RawAsync(EsHttpMethod.PUT,
            $"/{Uri.EscapeDataString(index)}/_doc/{Uri.EscapeDataString(id)}",
            PostData.String(docJson), ct);
        EnsureOk(resp, "index document");
    }

    public async Task<string?> GetDocumentAsync(string index, string id, CancellationToken ct = default)
    {
        var resp = await RawAsync(EsHttpMethod.GET,
            $"/{Uri.EscapeDataString(index)}/_doc/{Uri.EscapeDataString(id)}", null, ct);
        if (resp.ApiCallDetails.HttpStatusCode == 404) return null;
        EnsureOk(resp, "get document");
        using var doc = JsonDocument.Parse(resp.Body);
        return doc.RootElement.GetProperty("_source").GetRawText();
    }

    public async Task<long?> CountAsync(string index, CancellationToken ct = default)
    {
        try
        {
            var resp = await RawAsync(EsHttpMethod.GET,
                "/" + Uri.EscapeDataString(index) + "/_count", null, ct);
            if (!resp.ApiCallDetails.HasSuccessfulStatusCode) return null;
            using var doc = JsonDocument.Parse(resp.Body);
            return doc.RootElement.GetProperty("count").GetInt64();
        }
        catch { return null; }
    }

    public async Task<string> SearchAsync(string index, string bodyJson, CancellationToken ct = default)
    {
        var resp = await RawAsync(EsHttpMethod.POST,
            "/" + Uri.EscapeDataString(index) + "/_search", PostData.String(bodyJson), ct);
        if (resp.ApiCallDetails.HttpStatusCode == 404)
            return """{"hits":{"total":{"value":0},"hits":[]},"aggregations":{}}""";
        EnsureOk(resp, "search");
        return resp.Body;
    }

    public async Task RefreshAsync(string index, CancellationToken ct = default)
    {
        try
        {
            await RawAsync(EsHttpMethod.POST,
                "/" + Uri.EscapeDataString(index) + "/_refresh", null, ct);
        }
        catch { /* best effort */ }
    }
}

/// <summary>Denormalized ES order document (parsed _source) for sync comparisons.</summary>
public sealed record EsOrderDoc(int OrderId, DateTimeOffset OrderDate, string Status,
    double TotalAmount, DateTimeOffset UpdatedAt);

/// <summary>
/// The `orders` index: denormalized snapshots with deterministic ids `order-{id}`.
/// The admin search dashboard reads ONLY from here. `items` is a nested mapping.
/// </summary>
public sealed class SearchRepository(IEsGateway gateway)
{
    public static string DocId(int orderId) => $"order-{orderId}";

    public const string OrdersMapping = """
{
  "mappings": {
    "properties": {
      "order_id": { "type": "long" },
      "order_date": { "type": "date" },
      "status": { "type": "keyword" },
      "total_amount": { "type": "double" },
      "updated_at": { "type": "date" },
      "customer": {
        "type": "object",
        "properties": {
          "id": { "type": "long" },
          "name": { "type": "text", "fields": { "keyword": { "type": "keyword", "ignore_above": 256 } } },
          "email": { "type": "keyword" }
        }
      },
      "items": {
        "type": "nested",
        "properties": {
          "product_id": { "type": "keyword" },
          "title": { "type": "text", "fields": { "keyword": { "type": "keyword", "ignore_above": 256 } } },
          "quantity": { "type": "integer" },
          "unit_price": { "type": "double" }
        }
      }
    }
  }
}
""";

    private static readonly Dictionary<string, string> SortFields = new()
    {
        ["order_date"] = "order_date",
        ["total_amount"] = "total_amount",
        ["order_id"] = "order_id",
    };

    /// <summary>Create the index with the explicit mapping if missing. Never throws.</summary>
    public async Task<bool> EnsureIndexAsync(string index, CancellationToken ct = default)
    {
        try
        {
            if (!await gateway.IndexExistsAsync(index, ct))
                await gateway.CreateIndexAsync(index, OrdersMapping, ct);
            return true;
        }
        catch { return false; }
    }

    public Task UpsertOrderDocAsync(string index, Dictionary<string, object?> doc, CancellationToken ct = default)
        => gateway.IndexDocumentAsync(index, DocId(Convert.ToInt32(doc["order_id"])),
            JsonSerializer.Serialize(doc), ct);

    public async Task<EsOrderDoc?> GetOrderDocAsync(string index, int orderId, CancellationToken ct = default)
    {
        var json = await gateway.GetDocumentAsync(index, DocId(orderId), ct);
        return json is null ? null : ParseOrderDoc(json);
    }

    public Task<long?> CountDocsAsync(string index, CancellationToken ct = default)
        => gateway.CountAsync(index, ct);

    public Task DeleteIndexAsync(string index, CancellationToken ct = default)
        => gateway.DeleteIndexAsync(index, ct);

    public Task RefreshAsync(string index, CancellationToken ct = default)
        => gateway.RefreshAsync(index, ct);

    public static Dictionary<string, object?> BuildOrderDoc(DbOrder order, DbUser user,
        List<DbOrderItem> items)
    {
        static string Iso(DateTimeOffset dto) => Money.TruncateToSecond(dto).ToString("O");
        return new Dictionary<string, object?>
        {
            ["order_id"] = (long)order.Id,
            ["order_date"] = Iso(order.OrderDate),
            ["status"] = order.Status,
            ["total_amount"] = (double)order.TotalAmount,
            ["updated_at"] = Iso(order.UpdatedAt),
            ["customer"] = new Dictionary<string, object?>
            {
                ["id"] = (long)user.Id, ["name"] = user.Name, ["email"] = user.Email,
            },
            ["items"] = items.Select(i => (object?)new Dictionary<string, object?>
            {
                ["product_id"] = i.ProductId,
                ["title"] = i.Title,
                ["quantity"] = i.Quantity,
                ["unit_price"] = (double)i.UnitPrice,
            }).ToList(),
        };
    }

    public static EsOrderDoc ParseOrderDoc(string sourceJson)
    {
        using var doc = JsonDocument.Parse(sourceJson);
        var r = doc.RootElement;
        return new EsOrderDoc(
            r.GetProperty("order_id").GetInt32(),
            ParseDt(r.GetProperty("order_date").GetString()!),
            r.GetProperty("status").GetString()!,
            r.GetProperty("total_amount").GetDouble(),
            ParseDt(r.GetProperty("updated_at").GetString()!));
    }

    public static DateTimeOffset ParseDt(string value)
    {
        var dto = DateTimeOffset.Parse(value, null,
            System.Globalization.DateTimeStyles.RoundtripKind);
        return dto.Offset == TimeSpan.Zero ? dto
            : new DateTimeOffset(dto.DateTime, TimeSpan.Zero);
    }

    /// <summary>
    /// Build the ES query body: bool + multi_match (+ nested) + filters + aggs.
    /// Aggregations run in the SAME filter context as the hits.
    /// </summary>
    public static string BuildSearchBody(SearchRequestDto p)
    {
        var boolObj = new JsonObject();
        if (!string.IsNullOrWhiteSpace(p.Q))
        {
            boolObj["must"] = new JsonArray
            {
                new JsonObject
                {
                    ["bool"] = new JsonObject
                    {
                        ["should"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["multi_match"] = new JsonObject
                                {
                                    ["query"] = p.Q,
                                    ["fields"] = new JsonArray(
                                        JsonValue.Create("customer.name"),
                                        JsonValue.Create("customer.email"),
                                        JsonValue.Create("items.title")),
                                    ["type"] = "best_fields",
                                }
                            },
                            new JsonObject
                            {
                                ["nested"] = new JsonObject
                                {
                                    ["path"] = "items",
                                    ["query"] = new JsonObject
                                    {
                                        ["match"] = new JsonObject { ["items.title"] = p.Q }
                                    },
                                }
                            },
                        },
                        ["minimum_should_match"] = 1,
                    }
                }
            };
        }

        var filters = new JsonArray();
        if (p.Statuses is { Count: > 0 })
            filters.Add(new JsonObject
            {
                ["terms"] = new JsonObject
                {
                    ["status"] = new JsonArray(p.Statuses.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
                }
            });
        if (p.DateFrom is not null || p.DateTo is not null)
        {
            var rng = new JsonObject();
            if (p.DateFrom is not null) rng["gte"] = p.DateFrom.Value.ToString("yyyy-MM-dd");
            if (p.DateTo is not null) rng["lte"] = p.DateTo.Value.ToString("yyyy-MM-dd") + "T23:59:59";
            filters.Add(new JsonObject { ["range"] = new JsonObject { ["order_date"] = rng } });
        }
        if (p.MinTotal is not null || p.MaxTotal is not null)
        {
            var rng = new JsonObject();
            if (p.MinTotal is not null) rng["gte"] = p.MinTotal.Value;
            if (p.MaxTotal is not null) rng["lte"] = p.MaxTotal.Value;
            filters.Add(new JsonObject { ["range"] = new JsonObject { ["total_amount"] = rng } });
        }

        JsonNode query;
        if (boolObj.Count > 0 || filters.Count > 0)
        {
            if (boolObj.Count > 0) { /* must already set */ }
            if (filters.Count > 0) boolObj["filter"] = filters;
            query = new JsonObject { ["bool"] = boolObj };
        }
        else
        {
            query = new JsonObject { ["match_all"] = new JsonObject() };
        }

        var from = (p.Page - 1) * p.PageSize;
        var body = new JsonObject
        {
            ["query"] = query,
            ["sort"] = new JsonArray
            {
                new JsonObject
                {
                    [SortFields[p.SortBy]] = new JsonObject { ["order"] = p.SortDir }
                }
            },
            ["from"] = from,
            ["size"] = p.PageSize,
            ["aggs"] = new JsonObject
            {
                ["revenue"] = new JsonObject { ["sum"] = new JsonObject { ["field"] = "total_amount" } },
                ["by_status"] = new JsonObject { ["terms"] = new JsonObject { ["field"] = "status", ["size"] = 10 } },
                ["total_orders"] = new JsonObject { ["value_count"] = new JsonObject { ["field"] = "order_id" } },
            },
            ["track_total_hits"] = true,
        };
        return body.ToJsonString();
    }

    public async Task<SearchResponseDto> SearchOrdersAsync(string index, SearchRequestDto p,
        CancellationToken ct = default)
    {
        var body = BuildSearchBody(p);
        var responseJson = await gateway.SearchAsync(index, body, ct);
        return ParseSearchResponse(responseJson, p);
    }

    public static SearchResponseDto ParseSearchResponse(string responseJson, SearchRequestDto p)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var hits = root.GetProperty("hits");
        var total = hits.GetProperty("total").GetProperty("value").GetInt32();

        var orders = new List<SearchOrderRowDto>();
        foreach (var h in hits.GetProperty("hits").EnumerateArray())
        {
            var s = h.GetProperty("_source");
            var c = s.GetProperty("customer");
            orders.Add(new SearchOrderRowDto
            {
                OrderId = s.GetProperty("order_id").GetInt32(),
                OrderDate = ParseDt(s.GetProperty("order_date").GetString()!),
                Customer = new SearchCustomerDto(
                    c.GetProperty("id").GetInt32(),
                    c.GetProperty("name").GetString() ?? "",
                    c.GetProperty("email").GetString() ?? ""),
                TotalAmount = s.GetProperty("total_amount").GetDouble(),
                Status = s.GetProperty("status").GetString() ?? "PENDING",
            });
        }

        var revenue = 0.0;
        var byStatus = new Dictionary<string, int>();
        var totalOrders = 0;
        if (root.TryGetProperty("aggregations", out var aggs) && aggs.ValueKind == JsonValueKind.Object)
        {
            if (aggs.TryGetProperty("revenue", out var rev) &&
                rev.TryGetProperty("value", out var rv) && rv.ValueKind == JsonValueKind.Number)
                revenue = rv.GetDouble();
            if (aggs.TryGetProperty("by_status", out var bs) &&
                bs.TryGetProperty("buckets", out var buckets))
                foreach (var b in buckets.EnumerateArray())
                    byStatus[b.GetProperty("key").GetString()!] = b.GetProperty("doc_count").GetInt32();
            if (aggs.TryGetProperty("total_orders", out var to) &&
                to.TryGetProperty("value", out var tv) && tv.ValueKind == JsonValueKind.Number)
                totalOrders = (int)tv.GetDouble();
        }

        return new SearchResponseDto
        {
            Total = total,
            Page = p.Page,
            PageSize = p.PageSize,
            TotalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)p.PageSize),
            Orders = orders,
            Aggs = new SearchAggsDto { Revenue = revenue, ByStatus = byStatus, TotalOrders = totalOrders },
        };
    }
}
