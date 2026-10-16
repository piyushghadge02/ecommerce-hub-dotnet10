using System.Net;
using System.Text;
using System.Text.Json;
using ECommerce.Api.Infrastructure;
using ECommerce.Api.Models;
using MongoDB.Bson;

namespace ECommerce.Tests.Integration;

[Collection("api")]
public abstract class ApiTestBase
{
    protected readonly ApiFixture Fx;
    protected static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = SnakeCaseNamingPolicy.Instance,
    };

    protected ApiTestBase(ApiFixture fx) => Fx = fx;

    protected static StringContent JsonBody<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOpts), Encoding.UTF8, "application/json");

    protected static async Task<JsonDocument> ReadJson(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync());
}

public class ProductsApiTests(ApiFixture fx) : ApiTestBase(fx)
{
    [Fact]
    public async Task List_ReturnsActiveOnlyByDefault()
    {
        var r = await Fx.Client.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var doc = await ReadJson(r);
        var titles = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("title").GetString()).ToList();
        Assert.Equal(2, titles.Count);
        Assert.DoesNotContain("Inactive Thing", titles);
    }

    [Fact]
    public async Task List_ActiveFilters()
    {
        var all = await ReadJson(await Fx.Client.GetAsync("/api/products?active=all"));
        Assert.Equal(3, all.RootElement.GetArrayLength());
        var inactive = await ReadJson(await Fx.Client.GetAsync("/api/products?active=false"));
        Assert.Equal(1, inactive.RootElement.GetArrayLength());
        Assert.Equal("Inactive Thing",
            inactive.RootElement[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task List_CategoryAndQueryFilters()
    {
        var audio = await ReadJson(await Fx.Client.GetAsync("/api/products?category=audio"));
        Assert.Equal(1, audio.RootElement.GetArrayLength());
        var q = await ReadJson(await Fx.Client.GetAsync("/api/products?q=WIDGET"));
        Assert.Equal(1, q.RootElement.GetArrayLength());
        Assert.Equal("Test Widget", q.RootElement[0].GetProperty("title").GetString());
        var tag = await ReadJson(await Fx.Client.GetAsync("/api/products?q=wireless"));
        Assert.Equal(1, tag.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Get_ById_And_NotFound()
    {
        var id = await Fx.ProductIdBySkuAsync("TEST-001");
        var r = await Fx.Client.GetAsync($"/api/products/{id}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Test Widget", (await ReadJson(r)).RootElement.GetProperty("title").GetString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await Fx.Client.GetAsync("/api/products/not-an-objectid")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Fx.Client.GetAsync($"/api/products/{ObjectId.GenerateNewId()}")).StatusCode);
    }

    [Fact]
    public async Task Create_Returns201_And_DuplicateSku_Returns409()
    {
        var payload = new ProductCreateDto
        {
            Sku = "NEW-001", Title = "Brand New", Price = 12.5, Category = "office",
            Variants = [new Dictionary<string, object?> { ["sku"] = "NEW-001-A", ["stock"] = 3 }],
        };
        var r = await Fx.Client.PostAsync("/api/products", JsonBody(payload));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("Brand New", (await ReadJson(r)).RootElement.GetProperty("title").GetString());

        var dup = await Fx.Client.PostAsync("/api/products", JsonBody(payload));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains("duplicate sku",
            (await ReadJson(dup)).RootElement.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("", "Has Title", 10.0, "office", false)]   // blank sku
    [InlineData("X-1", "", 10.0, "office", false)]          // blank title
    [InlineData("X-1", "Has Title", 0.0, "office", false)]  // price not > 0
    [InlineData("X-1", "Has Title", -5.0, "office", false)] // negative price
    public async Task Create_Invalid_Returns422(
        string sku, string title, double price, string category, bool _)
    {
        var payload = new ProductCreateDto
        {
            Sku = sku, Title = title, Price = price, Category = category,
            Variants = [new Dictionary<string, object?> { ["sku"] = "V", ["stock"] = 1 }],
        };
        var r = await Fx.Client.PostAsync("/api/products", JsonBody(payload));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutVariants_Returns422()
    {
        var payload = new ProductCreateDto
        {
            Sku = "NV-001", Title = "No Variants", Price = 5, Category = "office",
            Variants = [],
        };
        var r = await Fx.Client.PostAsync("/api/products", JsonBody(payload));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
    }

    [Fact]
    public async Task Update_Works_And_EmptyUpdate_Returns422()
    {
        var id = await Fx.ProductIdBySkuAsync("TEST-001");
        var patch = new ProductUpdateDto { Title = "Renamed Widget", Price = 29.99 };
        var r = await Fx.Client.PutAsync($"/api/products/{id}", JsonBody(patch));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var doc = await ReadJson(r);
        Assert.Equal("Renamed Widget", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(29.99, doc.RootElement.GetProperty("price").GetDouble());

        // restore for other tests
        await Fx.Client.PutAsync($"/api/products/{id}",
            JsonBody(new ProductUpdateDto { Title = "Test Widget", Price = 25.00 }));

        var empty = await Fx.Client.PutAsync($"/api/products/{id}",
            JsonBody(new ProductUpdateDto()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);

        var missing = await Fx.Client.PutAsync(
            $"/api/products/{ObjectId.GenerateNewId()}", JsonBody(patch));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
