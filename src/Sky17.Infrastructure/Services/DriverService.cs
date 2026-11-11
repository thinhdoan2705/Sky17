using Microsoft.EntityFrameworkCore;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure.Data;

namespace Sky17.Infrastructure.Services;

public sealed class DriverService(
    IDbContextFactory<AppDbContext> dbf, ICurrentUserAccessor users, IClock clock, OrderWorkflow workflow, Notifier notifier) : IDriverService
{
    private async Task<string> DriverIdAsync(CancellationToken ct) => (await users.GetAsync(ct)).RequireRole(Roles.Driver);

    private static readonly OrderStatus[] ActiveStatuses = [OrderStatus.Assigned, OrderStatus.PickedUp, OrderStatus.Delivering];

    internal static DeliveryJobDto ToJob(Order o, bool mine, string? customerPhone) => new(
        o.Id, o.Code, o.Status, o.PickupStoreName, o.PickupAddress, o.PickupPhone,
        mine ? o.RecipientName : "Khách hàng Sky 17",
        // Customer contact details are only revealed to the assigned driver.
        mine ? customerPhone : null,
        o.DeliveryAddress, mine ? o.DeliveryNote : null,
        o.DeliveryFee, o.DriverEarning, o.GrandTotal, o.DistanceMeters, o.Items.Sum(i => i.Quantity),
        o.PlacedAtUtc, o.DeliveredAtUtc, mine, o.FailureReason)
    {
        PickupLatitude = o.PickupLatitude,
        PickupLongitude = o.PickupLongitude,
        // The exact drop-off point is personal data: only the assigned driver gets it.
        DropoffLatitude = mine ? o.DropoffLatitude : null,
        DropoffLongitude = mine ? o.DropoffLongitude : null,
    };

    public async Task<DriverProfileDto> GetProfileAsync(CancellationToken ct = default)
    {
        var uid = await DriverIdAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var d = await db.DriverProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid, ct)
            ?? throw new NotFoundException("Chưa có hồ sơ tài xế.");
        var p = await db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid, ct);
        var email = await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.Email).FirstAsync(ct);
        return new DriverProfileDto(uid, p?.FullName ?? email ?? "", p?.PhoneNumber, email ?? "", d.VehicleType, d.LicensePlate,
            d.IdentityNumber, d.ServiceArea, d.Status, d.StatusReason, d.IsOnline);
    }

    public async Task UpdateProfileAsync(DriverProfileInput input, CancellationToken ct = default)
    {
        var uid = await DriverIdAsync(ct);
        InputValidation.Validate(input);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var d = await db.DriverProfiles.FirstOrDefaultAsync(x => x.UserId == uid, ct) ?? throw new NotFoundException();
        d.VehicleType = input.VehicleType.Trim();
        d.LicensePlate = input.LicensePlate.Trim().ToUpperInvariant();
        d.IdentityNumber = input.IdentityNumber.Clean();
        d.ServiceArea = input.ServiceArea.Trim();
        db.Audit(uid, "Driver.ProfileUpdated", nameof(DriverProfile), uid);
        await db.SaveAsync(ct);
    }

    public async Task SetOnlineAsync(bool online, CancellationToken ct = default)
    {
        var uid = await DriverIdAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var d = await db.DriverProfiles.FirstOrDefaultAsync(x => x.UserId == uid, ct) ?? throw new NotFoundException();
        if (online && d.Status != DriverStatus.Active) throw new DomainException("Hồ sơ tài xế chưa được duyệt hoặc đang bị tạm khóa.");
        d.IsOnline = online;
        db.Audit(uid, online ? "Driver.Online" : "Driver.Offline", nameof(DriverProfile), uid);
        await db.SaveAsync(ct);
    }

    public async Task<DriverDashboardDto> GetDashboardAsync(CancellationToken ct = default)
    {
        var uid = await DriverIdAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var d = await db.DriverProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid, ct) ?? throw new NotFoundException();
        var today = VnTime.StartOfLocalDayUtc(clock.UtcNow);
        var mine = db.Orders.AsNoTracking().Where(o => o.DriverId == uid);
        var deliveredToday = mine.Where(o => o.Status == OrderStatus.Delivered && o.DeliveredAtUtc >= today);
        var unsettled = mine.Where(o => o.Status == OrderStatus.Delivered && o.DriverSettlementState != OrderSettlementState.Settled);
        var codHolding = await unsettled.SumAsync(o => o.CollectedCodAmount, ct);
        var unsettledEarnings = await unsettled.SumAsync(o => o.DriverEarning, ct);
        return new DriverDashboardDto(
            d.Status, d.IsOnline,
            await deliveredToday.SumAsync(o => o.DriverEarning, ct),
            await mine.CountAsync(o => ActiveStatuses.Contains(o.Status), ct),
            await deliveredToday.CountAsync(ct),
            codHolding, codHolding - unsettledEarnings,
            d.Status == DriverStatus.Active ? await db.Orders.CountAsync(o => o.Status == OrderStatus.ReadyForPickup && o.DriverId == null, ct) : 0,
            await Settings.MaxActiveDeliveriesAsync(db, ct));
    }

    public async Task<IReadOnlyList<DeliveryJobDto>> GetAvailableJobsAsync(CancellationToken ct = default)
    {
        var uid = await DriverIdAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var status = await db.DriverProfiles.AsNoTracking().Where(x => x.UserId == uid).Select(x => x.Status).FirstOrDefaultAsync(ct);
        if (status != DriverStatus.Active) return [];
        var orders = await db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.Status == OrderStatus.ReadyForPickup && o.DriverId == null)
            .OrderBy(o => o.PlacedAtUtc).Take(50).ToListAsync(ct);
        return orders.Select(o => ToJob(o, false, null)).ToList();
    }

    public async Task<IReadOnlyList<DeliveryJobDto>> GetMyActiveJobsAsync(CancellationToken ct = default)
    {
        var uid = await DriverIdAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var orders = await db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.DriverId == uid && ActiveStatuses.Contains(o.Status))
            .OrderBy(o => o.PlacedAtUtc).ToListAsync(ct);
        return orders.Select(o => ToJob(o, true, o.RecipientPhone)).ToList();
    }

    public async Task<DeliveryJobDto> GetJobAsync(int orderId, CancellationToken ct = default)
    {
        var uid = await DriverIdAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var o = await db.Orders.AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == orderId, ct)
            ?? throw new NotFoundException("Không tìm thấy đơn giao.");
        if (o.DriverId == uid) return ToJob(o, true, o.RecipientPhone);
        if (o.Status == OrderStatus.ReadyForPickup && o.DriverId is null)
        {
            var status = await db.DriverProfiles.AsNoTracking().Where(x => x.UserId == uid).Select(x => x.Status).FirstOrDefaultAsync(ct);
            if (status == DriverStatus.Active) return ToJob(o, false, null);
        }
        // Another driver's assignment (or not a public job) is private.
        throw new ForbiddenException("Bạn không có quyền xem chuyến giao này.");
    }

    public async Task AcceptAsync(int orderId, CancellationToken ct = default)
    {
        var uid = await DriverIdAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        await OrderWorkflow.EnsureDriverCanTakeAsync(db, uid, requireOnline: true, ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                await workflow.AssignAsync(db, orderId, uid, OrderActor.Driver, uid, ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException)
            {
                throw new DomainException("Đơn này đã được tài xế khác nhận.");
            }
        }
        await notifier.OrderStatusChangedAsync(orderId, ct);
    }

    private async Task MoveAsync(int orderId, OrderStatus target, string? reason, CancellationToken ct)
    {
        var uid = await DriverIdAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await OrderWorkflow.WithWorkflowGraph(db.Orders).FirstOrDefaultAsync(o => o.Id == orderId && o.DriverId == uid, ct)
            ?? throw new ForbiddenException("Đơn này không thuộc về bạn.");
        await workflow.ApplyAsync(db, order, target, OrderActor.Driver, uid, reason, ct);
        await db.SaveAsync(ct);
        await tx.CommitAsync(ct);
        await notifier.OrderStatusChangedAsync(orderId, ct);
    }

    public Task ConfirmPickupAsync(int orderId, CancellationToken ct = default) => MoveAsync(orderId, OrderStatus.PickedUp, "Tài xế đã lấy hàng", ct);
    public Task StartDeliveryAsync(int orderId, CancellationToken ct = default) => MoveAsync(orderId, OrderStatus.Delivering, "Bắt đầu giao hàng", ct);
    public Task ConfirmDeliveredAsync(int orderId, CancellationToken ct = default) => MoveAsync(orderId, OrderStatus.Delivered, "Giao thành công, đã thu COD", ct);
    public Task ReportFailedAsync(int orderId, string reason, CancellationToken ct = default) => MoveAsync(orderId, OrderStatus.DeliveryFailed, reason, ct);

    public async Task<PagedResult<DeliveryJobDto>> GetHistoryAsync(int page = 1, int pageSize = 10, CancellationToken ct = default)
    {
        var uid = await DriverIdAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var q = db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.Assignments.Any(a => a.DriverId == uid && !a.IsActive))
            .OrderByDescending(o => o.PlacedAtUtc);
        var paged = await q.ToPagedAsync(page, pageSize, ct);
        // History only shows the order to the driver who handled it; contact details stay hidden after completion.
        return new PagedResult<DeliveryJobDto>(paged.Items.Select(o => ToJob(o, o.DriverId == uid, null)).ToList(), paged.Page, paged.PageSize, paged.TotalCount);
    }

    public async Task<DriverEarningsDto> GetEarningsAsync(CancellationToken ct = default)
    {
        var uid = await DriverIdAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var delivered = await db.Orders.AsNoTracking().Where(o => o.DriverId == uid && o.Status == OrderStatus.Delivered)
            .Select(o => new { o.DriverEarning, o.CollectedCodAmount, o.DriverSettlementState, o.DeliveredAtUtc }).ToListAsync(ct);
        var unsettled = delivered.Where(o => o.DriverSettlementState != OrderSettlementState.Settled).ToList();
        var today = DateOnly.FromDateTime(VnTime.ToLocal(clock.UtcNow));
        var last7 = Enumerable.Range(0, 7).Select(i =>
        {
            var d = today.AddDays(i - 6);
            var rows = delivered.Where(o => o.DeliveredAtUtc is { } t && DateOnly.FromDateTime(VnTime.ToLocal(t)) == d).ToList();
            return new DailyPoint(d, rows.Count, rows.Sum(r => r.DriverEarning));
        }).ToList();
        var cod = await FinanceService.CodDtos(db, db.CodTransactions.Where(c => c.DriverId == uid)).Take(30).ToListAsync(ct);
        var settlements = await FinanceService.SettlementDtos(db, db.Settlements.Where(s => s.Type == SettlementType.Driver && s.DriverId == uid)).Take(20).ToListAsync(ct);
        var holding = unsettled.Sum(o => o.CollectedCodAmount);
        var unsettledEarnings = unsettled.Sum(o => o.DriverEarning);
        return new DriverEarningsDto(delivered.Sum(o => o.DriverEarning), unsettledEarnings, holding, holding - unsettledEarnings,
            delivered.Count, last7, cod, settlements);
    }
}
