using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure.Data;

namespace Sky17.Infrastructure.Services;

/// <summary>
/// GrabFood-style dispatcher.
/// <list type="bullet">
/// <item>An order enters driver search when the store accepts it (status Confirmed, no driver).</item>
/// <item>It is offered to ONE driver at a time: the nearest free driver (online, approved, no active trip,
/// fresh GPS, within radius, not already asked in this round). The offer rings for N seconds.</item>
/// <item>Decline / timeout → next nearest driver. After everyone nearby was asked, a new round starts after a cool-down.</item>
/// <item>Stores that never accept are auto-cancelled; admins are alerted when no driver is found for a while.</item>
/// </list>
/// Database guards: one pending offer per order and per driver (filtered unique indexes); acceptance uses the atomic assignment.
/// </summary>
public sealed class DispatchService(
    IDbContextFactory<AppDbContext> dbf, IClock clock, Notifier notifier, OrderWorkflow workflow, ILogger<DispatchService> logger) : IDispatchService
{
    /// <summary>A driver who was asked for an order is not asked again for it until this cool-down passes.</summary>
    public static readonly TimeSpan ReofferCooldown = TimeSpan.FromMinutes(2);

    public async Task<DispatchCycleResult> RunCycleAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        await using var db = await dbf.CreateDbContextAsync(ct);
        var s = await Settings.DispatchAsync(db, ct);

        var expired = await ExpireOffersAsync(db, now, ct);
        var cancelled = await AutoCancelUnconfirmedAsync(db, now, s, ct);

        var searching = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Confirmed && o.DriverId == null
                        && !o.Offers.Any(x => x.Status == DispatchOfferStatus.Pending))
            .OrderBy(o => o.DispatchStartedAtUtc)
            .Select(o => o.Id).Take(50).ToListAsync(ct);
        var sent = 0;
        foreach (var id in searching)
            if (await OfferNextAsync(db, id, now, s, ct)) sent++;

        var alerts = await AlertLongSearchesAsync(db, now, s, ct);
        return new DispatchCycleResult(expired, sent, cancelled, alerts);
    }

    public async Task DispatchOrderAsync(int orderId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var s = await Settings.DispatchAsync(db, ct);
        await OfferNextAsync(db, orderId, clock.UtcNow, s, ct);
    }

    private async Task<int> ExpireOffersAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var due = await db.DispatchOffers.AsNoTracking()
            .Where(x => x.Status == DispatchOfferStatus.Pending && x.ExpiresAtUtc <= now)
            .Select(x => new { x.Id, x.DriverId, x.OrderId }).ToListAsync(ct);
        var count = 0;
        foreach (var o in due)
        {
            var changed = await db.DispatchOffers.Where(x => x.Id == o.Id && x.Status == DispatchOfferStatus.Pending)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, DispatchOfferStatus.Expired).SetProperty(x => x.RespondedAtUtc, now), ct);
            if (changed == 0) continue;
            count++;
            // Closes the popup on the driver's phone.
            await notifier.PushAsync([o.DriverId], new RealtimeMessage("offer_closed", "Lời mời đã hết hạn", "Chuyến đã được chuyển cho tài xế khác.", null, o.OrderId), ct);
        }
        return count;
    }

    private async Task<int> AutoCancelUnconfirmedAsync(AppDbContext db, DateTime now, Settings.DispatchSettings s, CancellationToken ct)
    {
        var cutoff = now.AddMinutes(-s.StoreConfirmTimeoutMinutes);
        var ids = await db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.Pending && o.PlacedAtUtc < cutoff).Select(o => o.Id).ToListAsync(ct);
        var count = 0;
        foreach (var id in ids)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var order = await OrderWorkflow.WithWorkflowGraph(db.Orders).FirstAsync(o => o.Id == id, ct);
                if (order.Status != OrderStatus.Pending) continue;
                await workflow.ApplyAsync(db, order, OrderStatus.Cancelled, OrderActor.System, null,
                    $"Cửa hàng không xác nhận trong {s.StoreConfirmTimeoutMinutes} phút", ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                db.ChangeTracker.Clear();
                count++;
                await notifier.OrderStatusChangedAsync(id, ct);
            }
            catch (Exception ex) when (ex is DbUpdateException or DomainException)
            {
                db.ChangeTracker.Clear();
                logger.LogInformation("Auto-cancel skipped for order {OrderId}: {Message}", id, ex.Message);
            }
        }
        return count;
    }

    private sealed record Candidate(string UserId, int? DistanceMeters, DateTime LocationAt);

    /// <summary>Offers the order to the nearest eligible free driver. Returns true when an offer was created.</summary>
    private async Task<bool> OfferNextAsync(AppDbContext db, int orderId, DateTime now, Settings.DispatchSettings s, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking()
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Confirmed && o.DriverId == null)
            .Select(o => new
            {
                o.Id, o.Code, o.PickupStoreName, o.PickupLatitude, o.PickupLongitude, o.DispatchStartedAtUtc,
                StoreLat = o.Store.Latitude, StoreLng = o.Store.Longitude,
                HasPending = o.Offers.Any(x => x.Status == DispatchOfferStatus.Pending),
            })
            .FirstOrDefaultAsync(ct);
        if (order is null || order.HasPending) return false;

        var pickup = GeoPoint.From(order.PickupLatitude ?? order.StoreLat, order.PickupLongitude ?? order.StoreLng);
        var freshSince = now.AddMinutes(-s.LocationFreshMinutes);
        var recentSince = now - ReofferCooldown;

        // Online, approved, active account, fresh GPS, no active trip, no other ringing offer,
        // not asked for this order recently.
        var rows = await (from d in db.DriverProfiles.AsNoTracking()
                          join u in db.Users on d.UserId equals u.Id
                          where d.Status == DriverStatus.Active && d.IsOnline && u.IsActive
                                && d.LastLatitude != null && d.LastLongitude != null && d.LastLocationAtUtc >= freshSince
                                && !db.Orders.Any(o => o.DriverId == d.UserId && (o.Status == OrderStatus.Assigned || o.Status == OrderStatus.ArrivedAtStore
                                                        || o.Status == OrderStatus.Delivering || o.Status == OrderStatus.ArrivedAtCustomer))
                                && !db.DispatchOffers.Any(x => x.DriverId == d.UserId && x.Status == DispatchOfferStatus.Pending)
                                && !db.DispatchOffers.Any(x => x.DriverId == d.UserId && x.OrderId == orderId && x.OfferedAtUtc >= recentSince)
                          select new { d.UserId, d.LastLatitude, d.LastLongitude, d.LastLocationAtUtc }).ToListAsync(ct);

        var candidates = rows.Select(r =>
        {
            int? dist = pickup is { } p ? new GeoPoint(r.LastLatitude!.Value, r.LastLongitude!.Value).StraightLineMetersTo(p) : null;
            return new Candidate(r.UserId, dist, r.LastLocationAtUtc!.Value);
        });
        if (pickup is not null) candidates = candidates.Where(c => c.DistanceMeters <= s.RadiusKm * 1000).OrderBy(c => c.DistanceMeters);
        else candidates = candidates.OrderByDescending(c => c.LocationAt); // store not pinned: any free driver, freshest first

        var best = candidates.FirstOrDefault();
        if (best is null) return false;

        var offer = new DispatchOffer
        {
            OrderId = orderId,
            DriverId = best.UserId,
            DriverDistanceMeters = best.DistanceMeters,
            OfferedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(s.OfferTimeoutSeconds),
        };
        db.DispatchOffers.Add(offer);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another dispatcher created a pending offer for this order/driver at the same moment.
            db.ChangeTracker.Clear();
            return false;
        }
        db.ChangeTracker.Clear();

        var distanceText = best.DistanceMeters is { } m ? $" · cách bạn {(m >= 1000 ? $"{m / 1000.0:0.#} km" : $"{m} m")}" : "";
        await notifier.NotifyAsync([best.UserId], "offer", "🔔 Có chuyến mới!",
            $"Đơn {order.Code} tại {order.PickupStoreName}{distanceText}. Nhận trong {s.OfferTimeoutSeconds} giây!", "/driver", orderId, ct);
        logger.LogInformation("Offer {OfferId} for order {OrderId} sent to driver {DriverId} ({Distance} m)", offer.Id, orderId, best.UserId, best.DistanceMeters);
        return true;
    }

    private async Task<int> AlertLongSearchesAsync(AppDbContext db, DateTime now, Settings.DispatchSettings s, CancellationToken ct)
    {
        var cutoff = now.AddMinutes(-s.NoDriverAlertMinutes);
        var orders = await db.Orders
            .Where(o => o.Status == OrderStatus.Confirmed && o.DriverId == null && o.DispatchAlertedAtUtc == null && o.DispatchStartedAtUtc < cutoff)
            .ToListAsync(ct);
        if (orders.Count == 0) return 0;
        foreach (var o in orders) o.DispatchAlertedAtUtc = now;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var admins = await Notifier.AdminIdsAsync(db, ct);
        foreach (var o in orders)
        {
            await notifier.NotifyAsync(admins, "dispatch", "⚠️ Chưa tìm được tài xế",
                $"Đơn {o.Code} ({o.PickupStoreName}) đã tìm tài xế hơn {s.NoDriverAlertMinutes} phút. Hãy phân công thủ công.", $"/admin/orders/{o.Id}", o.Id, ct);
            await notifier.NotifyAsync([o.CustomerId], "order", "Đang tìm tài xế",
                $"Đơn {o.Code} đang được ưu tiên tìm tài xế, mong bạn thông cảm chờ thêm ít phút.", $"/orders/{o.Code}", o.Id, ct);
        }
        return orders.Count;
    }
}
