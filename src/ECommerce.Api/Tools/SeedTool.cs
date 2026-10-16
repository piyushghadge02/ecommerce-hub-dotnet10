using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using ECommerce.Api.Config;
using ECommerce.Api.Data;
using ECommerce.Api.Infrastructure;
using ECommerce.Api.Models;
using ECommerce.Api.Repositories;
using ECommerce.Api.Services;

namespace ECommerce.Api.Tools;

/// <summary>
/// Deterministic demo seed (seed=42) for PG + MongoDB + Elasticsearch.
/// Faithful port of the Python scripts/seed.py: same products, same order-plan
/// algorithm, same §9 rules, same deliberate snapshot mismatch.
/// Usage: dotnet run -- seed [--reset]   |   dotnet run -- reindex
/// </summary>
public static class SeedTool
{
    public const int Seed = 42;

    public sealed record SeedProduct(string Sku, string Title, double Price, string Category,
        List<string> Tags, Dictionary<string, object?> Attributes,
        List<Dictionary<string, object?>> Variants, string Description = "", bool Active = true);

    private static Dictionary<string, object?> Attr(params (string, object)[] pairs)
        => pairs.ToDictionary(p => p.Item1, p => (object?)p.Item2);

    private static Dictionary<string, object?> V(string sku, string color, int stock)
        => new() { ["sku"] = sku, ["color"] = color, ["stock"] = stock };

    private static SeedProduct P(string sku, string title, double price, string category,
        List<string> tags, Dictionary<string, object?> attributes,
        List<Dictionary<string, object?>> variants, string description = "", bool active = true)
        => new(sku, title, price, category, tags, attributes, variants,
            description == "" ? $"{title} — quality {category} product." : description, active);

