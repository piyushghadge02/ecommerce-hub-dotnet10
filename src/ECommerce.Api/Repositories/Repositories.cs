using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using ECommerce.Api.Infrastructure;
using ECommerce.Api.Models;

namespace ECommerce.Api.Repositories;

// ------------------------------------------------------------ users ---
public sealed class UsersRepository(NpgsqlDataSource ds)
{
    public async Task<List<DbUser>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id, name, email FROM users ORDER BY id", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DbUser>();
        while (await r.ReadAsync(ct))
            list.Add(new DbUser(r.GetInt32(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public async Task<DbUser?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id, name, email FROM users WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new DbUser(r.GetInt32(0), r.GetString(1), r.GetString(2)) : null;
    }
}

// ------------------------------------------------------------ products ---
public sealed class DuplicateSkuException(string sku) : Exception($"duplicate sku: {sku}")
{
    public string Sku { get; } = sku;
}

public sealed class ProductsRepository
{
    public const string CollectionName = "products";
    private readonly IMongoCollection<BsonDocument> _coll;

    public ProductsRepository(IMongoDatabase db)
        => _coll = db.GetCollection<BsonDocument>(CollectionName);

    public IMongoCollection<BsonDocument> Collection => _coll;

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        var idx = _coll.Indexes;
        await idx.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("sku"),
            new CreateIndexOptions { Unique = true }), cancellationToken: ct);
        await idx.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("category")), cancellationToken: ct);
        await idx.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("active")), cancellationToken: ct);
    }

    public async Task<List<ProductDto>> ListAsync(string? category, string? q, bool? active,
        CancellationToken ct = default)
    {
        var f = Builders<BsonDocument>.Filter.Empty;
        if (active.HasValue) f &= Builders<BsonDocument>.Filter.Eq("active", active.Value);
        if (!string.IsNullOrEmpty(category)) f &= Builders<BsonDocument>.Filter.Eq("category", category);
        if (!string.IsNullOrEmpty(q))
        {
            var rx = new BsonRegularExpression(q, "i");
            f &= Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Regex("title", rx),
                Builders<BsonDocument>.Filter.Regex("description", rx),
                Builders<BsonDocument>.Filter.Regex("tags", rx));
        }
        var docs = await _coll.Find(f).SortBy(d => d["title"]).ToListAsync(ct);
        return docs.Select(ToDto).ToList();
    }

    public async Task<ProductDto?> GetAsync(string id, CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(id, out var oid)) return null;
        var doc = await _coll.Find(Builders<BsonDocument>.Filter.Eq("_id", oid))
            .FirstOrDefaultAsync(ct);
        return doc is null ? null : ToDto(doc);
    }

    public async Task<ProductDto> CreateAsync(ProductCreateDto dto, CancellationToken ct = default)
    {
        var doc = new BsonDocument
        {
            ["sku"] = dto.Sku,
            ["title"] = dto.Title,
            ["description"] = dto.Description ?? "",
            ["price"] = dto.Price,
            ["category"] = dto.Category,
            ["tags"] = new BsonArray(dto.Tags),
            ["attributes"] = BsonConverter.FromObject(dto.Attributes),
            ["variants"] = new BsonArray(dto.Variants.Select(BsonConverter.FromObject)),
            ["active"] = dto.Active,
            ["updated_at"] = DateTime.UtcNow,
        };
        try
        {
            await _coll.InsertOneAsync(doc, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateSkuException(dto.Sku);
        }
        return ToDto(doc);
    }

    public async Task<ProductDto?> UpdateAsync(string id, Dictionary<string, BsonValue> patch,
        CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(id, out var oid)) return null;
        patch["updated_at"] = new BsonDateTime(DateTime.UtcNow);
        var update = Builders<BsonDocument>.Update.Combine(
            patch.Select(kv => Builders<BsonDocument>.Update.Set(kv.Key, kv.Value)));
        try
        {
            var result = await _coll.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", oid), update, cancellationToken: ct);
            if (result.MatchedCount == 0) return null;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateSkuException(patch.TryGetValue("sku", out var s) ? s.AsString : "?");
        }
        var doc = await _coll.Find(Builders<BsonDocument>.Filter.Eq("_id", oid))
            .FirstOrDefaultAsync(ct);
        return doc is null ? null : ToDto(doc);
    }

    public static ProductDto ToDto(BsonDocument d)
    {
        BsonValue Get(string name, BsonValue fallback) =>
            d.TryGetValue(name, out var v) ? v : fallback;
        return new ProductDto
        {
            Id = d["_id"].AsObjectId.ToString(),
            Sku = Get("sku", "").AsString,
            Title = Get("title", "").AsString,
            Description = Get("description", BsonNull.Value).BsonType == BsonType.Null
                ? null : Get("description", "").AsString,
            Price = Get("price", 0).ToDouble(),
            Category = Get("category", "").AsString,
            Tags = Get("tags", new BsonArray()).AsBsonArray.Select(x => x.AsString).ToList(),
            Attributes = (Dictionary<string, object?>)BsonConverter.ToObject(
                Get("attributes", new BsonDocument()))!,
            Variants = Get("variants", new BsonArray()).AsBsonArray
                .Select(x => (Dictionary<string, object?>)BsonConverter.ToObject(x)!).ToList(),
            Active = Get("active", true).AsBoolean,
            UpdatedAt = Get("updated_at", BsonNull.Value).BsonType == BsonType.DateTime
                ? new DateTimeOffset(Get("updated_at", BsonNull.Value).ToUniversalTime()) : null,
        };
    }
}
