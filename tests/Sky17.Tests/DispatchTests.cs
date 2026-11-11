using Microsoft.EntityFrameworkCore;
using Sky17.Application;
using Sky17.Domain;

namespace Sky17.Tests;

/// <summary>GrabFood-style dispatch: nearest free driver, 30 s offers, decline/timeout → next driver.</summary>
public class DispatchTests
{
    [Fact]
    public async Task Store_acceptance_offers_the_order_to_the_nearest_free_driver()
    {
        await using var app = await TestApp.CreateAsync();
        var far = await app.CreateDriverAsync("Far", metersNorth: 3_000);
        var near = await app.CreateDriverAsync("Near", metersNorth: 200);
        var (_, _, _, placed) = await app.CreateReadyOrderAsync(); // store accepted → dispatch runs immediately

        using (var n = app.As(near))
        {
            var offer = await n.Get<IDriverService>().GetPendingOfferAsync();
            Assert.NotNull(offer);
            Assert.Equal(placed.OrderId, offer.OrderId);
            Assert.InRange(offer.DriverDistanceMeters!.Value, 150, 250);
            Assert.Equal(30, offer.TimeoutSeconds);
        }
        using (var f = app.As(far)) Assert.Null(await f.Get<IDriverService>().GetPendingOfferAsync()); // one driver at a time
    }

    [Fact]
    public async Task Decline_moves_the_offer_to_the_next_nearest_driver()
    {
        await using var app = await TestApp.CreateAsync();
        var near = await app.CreateDriverAsync("Near", metersNorth: 200);
        var second = await app.CreateDriverAsync("Second", metersNorth: 900);
        var (_, _, _, placed) = await app.CreateReadyOrderAsync();

        using (var n = app.As(near))
        {
            var offer = await n.Get<IDriverService>().GetPendingOfferAsync();
            await n.Get<IDriverService>().DeclineOfferAsync(offer!.OfferId);
        }
        using (var s = app.As(second))
        {
            var offer = await s.Get<IDriverService>().GetPendingOfferAsync();
            Assert.NotNull(offer);
            await s.Get<IDriverService>().AcceptOfferAsync(offer.OfferId);
        }
        await using var db = app.Db();
        var order = await db.Orders.SingleAsync(o => o.Id == placed.OrderId);
        Assert.Equal(OrderStatus.Assigned, order.Status);
        Assert.Equal(second.Id, order.DriverId);
    }

    [Fact]
    public async Task Unanswered_offer_expires_after_30_seconds_and_cannot_be_accepted()
    {
        await using var app = await TestApp.CreateAsync();
        var near = await app.CreateDriverAsync("Near", metersNorth: 200);
        var second = await app.CreateDriverAsync("Second", metersNorth: 900);
        await app.CreateReadyOrderAsync();

        int staleOfferId;
        using (var n = app.As(near)) staleOfferId = (await n.Get<IDriverService>().GetPendingOfferAsync())!.OfferId;

        app.Clock.Advance(TimeSpan.FromSeconds(31));
        var cycle = await app.Dispatch.RunCycleAsync();
        Assert.Equal(1, cycle.OffersExpired);
        Assert.Equal(1, cycle.OffersSent);

        using (var n = app.As(near))
        {
            Assert.Null(await n.Get<IDriverService>().GetPendingOfferAsync());
            await Assert.ThrowsAsync<DomainException>(() => n.Get<IDriverService>().AcceptOfferAsync(staleOfferId));
        }
        using (var s = app.As(second)) Assert.NotNull(await s.Get<IDriverService>().GetPendingOfferAsync());
        await using var db = app.Db();
        Assert.Equal(DispatchOfferStatus.Expired, (await db.DispatchOffers.FindAsync(staleOfferId))!.Status);
    }

    [Fact]
    public async Task Busy_offline_unapproved_stale_and_far_drivers_are_not_offered()
    {
        await using var app = await TestApp.CreateAsync();
        await app.CreateDriverAsync("Offline", online: false);
        await app.CreateDriverAsync("Pending", approved: false);
        await app.CreateDriverAsync("Stale GPS", freshLocation: false);
        await app.CreateDriverAsync("Too far", metersNorth: 40_000); // radius 20 km
        var busy = await app.CreateDriverAsync("Busy", metersNorth: 100);
        var (_, _, _, first) = await app.CreateReadyOrderAsync();
        await app.AcceptViaDispatchAsync(busy, first.OrderId); // busy now has a trip

        await app.CreateReadyOrderAsync();
        var cycle = await app.Dispatch.RunCycleAsync();
        Assert.Equal(0, cycle.OffersSent);
        await using var db = app.Db();
        Assert.Equal(1, await db.DispatchOffers.CountAsync()); // only the first order's accepted offer
    }

