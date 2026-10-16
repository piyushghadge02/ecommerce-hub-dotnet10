using Npgsql;

namespace ECommerce.Api.Config;

/// <summary>
/// Application configuration. Every value can be overridden by an environment
/// variable of the same name (case-insensitive), e.g. POSTGRES_DSN, SYNC_STRATEGY.
/// </summary>
public sealed class AppOptions
{
    public string PostgresDsn { get; set; } = "postgresql+asyncpg://ecom:ecom@localhost:5432/ecommerce";
    public string MongoUri { get; set; } = "mongodb://localhost:27017";
    public string MongoDb { get; set; } = "ecommerce";
    public string ElasticsearchUrl { get; set; } = "http://localhost:9200";
    public string EsIndex { get; set; } = "orders";

    public string SyncStrategy { get; set; } = "dual_write";
    public int OutboxPollSeconds { get; set; } = 2;
    public int PollingIntervalSeconds { get; set; } = 15;
    public int OutboxMaxAttempts { get; set; } = 10;

    /// <summary>Set false in tests to keep background workers from starting.</summary>
    public bool StartWorkers { get; set; } = true;

    public string NormalizedStrategy => SyncStrategy.Trim().ToLowerInvariant();

    public string NpgsqlConnectionString() => DsnTranslator.ToNpgsql(PostgresDsn);

    public static AppOptions Load(IConfiguration config)
    {
        // Explicit UPPER_SNAKE mapping (same names as the Python .env):
        // config.Bind alone won't map POSTGRES_DSN -> PostgresDsn (underscores).
        string Str(string key, string fallback)
            => Environment.GetEnvironmentVariable(key)
               ?? config[key]
               ?? fallback;
        int Num(string key, int fallback)
            => int.TryParse(Environment.GetEnvironmentVariable(key) ?? config[key],
                            out var n) ? n : fallback;
        bool Flag(string key, bool fallback)
        {
            var raw = Environment.GetEnvironmentVariable(key) ?? config[key];
            return bool.TryParse(raw, out var b) ? b : fallback;
        }

        var defaults = new AppOptions();
        return new AppOptions
        {
            PostgresDsn = Str("POSTGRES_DSN", defaults.PostgresDsn),
            MongoUri = Str("MONGO_URI", defaults.MongoUri),
            MongoDb = Str("MONGO_DB", defaults.MongoDb),
            ElasticsearchUrl = Str("ELASTICSEARCH_URL", defaults.ElasticsearchUrl),
            EsIndex = Str("ES_INDEX", defaults.EsIndex),
            SyncStrategy = Str("SYNC_STRATEGY", defaults.SyncStrategy),
            OutboxPollSeconds = Num("OUTBOX_POLL_SECONDS", defaults.OutboxPollSeconds),
            PollingIntervalSeconds = Num("POLLING_INTERVAL_SECONDS", defaults.PollingIntervalSeconds),
            OutboxMaxAttempts = Num("OUTBOX_MAX_ATTEMPTS", defaults.OutboxMaxAttempts),
            StartWorkers = Flag("START_WORKERS", defaults.StartWorkers),
        };
    }
}

/// <summary>
/// Accepts SQLAlchemy-style DSNs (postgresql+asyncpg://...) as well as plain
/// Npgsql connection strings (Host=...;...).
/// </summary>
public static class DsnTranslator
{
    public static string ToNpgsql(string dsn)
    {
        if (dsn.StartsWith("Host=", StringComparison.OrdinalIgnoreCase))
            return dsn;
        var normalized = dsn.StartsWith("postgresql+asyncpg://", StringComparison.OrdinalIgnoreCase)
            ? "postgresql://" + dsn["postgresql+asyncpg://".Length..]
            : dsn;
        var uri = new Uri(normalized);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.Trim('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
            Timeout = 5,
            CommandTimeout = 15,
        };
        // Preserve query-string extras (e.g. ?search_path=test_schema).
        if (!string.IsNullOrEmpty(uri.Query))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("search_path", StringComparison.OrdinalIgnoreCase))
                    builder.SearchPath = Uri.UnescapeDataString(kv[1]);
            }
        }
        return builder.ToString();
    }
}
