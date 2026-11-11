using Microsoft.EntityFrameworkCore;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure.Data;

namespace Sky17.Infrastructure.Services;

public sealed class FinanceService(IDbContextFactory<AppDbContext> dbf, ICurrentUserAccessor users, IClock clock, Notifier notifier) : IFinanceService
{
    private async Task<string> AdminAsync(CancellationToken ct) => (await users.GetAsync(ct)).RequireRole(Roles.Admin);

    internal static IQueryable<SettlementDto> SettlementDtos(AppDbContext db, IQueryable<Settlement> q) =>
        from s in q.AsNoTracking()
        join dp in db.UserProfiles on s.DriverId equals dp.UserId into dps
        from dp in dps.DefaultIfEmpty()
        orderby s.CreatedAtUtc descending, s.Id descending
        select new SettlementDto(s.Id, s.Code, s.Type, s.StoreId, s.DriverId,
            s.Store != null ? s.Store.Name : (dp != null ? dp.FullName : "—"),
            s.Amount, s.GrossCod, s.DriverEarnings, s.Status, s.Items.Count, s.Note, s.CreatedAtUtc, s.ConfirmedAtUtc, s.CancelReason);

    internal static IQueryable<CodRecordDto> CodDtos(AppDbContext db, IQueryable<CodTransaction> q) =>
        from c in q.AsNoTracking()
        join dp in db.UserProfiles on c.DriverId equals dp.UserId into dps
        from dp in dps.DefaultIfEmpty()
        orderby c.CreatedAtUtc descending, c.Id descending
        select new CodRecordDto(c.Id, c.OrderId, c.Order.Code, c.DriverId, dp != null ? dp.FullName : "—", c.Type, c.Amount, c.SettlementId, c.CreatedAtUtc);

    private static IQueryable<Order> Delivered(AppDbContext db) => db.Orders.AsNoTracking().Where(o => o.Status == OrderStatus.Delivered);

    public async Task<FinanceOverviewDto> GetOverviewAsync(CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var driverOpen = Delivered(db).Where(o => o.DriverSettlementState != OrderSettlementState.Settled);
        var held = await driverOpen.SumAsync(o => o.CollectedCodAmount, ct);
        var earnings = await driverOpen.SumAsync(o => o.DriverEarning, ct);
        var payable = await Delivered(db).Where(o => o.StoreSettlementState != OrderSettlementState.Settled).SumAsync(o => o.StoreReceivable, ct);
        var revenue = await Delivered(db).SumAsync(o => o.PlatformFee + o.DeliveryFee - o.DriverEarning - o.DiscountAmount, ct);
        var confirmed = await db.Settlements.Where(s => s.Status == SettlementStatus.Confirmed).SumAsync(s => s.Amount, ct);
        var pending = await db.Settlements.CountAsync(s => s.Status == SettlementStatus.Pending, ct);
        return new FinanceOverviewDto(held, held - earnings, payable, earnings, revenue, confirmed, pending);
    }

    public async Task<PagedResult<CodRecordDto>> GetCodRecordsAsync(string? driverId, int page = 1, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var q = db.CodTransactions.AsQueryable();
        if (!string.IsNullOrEmpty(driverId)) q = q.Where(c => c.DriverId == driverId);
        return await CodDtos(db, q).ToPagedAsync(page, 15, ct);
    }

    public async Task<IReadOnlyList<PartyBalanceDto>> GetStoreBalancesAsync(CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var rows = await Delivered(db).Where(o => o.StoreSettlementState == OrderSettlementState.Unsettled)
            .GroupBy(o => new { o.StoreId, o.Store.Name })
            .Select(g => new { g.Key.StoreId, g.Key.Name, Count = g.Count(), Amount = g.Sum(o => o.StoreReceivable), Gross = g.Sum(o => o.ProductTotal) })
            .ToListAsync(ct);
        return rows.Select(r => new PartyBalanceDto(r.StoreId.ToString(), r.Name, r.Count, r.Amount, r.Gross, 0)).OrderByDescending(r => r.Amount).ToList();
    }

