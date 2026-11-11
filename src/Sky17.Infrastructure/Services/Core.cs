using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure.Data;

namespace Sky17.Infrastructure.Services;

internal static class CodeGen
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Random(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    public static string OrderCode(DateTime utc) => $"SKY{VnTime.ToLocal(utc):yyMMdd}-{Random(4)}";
    public static string SettlementCode(SettlementType type, DateTime utc) => $"{(type == SettlementType.Store ? "STS" : "STD")}{VnTime.ToLocal(utc):yyMMdd}-{Random(4)}";
}

internal static class QueryExtensions
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<T>(items, page, pageSize, total);
    }

    public static string? Clean(this string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

internal static class AuditExtensions
{
    public static void Audit(this AppDbContext db, string? userId, string action, string entityType, object? entityId, string? details = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            Details = details is { Length: > 1000 } ? details[..1000] : details,
            CreatedAtUtc = DateTime.UtcNow,
        });
    }
}

internal static class Settings
{
    public static async Task<DeliveryRateRule> GetRateRuleAsync(AppDbContext db, CancellationToken ct) =>
        await db.DeliveryRateRules.AsNoTracking().Where(r => r.IsActive).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct)
        ?? new DeliveryRateRule();

    public static async Task<string?> GetAsync(AppDbContext db, string key, CancellationToken ct) =>
        await db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);

    public static async Task<bool> ServiceAvailableAsync(AppDbContext db, CancellationToken ct) =>
        !string.Equals(await GetAsync(db, SettingKeys.ServiceAvailable, ct), "false", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> MaxActiveDeliveriesAsync(AppDbContext db, CancellationToken ct) =>
        await IntAsync(db, SettingKeys.MaxActiveDeliveriesPerDriver, 2, ct);

    public static async Task<int> IntAsync(AppDbContext db, string key, int fallback, CancellationToken ct) =>
        int.TryParse(await GetAsync(db, key, ct), out var v) && v > 0 ? v : fallback;

    public sealed record DispatchSettings(int OfferTimeoutSeconds, int RadiusKm, int LocationFreshMinutes, int StoreConfirmTimeoutMinutes, int NoDriverAlertMinutes);

    public static async Task<DispatchSettings> DispatchAsync(AppDbContext db, CancellationToken ct) => new(
        await IntAsync(db, SettingKeys.OfferTimeoutSeconds, 30, ct),
        await IntAsync(db, SettingKeys.DispatchRadiusKm, 20, ct),
        await IntAsync(db, SettingKeys.LocationFreshMinutes, 15, ct),
        await IntAsync(db, SettingKeys.StoreConfirmTimeoutMinutes, 10, ct),
        await IntAsync(db, SettingKeys.NoDriverAlertMinutes, 5, ct));

    public static async Task SetAsync(AppDbContext db, string key, string value, string? userId, CancellationToken ct)
    {
        var s = await db.SystemSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (s is null) db.SystemSettings.Add(new SystemSetting { Key = key, Value = value, UpdatedAtUtc = DateTime.UtcNow, UpdatedByUserId = userId });
        else { s.Value = value; s.UpdatedAtUtc = DateTime.UtcNow; s.UpdatedByUserId = userId; }
    }
}

internal static class Names
{
    public static async Task<Dictionary<string, string>> LookupAsync(AppDbContext db, IEnumerable<string?> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(i => i is not null).Select(i => i!).Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.UserProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, p => p.FullName, ct);
    }
}

/// <summary>Applies a validated status transition and all of its side effects to a tracked order.</summary>
public sealed class OrderWorkflow(IClock clock)
{
    public static IQueryable<Order> WithWorkflowGraph(IQueryable<Order> q) =>
        q.Include(o => o.Items).Include(o => o.Assignments.Where(a => a.IsActive)).Include(o => o.Payment);

