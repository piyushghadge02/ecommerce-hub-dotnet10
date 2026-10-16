using ECommerce.Api.Config;
using ECommerce.Api.Data;
using ECommerce.Api.Models;
using ECommerce.Api.Repositories;
using ECommerce.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;

namespace ECommerce.Tests.Integration;

/// <summary>
/// Boots the real API with isolated test resources:
/// a dedicated PG schema in the `ecommerce` DB, Mongo database `ecommerce_test`,
/// and ES index `orders_test`. The demo data is never touched.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly AppOptions _options;
    public ApiFactory(AppOptions options) => _options = options;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Registered after Program's own AppOptions: last registration wins.
            services.AddSingleton(_options);
        });
    }
}

[CollectionDefinition("api", DisableParallelization = true)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;

public sealed class ApiFixture : IAsyncLifetime
{
    public string SchemaName { get; } = "t_" + Guid.NewGuid().ToString("N")[..12];
    public AppOptions Options { get; private set; } = null!;
    public ApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public OrdersRepository Orders => Factory.Services.GetRequiredService<OrdersRepository>();
    public UsersRepository Users => Factory.Services.GetRequiredService<UsersRepository>();
    public ProductsRepository Products => Factory.Services.GetRequiredService<ProductsRepository>();
    public SearchRepository Search => Factory.Services.GetRequiredService<SearchRepository>();
    public SyncService Sync => Factory.Services.GetRequiredService<SyncService>();

    private const string AdminDsn =
        "Host=localhost;Port=5432;Database=ecommerce;Username=ecom;Password=ecom";

    public async Task InitializeAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(AdminDsn))
        await using (var conn = await admin.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand($"CREATE SCHEMA \"{SchemaName}\"", conn))
            await cmd.ExecuteNonQueryAsync();

        Options = new AppOptions
        {
            PostgresDsn = $"postgresql://ecom:ecom@localhost:5432/ecommerce?search_path={SchemaName}",
            MongoUri = "mongodb://127.0.0.1:27017",
            MongoDb = "ecommerce_test",
            ElasticsearchUrl = "http://localhost:9200",
            EsIndex = "orders_test",
            SyncStrategy = "dual_write",
            StartWorkers = false,
        };
        Factory = new ApiFactory(Options);
        Client = Factory.CreateClient();

        // Ensure test resources (Program's startup also runs these; idempotent).
        await Schema.EnsureAsync(Orders.DataSource);
        await Products.EnsureIndexesAsync();
        Assert.True(await Search.EnsureIndexAsync(Options.EsIndex),
            "elasticsearch must be reachable for integration tests");

        await SeedMinimalAsync();
    }

    /// <summary>Minimal fixtures: 2 users + 3 products (1 inactive).</summary>
    public async Task SeedMinimalAsync()
    {
        await using var ds = Orders.DataSource;
        await using var conn = await ds.OpenConnectionAsync();
        foreach (var (name, email) in new[] {
                     ("Test User One", "one@example.com"),
                     ("Wendy Wireless", "wendy.wireless@example.com") })
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO users (name, email) VALUES (@n, @e) ON CONFLICT (email) DO NOTHING", conn);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("e", email);
            await cmd.ExecuteNonQueryAsync();
        }

        await Products.Collection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
        await Products.CreateAsync(new ProductCreateDto
        {
            Sku = "TEST-001", Title = "Test Widget", Price = 25.00, Category = "office",
            Tags = ["test"], Variants = [new Dictionary<string, object?> { ["sku"] = "TEST-001-A", ["stock"] = 10 }],
        });
        await Products.CreateAsync(new ProductCreateDto
        {
            Sku = "TEST-002", Title = "Wireless Test Gadget", Price = 60.00, Category = "audio",
            Tags = ["wireless", "test"], Variants = [new Dictionary<string, object?> { ["sku"] = "TEST-002-A", ["stock"] = 5 }],
        });
        await Products.CreateAsync(new ProductCreateDto
        {
            Sku = "TEST-003", Title = "Inactive Thing", Price = 10.00, Category = "cables",
            Variants = [new Dictionary<string, object?> { ["sku"] = "TEST-003-A", ["stock"] = 1 }],
            Active = false,
        });
    }

    public async Task<int> UserIdByEmailAsync(string email)
    {
        await using var conn = await Orders.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT id FROM users WHERE email = @e", conn);
        cmd.Parameters.AddWithValue("e", email);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task<string> ProductIdBySkuAsync(string sku)
    {
        var doc = await Products.Collection
            .Find(Builders<BsonDocument>.Filter.Eq("sku", sku)).FirstAsync();
        return doc["_id"].AsObjectId.ToString();
    }

    public async Task DisposeAsync()
    {
        try
        {
            await using var admin = NpgsqlDataSource.Create(AdminDsn);
            await using var conn = await admin.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{SchemaName}\" CASCADE", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best effort */ }
        try
        {
            var mongo = new MongoClient(Options.MongoUri);
            await mongo.DropDatabaseAsync(Options.MongoDb);
        }
        catch { /* best effort */ }
        try { await Search.DeleteIndexAsync(Options.EsIndex); }
        catch { /* best effort */ }
        Factory.Dispose();
    }
}