    [Fact]
    public async Task Each_driver_rings_for_only_one_order_at_a_time()
    {
        await using var app = await TestApp.CreateAsync();
        var d1 = await app.CreateDriverAsync("D1", metersNorth: 100);
        var d2 = await app.CreateDriverAsync("D2", metersNorth: 800);
        var (_, _, _, a) = await app.CreateReadyOrderAsync();
        var (_, _, _, b) = await app.CreateReadyOrderAsync();
        await app.Dispatch.RunCycleAsync();
        await using var db = app.Db();
        var pending = await db.DispatchOffers.Where(x => x.Status == DispatchOfferStatus.Pending).ToListAsync();
        Assert.Equal(2, pending.Count);
        Assert.Equal(2, pending.Select(p => p.DriverId).Distinct().Count());
        Assert.Equal(2, pending.Select(p => p.OrderId).Distinct().Count());
    }

    [Fact]
    public async Task Admin_assignment_and_driver_acceptance_race_has_one_winner()
    {
        await using var app = await TestApp.CreateAsync();
        var offered = await app.CreateDriverAsync("Offered", metersNorth: 100);
        var manual = await app.CreateDriverAsync("Manual", metersNorth: 5_000);
        var admin = await app.CreateUserAsync("Admin", Roles.Admin);
        var (_, _, _, placed) = await app.CreateReadyOrderAsync();
        int offerId;
        using (var o = app.As(offered)) offerId = (await o.Get<IDriverService>().GetPendingOfferAsync())!.OfferId;

        // Concurrent: whatever the order of execution (admin may legitimately reassign after the driver accepted),
        // the order ends with exactly one active assignment that matches Order.DriverId and no ringing offer.
        await Task.WhenAll(
            Task.Run(async () => { using var s = app.As(offered); try { await s.Get<IDriverService>().AcceptOfferAsync(offerId); } catch (DomainException) { } }),
            Task.Run(async () => { using var s = app.As(admin); try { await s.Get<IAdminService>().AssignDriverAsync(placed.OrderId, manual.Id); } catch (Exception ex) when (ex is DomainException or ConcurrencyConflictException) { } }));

        await using var db = app.Db();
        var active = await db.DeliveryAssignments.Where(x => x.OrderId == placed.OrderId && x.IsActive).ToListAsync();
        Assert.Single(active);
        Assert.Equal((await db.Orders.SingleAsync(o => o.Id == placed.OrderId)).DriverId, active[0].DriverId);
        Assert.False(await db.DispatchOffers.AnyAsync(x => x.Status == DispatchOfferStatus.Pending));
    }

    [Fact]
    public async Task Admin_assignment_withdraws_the_ringing_offer()
    {
        await using var app = await TestApp.CreateAsync();
        var offered = await app.CreateDriverAsync("Offered", metersNorth: 100);
        var manual = await app.CreateDriverAsync("Manual", metersNorth: 5_000);
        var admin = await app.CreateUserAsync("Admin", Roles.Admin);
        var (_, _, _, placed) = await app.CreateReadyOrderAsync();
        int offerId;
        using (var o = app.As(offered)) offerId = (await o.Get<IDriverService>().GetPendingOfferAsync())!.OfferId;

        using (var a = app.As(admin)) await a.Get<IAdminService>().AssignDriverAsync(placed.OrderId, manual.Id);

        using (var o = app.As(offered))
        {
            Assert.Null(await o.Get<IDriverService>().GetPendingOfferAsync()); // popup closes
            await Assert.ThrowsAsync<DomainException>(() => o.Get<IDriverService>().AcceptOfferAsync(offerId));
        }
        await using var db = app.Db();
        Assert.Equal(DispatchOfferStatus.Withdrawn, (await db.DispatchOffers.FindAsync(offerId))!.Status);
        Assert.Equal(manual.Id, (await db.Orders.SingleAsync(o => o.Id == placed.OrderId)).DriverId);
    }

    [Fact]
    public async Task Full_grab_flow_with_food_ready_and_cod()
    {
        await using var app = await TestApp.CreateAsync();
        var driver = await app.CreateDriverAsync("Lê Hoàng");
        var (customer, storeId, owner, placed) = await app.CreateReadyOrderAsync();
        await app.AcceptViaDispatchAsync(driver, placed.OrderId);

        using (var s = app.As(owner)) await s.Get<ISellerService>().MarkFoodReadyAsync(storeId, placed.OrderId);
        using (var d = app.As(driver))
        {
            var svc = d.Get<IDriverService>();
            Assert.Equal(PreparationState.Ready, (await svc.GetJobAsync(placed.OrderId)).PreparationState);
            await Assert.ThrowsAnyAsync<Exception>(() => svc.ConfirmPickupAsync(placed.OrderId)); // must arrive at the store first
        }
        await app.CompleteTripAsync(driver, placed.OrderId);

        await using var db = app.Db();
        var order = await db.Orders.Include(o => o.Payment).SingleAsync(o => o.Id == placed.OrderId);
        Assert.Equal(OrderStatus.Delivered, order.Status);
        Assert.Equal(PaymentStatus.Collected, order.Payment!.Status);
        Assert.Equal(105_000, (await db.CodTransactions.SingleAsync(c => c.OrderId == placed.OrderId)).Amount);
        var statuses = await db.OrderStatusHistories.Where(h => h.OrderId == placed.OrderId).OrderBy(h => h.Id).Select(h => h.NewStatus).ToListAsync();
        Assert.Equal([OrderStatus.Pending, OrderStatus.Confirmed, OrderStatus.Assigned, OrderStatus.ArrivedAtStore, OrderStatus.Delivering, OrderStatus.ArrivedAtCustomer, OrderStatus.Delivered], statuses);
        using var dr = app.As(driver);
        var dash = await dr.Get<IDriverService>().GetDashboardAsync();
        Assert.Equal(105_000, dash.CodHolding);
        Assert.Equal(93_000, dash.OwedToPlatform);
    }