    public async Task ApplyAsync(AppDbContext db, Order order, OrderStatus to, OrderActor actor, string? userId, string? reason, CancellationToken ct)
    {
        OrderStateMachine.EnsureCanTransition(order.Status, to, actor);
        var from = order.Status;
        var now = clock.UtcNow;
        var active = order.Assignments.FirstOrDefault(a => a.IsActive);
        reason = reason.Clean();

        switch (to)
        {
            case OrderStatus.Cancelled:
                if (reason is null) throw new DomainException("Vui lòng nhập lý do hủy đơn.");
                order.CancelReason = reason;
                if (OrderStateMachine.IsBeforePickup(from)) await RestoreStockAsync(db, order, ct);
                CloseAssignment(active, AssignmentStatus.Cancelled, now, reason);
                if (order.Payment is not null) order.Payment.Status = PaymentStatus.Cancelled;
                break;

            case OrderStatus.Returned:
                order.CancelReason = reason;
                await RestoreStockAsync(db, order, ct);
                if (order.Payment is not null) order.Payment.Status = PaymentStatus.Cancelled;
                break;

            case OrderStatus.Confirmed when from == OrderStatus.Pending:
                // Store accepted: kitchen starts, driver search starts immediately (GrabFood style).
                order.ConfirmedAtUtc = now;
                order.DispatchStartedAtUtc = now;
                order.DispatchAlertedAtUtc = null;
                order.PreparationState = PreparationState.Preparing;
                break;

            case OrderStatus.Confirmed:
                // Back to driver search: driver cancelled the trip, admin unassigned, or failed delivery retried.
                if (actor != OrderActor.System && reason is null) throw new DomainException("Vui lòng nhập lý do.");
                CloseAssignment(active, AssignmentStatus.Released, now, reason);
                order.DriverId = null;
                order.DispatchStartedAtUtc = now;
                order.DispatchAlertedAtUtc = null;
                break;

            case OrderStatus.ArrivedAtStore:
                RequireActive(active).Status = AssignmentStatus.ArrivedAtStore;
                break;

            case OrderStatus.Delivering:
                RequireActive(active).Status = AssignmentStatus.Delivering;
                active!.PickedUpAtUtc = now;
                active.DeliveringAtUtc = now;
                // Picking up implies the food was handed over.
                if (order.PreparationState != PreparationState.Ready)
                {
                    order.PreparationState = PreparationState.Ready;
                    order.FoodReadyAtUtc ??= now;
                }
                break;

            case OrderStatus.ArrivedAtCustomer:
                RequireActive(active).Status = AssignmentStatus.ArrivedAtCustomer;
                break;

            case OrderStatus.Delivered:
                CloseAssignment(RequireActive(active), AssignmentStatus.Delivered, now, null);
                order.DeliveredAtUtc = now;
                order.CollectedCodAmount = order.GrandTotal;
                if (order.Payment is not null)
                {
                    order.Payment.Status = PaymentStatus.Collected;
                    order.Payment.CollectedAtUtc = now;
                    order.Payment.CollectedByUserId = order.DriverId;
                }
                db.CodTransactions.Add(new CodTransaction
                {
                    OrderId = order.Id,
                    DriverId = order.DriverId ?? throw new DomainException("Đơn chưa có tài xế."),
                    Type = CodTransactionType.CollectedFromCustomer,
                    Amount = order.GrandTotal,
                    Note = $"Thu hộ đơn {order.Code}",
                });
                break;

            case OrderStatus.DeliveryFailed:
                if (reason is null) throw new DomainException("Vui lòng nhập lý do giao thất bại.");
                order.FailureReason = reason;
                CloseAssignment(active, AssignmentStatus.Failed, now, reason);
                break;

            case OrderStatus.Assigned:
                throw new InvalidOperationException("Use AssignAsync for assignments.");
        }

        // Any change of hands withdraws a ringing offer.
        if (to is OrderStatus.Cancelled or OrderStatus.Returned)
            await WithdrawOffersAsync(db, order.Id, now, ct);

        order.Status = to;
        db.OrderStatusHistories.Add(new OrderStatusHistory
        {
            OrderId = order.Id,
            PreviousStatus = from,
            NewStatus = to,
            ChangedByUserId = userId,
            ChangedAtUtc = now,
            Reason = reason,
        });
        db.Audit(userId, $"Order.{to}", nameof(Order), order.Id, $"{order.Code}: {from} → {to}{(reason is null ? "" : " · " + reason)}");
    }