    public static readonly List<SeedProduct> Products =
    [
        // peripherals (7 active)
        P("WM-001", "Wireless Mouse", 50.16, "peripherals", ["wireless", "usb", "office"],
            Attr(("color", "black"), ("dpi", 1600), ("battery", "AA")),
            [V("WM-001-BLK", "black", 40), V("WM-001-WHT", "white", 12)]),
        P("MK-101", "Mechanical Keyboard", 100.34, "peripherals", ["wired", "rgb", "gaming"],
            Attr(("switch", "brown"), ("layout", "TKL"), ("backlight", true)),
            [V("MK-101-BRN", "black", 25)]),
        P("WK-102", "Wireless Keyboard", 65.00, "peripherals", ["wireless", "bluetooth"],
            Attr(("layout", "full"), ("battery", "rechargeable")),
            [V("WK-102-BLK", "black", 30)]),
        P("HUB-201", "USB-C Hub 7-in-1", 45.00, "peripherals", ["usb-c", "hub", "laptop"],
            Attr(("ports", 7), ("hdmi", "4K")),
            [V("HUB-201-GRY", "gray", 50)]),
        P("MP-202", "Ergonomic Mouse Pad", 18.99, "peripherals", ["office", "desk"],
            Attr(("size", "XL"), ("material", "rubber")),
            [V("MP-202-BLK", "black", 100)]),
        P("CAM-203", "Webcam HD 1080p", 55.50, "peripherals", ["video", "usb"],
            Attr(("resolution", "1080p"), ("microphone", true)),
            [V("CAM-203-BLK", "black", 35)]),
        P("MON-204", "4K Monitor 27-inch", 249.99, "peripherals", ["display", "4k", "usb-c"],
            Attr(("size", "27in"), ("panel", "IPS")),
            [V("MON-204-BLK", "black", 15)]),
        // audio (6 active)
        P("EB-301", "Wireless Earbuds", 79.99, "audio", ["wireless", "bluetooth", "audio"],
            Attr(("battery_hours", 24), ("anc", false)),
            [V("EB-301-WHT", "white", 60), V("EB-301-BLK", "black", 45)]),
        P("NCH-302", "Noise Cancelling Headphones", 199.00, "audio", ["wireless", "anc", "premium"],
            Attr(("battery_hours", 40), ("anc", true)),
            [V("NCH-302-BLK", "black", 20)]),
        P("SPK-303", "Bluetooth Speaker Mini", 35.75, "audio", ["wireless", "bluetooth", "speaker"],
            Attr(("battery_hours", 12), ("waterproof", "IPX5")),
            [V("SPK-303-BLU", "blue", 40)]),
        P("MIC-304", "USB Microphone Pro", 89.00, "audio", ["usb", "streaming"],
            Attr(("pattern", "cardioid"), ("mount", "desk")),
            [V("MIC-304-BLK", "black", 28)]),
        P("EP-305", "Wired Earphones", 12.99, "audio", ["wired", "budget"],
            Attr(("connector", "3.5mm")),
            [V("EP-305-WHT", "white", 120)]),
        P("SB-306", "Soundbar Slim", 129.99, "audio", ["tv", "hdmi", "audio"],
            Attr(("channels", "2.1"), ("hdmi_arc", true)),
            [V("SB-306-BLK", "black", 18)]),
        // cables (6 active)
        P("CBL-401", "USB-C to USB-C Cable 2m", 14.99, "cables", ["usb-c", "charging"],
            Attr(("length", "2m"), ("wattage", 100)),
            [V("CBL-401-BLK", "black", 200)]),
        P("HDMI-402", "HDMI 2.1 Cable", 19.99, "cables", ["hdmi", "4k"],
            Attr(("length", "2m"), ("version", "2.1")),
            [V("HDMI-402-BLK", "black", 150)]),
        P("WCP-403", "Wireless Charging Pad", 29.99, "cables", ["wireless", "charging", "qi"],
            Attr(("wattage", 15)),
            [V("WCP-403-WHT", "white", 70)]),
        P("LGT-404", "Lightning Cable 1m", 11.50, "cables", ["apple", "charging"],
            Attr(("length", "1m"), ("mfi", true)),
            [V("LGT-404-WHT", "white", 180)]),
        P("DP-405", "DisplayPort Cable", 22.00, "cables", ["display", "pc"],
            Attr(("length", "1.8m"), ("version", "1.4")),
            [V("DP-405-BLK", "black", 90)]),
        P("ETH-406", "Ethernet Cable Cat6 5m", 16.75, "cables", ["network", "wired"],
            Attr(("length", "5m"), ("category", "Cat6")),
            [V("ETH-406-BLU", "blue", 110)]),
        // office (6 active + 1 inactive)
        P("LAMP-501", "Desk Lamp LED", 32.50, "office", ["desk", "lighting"],
            Attr(("dimmable", true), ("color_temp", "3000-6000K")),
            [V("LAMP-501-WHT", "white", 55)]),
        P("MST-502", "Monitor Stand Bamboo", 49.99, "office", ["desk", "ergonomic"],
            Attr(("material", "bamboo"), ("max_load_kg", 15)),
            [V("MST-502-NAT", "natural", 32)]),
        P("PRS-503", "Wireless Presenter Remote", 39.95, "office", ["wireless", "presentation"],
            Attr(("range_m", 30), ("laser", "red")),
            [V("PRS-503-BLK", "black", 48)]),
        P("LST-504", "Laptop Stand Aluminum", 42.00, "office", ["desk", "laptop"],
            Attr(("material", "aluminum"), ("adjustable", true)),
            [V("LST-504-SLV", "silver", 42)]),
        P("ORG-505", "Desk Organizer Set", 24.99, "office", ["desk", "storage"],
            Attr(("pieces", 5)),
            [V("ORG-505-GRY", "gray", 65)]),
        P("CUSH-506", "Office Chair Cushion", 27.50, "office", ["comfort", "chair"],
            Attr(("material", "memory foam")),
            [V("CUSH-506-BLK", "black", 80)]),
        P("CRT-507", "Old CRT Monitor", 99.99, "office", ["vintage"],
            Attr(("size", "17in")),
            [V("CRT-507-BGE", "beige", 2)], active: false),
    ];

