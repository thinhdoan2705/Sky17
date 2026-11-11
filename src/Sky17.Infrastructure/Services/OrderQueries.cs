using Microsoft.EntityFrameworkCore;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure.Data;

namespace Sky17.Infrastructure.Services;

internal static class OrderQueries
{
    public static IQueryable<OrderSummaryDto> ToSummaries(this IQueryable<Order> orders, AppDbContext db) =>
        from o in orders
        join cp in db.UserProfiles on o.CustomerId equals cp.UserId into cps
        from cp in cps.DefaultIfEmpty()
        join dp in db.UserProfiles on o.DriverId equals dp.UserId into dps
        from dp in dps.DefaultIfEmpty()
        orderby o.PlacedAtUtc descending
        select new OrderSummaryDto(
            o.Id, o.Code, o.Status, o.PickupStoreName,
            cp == null ? o.RecipientName : cp.FullName,
            o.RecipientPhone,
            dp == null ? null : dp.FullName,
            o.GrandTotal,
            o.Items.Sum(i => i.Quantity),
            o.PlacedAtUtc)
        {
            PreparationState = o.PreparationState,
        };

    public static IQueryable<Order> ApplyFilter(this IQueryable<Order> q, OrderFilter f)
    {
        if (f.Status is { } st) q = q.Where(o => o.Status == st);
        if (f.Bucket == "active")
            q = q.Where(o => o.Status != OrderStatus.Delivered && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned);
        else if (f.Bucket == "done")
            q = q.Where(o => o.Status == OrderStatus.Delivered || o.Status == OrderStatus.Cancelled || o.Status == OrderStatus.Returned);
        if (f.StoreId is { } sid) q = q.Where(o => o.StoreId == sid);
        if (f.From is { } from) { var u = VnTime.LocalDateToUtc(from); q = q.Where(o => o.PlacedAtUtc >= u); }
        if (f.To is { } to) { var u = VnTime.LocalDateToUtc(to.AddDays(1)); q = q.Where(o => o.PlacedAtUtc < u); }
        if (f.Search.Clean() is { } s)
        {
            var like = $"%{s}%";
            q = q.Where(o => EF.Functions.Like(o.Code, like) || EF.Functions.Like(o.RecipientName, like)
                || EF.Functions.Like(o.RecipientPhone, like) || EF.Functions.Like(o.PickupStoreName, like));
        }
        return q;
    }

    public static async Task<OrderDetailDto> LoadDetailAsync(AppDbContext db, int orderId, OrderActor viewer, CancellationToken ct)
    {
        var o = await db.Orders.AsNoTracking()
            .Include(x => x.Items)
            .Include(x => x.History)
            .Include(x => x.Assignments)
            .Include(x => x.Payment)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == orderId, ct) ?? throw new NotFoundException("Không tìm thấy đơn hàng.");

        var userIds = new List<string?> { o.CustomerId, o.DriverId };
        userIds.AddRange(o.History.Select(h => h.ChangedByUserId));
        userIds.AddRange(o.Assignments.Select(a => a.DriverId));
        var names = await Names.LookupAsync(db, userIds, ct);

        string? driverPhone = null, driverPlate = null;
        (double? Lat, double? Lng, DateTime? At) driverLocation = default;
        var offers = viewer == OrderActor.Admin
            ? await (from f in db.DispatchOffers.AsNoTracking()
                     where f.OrderId == orderId
                     join p in db.UserProfiles on f.DriverId equals p.UserId into ps
                     from p in ps.DefaultIfEmpty()
                     orderby f.OfferedAtUtc descending
                     select new DispatchOfferHistoryDto(f.Id, f.DriverId, p == null ? f.DriverId : p.FullName, f.Status, f.DriverDistanceMeters, f.OfferedAtUtc, f.RespondedAtUtc)).ToListAsync(ct)
            : [];
        if (o.DriverId is not null)
        {
            driverPhone = await db.UserProfiles.AsNoTracking().Where(p => p.UserId == o.DriverId).Select(p => p.PhoneNumber).FirstOrDefaultAsync(ct);
            var dp = await db.DriverProfiles.AsNoTracking().Where(p => p.UserId == o.DriverId)
                .Select(p => new { p.LicensePlate, p.LastLatitude, p.LastLongitude, p.LastLocationAtUtc }).FirstOrDefaultAsync(ct);
            driverPlate = dp?.LicensePlate;
            // Live position is shared only while the driver is working on this order.
            if (dp is not null && OrderStateMachine.IsInDelivery(o.Status)) driverLocation = (dp.LastLatitude, dp.LastLongitude, dp.LastLocationAtUtc);
        }