    private static DeliveryAssignment RequireActive(DeliveryAssignment? a) =>
        a ?? throw new DomainException("Đơn không có phân công giao hàng đang hoạt động.");

    private static void CloseAssignment(DeliveryAssignment? a, AssignmentStatus status, DateTime now, string? note)
    {
        if (a is null) return;
        a.Status = status;
        a.IsActive = false;
        a.CompletedAtUtc = now;
        a.Note = note ?? a.Note;
    }

    private static async Task RestoreStockAsync(AppDbContext db, Order order, CancellationToken ct)
    {
        foreach (var item in order.Items)
        {
            var qty = item.Quantity;
            await db.Products.Where(p => p.Id == item.ProductId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock + qty).SetProperty(p => p.Version, p => p.Version + 1), ct);
        }
    }

    public static Task WithdrawOffersAsync(AppDbContext db, int orderId, DateTime now, CancellationToken ct) =>
        db.DispatchOffers.Where(x => x.OrderId == orderId && x.Status == DispatchOfferStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DispatchOfferStatus.Withdrawn).SetProperty(x => x.RespondedAtUtc, now), ct);

    /// <summary>
    /// Atomically claims a searching (Confirmed, no driver) order for a driver. Must run inside a transaction.
    /// Only one caller can win: the conditional UPDATE matches at most once, and a filtered
    /// unique index allows a single active assignment per order.
    /// </summary>
    public async Task AssignAsync(AppDbContext db, int orderId, string driverId, OrderActor actor, string actingUserId, CancellationToken ct)
    {
        OrderStateMachine.EnsureCanTransition(OrderStatus.Confirmed, OrderStatus.Assigned, actor);
        var now = clock.UtcNow;
        var claimed = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Confirmed && o.DriverId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OrderStatus.Assigned)
                .SetProperty(o => o.DriverId, driverId)
                .SetProperty(o => o.UpdatedAtUtc, now)
                .SetProperty(o => o.Version, o => o.Version + 1), ct);
        if (claimed == 0)
            throw new DomainException("Đơn này đã được tài xế khác nhận hoặc không còn sẵn sàng.");

        db.DeliveryAssignments.Add(new DeliveryAssignment
        {
            OrderId = orderId,
            DriverId = driverId,
            Status = AssignmentStatus.Assigned,
            IsActive = true,
            AssignedByUserId = actor == OrderActor.Admin ? actingUserId : null,
            AssignedAtUtc = now,
        });
        db.OrderStatusHistories.Add(new OrderStatusHistory
        {
            OrderId = orderId,
            PreviousStatus = OrderStatus.Confirmed,
            NewStatus = OrderStatus.Assigned,
            ChangedByUserId = actingUserId,
            ChangedAtUtc = now,
            Reason = actor == OrderActor.Admin ? "Admin phân công tài xế" : "Tài xế nhận chuyến",
        });
        // Other ringing offers for this order (e.g. admin assigned while a popup was open) are withdrawn.
        await db.DispatchOffers.Where(x => x.OrderId == orderId && x.Status == DispatchOfferStatus.Pending && x.DriverId != driverId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DispatchOfferStatus.Withdrawn).SetProperty(x => x.RespondedAtUtc, now), ct);
        db.Audit(actingUserId, "Order.Assigned", nameof(Order), orderId, $"driver={driverId}");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Validates that the driver can take one more delivery.</summary>
    public static async Task EnsureDriverCanTakeAsync(AppDbContext db, string driverId, bool requireOnline, CancellationToken ct)
    {
        var profile = await db.DriverProfiles.AsNoTracking().FirstOrDefaultAsync(d => d.UserId == driverId, ct)
            ?? throw new DomainException("Không tìm thấy hồ sơ tài xế.");
        if (profile.Status != DriverStatus.Active) throw new DomainException("Tài xế chưa được duyệt hoặc đang bị tạm khóa.");
        var accountActive = await db.Users.AsNoTracking().Where(u => u.Id == driverId).Select(u => u.IsActive).FirstOrDefaultAsync(ct);
        if (!accountActive) throw new DomainException("Tài khoản tài xế đang bị vô hiệu hóa.");
        if (requireOnline && !profile.IsOnline) throw new DomainException("Bạn cần bật trạng thái trực tuyến để nhận đơn.");
        var max = await Settings.MaxActiveDeliveriesAsync(db, ct);
        var active = await ActiveCountAsync(db, driverId, ct);
        if (active >= max) throw new DomainException($"Tài xế đang có {active} đơn, tối đa {max} đơn cùng lúc.");
    }

    public static Task<int> ActiveCountAsync(AppDbContext db, string driverId, CancellationToken ct) =>
        db.Orders.CountAsync(o => o.DriverId == driverId && (o.Status == OrderStatus.Assigned || o.Status == OrderStatus.ArrivedAtStore
            || o.Status == OrderStatus.Delivering || o.Status == OrderStatus.ArrivedAtCustomer), ct);
}