    public async Task<IReadOnlyList<PartyBalanceDto>> GetDriverBalancesAsync(CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var rows = await Delivered(db).Where(o => o.DriverSettlementState == OrderSettlementState.Unsettled && o.DriverId != null)
            .GroupBy(o => o.DriverId!)
            .Select(g => new { DriverId = g.Key, Count = g.Count(), Cod = g.Sum(o => o.CollectedCodAmount), Earn = g.Sum(o => o.DriverEarning) })
            .ToListAsync(ct);
        var names = await Names.LookupAsync(db, rows.Select(r => r.DriverId), ct);
        return rows.Select(r => new PartyBalanceDto(r.DriverId, names.GetValueOrDefault(r.DriverId, "—"), r.Count, r.Cod - r.Earn, r.Cod, r.Earn))
            .OrderByDescending(r => r.Amount).ToList();
    }

    public async Task<int> CreateStoreSettlementAsync(int storeId, string? note, CancellationToken ct = default)
    {
        var uid = await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var orders = await db.Orders.Where(o => o.StoreId == storeId && o.Status == OrderStatus.Delivered && o.StoreSettlementState == OrderSettlementState.Unsettled).ToListAsync(ct);
        if (orders.Count == 0) throw new DomainException("Cửa hàng không có đơn nào cần đối soát.");
        var s = new Settlement
        {
            Code = CodeGen.SettlementCode(SettlementType.Store, clock.UtcNow),
            Type = SettlementType.Store,
            StoreId = storeId,
            Amount = orders.Sum(o => o.StoreReceivable),
            GrossCod = orders.Sum(o => o.ProductTotal),
            Note = note.Clean(),
            CreatedByUserId = uid,
        };
        foreach (var o in orders)
        {
            s.Items.Add(new SettlementItem { OrderId = o.Id, Type = SettlementType.Store, Amount = o.StoreReceivable });
            o.StoreSettlementState = OrderSettlementState.InSettlement;
        }
        db.Settlements.Add(s);
        db.Audit(uid, "Settlement.Created", nameof(Settlement), s.Code, $"store={storeId} amount={s.Amount}");
        await db.SaveAsync(ct);
        await tx.CommitAsync(ct);
        return s.Id;
    }

    public async Task<int> CreateDriverSettlementAsync(string driverId, string? note, CancellationToken ct = default)
    {
        var uid = await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var orders = await db.Orders.Where(o => o.DriverId == driverId && o.Status == OrderStatus.Delivered && o.DriverSettlementState == OrderSettlementState.Unsettled).ToListAsync(ct);
        if (orders.Count == 0) throw new DomainException("Tài xế không có khoản COD nào cần đối soát.");
        var s = new Settlement
        {
            Code = CodeGen.SettlementCode(SettlementType.Driver, clock.UtcNow),
            Type = SettlementType.Driver,
            DriverId = driverId,
            GrossCod = orders.Sum(o => o.CollectedCodAmount),
            DriverEarnings = orders.Sum(o => o.DriverEarning),
            Note = note.Clean(),
            CreatedByUserId = uid,
        };
        s.Amount = s.GrossCod - s.DriverEarnings;
        foreach (var o in orders)
        {
            s.Items.Add(new SettlementItem { OrderId = o.Id, Type = SettlementType.Driver, Amount = o.CollectedCodAmount - o.DriverEarning });
            o.DriverSettlementState = OrderSettlementState.InSettlement;
        }
        db.Settlements.Add(s);
        db.Audit(uid, "Settlement.Created", nameof(Settlement), s.Code, $"driver={driverId} amount={s.Amount}");
        await db.SaveAsync(ct);
        await tx.CommitAsync(ct);
        return s.Id;
    }

    public async Task ConfirmSettlementAsync(int settlementId, CancellationToken ct = default)
    {
        var uid = await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.UtcNow;
        var s = await db.Settlements.AsNoTracking().FirstOrDefaultAsync(x => x.Id == settlementId, ct) ?? throw new NotFoundException();
        if (s.Status == SettlementStatus.Confirmed) return; // idempotent: confirming twice has no further effect
        if (s.Status == SettlementStatus.Cancelled) throw new DomainException("Phiếu đối soát đã bị hủy.");

        // Conditional update: only one confirmation can ever succeed.
        var changed = await db.Settlements.Where(x => x.Id == settlementId && x.Status == SettlementStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, SettlementStatus.Confirmed)
                .SetProperty(x => x.ConfirmedByUserId, uid).SetProperty(x => x.ConfirmedAtUtc, now)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);
        if (changed == 0) return;

