using ECommerce.Api.Config;
using ECommerce.Api.Infrastructure;
using ECommerce.Api.Models;
using ECommerce.Api.Repositories;
using ECommerce.Api.Services;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;

namespace ECommerce.Api.Controllers;

// ------------------------------------------------------------ users ---
[ApiController]
[Route("api/users")]
public sealed class UsersController(UsersRepository repo) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<UserDto>>> List(CancellationToken ct)
        => Ok((await repo.ListAsync(ct)).Select(u => new UserDto(u.Id, u.Name, u.Email)).ToList());
}

// ------------------------------------------------------------ products ---
[ApiController]
[Route("api/products")]
public sealed class ProductsController(ProductsRepository repo) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ProductDto>>> List(
        [FromQuery] string? category, [FromQuery] string? q,
        [FromQuery] string active = "true", CancellationToken ct = default)
    {
        bool? activeFilter = active switch
        {
            "true" => true,
            "false" => false,
            "all" => null,
            _ => throw new ApiException(422, $"invalid active value: {active}"),
        };
        return Ok(await repo.ListAsync(category, q, activeFilter, ct));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ProductDto>> Get(string id, CancellationToken ct)
    {
        var doc = await repo.GetAsync(id, ct);
        if (doc is null) throw new ApiException(404, $"product not found: {id}");
        return Ok(doc);
    }

    [HttpPost]
    public async Task<ActionResult<ProductDto>> Create([FromBody] ProductCreateDto dto,
        CancellationToken ct)
    {
        ValidateCreate(dto);
        try
        {
            var created = await repo.CreateAsync(dto, ct);
            return StatusCode(201, created);
        }
        catch (DuplicateSkuException ex)
        {
            throw new ApiException(409, ex.Message);
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ProductDto>> Update(string id, [FromBody] ProductUpdateDto dto,
        CancellationToken ct)
    {
        var patch = BuildPatch(dto); // throws 422 on empty/invalid update
        try
        {
            var updated = await repo.UpdateAsync(id, patch, ct);
            if (updated is null) throw new ApiException(404, $"product not found: {id}");
            return Ok(updated); // catalog edits touch Mongo only
        }
        catch (DuplicateSkuException ex)
        {
            throw new ApiException(409, ex.Message);
        }
    }

    private static void ValidateCreate(ProductCreateDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Sku)) throw new ApiException(422, "sku must not be blank");
        if (string.IsNullOrWhiteSpace(dto.Title)) throw new ApiException(422, "title must not be blank");
        if (string.IsNullOrWhiteSpace(dto.Category)) throw new ApiException(422, "category must not be blank");
        if (dto.Price <= 0) throw new ApiException(422, "price must be > 0");
        if (dto.Variants is not { Count: > 0 }) throw new ApiException(422, "at least one variant is required");
    }

    private static Dictionary<string, BsonValue> BuildPatch(ProductUpdateDto dto)
    {
        var patch = new Dictionary<string, BsonValue>();
        if (dto.Sku is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Sku)) throw new ApiException(422, "sku must not be blank");
            patch["sku"] = dto.Sku;
        }
        if (dto.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Title)) throw new ApiException(422, "title must not be blank");
            patch["title"] = dto.Title;
        }
        if (dto.Description is not null) patch["description"] = dto.Description;
        if (dto.Price is not null)
        {
            if (dto.Price.Value <= 0) throw new ApiException(422, "price must be > 0");
            patch["price"] = dto.Price.Value;
        }
        if (dto.Category is not null) patch["category"] = dto.Category;
        if (dto.Tags is not null) patch["tags"] = BsonConverter.FromObject(dto.Tags);
        if (dto.Attributes is not null) patch["attributes"] = BsonConverter.FromObject(dto.Attributes);
        if (dto.Variants is not null) patch["variants"] = BsonConverter.FromObject(dto.Variants);
        if (dto.Active is not null) patch["active"] = dto.Active.Value;
        if (patch.Count == 0) throw new ApiException(422, "empty update");
        return patch;
    }
}

// ------------------------------------------------------------ orders ---
[ApiController]
[Route("api/orders")]
public sealed class OrdersController(OrderService service) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<OrderCreateOutDto>> Create([FromBody] OrderCreateDto dto,
        CancellationToken ct)
    {
        ValidateCreate(dto);
        var (out_, statusCode) = await service.PlaceOrderAsync(dto, ct);
        return StatusCode(statusCode, out_);
    }

    [HttpGet("{orderId:int}")]
    public async Task<ActionResult<OrderDetailOutDto>> Get(int orderId, CancellationToken ct)
        => Ok(await service.GetDetailAsync(orderId, ct));

    [HttpPatch("{orderId:int}/status")]
    public async Task<ActionResult<StatusUpdateOutDto>> UpdateStatus(int orderId,
        [FromBody] StatusUpdateDto dto, CancellationToken ct)
        => Ok(await service.UpdateStatusAsync(orderId, dto.Status, ct));

    private static void ValidateCreate(OrderCreateDto dto)
    {
        if (dto.Items is not { Count: > 0 })
            throw new ApiException(422, "items must not be empty");
        foreach (var item in dto.Items)
        {
            if (string.IsNullOrWhiteSpace(item.ProductId))
                throw new ApiException(422, "product_id must not be blank");
            if (item.Quantity < 1)
                throw new ApiException(422, "quantity must be >= 1");
        }
    }
}

