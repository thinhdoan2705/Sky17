using Microsoft.EntityFrameworkCore;
using Sky17.Application;
using Sky17.Domain;

namespace Sky17.Tests;

/// <summary>Seller ringing panel: many customers order at once; every order is accepted or rejected independently and safely.</summary>
public class IncomingOrdersTests
{
    private static async Task<PlaceOrderResult> OrderAsync(TestApp app, int productId, string name)
    {
        var customer = await app.CreateUserAsync(name, Roles.Customer);
        using var c = app.As(customer);
        await c.Get<ICartService>().AddAsync(productId, 1);
        return await c.PlaceOrderAsync(TestApp.Checkout());
    }

    [Fact]
    public async Task Panel_lists_pending_orders_of_all_my_stores_oldest_first()
    {
        await using var app = await TestApp.CreateAsync();
        var (owner, storeA, a1, _) = await app.CreateStoreAsync("Quán A", stock: 50);
        var (_, storeB, b1, _) = await app.CreateStoreAsync("Quán B", stock: 50);
        var (_, _, c1, _) = await app.CreateStoreAsync("Quán của người khác", stock: 50);
        await using (var db = app.Db())
        {
            db.StoreMembers.Add(new StoreMember { StoreId = storeB, UserId = owner.Id, Role = StoreMemberRole.Staff });
            await db.SaveChangesAsync();
        }

        var o1 = await OrderAsync(app, a1, "Khách 1");
        var o2 = await OrderAsync(app, b1, "Khách 2");
        var o3 = await OrderAsync(app, a1, "Khách 3");
        await OrderAsync(app, c1, "Khách 4"); // other owner's store: not shown

        using var s = app.As(owner);
        var seller = s.Get<ISellerService>();
        var incoming = await seller.GetIncomingOrdersAsync();
        Assert.Equal([o1.OrderId, o2.OrderId, o3.OrderId], incoming.Select(o => o.OrderId).ToArray());
        Assert.Equal(storeB, incoming[1].StoreId);
        Assert.All(incoming, o => Assert.Equal(o.PlacedAtUtc.AddMinutes(10), o.AutoCancelAtUtc));
        Assert.Equal("Cà phê sữa đá", Assert.Single(incoming[0].Items).Name);

        // Accepting one removes it from the panel; the others stay.
        await seller.ChangeOrderStatusAsync(storeA, o1.OrderId, OrderStatus.Confirmed, null);
        Assert.Equal([o2.OrderId, o3.OrderId], (await seller.GetIncomingOrdersAsync()).Select(o => o.OrderId).ToArray());
    }

    [Fact]
    public async Task Many_simultaneous_orders_can_all_be_accepted_or_rejected()
    {
        await using var app = await TestApp.CreateAsync();
        var (owner, storeId, pa, _) = await app.CreateStoreAsync(stock: 50);
        var placed = await Task.WhenAll(Enumerable.Range(1, 6).Select(i => OrderAsync(app, pa, $"Khách {i}")));
        Assert.Equal(6, placed.Select(p => p.OrderId).Distinct().Count());

        using var s = app.As(owner);
        var seller = s.Get<ISellerService>();
        Assert.Equal(6, (await seller.GetIncomingOrdersAsync()).Count);

        // Reject one with a reason, accept the rest.
        await seller.ChangeOrderStatusAsync(storeId, placed[0].OrderId, OrderStatus.Cancelled, "Hết món");
        foreach (var p in placed.Skip(1)) await seller.ChangeOrderStatusAsync(storeId, p.OrderId, OrderStatus.Confirmed, null);

        Assert.Empty(await seller.GetIncomingOrdersAsync());
        await using var db = app.Db();
        var orders = await db.Orders.ToDictionaryAsync(o => o.Id);
        Assert.Equal(OrderStatus.Cancelled, orders[placed[0].OrderId].Status);
        Assert.Equal("Hết món", orders[placed[0].OrderId].CancelReason);
        Assert.All(placed.Skip(1), p => Assert.Equal(OrderStatus.Confirmed, orders[p.OrderId].Status));
        Assert.Equal(50 - 5, (await db.Products.FindAsync(pa))!.Stock); // rejected order's stock was restored
    }

    [Fact]
    public async Task Two_staff_accepting_the_same_order_only_one_wins()
    {
        await using var app = await TestApp.CreateAsync();
        var (owner, storeId, pa, _) = await app.CreateStoreAsync(stock: 50);
        var staff = await app.CreateUserAsync("Nhân viên", Roles.Seller);
        await using (var db = app.Db())
        {
            db.StoreMembers.Add(new StoreMember { StoreId = storeId, UserId = staff.Id, Role = StoreMemberRole.Staff });
            await db.SaveChangesAsync();
        }
        var order = await OrderAsync(app, pa, "Khách");

        using var a = app.As(owner);
        using var b = app.As(staff);
        var results = await Task.WhenAll(
            Try(() => a.Get<ISellerService>().RespondToIncomingAsync(storeId, order.OrderId, true, null)),
            Try(() => b.Get<ISellerService>().RespondToIncomingAsync(storeId, order.OrderId, false, "Quán đang quá tải")));
        Assert.Equal(1, results.Count(ok => ok));

        await using var check = app.Db();
        var o = await check.Orders.Include(x => x.History).SingleAsync(x => x.Id == order.OrderId);
        Assert.Contains(o.Status, new[] { OrderStatus.Confirmed, OrderStatus.Cancelled });
        Assert.Single(o.History, h => h.PreviousStatus == OrderStatus.Pending); // exactly one transition out of Pending
        Assert.Equal(2, o.History.Count); // placed + one response: the stale panel could not cancel an accepted order (or vice versa)
    }

    [Fact]
    public async Task Accepting_an_order_the_customer_already_cancelled_fails_cleanly()
    {
        await using var app = await TestApp.CreateAsync();
        var (owner, storeId, pa, _) = await app.CreateStoreAsync(stock: 50);
        var customer = await app.CreateUserAsync("Khách", Roles.Customer);
        PlaceOrderResult placed;
        using (var c = app.As(customer))
        {
            await c.Get<ICartService>().AddAsync(pa, 1);
            placed = await c.PlaceOrderAsync(TestApp.Checkout());
            await c.Get<ICustomerOrderService>().CancelAsync(placed.Code, "Đổi ý");
        }
        using var s = app.As(owner);
        await Assert.ThrowsAsync<DomainException>(() => s.Get<ISellerService>().RespondToIncomingAsync(storeId, placed.OrderId, true, null));
        Assert.Empty(await s.Get<ISellerService>().GetIncomingOrdersAsync());
    }

    private static async Task<bool> Try(Func<Task> action)
    {
        try { await action(); return true; }
        catch (Exception ex) when (ex is DomainException or ConcurrencyConflictException) { return false; }
    }
}