    public static readonly List<(string Name, string Email)> Users =
    [
        ("John Doe", "john.doe@example.com"),
        ("Jane Smith", "jane.smith@example.com"),
        ("Wendy Wireless", "wendy.wireless@example.com"),
        ("Alex Rivera", "alex.rivera@example.com"),
        ("Sam Patel", "sam.patel@example.com"),
        ("Casey Nguyen", "casey.nguyen@example.com"),
        ("Morgan Lee", "morgan.lee@example.com"),
        ("Riley Brooks", "riley.brooks@example.com"),
    ];

    // ------------------------------------------------------- order plan ---
    public sealed record PlanOrder(int UserIdx, List<(int ProdIdx, int Qty)> Lines,
        string? Status, int? DaysAgo);

    public static List<PlanOrder> BuildOrderPlan(IReadOnlyList<SeedProduct> products)
    {
        var rng = new Random(Seed);
        var sku = products.Select((p, i) => (p.Sku, i)).ToDictionary(x => x.Sku, x => x.i);
        var active = products.Select((p, i) => (p, i)).Where(x => x.p.Active).Select(x => x.i).ToList();
        var wireless = active.Where(i =>
            products[i].Title.Contains("wireless", StringComparison.OrdinalIgnoreCase) ||
            products[i].Tags.Any(t => t.Equals("wireless", StringComparison.OrdinalIgnoreCase))).ToList();
        var cheap = active.Where(i => products[i].Price < 25).ToList();
        var mid = active.Where(i => products[i].Price >= 25 && products[i].Price <= 99).ToList();
        int WM = sku["WM-001"], MK = sku["MK-101"];
        int NCH = sku["NCH-302"], SB = sku["SB-306"], MON = sku["MON-204"], EP = sku["EP-305"];

        var plan = new List<PlanOrder>();
        int AddOrder(int userIdx, List<(int, int)> lines)
        {
            plan.Add(new PlanOrder(userIdx, lines, null, null));
            return plan.Count - 1;
        }
        List<(int, int)> RandomLines(int n) =>
            Enumerable.Range(0, n).Select(_ => (active[rng.Next(active.Count)], rng.Next(1, 4))).ToList();
        int Choice234() => new[] { 2, 2, 3 }[rng.Next(3)];

        for (var u = 0; u < 8; u++)
            for (var k = 0; k < 2; k++)
                AddOrder(u, RandomLines(Choice234()));
        for (var k = 0; k < 3; k++)
        {
            var lines = new List<(int, int)> { (WM, rng.Next(1, 3)) };
            lines.AddRange(RandomLines(rng.Next(0, 2)));
            AddOrder(2, lines);
        }
        for (var i = 0; i < 5; i++)
            AddOrder(rng.Next(8), [(cheap[i % cheap.Count], 1)]);
        for (var i = 0; i < 5; i++)
        {
            var p = mid[i % mid.Count];
            var qty = products[p].Price * 2 > 150 ? 1 : 2;
            AddOrder(rng.Next(8), [(p, qty)]);
        }
        foreach (var lines in new List<(int, int)>[]
                 { [(NCH, 2)], [(SB, 2)], [(MON, 1)], [(MK, 3)], [(NCH, 1), (EP, 1)] })
            AddOrder(rng.Next(8), lines);
        for (var k = 0; k < 10; k++)
            AddOrder(rng.Next(8), RandomLines(Choice234()));

        var pool = Enumerable.Range(34, 10).ToList();
        Shuffle(pool, rng);
        var wmSlots = new[] { 16, 17, 18 }.Concat(pool.Take(5)).ToList();
        var mkSlots = pool.Skip(5).Take(3).ToList();
        var bothSlots = pool.Take(2).ToList();
        foreach (var s in wmSlots)
            if (!plan[s].Lines.Any(x => x.ProdIdx == WM)) plan[s].Lines.Add((WM, 1));
        foreach (var s in mkSlots) plan[s].Lines.Add((MK, 1));
        foreach (var s in bothSlots)
            if (!plan[s].Lines.Any(x => x.ProdIdx == MK)) plan[s].Lines.Add((MK, 1));

        for (var i = 0; i < plan.Count; i++)
            plan[i] = plan[i] with { Status = i < 15 ? "PENDING" : i < 30 ? "PROCESSING" : "SHIPPED" };

        var daysAgo = new List<int> { 1, 2, 4, 5, 6 };
        daysAgo.AddRange(Enumerable.Range(0, 39).Select(i => 8 + (i * 82) / 38));
        Shuffle(daysAgo, rng);
        for (var i = 0; i < plan.Count; i++)
            plan[i] = plan[i] with { DaysAgo = daysAgo[i] };

        return plan;
    }