        return new OrderDetailDto(
            o.Id, o.Code, o.Status, o.StoreId, o.PickupStoreName, o.PickupAddress, o.PickupPhone,
            o.CustomerId, names.GetValueOrDefault(o.CustomerId, o.RecipientName), o.RecipientName, o.RecipientPhone, o.DeliveryAddress, o.DeliveryNote,
            o.DistanceMeters,
            o.ProductTotal, o.DeliveryFee, o.DiscountAmount, o.GrandTotal, o.CollectedCodAmount,
            o.PlatformFee, o.StoreReceivable, o.DriverEarning,
            o.Payment?.Status ?? PaymentStatus.Pending, o.StoreSettlementState, o.DriverSettlementState,
            o.DriverId, o.DriverId is null ? null : names.GetValueOrDefault(o.DriverId), driverPhone, driverPlate,
            o.CancelReason, o.FailureReason, o.PlacedAtUtc, o.DeliveredAtUtc,
            o.Items.OrderBy(i => i.Id).Select(i => new OrderItemDto(i.ProductId, i.ProductName, i.Emoji, i.UnitPrice, i.Quantity, i.LineTotal)).ToList(),
            o.History.OrderBy(h => h.ChangedAtUtc).ThenBy(h => h.Id)
                .Select(h => new OrderHistoryDto(h.PreviousStatus, h.NewStatus, h.ChangedByUserId is null ? "Hệ thống" : names.GetValueOrDefault(h.ChangedByUserId), h.ChangedAtUtc, h.Reason)).ToList(),
            o.Assignments.OrderByDescending(a => a.AssignedAtUtc)
                .Select(a => new AssignmentDto(a.Id, a.DriverId, names.GetValueOrDefault(a.DriverId, "—"), a.Status, a.IsActive, a.AssignedByUserId is null, a.AssignedAtUtc, a.CompletedAtUtc, a.Note)).ToList(),
            OrderStateMachine.NextStatuses(o.Status, viewer))
        {
            PickupLatitude = o.PickupLatitude,
            PickupLongitude = o.PickupLongitude,
            DropoffLatitude = o.DropoffLatitude,
            DropoffLongitude = o.DropoffLongitude,
            DistanceSource = o.DistanceSource,
            PreparationState = o.PreparationState,
            FoodReadyAtUtc = o.FoodReadyAtUtc,
            DispatchStartedAtUtc = o.DispatchStartedAtUtc,
            DriverLatitude = driverLocation.Lat,
            DriverLongitude = driverLocation.Lng,
            DriverLocationAtUtc = driverLocation.At,
            Offers = offers,
        };
    }

    public static async Task<List<DailyPoint>> DailyAsync(IQueryable<Order> orders, int days, Func<Order, long> amount, DateTime nowUtc, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(VnTime.ToLocal(nowUtc));
        var start = today.AddDays(-(days - 1));
        var startUtc = VnTime.LocalDateToUtc(start);
        var rows = await orders.Where(o => o.PlacedAtUtc >= startUtc).AsNoTracking().ToListAsync(ct);
        return Enumerable.Range(0, days).Select(i =>
        {
            var d = start.AddDays(i);
            var dayRows = rows.Where(o => DateOnly.FromDateTime(VnTime.ToLocal(o.PlacedAtUtc)) == d).ToList();
            return new DailyPoint(d, dayRows.Count, dayRows.Sum(amount));
        }).ToList();
    }
}
