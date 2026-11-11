using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure.Data;

namespace Sky17.Infrastructure.Services;

public sealed class AdminService(
    IDbContextFactory<AppDbContext> dbf, ICurrentUserAccessor users, IClock clock, OrderWorkflow workflow,
    Notifier notifier, IServiceScopeFactory scopes) : IAdminService
{
    private async Task<string> AdminAsync(CancellationToken ct) => (await users.GetAsync(ct)).RequireRole(Roles.Admin);

    // ------------------------------------------------------------ Dashboard

    public async Task<AdminDashboardDto> GetDashboardAsync(CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var now = clock.UtcNow;
        var today = VnTime.StartOfLocalDayUtc(now);
        var orders = db.Orders.AsNoTracking();
        var delivered = orders.Where(o => o.Status == OrderStatus.Delivered);

        var todayByStatus = await orders.Where(o => o.PlacedAtUtc >= today).GroupBy(o => o.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var recentLogs = await AuditDtos(db, db.AuditLogs).Take(8).ToListAsync(ct);

        return new AdminDashboardDto(
            await orders.CountAsync(ct),
            await orders.CountAsync(o => o.PlacedAtUtc >= today, ct),
            await orders.CountAsync(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Confirmed || o.Status == OrderStatus.Preparing || o.Status == OrderStatus.ReadyForPickup, ct),
            await orders.CountAsync(o => o.Status == OrderStatus.Assigned || o.Status == OrderStatus.PickedUp || o.Status == OrderStatus.Delivering, ct),
            await delivered.CountAsync(ct),
            await orders.CountAsync(o => o.Status == OrderStatus.DeliveryFailed, ct),
            await db.Stores.CountAsync(s => s.Status == StoreStatus.Active, ct),
            await db.Stores.CountAsync(s => s.Status == StoreStatus.PendingApproval, ct),
            await db.DriverProfiles.CountAsync(d => d.Status == DriverStatus.Active, ct),
            await db.DriverProfiles.CountAsync(d => d.Status == DriverStatus.Active && d.IsOnline, ct),
            await db.DriverProfiles.CountAsync(d => d.Status == DriverStatus.PendingApproval, ct),
            await delivered.SumAsync(o => o.ProductTotal, ct),
            await delivered.SumAsync(o => o.DeliveryFee, ct),
            await delivered.Where(o => o.DriverSettlementState != OrderSettlementState.Settled).SumAsync(o => o.CollectedCodAmount - o.DriverEarning, ct),
            await delivered.SumAsync(o => o.PlatformFee + o.DeliveryFee - o.DriverEarning - o.DiscountAmount, ct),
            await OrderQueries.DailyAsync(orders.Where(o => o.Status != OrderStatus.Cancelled), 7, o => o.GrandTotal, now, ct),
            todayByStatus.OrderBy(x => x.Key).Select(x => (x.Key, x.Count)).ToList(),
            recentLogs,
            await orders.ToSummaries(db).Take(8).ToListAsync(ct));
    }

    // ------------------------------------------------------------ Orders

    public async Task<PagedResult<OrderSummaryDto>> SearchOrdersAsync(OrderFilter filter, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Orders.AsNoTracking().ApplyFilter(filter).ToSummaries(db).ToPagedAsync(filter.Page, filter.PageSize, ct);
    }

    public async Task<OrderDetailDto> GetOrderAsync(int orderId, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await OrderQueries.LoadDetailAsync(db, orderId, OrderActor.Admin, ct);
    }

    public async Task<IReadOnlyList<DriverOptionDto>> GetAssignableDriversAsync(CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var rows = await (from d in db.DriverProfiles.AsNoTracking()
                          where d.Status == DriverStatus.Active
                          join u in db.Users on d.UserId equals u.Id
                          where u.IsActive
                          join p in db.UserProfiles on d.UserId equals p.UserId
                          select new
                          {
                              d.UserId, p.FullName, d.LicensePlate, d.IsOnline,
                              Active = db.Orders.Count(o => o.DriverId == d.UserId && (o.Status == OrderStatus.Assigned || o.Status == OrderStatus.PickedUp || o.Status == OrderStatus.Delivering)),
                          }).ToListAsync(ct);
        return rows.Select(r => new DriverOptionDto(r.UserId, r.FullName, r.LicensePlate, r.IsOnline, r.Active))
            .OrderByDescending(r => r.IsOnline).ThenBy(r => r.ActiveDeliveries).ToList();
    }

    public async Task AssignDriverAsync(int orderId, string driverId, CancellationToken ct = default)
    {
        var uid = await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        await OrderWorkflow.EnsureDriverCanTakeAsync(db, driverId, requireOnline: false, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await OrderWorkflow.WithWorkflowGraph(db.Orders).FirstOrDefaultAsync(o => o.Id == orderId, ct) ?? throw new NotFoundException();
        if (order.Status == OrderStatus.Assigned)
        {
            // Reassignment is only permitted before pickup.
            if (order.DriverId == driverId) throw new DomainException("Tài xế này đang được phân công cho đơn.");
            await workflow.ApplyAsync(db, order, OrderStatus.ReadyForPickup, OrderActor.Admin, uid, "Admin phân công lại tài xế", ct);
            await db.SaveAsync(ct);
        }
        db.ChangeTracker.Clear();
        await workflow.AssignAsync(db, orderId, driverId, OrderActor.Admin, uid, ct);
        await tx.CommitAsync(ct);
        await notifier.OrderStatusChangedAsync(orderId, ct);
    }

    private async Task TransitionAsync(int orderId, OrderStatus target, string? reason, CancellationToken ct)
    {
        var uid = await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await OrderWorkflow.WithWorkflowGraph(db.Orders).FirstOrDefaultAsync(o => o.Id == orderId, ct) ?? throw new NotFoundException();
        await workflow.ApplyAsync(db, order, target, OrderActor.Admin, uid, reason, ct);
        await db.SaveAsync(ct);
        await tx.CommitAsync(ct);
        await notifier.OrderStatusChangedAsync(orderId, ct);
    }

    public Task UnassignDriverAsync(int orderId, string reason, CancellationToken ct = default) => TransitionAsync(orderId, OrderStatus.ReadyForPickup, reason, ct);
    public Task CancelOrderAsync(int orderId, string reason, CancellationToken ct = default) => TransitionAsync(orderId, OrderStatus.Cancelled, reason, ct);
    public Task MarkDeliveryFailedAsync(int orderId, string reason, CancellationToken ct = default) => TransitionAsync(orderId, OrderStatus.DeliveryFailed, reason, ct);
    public Task ResolveFailedDeliveryAsync(int orderId, bool retry, string reason, CancellationToken ct = default) =>
        TransitionAsync(orderId, retry ? OrderStatus.ReadyForPickup : OrderStatus.Returned, reason, ct);

    public async Task SetDistanceAsync(int orderId, int distanceMeters, string reason, CancellationToken ct = default)
    {
        var uid = await AdminAsync(ct);
        if (distanceMeters is < 1 or > 100_000) throw new DomainException("Khoảng cách phải từ 1m đến 100km.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Vui lòng nhập lý do điều chỉnh.");
        await using var db = await dbf.CreateDbContextAsync(ct);
        var order = await db.Orders.Include(o => o.Payment).FirstOrDefaultAsync(o => o.Id == orderId, ct) ?? throw new NotFoundException();
        if (!OrderStateMachine.IsBeforePickup(order.Status)) throw new DomainException("Chỉ điều chỉnh khoảng cách trước khi tài xế lấy hàng.");
        var rule = await Settings.GetRateRuleAsync(db, ct);
        var oldFee = order.DeliveryFee;
        var amounts = OrderFinancials.Calculate(order.ProductTotal, DeliveryPricing.CalculateFee(rule, distanceMeters), order.DiscountAmount, rule);
        OrderFinancials.Apply(order, amounts);
        order.DistanceMeters = distanceMeters;
        if (order.Payment is not null) order.Payment.Amount = order.GrandTotal;
        db.Audit(uid, "Order.DistanceOverride", nameof(Order), order.Id, $"{order.Code}: {distanceMeters}m, phí {oldFee}→{order.DeliveryFee}. {reason}");
        await db.SaveAsync(ct);
        await notifier.NotifyAsync([order.CustomerId], "order", "Cập nhật phí giao hàng",
            $"Đơn {order.Code}: phí giao hàng {order.DeliveryFee:N0}đ, tổng {order.GrandTotal:N0}đ.", $"/orders/{order.Code}", order.Id, ct);
    }

    // ------------------------------------------------------------ Stores

    private static IQueryable<AdminStoreDto> StoreDtos(AppDbContext db, IQueryable<Store> q) =>
        from s in q.AsNoTracking()
        let owner = db.StoreMembers.Where(m => m.StoreId == s.Id && m.Role == StoreMemberRole.Owner).Select(m => m.UserId).FirstOrDefault()
        join p in db.UserProfiles on owner equals p.UserId into ps
        from p in ps.DefaultIfEmpty()
        join u in db.Users on owner equals u.Id into us
        from u in us.DefaultIfEmpty()
        orderby s.Status, s.Name
        select new AdminStoreDto(s.Id, s.Name, s.Emoji, s.Tagline, s.Address, s.PhoneNumber, s.Status, s.StatusReason,
            p == null ? "—" : p.FullName, u == null ? "—" : u.Email!,
            s.Products.Count(x => x.IsActive),
            db.Orders.Count(o => o.StoreId == s.Id && o.Status == OrderStatus.Delivered),
            db.Orders.Where(o => o.StoreId == s.Id && o.Status == OrderStatus.Delivered).Sum(o => (long?)o.ProductTotal) ?? 0,
            s.CreatedAtUtc);

    public async Task<IReadOnlyList<AdminStoreDto>> GetStoresAsync(StoreStatus? status = null, string? search = null, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var q = db.Stores.AsQueryable();
        if (status is { } st) q = q.Where(s => s.Status == st);
        if (search.Clean() is { } term) { var like = $"%{term}%"; q = q.Where(s => EF.Functions.Like(s.Name, like) || EF.Functions.Like(s.Address, like)); }
        return await StoreDtos(db, q).ToListAsync(ct);
    }

    public async Task<AdminStoreDetailDto> GetStoreAsync(int storeId, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var dto = await StoreDtos(db, db.Stores.Where(s => s.Id == storeId)).FirstOrDefaultAsync(ct) ?? throw new NotFoundException();
        var s = await db.Stores.AsNoTracking().FirstAsync(x => x.Id == storeId, ct);
        var members = await (from m in db.StoreMembers.AsNoTracking()
                             where m.StoreId == storeId
                             join u in db.Users on m.UserId equals u.Id
                             join p in db.UserProfiles on m.UserId equals p.UserId into ps
                             from p in ps.DefaultIfEmpty()
                             select new StoreMemberDto(m.Id, m.UserId, p == null ? u.Email! : p.FullName, u.Email!, m.Role)).ToListAsync(ct);
        var products = await db.Products.AsNoTracking().Where(p => p.StoreId == storeId).OrderBy(p => p.Name)
            .Select(p => new SellerProductDto(p.Id, p.Name, p.Emoji, p.Images.Select(i => i.Url).FirstOrDefault(), p.Category == null ? null : p.Category.Name,
                p.CategoryId, p.Price, p.Stock, p.IsActive, p.IsAvailable,
                db.OrderItems.Where(i => i.ProductId == p.Id && i.Order.Status == OrderStatus.Delivered).Sum(i => (int?)i.Quantity) ?? 0))
            .ToListAsync(ct);
        var recent = await db.Orders.AsNoTracking().Where(o => o.StoreId == storeId).ToSummaries(db).Take(10).ToListAsync(ct);
        var pending = await db.Orders.Where(o => o.StoreId == storeId && o.Status == OrderStatus.Delivered && o.StoreSettlementState != OrderSettlementState.Settled)
            .SumAsync(o => o.StoreReceivable, ct);
        return new AdminStoreDetailDto(dto, s.Description, s.OpenTime, s.CloseTime, s.IsAcceptingOrders, members, products, recent, pending);
    }

    public async Task SetStoreStatusAsync(int storeId, StoreStatus status, string? reason, CancellationToken ct = default)
    {
        var uid = await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var s = await db.Stores.FirstOrDefaultAsync(x => x.Id == storeId, ct) ?? throw new NotFoundException();
        var allowed = (s.Status, status) switch
        {
            (StoreStatus.PendingApproval, StoreStatus.Active) => true,
            (StoreStatus.PendingApproval, StoreStatus.Rejected) => true,
            (StoreStatus.Active, StoreStatus.Suspended) => true,
            (StoreStatus.Suspended, StoreStatus.Active) => true,
            (StoreStatus.Rejected, StoreStatus.Active) => true,
            _ => false,
        };
        if (!allowed) throw new DomainException("Không thể chuyển trạng thái cửa hàng như yêu cầu.");
        if (status is StoreStatus.Rejected or StoreStatus.Suspended && string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Vui lòng nhập lý do.");
        s.Status = status;
        s.StatusReason = reason.Clean();
        if (status == StoreStatus.Active) s.ApprovedAtUtc ??= clock.UtcNow;
        db.Audit(uid, $"Store.{status}", nameof(Store), storeId, $"{s.Name} {reason}");
        await db.SaveAsync(ct);

        var owners = await db.StoreMembers.Where(m => m.StoreId == storeId).Select(m => m.UserId).ToListAsync(ct);
        var (title, msg) = status switch
        {
            StoreStatus.Active => ("🎉 Cửa hàng đã được duyệt", $"{s.Name} đã hoạt động trên Sky 17."),
            StoreStatus.Rejected => ("Hồ sơ cửa hàng bị từ chối", $"{s.Name}: {reason}"),
            _ => ("Cửa hàng bị tạm ngưng", $"{s.Name}: {reason}"),
        };
        await notifier.NotifyAsync(owners, "store", title, msg, "/seller", null, ct);
    }

    // ------------------------------------------------------------ Drivers

    private static IQueryable<AdminDriverDto> DriverDtos(AppDbContext db, IQueryable<DriverProfile> q) =>
        from d in q.AsNoTracking()
        join u in db.Users on d.UserId equals u.Id
        join p in db.UserProfiles on d.UserId equals p.UserId into ps
        from p in ps.DefaultIfEmpty()
        orderby d.Status, d.IsOnline descending
        select new AdminDriverDto(d.UserId, p == null ? u.Email! : p.FullName, u.Email!, p == null ? null : p.PhoneNumber, d.VehicleType, d.LicensePlate,
            d.Status, d.StatusReason, d.IsOnline,
            db.Orders.Count(o => o.DriverId == d.UserId && (o.Status == OrderStatus.Assigned || o.Status == OrderStatus.PickedUp || o.Status == OrderStatus.Delivering)),
            db.Orders.Count(o => o.DriverId == d.UserId && o.Status == OrderStatus.Delivered),
            db.Orders.Where(o => o.DriverId == d.UserId && o.Status == OrderStatus.Delivered && o.DriverSettlementState != OrderSettlementState.Settled).Sum(o => (long?)o.CollectedCodAmount) ?? 0,
            db.Orders.Where(o => o.DriverId == d.UserId && o.Status == OrderStatus.Delivered && o.DriverSettlementState != OrderSettlementState.Settled).Sum(o => (long?)(o.CollectedCodAmount - o.DriverEarning)) ?? 0,
            u.IsActive, d.CreatedAtUtc);

    public async Task<IReadOnlyList<AdminDriverDto>> GetDriversAsync(DriverStatus? status = null, string? search = null, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var q = db.DriverProfiles.AsQueryable();
        if (status is { } st) q = q.Where(d => d.Status == st);
        var list = await DriverDtos(db, q).ToListAsync(ct);
        if (search.Clean() is { } term)
            list = list.Where(d => d.FullName.Contains(term, StringComparison.CurrentCultureIgnoreCase) || d.LicensePlate.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (d.PhoneNumber ?? "").Contains(term)).ToList();
        return list;
    }

    public async Task<AdminDriverDetailDto> GetDriverAsync(string userId, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var dto = await DriverDtos(db, db.DriverProfiles.Where(d => d.UserId == userId)).FirstOrDefaultAsync(ct) ?? throw new NotFoundException();
        var profile = await db.DriverProfiles.AsNoTracking().FirstAsync(d => d.UserId == userId, ct);
        var orders = await db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.Assignments.Any(a => a.DriverId == userId)).OrderByDescending(o => o.PlacedAtUtc).Take(30).ToListAsync(ct);
        return new AdminDriverDetailDto(dto, profile.IdentityNumber, profile.ServiceArea,
            orders.Select(o => DriverService.ToJob(o, true, o.RecipientPhone)).ToList());
    }

    public async Task SetDriverStatusAsync(string userId, DriverStatus status, string? reason, CancellationToken ct = default)
    {
        var uid = await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var d = await db.DriverProfiles.FirstOrDefaultAsync(x => x.UserId == userId, ct) ?? throw new NotFoundException();
        var allowed = (d.Status, status) switch
        {
            (DriverStatus.PendingApproval, DriverStatus.Active) => true,
            (DriverStatus.PendingApproval, DriverStatus.Rejected) => true,
            (DriverStatus.Active, DriverStatus.Suspended) => true,
            (DriverStatus.Suspended, DriverStatus.Active) => true,
            (DriverStatus.Rejected, DriverStatus.Active) => true,
            _ => false,
        };
        if (!allowed) throw new DomainException("Không thể chuyển trạng thái tài xế như yêu cầu.");
        if (status is DriverStatus.Rejected or DriverStatus.Suspended)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Vui lòng nhập lý do.");
            var active = await db.Orders.CountAsync(o => o.DriverId == userId && (o.Status == OrderStatus.Assigned || o.Status == OrderStatus.PickedUp || o.Status == OrderStatus.Delivering), ct);
            if (active > 0) throw new DomainException($"Tài xế đang có {active} đơn chưa hoàn tất. Hãy phân công lại trước khi tạm khóa.");
            d.IsOnline = false;
        }
        d.Status = status;
        d.StatusReason = reason.Clean();
        if (status == DriverStatus.Active) d.ApprovedAtUtc ??= clock.UtcNow;
        db.Audit(uid, $"Driver.{status}", nameof(DriverProfile), userId, reason);
        await db.SaveAsync(ct);
        var (title, msg) = status switch
        {
            DriverStatus.Active => ("🎉 Hồ sơ tài xế đã được duyệt", "Bạn có thể bật trực tuyến và nhận đơn ngay."),
            DriverStatus.Rejected => ("Hồ sơ tài xế bị từ chối", reason ?? ""),
            _ => ("Tài khoản tài xế bị tạm khóa", reason ?? ""),
        };
        await notifier.NotifyAsync([userId], "driver", title, msg, "/driver", null, ct);
    }

    // ------------------------------------------------------------ Customers & users

    public async Task<PagedResult<AdminCustomerDto>> GetCustomersAsync(string? search, int page = 1, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var customerRole = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).FirstAsync(ct);
        var q = from u in db.Users.AsNoTracking()
                where db.UserRoles.Any(r => r.UserId == u.Id && r.RoleId == customerRole)
                join p in db.UserProfiles on u.Id equals p.UserId into ps
                from p in ps.DefaultIfEmpty()
                select new { u, p };
        if (search.Clean() is { } term)
        {
            var like = $"%{term}%";
            q = q.Where(x => EF.Functions.Like(x.u.Email!, like) || (x.p != null && (EF.Functions.Like(x.p.FullName, like) || EF.Functions.Like(x.p.PhoneNumber ?? "", like))));
        }
        return await q.OrderByDescending(x => x.u.CreatedAtUtc)
            .Select(x => new AdminCustomerDto(x.u.Id, x.p == null ? x.u.Email! : x.p.FullName, x.u.Email!, x.p == null ? null : x.p.PhoneNumber, x.u.IsActive,
                db.Orders.Count(o => o.CustomerId == x.u.Id),
                db.Orders.Where(o => o.CustomerId == x.u.Id && o.Status == OrderStatus.Delivered).Sum(o => (long?)o.GrandTotal) ?? 0,
                x.u.CreatedAtUtc))
            .ToPagedAsync(page, 15, ct);
    }

    public async Task<AdminCustomerDetailDto> GetCustomerAsync(string userId, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct) ?? throw new NotFoundException();
        var p = await db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        var orders = await db.Orders.AsNoTracking().Where(o => o.CustomerId == userId).ToSummaries(db).Take(50).ToListAsync(ct);
        var spent = await db.Orders.Where(o => o.CustomerId == userId && o.Status == OrderStatus.Delivered).SumAsync(o => o.GrandTotal, ct);
        var addresses = (await db.Addresses.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct))
            .Select(a => new AddressDto(a.Id, a.Label, a.RecipientName, a.PhoneNumber, a.FullText, a.Note, a.IsDefault)).ToList();
        return new AdminCustomerDetailDto(
            new AdminCustomerDto(u.Id, p?.FullName ?? u.Email!, u.Email!, p?.PhoneNumber, u.IsActive, orders.Count, spent, u.CreatedAtUtc),
            addresses, orders);
    }

    public async Task<PagedResult<AdminUserDto>> GetUsersAsync(string? search, string? role, int page = 1, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var q = from u in db.Users.AsNoTracking()
                join p in db.UserProfiles on u.Id equals p.UserId into ps
                from p in ps.DefaultIfEmpty()
                select new { u, p };
        if (search.Clean() is { } term)
        {
            var like = $"%{term}%";
            q = q.Where(x => EF.Functions.Like(x.u.Email!, like) || (x.p != null && EF.Functions.Like(x.p.FullName, like)));
        }
        if (!string.IsNullOrEmpty(role))
        {
            var roleId = await db.Roles.Where(r => r.Name == role).Select(r => r.Id).FirstOrDefaultAsync(ct);
            q = q.Where(x => db.UserRoles.Any(r => r.UserId == x.u.Id && r.RoleId == roleId));
        }
        var paged = await q.OrderByDescending(x => x.u.CreatedAtUtc)
            .Select(x => new { x.u.Id, Name = x.p == null ? x.u.Email! : x.p.FullName, x.u.Email, Phone = x.p == null ? null : x.p.PhoneNumber, x.u.IsActive, x.u.CreatedAtUtc })
            .ToPagedAsync(page, 15, ct);
        var ids = paged.Items.Select(i => i.Id).ToList();
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ids.Contains(ur.UserId) select new { ur.UserId, r.Name }).ToListAsync(ct);
        return new PagedResult<AdminUserDto>(
            paged.Items.Select(i => new AdminUserDto(i.Id, i.Name, i.Email!, i.Phone, roles.Where(r => r.UserId == i.Id).Select(r => r.Name!).ToList(), i.IsActive, i.CreatedAtUtc)).ToList(),
            paged.Page, paged.PageSize, paged.TotalCount);
    }

    public async Task SetUserActiveAsync(string userId, bool active, CancellationToken ct = default)
    {
        var uid = await AdminAsync(ct);
        if (userId == uid && !active) throw new DomainException("Không thể tự vô hiệu hóa tài khoản của mình.");
        using var scope = scopes.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await um.FindByIdAsync(userId) ?? throw new NotFoundException();
        user.IsActive = active;
        await um.UpdateAsync(user);
        await um.SetLockoutEnabledAsync(user, true);
        await um.SetLockoutEndDateAsync(user, active ? null : DateTimeOffset.MaxValue);
        await um.UpdateSecurityStampAsync(user); // signs the user out on next revalidation

        await using var db = await dbf.CreateDbContextAsync(ct);
        if (!active) await db.DriverProfiles.Where(d => d.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(d => d.IsOnline, false), ct);
        db.Audit(uid, active ? "User.Activated" : "User.Deactivated", "User", userId, user.Email);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetUserRolesAsync(string userId, IReadOnlyCollection<string> roles, CancellationToken ct = default)
    {
        var uid = await AdminAsync(ct);
        var wanted = roles.Where(Roles.All.Contains).Distinct().ToList();
        if (userId == uid && !wanted.Contains(Roles.Admin)) throw new DomainException("Không thể tự gỡ quyền Admin của mình.");
        using var scope = scopes.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await um.FindByIdAsync(userId) ?? throw new NotFoundException();
        var current = await um.GetRolesAsync(user);
        var remove = current.Except(wanted).ToList();
        var add = wanted.Except(current).ToList();
        if (remove.Count > 0) await um.RemoveFromRolesAsync(user, remove);
        if (add.Count > 0) await um.AddToRolesAsync(user, add);
        await um.UpdateSecurityStampAsync(user);

        await using var db = await dbf.CreateDbContextAsync(ct);
        if (add.Contains(Roles.Driver) && !await db.DriverProfiles.AnyAsync(d => d.UserId == userId, ct))
            db.DriverProfiles.Add(new DriverProfile { UserId = userId, LicensePlate = "Chưa cập nhật", Status = DriverStatus.PendingApproval });
        db.Audit(uid, "User.RolesChanged", "User", userId, $"+[{string.Join(",", add)}] -[{string.Join(",", remove)}]");
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------ Audit & notifications

    private static IQueryable<AuditLogDto> AuditDtos(AppDbContext db, IQueryable<AuditLog> q) =>
        from a in q.AsNoTracking()
        join p in db.UserProfiles on a.UserId equals p.UserId into ps
        from p in ps.DefaultIfEmpty()
        orderby a.CreatedAtUtc descending, a.Id descending
        select new AuditLogDto(a.Id, p == null ? null : p.FullName, a.Action, a.EntityType, a.EntityId, a.Details, a.CreatedAtUtc);

    public async Task<PagedResult<AuditLogDto>> GetAuditLogsAsync(string? search, int page = 1, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var q = db.AuditLogs.AsQueryable();
        if (search.Clean() is { } term)
        {
            var like = $"%{term}%";
            q = q.Where(a => EF.Functions.Like(a.Action, like) || EF.Functions.Like(a.Details ?? "", like) || EF.Functions.Like(a.EntityId ?? "", like));
        }
        return await AuditDtos(db, q).ToPagedAsync(page, 20, ct);
    }

    public async Task<PagedResult<AdminNotificationDto>> GetNotificationHistoryAsync(int page = 1, CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await (from n in db.Notifications.AsNoTracking()
                      join p in db.UserProfiles on n.UserId equals p.UserId into ps
                      from p in ps.DefaultIfEmpty()
                      orderby n.CreatedAtUtc descending, n.Id descending
                      select new AdminNotificationDto(n.Id, p == null ? n.UserId : p.FullName, n.Type, n.Title, n.Message, n.IsRead, n.CreatedAtUtc))
            .ToPagedAsync(page, 20, ct);
    }

    // ------------------------------------------------------------ Settings

    public async Task<DeliverySettingsInput> GetSettingsAsync(CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var rule = await Settings.GetRateRuleAsync(db, ct);
        return new DeliverySettingsInput
        {
            BaseFee = rule.BaseFee,
            BaseDistanceMeters = rule.BaseDistanceMeters,
            FeePerKm = rule.FeePerKm,
            PlatformFeeBps = rule.PlatformFeeBps,
            DriverShareBps = rule.DriverShareBps,
            ServiceAvailable = await Settings.ServiceAvailableAsync(db, ct),
            BrandName = await Settings.GetAsync(db, SettingKeys.BrandName, ct) ?? "Ship Hoả Tốc Sky 17",
            SupportPhone = await Settings.GetAsync(db, SettingKeys.SupportPhone, ct),
            ServiceArea = await Settings.GetAsync(db, SettingKeys.ServiceArea, ct) ?? "Bình Dương",
            MaxActiveDeliveriesPerDriver = await Settings.MaxActiveDeliveriesAsync(db, ct),
        };
    }

    public async Task SaveSettingsAsync(DeliverySettingsInput input, CancellationToken ct = default)
    {
        var uid = await AdminAsync(ct);
        InputValidation.Validate(input);
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await db.DeliveryRateRules.Where(r => r.IsActive).ToListAsync(ct);
        var changed = current.Count != 1 || current[0].BaseFee != input.BaseFee || current[0].BaseDistanceMeters != input.BaseDistanceMeters
            || current[0].FeePerKm != input.FeePerKm || current[0].PlatformFeeBps != input.PlatformFeeBps || current[0].DriverShareBps != input.DriverShareBps;
        if (changed)
        {
            // Keep previous rules for history; only one active rule.
            foreach (var r in current) r.IsActive = false;
            db.DeliveryRateRules.Add(new DeliveryRateRule
            {
                Name = $"Bảng giá {VnTime.ToLocal(clock.UtcNow):dd/MM/yyyy HH:mm}",
                BaseFee = input.BaseFee, BaseDistanceMeters = input.BaseDistanceMeters, FeePerKm = input.FeePerKm,
                PlatformFeeBps = input.PlatformFeeBps, DriverShareBps = input.DriverShareBps, IsActive = true,
            });
        }
        await Settings.SetAsync(db, SettingKeys.ServiceAvailable, input.ServiceAvailable ? "true" : "false", uid, ct);
        await Settings.SetAsync(db, SettingKeys.BrandName, input.BrandName.Trim(), uid, ct);
        await Settings.SetAsync(db, SettingKeys.SupportPhone, input.SupportPhone?.Trim() ?? "", uid, ct);
        await Settings.SetAsync(db, SettingKeys.ServiceArea, input.ServiceArea.Trim(), uid, ct);
        await Settings.SetAsync(db, SettingKeys.MaxActiveDeliveriesPerDriver, input.MaxActiveDeliveriesPerDriver.ToString(), uid, ct);
        db.Audit(uid, "Settings.Updated", nameof(SystemSetting), null,
            $"base={input.BaseFee} baseM={input.BaseDistanceMeters} perKm={input.FeePerKm} fee={input.PlatformFeeBps}bps driver={input.DriverShareBps}bps service={input.ServiceAvailable}");
        await db.SaveAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<CategoryDto>> GetPlatformCategoriesAsync(CancellationToken ct = default)
    {
        await AdminAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Categories.AsNoTracking().Where(c => c.StoreId == null).OrderBy(c => c.SortOrder)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Emoji, null, c.SortOrder, c.IsActive, db.Products.Count(p => p.CategoryId == c.Id)))
            .ToListAsync(ct);
    }

    public async Task<int> SavePlatformCategoryAsync(CategoryInput input, CancellationToken ct = default)
    {
        var uid = await AdminAsync(ct);
        InputValidation.Validate(input);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var name = input.Name.Trim();
        if (await db.Categories.AnyAsync(c => c.StoreId == null && c.Name == name && c.Id != input.Id, ct)) throw new DomainException("Danh mục đã tồn tại.");
        Category c;
        if (input.Id == 0) db.Categories.Add(c = new Category { Name = name });
        else c = await db.Categories.FirstOrDefaultAsync(x => x.Id == input.Id && x.StoreId == null, ct) ?? throw new NotFoundException();
        c.Name = name;
        c.Emoji = input.Emoji;
        c.SortOrder = input.SortOrder;
        c.IsActive = input.IsActive;
        db.Audit(uid, "Category.PlatformSaved", nameof(Category), name);
        await db.SaveAsync(ct);
        return c.Id;
    }
}
