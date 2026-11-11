using Microsoft.EntityFrameworkCore;
using Sky17.Application;
using Sky17.Domain;

namespace Sky17.Tests;

/// <summary>One cart per store, checked out per store; dish search grouped by store, nearest first.</summary>
public class CartPerStoreTests
{
    private static async Task MoveStoreAsync(TestApp app, int storeId, double? lat, double? lng)
    {
        await using var db = app.Db();
        await db.Stores.Where(s => s.Id == storeId).ExecuteUpdateAsync(x => x.SetProperty(s => s.Latitude, lat).SetProperty(s => s.Longitude, lng));
    }

    [Fact]
    public async Task Each_store_has_its_own_cart_and_checkout_only_takes_that_cart()
    {
        await using var app = await TestApp.CreateAsync();
        var (_, storeA, a1, a2) = await app.CreateStoreAsync("Quán A");
        var (_, storeB, b1, _) = await app.CreateStoreAsync("Quán B");
        var customer = await app.CreateUserAsync("Khách", Roles.Customer);
        using var c = app.As(customer);
        var carts = c.Get<ICartService>();

        await carts.AddAsync(a1, 2);
        await carts.AddAsync(a2, 1);
        var addB = await carts.AddAsync(b1, 3); // no "switch store" prompt: B gets its own cart
        Assert.True(addB.Success);
        Assert.Equal(6, addB.TotalItems);
        Assert.Equal(storeB, addB.Cart!.StoreId);

        var all = await carts.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(3, (await carts.GetAsync(storeA)).Count);
        Assert.Equal(3, (await carts.GetAsync(storeB)).Count);
        Assert.Equal(6, await carts.CountAsync());

        var input = TestApp.Checkout();
        input.StoreId = storeA;
        var placed = await c.Get<ICustomerOrderService>().PlaceOrderAsync(input);
        var order = await c.Get<ICustomerOrderService>().GetMyOrderAsync(placed.Code);
        Assert.Equal(storeA, order!.StoreId);
        Assert.Equal(3, order.Items.Sum(i => i.Quantity));

        // Store B's cart is untouched.
        var left = Assert.Single(await carts.GetAllAsync());
        Assert.Equal(storeB, left.StoreId);
        Assert.Equal(3, await carts.CountAsync());

        // Checking out a store without a cart fails.
        var again = TestApp.Checkout();
        again.StoreId = storeA;
        await Assert.ThrowsAsync<DomainException>(() => c.Get<ICustomerOrderService>().PlaceOrderAsync(again));
    }

    [Fact]
    public async Task Removing_the_last_item_or_clearing_affects_only_that_store()
    {
        await using var app = await TestApp.CreateAsync();
        var (_, storeA, a1, _) = await app.CreateStoreAsync("Quán A");
        var (_, storeB, b1, _) = await app.CreateStoreAsync("Quán B");
        var customer = await app.CreateUserAsync("Khách", Roles.Customer);
        using var c = app.As(customer);
        var carts = c.Get<ICartService>();
        await carts.AddAsync(a1, 1);
        await carts.AddAsync(b1, 2);

        var a = await carts.SetQuantityAsync(a1, 0);
        Assert.True(a.IsEmpty);
        Assert.Equal(storeB, Assert.Single(await carts.GetAllAsync()).StoreId);

        await carts.AddAsync(a1, 1);
        await carts.ClearAsync(storeB);
        Assert.Equal(storeA, Assert.Single(await carts.GetAllAsync()).StoreId);

        await using var db = app.Db();
        Assert.Equal(1, await db.Carts.CountAsync()); // empty carts are deleted, not kept around
    }

    [Fact]
    public async Task Search_groups_dishes_by_store_nearest_first_with_distance()
    {
        await using var app = await TestApp.CreateAsync();
        var (_, near, _, _) = await app.CreateStoreAsync("Quán Gần");
        var (_, far, _, _) = await app.CreateStoreAsync("Quán Xa");
        var (_, nopin, _, _) = await app.CreateStoreAsync("Quán Chưa Ghim");
        await MoveStoreAsync(app, near, 10.9804, 106.6531);   // ~0 km from the customer
        await MoveStoreAsync(app, far, 11.0254, 106.6531);    // ~5 km north
        await MoveStoreAsync(app, nopin, null, null);

        using var anon = app.Anonymous();
        var catalog = anon.Get<ICatalogService>();
        var groups = await catalog.SearchByStoreAsync(new StoreSearchQuery("cà phê", Latitude: 10.9804, Longitude: 106.6531));

        Assert.Equal([near, far, nopin], groups.Select(g => g.Store.Id).ToArray());
        Assert.Equal(0.0, groups[0].DistanceKm);
        Assert.InRange(groups[1].DistanceKm!.Value, 4.9, 5.1);
        Assert.Null(groups[2].DistanceKm);
        Assert.All(groups, g => Assert.Equal("Cà phê sữa đá", Assert.Single(g.Products).Name)); // only matching dishes

        // Customer elsewhere: order flips.
        var flipped = await catalog.SearchByStoreAsync(new StoreSearchQuery("cà phê", Latitude: 11.0300, Longitude: 106.6531));
        Assert.Equal(far, flipped[0].Store.Id);

        // Store-name match shows that store's menu.
        var byName = Assert.Single(await catalog.SearchByStoreAsync(new StoreSearchQuery("Quán Xa")));
        Assert.True(byName.StoreNameMatched);
        Assert.Equal(2, byName.Products.Count);

        // No location: still grouped, no distance.
        var noLoc = await catalog.SearchByStoreAsync(new StoreSearchQuery("bánh mì"));
        Assert.Equal(3, noLoc.Count);
        Assert.All(noLoc, g => Assert.Null(g.DistanceKm));

        Assert.Empty(await catalog.SearchByStoreAsync(new StoreSearchQuery("pizza không có")));
    }
}