// ------------------------------------------------------------ search ---
[ApiController]
[Route("api/search")]
public sealed class SearchController(SearchRepository repo, AppOptions options) : ControllerBase
{
    private static readonly string[] SortFields = ["order_date", "total_amount", "order_id"];

    [HttpPost("orders")]
    public async Task<ActionResult<SearchResponseDto>> SearchOrders(
        [FromBody] SearchRequestDto dto, CancellationToken ct)
    {
        Validate(dto);
        try
        {
            return Ok(await repo.SearchOrdersAsync(options.EsIndex, dto, ct));
        }
        catch (EsUnavailableException)
        {
            throw new ApiException(503, "search unavailable: elasticsearch unreachable");
        }
    }

    private static void Validate(SearchRequestDto dto)
    {
        if (dto.Page < 1) throw new ApiException(422, "page must be >= 1");
        if (dto.PageSize < 1 || dto.PageSize > 100)
            throw new ApiException(422, "page_size must be between 1 and 100");
        if (!SortFields.Contains(dto.SortBy))
            throw new ApiException(422, $"invalid sort_by: {dto.SortBy}");
        if (dto.SortDir is not ("asc" or "desc"))
            throw new ApiException(422, $"invalid sort_dir: {dto.SortDir}");
        if (dto.Statuses is not null)
            foreach (var s in dto.Statuses)
                if (!OrderService.Statuses.Contains(s))
                    throw new ApiException(422, $"invalid status: {s}");
    }
}

// ------------------------------------------------------------ health ---
[ApiController]
[Route("api/health")]
public sealed class HealthController(
    NpgsqlDataSource pg, IMongoDatabase mongo, IEsGateway es, AppOptions options) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<HealthDto>> Get(CancellationToken ct)
    {
        var pgUp = await PgOk(ct);
        var mongoUp = await MongoOk(ct);
        var esUp = await EsOk(ct);
        return Ok(new HealthDto
        {
            Status = pgUp && mongoUp && esUp ? "ok" : "degraded",
            Postgres = pgUp ? "up" : "down",
            Mongodb = mongoUp ? "up" : "down",
            Elasticsearch = esUp ? "up" : "down",
            SyncStrategy = options.NormalizedStrategy,
        });
    }

    private async Task<bool> PgOk(CancellationToken ct)
    {
        try
        {
            await using var conn = await pg.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch { return false; }
    }

    private async Task<bool> MongoOk(CancellationToken ct)
    {
        try
        {
            var admin = mongo.Client.GetDatabase("admin");
            await admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            return true;
        }
        catch { return false; }
    }

    private async Task<bool> EsOk(CancellationToken ct)
    {
        try { return await es.PingAsync(ct); }
        catch { return false; }
    }
}

// ------------------------------------------------------------ admin ---
[ApiController]
[Route("api/admin")]
public sealed class AdminController(
    OrdersRepository orders, SearchRepository search, AppOptions options, SyncService sync)
    : ControllerBase
{
    [HttpGet("sync-status")]
    public async Task<ActionResult<SyncStatusDto>> SyncStatus(CancellationToken ct)
    {
        var counts = await orders.CountOutboxByStatusAsync(ct);
        counts.TryGetValue("pending", out var pending);
        counts.TryGetValue("processing", out var processing);
        counts.TryGetValue("failed", out var failed);
        var watermark = await orders.GetSyncStateAsync(OrdersRepository.PollingWatermarkKey, ct);
        var docs = await search.CountDocsAsync(options.EsIndex, ct);
        return Ok(new SyncStatusDto
        {
            OutboxPending = pending + processing,
            OutboxFailed = failed,
            PollingWatermark = watermark,
            ElasticsearchDocs = docs,
        });
    }

    [HttpPost("reindex")]
    public async Task<ActionResult<ReindexDto>> Reindex(CancellationToken ct)
    {
        try
        {
            var indexed = await sync.ReindexAllAsync(ct);
            return Ok(new ReindexDto(indexed));
        }
        catch (EsUnavailableException)
        {
            throw new ApiException(503, "reindex unavailable: elasticsearch unreachable");
        }
    }
}