    [Fact]
    public async Task Driver_cancelling_a_trip_sends_the_order_to_another_driver()
    {
        await using var app = await TestApp.CreateAsync();
        var first = await app.CreateDriverAsync("First", metersNorth: 100);
        var (_, _, _, placed) = await app.CreateReadyOrderAsync();
        await app.AcceptViaDispatchAsync(first, placed.OrderId);
        var second = await app.CreateDriverAsync("Second", metersNorth: 700);

        using (var d = app.As(first)) await d.Get<IDriverService>().CancelTripAsync(placed.OrderId, "Xe hỏng");

        using (var s = app.As(second))
        {
            var offer = await s.Get<IDriverService>().GetPendingOfferAsync();
            Assert.NotNull(offer); // re-dispatched immediately, the cancelling driver is skipped
            Assert.Equal(placed.OrderId, offer.OrderId);
        }
        using (var d = app.As(first)) Assert.Null(await d.Get<IDriverService>().GetPendingOfferAsync());
    }

    [Fact]
    public async Task Store_that_never_accepts_gets_the_order_auto_cancelled_with_stock_restored()
    {
        await using var app = await TestApp.CreateAsync();
        var (_, _, pa, _) = await app.CreateStoreAsync(stock: 10);
        var customer = await app.CreateUserAsync("C", Roles.Customer);
        PlaceOrderResult placed;
        using (var c = app.As(customer))
        {
            await c.Get<ICartService>().AddAsync(pa, 3);
            placed = await c.Get<ICustomerOrderService>().PlaceOrderAsync(TestApp.Checkout());
        }
        Assert.Equal(0, (await app.Dispatch.RunCycleAsync()).OrdersAutoCancelled);
        app.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, (await app.Dispatch.RunCycleAsync()).OrdersAutoCancelled);

        await using var db = app.Db();
        var order = await db.Orders.SingleAsync(o => o.Id == placed.OrderId);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Contains("không xác nhận", order.CancelReason);
        Assert.Equal(10, (await db.Products.FindAsync(pa))!.Stock);
    }

    [Fact]
    public async Task Admins_are_alerted_when_no_driver_is_found()
    {
        await using var app = await TestApp.CreateAsync();
        var admin = await app.CreateUserAsync("Admin", Roles.Admin);
        await app.CreateReadyOrderAsync(); // no drivers at all
        app.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, (await app.Dispatch.RunCycleAsync()).Alerts);
        Assert.Equal(0, (await app.Dispatch.RunCycleAsync()).Alerts); // only once
        using var a = app.As(admin);
        Assert.Contains(await a.Get<INotificationService>().GetMineAsync(), n => n.Title.Contains("Chưa tìm được tài xế"));
    }

    [Fact]
    public async Task Driver_only_sees_order_details_while_offered_or_assigned()
    {
        await using var app = await TestApp.CreateAsync();
        var offered = await app.CreateDriverAsync("Offered", metersNorth: 100);
        var other = await app.CreateDriverAsync("Other", metersNorth: 5_000);
        var (_, _, _, placed) = await app.CreateReadyOrderAsync();
        using (var o = app.As(offered))
        {
            var preview = await o.Get<IDriverService>().GetJobAsync(placed.OrderId);
            Assert.Null(preview.CustomerPhone); // contact hidden until accepted
        }
        using (var o = app.As(other)) await Assert.ThrowsAsync<ForbiddenException>(() => o.Get<IDriverService>().GetJobAsync(placed.OrderId));
    }

    [Fact]
    public async Task Going_offline_declines_the_ringing_offer()
    {
        await using var app = await TestApp.CreateAsync();
        var d1 = await app.CreateDriverAsync("D1", metersNorth: 100);
        var d2 = await app.CreateDriverAsync("D2", metersNorth: 600);
        await app.CreateReadyOrderAsync();
        using (var s = app.As(d1)) await s.Get<IDriverService>().SetOnlineAsync(false);
        using (var s = app.As(d2)) Assert.NotNull(await s.Get<IDriverService>().GetPendingOfferAsync());
    }

    [Fact]
    public async Task Location_heartbeat_is_saved()
    {
        await using var app = await TestApp.CreateAsync();
        var d = await app.CreateDriverAsync("D");
        using var s = app.As(d);
        await s.Get<IDriverService>().UpdateLocationAsync(10.99, 106.66);
        var p = await s.Get<IDriverService>().GetProfileAsync();
        Assert.Equal(10.99, p.LastLatitude);
        await Assert.ThrowsAsync<DomainException>(() => s.Get<IDriverService>().UpdateLocationAsync(200, 0));
    }
}
