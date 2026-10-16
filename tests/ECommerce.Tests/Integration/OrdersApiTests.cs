using System.Net;
using ECommerce.Api.Models;

namespace ECommerce.Tests.Integration;

[Collection("api")]
public class OrdersApiTests(ApiFixture fx) : ApiTestBase(fx)
{
    private async Task<(int userId, string productId)> IdsAsync()
        => (await Fx.UserIdByEmailAsync("one@example.com"),
            await Fx.ProductIdBySkuAsync("TEST-001"));

    [Fact]
    public async Task Create_Returns201_WithServerSideTotals()
    {
        var (userId, productId) = await IdsAsync();
        var payload = new OrderCreateDto(userId,
            [new OrderItemCreateDto(productId, 2)], "ord-test-001");
        var r = await Fx.Client.PostAsync("/api/orders", JsonBody(payload));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var doc = await ReadJson(r);
        Assert.Equal("50.00", doc.RootElement.GetProperty("total_amount").GetString());
        Assert.Equal("PENDING", doc.RootElement.GetProperty("status").GetString());
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal("Test Widget", item.GetProperty("title").GetString());
        Assert.Equal("25.00", item.GetProperty("unit_price").GetString());
        Assert.Equal("50.00", item.GetProperty("line_total").GetString());
        var sync = doc.RootElement.GetProperty("es_sync");
        Assert.Equal("dual_write", sync.GetProperty("strategy").GetString());
        Assert.Equal("synced", sync.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Create_UsesCurrentCatalogPrice()
    {
        var (userId, productId) = await IdsAsync();
        // Change the catalog price; the new checkout must use the NEW price.
        await Fx.Client.PutAsync($"/api/products/{productId}",
            JsonBody(new ProductUpdateDto { Price = 99.99 }));
        try
        {
            var r = await Fx.Client.PostAsync("/api/orders", JsonBody(
                new OrderCreateDto(userId, [new OrderItemCreateDto(productId, 1)], "ord-test-price")));
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            Assert.Equal("99.99",
                (await ReadJson(r)).RootElement.GetProperty("total_amount").GetString());
        }
        finally
        {
            await Fx.Client.PutAsync($"/api/products/{productId}",
                JsonBody(new ProductUpdateDto { Price = 25.00 }));
        }
    }

    [Theory]
    [InlineData(0, "ord-bad-qty")]
    [InlineData(-3, "ord-bad-qty2")]
    public async Task Create_BadQuantity_Returns422(int qty, string key)
    {
        var (userId, productId) = await IdsAsync();
        var r = await Fx.Client.PostAsync("/api/orders", JsonBody(
            new OrderCreateDto(userId, [new OrderItemCreateDto(productId, qty)], key)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
    }

    [Fact]
    public async Task Create_EmptyItems_Returns422()
    {
        var (userId, _) = await IdsAsync();
        var r = await Fx.Client.PostAsync("/api/orders",
            JsonBody(new OrderCreateDto(userId, [], "ord-empty")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownUser_Returns400()
    {
        var (_, productId) = await IdsAsync();
        var r = await Fx.Client.PostAsync("/api/orders", JsonBody(
            new OrderCreateDto(999999, [new OrderItemCreateDto(productId, 1)], "ord-nouser")));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Create_UnknownOrInactiveProduct_Returns422()
    {
        var (userId, _) = await IdsAsync();
        var bad = await Fx.Client.PostAsync("/api/orders", JsonBody(
            new OrderCreateDto(userId,
                [new OrderItemCreateDto(MongoDB.Bson.ObjectId.GenerateNewId().ToString(), 1)],
                "ord-badprod")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        var inactiveId = await Fx.ProductIdBySkuAsync("TEST-003");
        var inact = await Fx.Client.PostAsync("/api/orders", JsonBody(
            new OrderCreateDto(userId, [new OrderItemCreateDto(inactiveId, 1)], "ord-inact")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inact.StatusCode);
        Assert.Contains("inactive",
            (await ReadJson(inact)).RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Create_RollsBack_WhenItemInsertFails()
    {
        var before = await Fx.Orders.CountOrdersAsync();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            Fx.Orders.CreateOrderAsync(1,
                [new OrderLine(null!, "boom", 1, 1.00m)], // NULL product_id violates NOT NULL
                null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
        Assert.Equal(before, await Fx.Orders.CountOrdersAsync());
    }

    [Fact]
    public async Task Create_IdempotentReplay_ReturnsSameOrder()
    {
        var (userId, productId) = await IdsAsync();
        var payload = new OrderCreateDto(userId,
            [new OrderItemCreateDto(productId, 1)], "ord-idem-001");
        var r1 = await Fx.Client.PostAsync("/api/orders", JsonBody(payload));
        var r2 = await Fx.Client.PostAsync("/api/orders", JsonBody(payload));
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var id1 = (await ReadJson(r1)).RootElement.GetProperty("order_id").GetInt32();
        var id2 = (await ReadJson(r2)).RootElement.GetProperty("order_id").GetInt32();
        Assert.Equal(id1, id2);
    }

    [Fact]
    public async Task Get_Unknown_Returns404_And_Detail_HasSyncBadge()
    {
        var r = await Fx.Client.GetAsync("/api/orders/999999");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        var (userId, productId) = await IdsAsync();
        var created = await ReadJson(await Fx.Client.PostAsync("/api/orders", JsonBody(
            new OrderCreateDto(userId, [new OrderItemCreateDto(productId, 1)], "ord-detail-001"))));
        var orderId = created.RootElement.GetProperty("order_id").GetInt32();

        var detail = await ReadJson(await Fx.Client.GetAsync($"/api/orders/{orderId}"));
        Assert.Equal("Test Widget",
            detail.RootElement.GetProperty("items")[0].GetProperty("title").GetString());
        Assert.True(detail.RootElement.GetProperty("sync").GetProperty("es_in_sync").GetBoolean());
    }

    [Fact]
    public async Task Patch_Status_UpdatesPg_And_EventuallyEs()
    {
        var (userId, productId) = await IdsAsync();
        var created = await ReadJson(await Fx.Client.PostAsync("/api/orders", JsonBody(
            new OrderCreateDto(userId, [new OrderItemCreateDto(productId, 1)], "ord-patch-001"))));
        var orderId = created.RootElement.GetProperty("order_id").GetInt32();

        var r = await Fx.Client.PatchAsync($"/api/orders/{orderId}/status",
            JsonBody(new StatusUpdateDto("SHIPPED")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("SHIPPED",
            (await ReadJson(r)).RootElement.GetProperty("status").GetString());

        // dual-write is immediate: the ES doc reflects the new status.
        var doc = await Fx.Search.GetOrderDocAsync(Fx.Options.EsIndex, orderId);
        Assert.NotNull(doc);
        Assert.Equal("SHIPPED", doc!.Status);

        var bad = await Fx.Client.PatchAsync($"/api/orders/{orderId}/status",
            JsonBody(new StatusUpdateDto("BOGUS")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        var missing = await Fx.Client.PatchAsync("/api/orders/999999/status",
            JsonBody(new StatusUpdateDto("SHIPPED")));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