    private static void Shuffle<T>(IList<T> list, Random rng)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>Returns violated §9 rules (empty = all satisfied).</summary>
    public static List<string> CheckPlan(List<PlanOrder> plan, IReadOnlyList<SeedProduct> products)
    {
        var problems = new List<string>();
        var sku = products.Select((p, i) => (p.Sku, i)).ToDictionary(x => x.Sku, x => x.i);
        int WM = sku["WM-001"], MK = sku["MK-101"];

        decimal Total(PlanOrder o) =>
            o.Lines.Sum(x => Money.FromDouble(products[x.ProdIdx].Price) * x.Qty);

        bool WirelessProd(int pi) =>
            products[pi].Title.Contains("wireless", StringComparison.OrdinalIgnoreCase) ||
            products[pi].Tags.Any(t => t.Equals("wireless", StringComparison.OrdinalIgnoreCase));

        if (plan.Count != 44) problems.Add($"expected 44 orders, got {plan.Count}");
        var nItems = plan.Sum(o => o.Lines.Count);
        if (nItems < 80) problems.Add($"expected >=80 items, got {nItems}");
        foreach (var (st, need) in new[] { ("PENDING", 10), ("PROCESSING", 10), ("SHIPPED", 10) })
            if (plan.Count(o => o.Status == st) < need)
                problems.Add($"status {st}: {plan.Count(o => o.Status == st)} < {need}");
        if (plan.Count(o => o.DaysAgo > 60) < 5) problems.Add("need >=5 orders older than 60 days");
        if (plan.Count(o => o.DaysAgo <= 7) < 5) problems.Add("need >=5 orders within last 7 days");
        if (plan.Max(o => o.DaysAgo) < 85) problems.Add("dates do not spread over ~90 days");
        var totals = plan.Select(Total).ToList();
        if (totals.Count(t => t < 30) < 5) problems.Add("need >=5 orders under $30");
        if (totals.Count(t => t >= 30 && t <= 150) < 5) problems.Add("need >=5 orders $30-$150");
        if (totals.Count(t => t > 200) < 5) problems.Add("need >=5 orders over $200");
        var perUser = Enumerable.Range(0, 8).Select(u => plan.Count(o => o.UserIdx == u)).ToList();
        if (perUser.Min() < 2) problems.Add($"every user needs >=2 orders: [{string.Join(",", perUser)}]");
        if (plan.Count(o => o.UserIdx == 2 && o.Lines.Any(x => WirelessProd(x.ProdIdx))) < 3)
            problems.Add("Wendy needs >=3 wireless orders");
        if (plan.Count(o => o.Lines.Any(x => x.ProdIdx == WM)) < 8)
            problems.Add("need >=8 Wireless Mouse orders");
        if (plan.Count(o => o.Lines.Any(x => x.ProdIdx == MK)) < 3)
            problems.Add("need >=3 Mechanical Keyboard orders");
        if (plan.Count(o => o.Lines.Any(x => x.ProdIdx == WM) && o.Lines.Any(x => x.ProdIdx == MK)) < 2)
            problems.Add("need >=2 orders with both products");
        return problems;
    }