        var items = await db.SettlementItems.AsNoTracking().Where(i => i.SettlementId == settlementId).ToListAsync(ct);
        var orderIds = items.Select(i => i.OrderId).ToList();
        if (s.Type == SettlementType.Store)
        {
            await db.Orders.Where(o => orderIds.Contains(o.Id)).ExecuteUpdateAsync(u => u.SetProperty(o => o.StoreSettlementState, OrderSettlementState.Settled), ct);
        }
        else
        {
            await db.Orders.Where(o => orderIds.Contains(o.Id)).ExecuteUpdateAsync(u => u.SetProperty(o => o.DriverSettlementState, OrderSettlementState.Settled), ct);
            foreach (var i in items)
                db.CodTransactions.Add(new CodTransaction { OrderId = i.OrderId, DriverId = s.DriverId!, Type = CodTransactionType.RemittedToPlatform, Amount = i.Amount, SettlementId = s.Id, Note = $"Nộp COD theo phiếu {s.Code}" });
        }
        db.Audit(uid, "Settlement.Confirmed", nameof(Settlement), s.Code, $"amount={s.Amount}");
        await db.SaveAsync(ct);
        await tx.CommitAsync(ct);

        var recipients = s.Type == SettlementType.Store
            ? await db.StoreMembers.Where(m => m.StoreId == s.StoreId && m.Role == StoreMemberRole.Owner).Select(m => m.UserId).ToListAsync(ct)
            : [s.DriverId!];
        await notifier.NotifyAsync(recipients, "settlement", "💰 Đối soát đã xác nhận",
            $"Phiếu {s.Code} trị giá {s.Amount:N0}đ đã được xác nhận.", s.Type == SettlementType.Store ? "/seller/settlements" : "/driver/earnings", null, ct);
    }

    public async Task CancelSettlementAsync(int settlementId, string reason, CancellationToken ct = default)
    {
        var uid = await AdminAsync(ct);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Vui lòng nhập lý do hủy.");
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var s = await db.Settlements.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == settlementId, ct) ?? throw new NotFoundException();
        if (s.Status != SettlementStatus.Pending) throw new DomainException("Chỉ hủy được phiếu đang chờ xác nhận.");
        var orderIds = s.Items.Select(i => i.OrderId).ToList();
        if (s.Type == SettlementType.Store)
            await db.Orders.Where(o => orderIds.Contains(o.Id)).ExecuteUpdateAsync(u => u.SetProperty(o => o.StoreSettlementState, OrderSettlementState.Unsettled), ct);
        else
            await db.Orders.Where(o => orderIds.Contains(o.Id)).ExecuteUpdateAsync(u => u.SetProperty(o => o.DriverSettlementState, OrderSettlementState.Unsettled), ct);
        s.Note = $"{s.Note} [{s.Items.Count} đơn]".Trim();
        db.SettlementItems.RemoveRange(s.Items); // releases orders for a future settlement
        s.Status = SettlementStatus.Cancelled;
        s.CancelledAtUtc = clock.UtcNow;
        s.CancelReason = reason.Trim();
        db.Audit(uid, "Settlement.Cancelled", nameof(Settlement), s.Code, reason);
        await db.SaveAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<PagedResult<SettlementDto>> GetSettlementsAsync(SettlementType? type, SettlementStatus? status, int page = 1, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var q = db.Settlements.AsQueryable();
        if (type is { } t) q = q.Where(s => s.Type == t);
        if (status is { } st) q = q.Where(s => s.Status == st);
        return await SettlementDtos(db, q).ToPagedAsync(page, 15, ct);
    }

    public async Task<SettlementDetailDto> GetSettlementAsync(int settlementId, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var dto = await SettlementDtos(db, db.Settlements.Where(s => s.Id == settlementId)).FirstOrDefaultAsync(ct) ?? throw new NotFoundException();
        var items = await db.SettlementItems.AsNoTracking().Where(i => i.SettlementId == settlementId)
            .Select(i => new SettlementItemDto(i.OrderId, i.Order.Code, i.Amount, i.Order.DeliveredAtUtc)).ToListAsync(ct);
        return new SettlementDetailDto(dto, items);
    }
}
