using ECommerce.Api.Config;
using ECommerce.Api.Data;
using ECommerce.Api.Infrastructure;
using ECommerce.Api.Repositories;
using ECommerce.Api.Services;
using ECommerce.Api.Tools;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Npgsql;

var apiArgs = args;

// ---- CLI tools: dotnet run -- seed [--reset] | dotnet run -- reindex ----
if (apiArgs.Any(a => a.Equals("seed", StringComparison.OrdinalIgnoreCase)))
{
    var options = LoadOptions();
    return await SeedTool.RunAsync(options, apiArgs.Contains("--reset"));
}
if (apiArgs.Any(a => a.Equals("reindex", StringComparison.OrdinalIgnoreCase)))
{
    var options = LoadOptions();
    return await SeedTool.RunReindexAsync(options);
}

// ---- Web API ----
var builder = WebApplication.CreateBuilder(apiArgs);
var appOptions = AppOptions.Load(builder.Configuration);

builder.Services.AddSingleton(appOptions);
builder.Services.AddSingleton(sp =>
    NpgsqlDataSource.Create(sp.GetRequiredService<AppOptions>().NpgsqlConnectionString()));
builder.Services.AddSingleton<IMongoDatabase>(sp =>
{
    var o = sp.GetRequiredService<AppOptions>();
    return new MongoClient(o.MongoUri).GetDatabase(o.MongoDb);
});
builder.Services.AddSingleton<IEsGateway>(sp =>
    new EsGateway(sp.GetRequiredService<AppOptions>().ElasticsearchUrl));
builder.Services.AddSingleton<UsersRepository>();
builder.Services.AddSingleton<ProductsRepository>();
builder.Services.AddSingleton<OrdersRepository>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddSingleton<OrderService>();
builder.Services.AddSingleton<SearchRepository>();
builder.Services.AddHostedService<SyncWorker>();

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = SnakeCaseNamingPolicy.Instance;
    // NOTE: DictionaryKeyPolicy intentionally unset — by_status keys ("PENDING"...)
    // and ES doc keys must keep their exact casing.
});
// FastAPI-compatible validation errors: 422 with a {"detail": [...]} envelope.
builder.Services.Configure<ApiBehaviorOptions>(o =>
{
    o.InvalidModelStateResponseFactory = ctx =>
    {
        var details = ctx.ModelState
            .SelectMany(kv => kv.Value!.Errors.Select(e => new
            {
                loc = new[] { "body", kv.Key },
                msg = e.ErrorMessage,
                type = "value_error",
            }));
        return new UnprocessableEntityObjectResult(new { detail = details });
    };
});

builder.Services.AddCors(p => p.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyMethod()
    .AllowAnyHeader()
    .AllowCredentials()));

var apiHost = Environment.GetEnvironmentVariable("API_HOST") ?? "0.0.0.0";
var apiPort = Environment.GetEnvironmentVariable("API_PORT") ?? "8000";
builder.WebHost.UseUrls($"http://{apiHost}:{apiPort}");

var app = builder.Build();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseCors();
app.MapControllers();

// Startup: ensure PG schema, ES index, Mongo indexes (each graceful if its DB is down).
// Options are resolved from DI so test overrides apply.
var startupOptions = app.Services.GetRequiredService<AppOptions>();
try
{
    await Schema.EnsureAsync(app.Services.GetRequiredService<NpgsqlDataSource>());
    Console.WriteLine("postgres tables ensured");
}
catch (Exception ex) { Console.WriteLine($"postgres init failed: {ex.Message}"); }
try
{
    var products = app.Services.GetRequiredService<ProductsRepository>();
    await products.EnsureIndexesAsync();
    Console.WriteLine("mongo indexes ensured");
}
catch (Exception ex) { Console.WriteLine($"mongo init failed: {ex.Message}"); }
try
{
    var search = app.Services.GetRequiredService<SearchRepository>();
    var ok = await search.EnsureIndexAsync(startupOptions.EsIndex);
    Console.WriteLine(ok ? "elasticsearch index ensured" : "elasticsearch init failed");
}
catch (Exception ex) { Console.WriteLine($"elasticsearch init failed: {ex.Message}"); }

Console.WriteLine($"startup complete (sync_strategy={startupOptions.NormalizedStrategy})");
app.Run();
return 0;

static AppOptions LoadOptions()
{
    var config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();
    return AppOptions.Load(config);
}

public partial class Program;