    // ------------------------------------------------------- db execution ---
    public static async Task<int> RunAsync(AppOptions options, bool reset)
    {
        var now = DateTimeOffset.UtcNow;
        await using var ds = NpgsqlDataSource.Create(options.NpgsqlConnectionString());
        await Schema.EnsureAsync(ds);
        var orders = new OrdersRepository(ds);
        var mongo = new MongoClient(options.MongoUri).GetDatabase(options.MongoDb);
        var products = new ProductsRepository(mongo);
        var es = new EsGateway(options.ElasticsearchUrl);
        var search = new SearchRepository(es);

        if (reset)
        {
            await orders.DeleteAllDemoDataAsync();
            await products.Collection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
            try { await search.DeleteIndexAsync(options.EsIndex); } catch { }
            Console.WriteLine("demo data wiped");
        }

        var userMap = await SeedUsersAsync(ds);
        await products.EnsureIndexesAsync();
        var prodDocs = await SeedProductsAsync(products);
        await SeedOrdersAsync(orders, userMap, prodDocs, now);
        await ApplySnapshotMismatchAsync(products);

        if (!await search.EnsureIndexAsync(options.EsIndex))
        {
            Console.WriteLine("ERROR: elasticsearch unreachable — PG/Mongo seeded, ES reindex skipped");
            return 1;
        }
        var sync = new SyncService(orders, search, options);
        var indexed = await sync.ReindexAllAsync();
        Console.WriteLine($"reindexed {indexed} orders");

        await VerifyAsync(ds, products, search, options);
        return 0;
    }

    public static async Task<int> RunReindexAsync(AppOptions options)
    {
        await using var ds = NpgsqlDataSource.Create(options.NpgsqlConnectionString());
        var orders = new OrdersRepository(ds);
        var search = new SearchRepository(new EsGateway(options.ElasticsearchUrl));
        var sync = new SyncService(orders, search, options);
        try
        {
            var indexed = await sync.ReindexAllAsync();
            Console.WriteLine($"reindexed {indexed} orders");
            return 0;
        }
        catch (EsUnavailableException)
        {
            Console.WriteLine("ERROR: elasticsearch unreachable");
            return 1;
        }
    }