internal static class Persist
{
    /// <summary>Saves and translates optimistic concurrency failures into a user-friendly exception.</summary>
    public static async Task SaveAsync(this AppDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ConcurrencyConflictException("Thao tác bị trùng lặp hoặc dữ liệu đã thay đổi. Vui lòng tải lại.");
        }
    }
}

/// <summary>Persists notifications and pushes them to the relevant users only.</summary>
public sealed class Notifier(IDbContextFactory<AppDbContext> dbf, IRealtimePublisher publisher, ILogger<Notifier> logger)
{
    public async Task NotifyAsync(IEnumerable<string> userIds, string type, string title, string message, string? link, int? orderId = null, CancellationToken ct = default)
    {
        var ids = userIds.Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
        if (ids.Count == 0) return;
        try
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var id in ids)
                db.Notifications.Add(new Notification { UserId = id, Type = type, Title = title, Message = message, Link = link, CreatedAtUtc = now });
            await db.SaveChangesAsync(ct);
            await publisher.PublishAsync(ids, new RealtimeMessage(type, title, message, link, orderId), ct);
        }
        catch (Exception ex)
        {
            // Notifications must never break the business operation that already committed.
            logger.LogWarning(ex, "Failed to deliver notification {Type}", type);
        }
    }

    /// <summary>Realtime-only signal (not persisted), e.g. closing an offer popup.</summary>
    public async Task PushAsync(IReadOnlyCollection<string> userIds, RealtimeMessage message, CancellationToken ct = default)
    {
        try { await publisher.PublishAsync(userIds, message, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Realtime push failed"); }
    }

    public async Task OrderStatusChangedAsync(int orderId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var o = await db.Orders.AsNoTracking()
                .Where(x => x.Id == orderId)
                .Select(x => new { x.Id, x.Code, x.Status, x.CustomerId, x.StoreId, x.DriverId, x.PickupStoreName, x.GrandTotal, x.CancelReason, x.FailureReason })
                .FirstOrDefaultAsync(ct);
            if (o is null) return;

            var storeUsers = await db.StoreMembers.AsNoTracking().Where(m => m.StoreId == o.StoreId).Select(m => m.UserId).ToListAsync(ct);
            var driverName = o.DriverId is null ? null
                : await db.UserProfiles.AsNoTracking().Where(p => p.UserId == o.DriverId).Select(p => p.FullName).FirstOrDefaultAsync(ct);
            var customerLink = $"/orders/{o.Code}";
            var sellerLink = $"/seller/orders/{o.Id}";
            var driverLink = $"/driver/deliveries/{o.Id}";
            const string t = "order";

            switch (o.Status)
            {
                case OrderStatus.Pending:
                    await NotifyAsync(storeUsers, t, "🛎️ Đơn hàng mới", $"Đơn {o.Code} vừa được đặt, tổng {o.GrandTotal:N0}đ.", sellerLink, o.Id, ct);
                    await NotifyAsync([o.CustomerId], t, "Đặt hàng thành công", $"Đơn {o.Code} đã gửi tới {o.PickupStoreName}.", customerLink, o.Id, ct);
                    break;
                case OrderStatus.Confirmed:
                    await NotifyAsync([o.CustomerId], t, "🍳 Cửa hàng đã nhận đơn", $"{o.PickupStoreName} đang chuẩn bị đơn {o.Code}. Đang tìm tài xế gần nhất...", customerLink, o.Id, ct);
                    break;
                case OrderStatus.Assigned:
                    await NotifyAsync([o.CustomerId, .. storeUsers], t, "🛵 Đã tìm thấy tài xế", $"Tài xế {driverName} đang đến {o.PickupStoreName} lấy đơn {o.Code}.", customerLink, o.Id, ct);
                    if (o.DriverId is not null)
                        await NotifyAsync([o.DriverId], t, "Bạn có chuyến mới", $"Đến {o.PickupStoreName} lấy đơn {o.Code}.", driverLink, o.Id, ct);
                    break;
                case OrderStatus.ArrivedAtStore:
                    await NotifyAsync(storeUsers, t, "🛵 Tài xế đã đến quán", $"Tài xế {driverName} đã đến lấy đơn {o.Code}.", sellerLink, o.Id, ct);
                    await NotifyAsync([o.CustomerId], t, "Tài xế đã đến quán", $"Tài xế {driverName} đang chờ lấy món đơn {o.Code}.", customerLink, o.Id, ct);
                    break;
                case OrderStatus.Delivering:
                    await NotifyAsync([o.CustomerId], t, "🛵 Đang giao hàng", $"Tài xế {driverName} đã lấy món và đang trên đường tới bạn.", customerLink, o.Id, ct);
                    await NotifyAsync(storeUsers, t, "Tài xế đã lấy món", $"Đơn {o.Code} đã rời quán.", sellerLink, o.Id, ct);
                    break;
                case OrderStatus.ArrivedAtCustomer:
                    await NotifyAsync([o.CustomerId], t, "📍 Tài xế đã đến nơi", $"Tài xế {driverName} đã đến, vui lòng ra nhận đơn {o.Code} và chuẩn bị {o.GrandTotal:N0}đ.", customerLink, o.Id, ct);
                    break;
                case OrderStatus.Delivered:
                    await NotifyAsync([o.CustomerId, .. storeUsers], t, "✅ Giao hàng thành công", $"Đơn {o.Code} đã giao thành công.", customerLink, o.Id, ct);
                    break;
                case OrderStatus.Cancelled:
                    var cancelUsers = new List<string>([o.CustomerId, .. storeUsers]);
                    if (o.DriverId is not null) cancelUsers.Add(o.DriverId);
                    await NotifyAsync(cancelUsers, t, "Đơn hàng đã hủy", $"Đơn {o.Code} đã bị hủy: {o.CancelReason}", customerLink, o.Id, ct);
                    break;
                case OrderStatus.DeliveryFailed:
                    var admins = await AdminIdsAsync(db, ct);
                    await NotifyAsync([o.CustomerId, .. storeUsers, .. admins], t, "⚠️ Giao hàng thất bại", $"Đơn {o.Code}: {o.FailureReason}", customerLink, o.Id, ct);
                    break;
                case OrderStatus.Returned:
                    await NotifyAsync([o.CustomerId, .. storeUsers], t, "Đơn đã hoàn trả", $"Đơn {o.Code} đã được hoàn về cửa hàng.", customerLink, o.Id, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify order {OrderId} status change", orderId);
        }
    }

    public static async Task<List<string>> AdminIdsAsync(AppDbContext db, CancellationToken ct) =>
        await (from ur in db.UserRoles
               join r in db.Roles on ur.RoleId equals r.Id
               where r.Name == Roles.Admin
               select ur.UserId).ToListAsync(ct);
}
