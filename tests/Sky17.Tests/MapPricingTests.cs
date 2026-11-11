using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sky17.Application;
using Sky17.Domain;

namespace Sky17.Tests;

public class MapPricingTests
{
    private static CheckoutInput PinnedCheckout() => new()
    {
        RecipientName = "Nguyễn Văn A",
        RecipientPhone = "0901234567",
        DeliveryAddress = "118 Lê Hồng Phong, Thủ Dầu Một",
        IdempotencyKey = Guid.NewGuid(),
        Latitude = 10.9757,
        Longitude = 106.6688,
    };

    [Fact]
    public async Task Pinned_dropoff_uses_road_distance_for_fee_and_snapshots_coordinates()
    {
        await using var app = await TestApp.CreateAsync();
        app.Services.GetRequiredService<FakeRoutingService>().DistanceMeters = 5_000; // 2km base + 3 extra km
        var (_, _, pa, _) = await app.CreateStoreAsync(priceA: 29_000);
        var customer = await app.CreateUserAsync("C", Roles.Customer);
        using var c = app.As(customer);
        await c.Get<ICartService>().AddAsync(pa, 1);

        var quote = await c.Get<ICartService>().QuoteAsync(10.9757, 106.6688);
        Assert.Equal(5_000, quote.DistanceMeters);
        Assert.Equal(30_000, quote.DeliveryFee);

        var placed = await c.Get<ICustomerOrderService>().PlaceOrderAsync(PinnedCheckout());
        await using var db = app.Db();
        var order = await db.Orders.SingleAsync(o => o.Id == placed.OrderId);
        Assert.Equal(30_000, order.DeliveryFee);
        Assert.Equal(59_000, order.GrandTotal);
        Assert.Equal(5_000, order.DistanceMeters);
        Assert.Equal(DistanceSource.Route, order.DistanceSource);
        Assert.Equal(10.9804, order.PickupLatitude);
        Assert.Equal(10.9757, order.DropoffLatitude);
    }

    [Fact]
    public async Task Routing_unavailable_falls_back_to_base_fee_without_inventing_distance()
    {
        await using var app = await TestApp.CreateAsync();
        app.Services.GetRequiredService<FakeRoutingService>().DistanceMeters = null;
        var (_, _, pa, _) = await app.CreateStoreAsync();
        var customer = await app.CreateUserAsync("C", Roles.Customer);
        using var c = app.As(customer);
        await c.Get<ICartService>().AddAsync(pa, 1);
        var placed = await c.Get<ICustomerOrderService>().PlaceOrderAsync(PinnedCheckout());

        await using var db = app.Db();
        var order = await db.Orders.SingleAsync(o => o.Id == placed.OrderId);
        Assert.Equal(15_000, order.DeliveryFee);
        Assert.Null(order.DistanceMeters);
        Assert.Equal(DistanceSource.None, order.DistanceSource);
        Assert.NotNull(order.DropoffLatitude); // the pin is still kept for the driver's map
    }

    [Fact]
    public async Task Unpinned_checkout_does_not_call_routing()
    {
        await using var app = await TestApp.CreateAsync();
        var routing = app.Services.GetRequiredService<FakeRoutingService>();
        var (_, _, pa, _) = await app.CreateStoreAsync();
        var customer = await app.CreateUserAsync("C", Roles.Customer);
        using var c = app.As(customer);
        await c.Get<ICartService>().AddAsync(pa, 1);
        await c.Get<ICustomerOrderService>().PlaceOrderAsync(TestApp.Checkout());
        Assert.Equal(0, routing.Calls);
    }

    [Fact]
    public async Task Invalid_coordinates_are_rejected()
    {
        await using var app = await TestApp.CreateAsync();
        var (_, _, pa, _) = await app.CreateStoreAsync();
        var customer = await app.CreateUserAsync("C", Roles.Customer);
        using var c = app.As(customer);
        await c.Get<ICartService>().AddAsync(pa, 1);
        var input = PinnedCheckout();
        input.Latitude = 123; // out of range
        await Assert.ThrowsAsync<DomainException>(() => c.Get<ICustomerOrderService>().PlaceOrderAsync(input));
    }

    [Fact]
    public async Task Dropoff_point_is_hidden_from_drivers_until_assigned()
    {
        await using var app = await TestApp.CreateAsync();
        var (_, storeId, pa, _) = await app.CreateStoreAsync();
        var customer = await app.CreateUserAsync("C", Roles.Customer);
        PlaceOrderResult placed;
        using (var c = app.As(customer))
        {
            await c.Get<ICartService>().AddAsync(pa, 1);
            placed = await c.Get<ICustomerOrderService>().PlaceOrderAsync(PinnedCheckout());
        }
        await using (var db = app.Db())
        {
            await db.Orders.Where(o => o.Id == placed.OrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.ReadyForPickup));
        }
        var driver = await app.CreateDriverAsync("D");
        using var d = app.As(driver);
        var svc = d.Get<IDriverService>();
        var open = (await svc.GetAvailableJobsAsync()).Single();
        Assert.NotNull(open.PickupLatitude);
        Assert.Null(open.DropoffLatitude);
        await svc.AcceptAsync(placed.OrderId);
        var mine = await svc.GetJobAsync(placed.OrderId);
        Assert.Equal(10.9757, mine.DropoffLatitude);
    }

    [Fact]
    public async Task Address_and_store_locations_are_saved()
    {
        await using var app = await TestApp.CreateAsync();
        var (owner, storeId, _, _) = await app.CreateStoreAsync();
        var customer = await app.CreateUserAsync("C", Roles.Customer);
        using (var c = app.As(customer))
        {
            var id = await c.Get<IAddressService>().SaveAsync(new AddressInput { RecipientName = "A", PhoneNumber = "0901234567", Street = "1 Test", City = "Bình Dương", Latitude = 10.99, Longitude = 106.67 });
            var dto = (await c.Get<IAddressService>().GetMineAsync()).Single(a => a.Id == id);
            Assert.Equal(10.99, dto.Latitude);
        }
        using var s = app.As(owner);
        var seller = s.Get<ISellerService>();
        var profile = await seller.GetStoreProfileAsync(storeId);
        profile.Latitude = 10.95;
        profile.Longitude = 106.65;
        await seller.UpdateStoreProfileAsync(storeId, profile);
        Assert.Equal(10.95, (await seller.GetStoreProfileAsync(storeId)).Latitude);
    }
}