    private static async Task<Dictionary<int, int>> SeedUsersAsync(NpgsqlDataSource ds)
    {
        var map = new Dictionary<int, int>();
        await using var conn = await ds.OpenConnectionAsync();
        for (var i = 0; i < Users.Count; i++)
        {
            var (name, email) = Users[i];
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO users (name, email) VALUES (@n, @e) " +
                "ON CONFLICT (email) DO UPDATE SET name = EXCLUDED.name RETURNING id", conn);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("e", email);
            map[i] = (int)(await cmd.ExecuteScalarAsync())!;
        }
        return map;
    }

    private sealed record ProdRef(string MongoId, string Title, decimal Price);

    private static async Task<List<ProdRef>> SeedProductsAsync(ProductsRepository products)
    {
        var refs = new List<ProdRef>();
        foreach (var p in Products)
        {
            var existing = await products.Collection
                .Find(Builders<BsonDocument>.Filter.Eq("sku", p.Sku))
                .FirstOrDefaultAsync();
            if (existing is null)
            {
                var doc = new BsonDocument
                {
                    ["sku"] = p.Sku,
                    ["title"] = p.Title,
                    ["description"] = p.Description,
                    ["price"] = p.Price,
                    ["category"] = p.Category,
                    ["tags"] = new BsonArray(p.Tags),
                    ["attributes"] = BsonConverter.FromObject(p.Attributes),
                    ["variants"] = new BsonArray(p.Variants.Select(BsonConverter.FromObject)),
                    ["active"] = p.Active,
                    ["updated_at"] = DateTime.UtcNow,
                };
                await products.Collection.InsertOneAsync(doc);
                existing = doc;
            }
            refs.Add(new ProdRef(existing["_id"].AsObjectId.ToString(),
                existing["title"].AsString, Money.FromDouble(existing["price"].ToDouble())));
        }
        return refs;
    }

    private static async Task SeedOrdersAsync(OrdersRepository orders,
        Dictionary<int, int> userMap, List<ProdRef> prodRefs, DateTimeOffset now)
    {
        if (await orders.CountOrdersAsync() > 0)
        {
            Console.WriteLine("orders already seeded; skipping");
            return;
        }
        var plan = BuildOrderPlan(Products);
        var problems = CheckPlan(plan, Products);
        if (problems.Count > 0)
            throw new InvalidOperationException($"seed plan violates rules: {string.Join("; ", problems)}");

        foreach (var o in plan)
        {
            var lines = o.Lines.Select(x =>
                new OrderLine(prodRefs[x.ProdIdx].MongoId, prodRefs[x.ProdIdx].Title,
                    x.Qty, prodRefs[x.ProdIdx].Price)).ToList();
            var orderDate = now.AddDays(-o.DaysAgo!.Value)
                .AddMinutes(-(o.DaysAgo.Value * 37) % 1440);
            var (order, _) = await orders.CreateOrderAsync(
                userMap[o.UserIdx], lines, null, orderDate, orderDate, null);
            if (o.Status != "PENDING")
                await orders.UpdateStatusAsync(order.Id, o.Status!, null);
        }
        Console.WriteLine($"orders seeded: {plan.Count}");
    }

    private static async Task ApplySnapshotMismatchAsync(ProductsRepository products)
    {
        var patch = new Dictionary<string, BsonValue>
        {
            ["title"] = "Wireless Mouse Pro",
            ["price"] = 59.99,
        };
        var doc = await products.Collection.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("sku", "WM-001"),
            Builders<BsonDocument>.Update
                .Set("title", "Wireless Mouse Pro")
                .Set("price", 59.99)
                .Set("updated_at", DateTime.UtcNow));
        Console.WriteLine($"snapshot mismatch applied (WM-001 -> Wireless Mouse Pro): {doc is not null}");
    }

    private static async Task VerifyAsync(NpgsqlDataSource ds, ProductsRepository products,
        SearchRepository search, AppOptions options)
    {
        await using var conn = await ds.OpenConnectionAsync();
        async Task<long> Count(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            return (long)(await cmd.ExecuteScalarAsync())!;
        }
        var nUsers = await Count("SELECT COUNT(*) FROM users");
        var nOrders = await Count("SELECT COUNT(*) FROM orders");
        var nItems = await Count("SELECT COUNT(*) FROM order_items");
        var nProducts = await products.Collection.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("active", true));
        var nInactive = await products.Collection.CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("active", false));
        var wm = await products.Collection
            .Find(Builders<BsonDocument>.Filter.Eq("sku", "WM-001")).FirstAsync();
        var esDocs = await search.CountDocsAsync(options.EsIndex);

        await using var oc = new NpgsqlCommand(
            "SELECT title, unit_price FROM order_items WHERE title = 'Wireless Mouse' LIMIT 1", conn);
        await using var r = await oc.ExecuteReaderAsync();
        var hasOld = await r.ReadAsync();
        var oldTitle = hasOld ? r.GetString(0) : null;
        var oldPrice = hasOld ? r.GetDecimal(1) : 0;

        Console.WriteLine($"users={nUsers} products(active)={nProducts} inactive={nInactive} " +
                          $"orders={nOrders} items={nItems} es_docs={esDocs}");
        Console.WriteLine($"mongo WM-001 title='{wm["title"]}' price={wm["price"]}");
        Console.WriteLine($"historical PG snapshot intact: {hasOld} ({oldTitle}, {oldPrice})");

        if (nUsers != 8) throw new InvalidOperationException("expected 8 users");
        if (nProducts < 24) throw new InvalidOperationException("expected >=24 active products");
        if (nInactive < 1) throw new InvalidOperationException("expected >=1 inactive product");
        if (nOrders != 44) throw new InvalidOperationException("expected 44 orders");
        if (nItems < 80) throw new InvalidOperationException("expected >=80 items");
        if (wm["title"].AsString != "Wireless Mouse Pro" || wm["price"].ToDouble() != 59.99)
            throw new InvalidOperationException("snapshot mismatch not applied");
        if (!hasOld || oldTitle != "Wireless Mouse" || oldPrice != 50.16m)
            throw new InvalidOperationException("historical snapshot was rewritten!");
        if (esDocs is not null && esDocs != nOrders)
            throw new InvalidOperationException($"ES docs {esDocs} != PG orders {nOrders}");
        Console.WriteLine("seed verification passed");
    }
}
