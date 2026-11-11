using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure.Data;

namespace Sky17.Infrastructure.Services;

public sealed class SellerService(
    IDbContextFactory<AppDbContext> dbf, ICurrentUserAccessor users, IClock clock,
    OrderWorkflow workflow, Notifier notifier, IFileStorage files, IDispatchService dispatch) : ISellerService
{
    private const int MaxImagesPerProduct = 5;
    private static readonly OrderStatus[] SellerTargets = [OrderStatus.Confirmed, OrderStatus.Cancelled];

    /// <summary>Server-side store membership check. Every store-scoped operation goes through here.</summary>
    private async Task<(string UserId, StoreMemberRole Role)> AccessAsync(AppDbContext db, int storeId, bool ownerOnly, CancellationToken ct)
    {
        var uid = (await users.GetAsync(ct)).RequireRole(Roles.Seller);
        var role = await db.StoreMembers.AsNoTracking()
            .Where(m => m.StoreId == storeId && m.UserId == uid)
            .Select(m => (StoreMemberRole?)m.Role).FirstOrDefaultAsync(ct);
        if (role is null) throw new ForbiddenException("Bạn không phải thành viên của cửa hàng này.");
        if (ownerOnly && role != StoreMemberRole.Owner) throw new ForbiddenException("Chỉ chủ cửa hàng mới được thực hiện thao tác này.");
        return (uid, role.Value);
    }

    public async Task<IReadOnlyList<MyStoreDto>> GetMyStoresAsync(CancellationToken ct = default)
    {
        var uid = (await users.GetAsync(ct)).RequireRole(Roles.Seller);
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.StoreMembers.AsNoTracking().Where(m => m.UserId == uid)
            .OrderBy(m => m.Role).ThenBy(m => m.Store.Name)
            .Select(m => new MyStoreDto(m.StoreId, m.Store.Name, m.Store.Emoji, m.Store.Status, m.Role))
            .ToListAsync(ct);
    }

    public async Task<SellerDashboardDto> GetDashboardAsync(int storeId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await AccessAsync(db, storeId, false, ct);
        var now = clock.UtcNow;
        var todayUtc = VnTime.StartOfLocalDayUtc(now);
        var orders = db.Orders.AsNoTracking().Where(o => o.StoreId == storeId);
        var notCancelled = orders.Where(o => o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned);

        var ordersToday = await orders.CountAsync(o => o.PlacedAtUtc >= todayUtc, ct);
        var revenueToday = await notCancelled.Where(o => o.PlacedAtUtc >= todayUtc).SumAsync(o => o.ProductTotal, ct);
        var pending = await orders.CountAsync(o => o.Status == OrderStatus.Pending, ct);
        var inProgress = await orders.CountAsync(o => o.Status == OrderStatus.Confirmed || o.Status == OrderStatus.Assigned || o.Status == OrderStatus.ArrivedAtStore, ct);
        var activeProducts = await db.Products.CountAsync(p => p.StoreId == storeId && p.IsActive && p.IsAvailable, ct);
        var lowStock = await db.Products.CountAsync(p => p.StoreId == storeId && p.IsActive && p.Stock <= 5, ct);

        var since = now.AddDays(-30);
        var total30 = await orders.CountAsync(o => o.PlacedAtUtc >= since && o.Status != OrderStatus.Pending, ct);
        var cancelled30 = await orders.CountAsync(o => o.PlacedAtUtc >= since && o.Status == OrderStatus.Cancelled, ct);
        var rate = total30 == 0 ? 100 : (int)((total30 - cancelled30) * 100L / total30);

        var last7 = await OrderQueries.DailyAsync(notCancelled, 7, o => o.ProductTotal, now, ct);
        var action = await orders.Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Confirmed || o.Status == OrderStatus.Assigned || o.Status == OrderStatus.ArrivedAtStore)
            .ToSummaries(db).Take(6).ToListAsync(ct);
        var top = (await ProductsQuery(db, storeId).ToListAsync(ct)).Where(p => p.IsActive).OrderByDescending(p => p.SoldQuantity).Take(4).ToList();

        return new SellerDashboardDto(ordersToday, revenueToday, pending, inProgress, activeProducts, lowStock, rate, last7, action, top);
    }

    // ------------------------------------------------------------ Store profile

    public async Task<StoreProfileInput> GetStoreProfileAsync(int storeId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await AccessAsync(db, storeId, false, ct);
        var s = await db.Stores.AsNoTracking().FirstAsync(x => x.Id == storeId, ct);
        return new StoreProfileInput
        {
            Name = s.Name, Description = s.Description, Tagline = s.Tagline, PhoneNumber = s.PhoneNumber, Address = s.Address,
            Emoji = s.Emoji, OpenTime = s.OpenTime, CloseTime = s.CloseTime, IsAcceptingOrders = s.IsAcceptingOrders,
            Latitude = s.Latitude, Longitude = s.Longitude,
        };
    }

    public async Task UpdateStoreProfileAsync(int storeId, StoreProfileInput input, CancellationToken ct = default)
    {
        InputValidation.Validate(input);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (uid, _) = await AccessAsync(db, storeId, true, ct);
        var s = await db.Stores.FirstAsync(x => x.Id == storeId, ct);
        s.Name = input.Name.Trim();
        s.Description = input.Description.Clean();
        s.Tagline = input.Tagline.Clean();
        s.PhoneNumber = input.PhoneNumber.Trim();
        s.Address = input.Address.Trim();
        s.Emoji = string.IsNullOrWhiteSpace(input.Emoji) ? "🏪" : input.Emoji.Trim();
        s.OpenTime = input.OpenTime;
        s.CloseTime = input.CloseTime;
        s.IsAcceptingOrders = input.IsAcceptingOrders;
        var point = GeoPoint.From(input.Latitude, input.Longitude);
        s.Latitude = point?.Latitude;
        s.Longitude = point?.Longitude;
        db.Audit(uid, "Store.ProfileUpdated", nameof(Store), storeId);
        await db.SaveAsync(ct);
    }

    // ------------------------------------------------------------ Products

    private static IQueryable<SellerProductDto> ProductsQuery(AppDbContext db, int storeId) =>
        db.Products.AsNoTracking().Where(p => p.StoreId == storeId)
            .Select(p => new SellerProductDto(p.Id, p.Name, p.Emoji,
                p.Images.OrderBy(i => i.SortOrder).Select(i => i.Url).FirstOrDefault(),
                p.Category == null ? null : p.Category.Name, p.CategoryId, p.Price, p.Stock, p.IsActive, p.IsAvailable,
                db.OrderItems.Where(i => i.ProductId == p.Id && i.Order.Status == OrderStatus.Delivered).Sum(i => (int?)i.Quantity) ?? 0));

    public async Task<IReadOnlyList<SellerProductDto>> GetProductsAsync(int storeId, string? search = null, bool includeInactive = true, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await AccessAsync(db, storeId, false, ct);
        var list = await ProductsQuery(db, storeId).ToListAsync(ct);
        if (!includeInactive) list = list.Where(p => p.IsActive).ToList();
        if (search.Clean() is { } s) list = list.Where(p => p.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase)).ToList();
        return list.OrderByDescending(p => p.IsActive).ThenBy(p => p.Name).ToList();
    }

    public async Task<SellerProductEditDto> GetProductAsync(int storeId, int productId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await AccessAsync(db, storeId, false, ct);
        var p = await db.Products.AsNoTracking().Include(x => x.Images).FirstOrDefaultAsync(x => x.Id == productId && x.StoreId == storeId, ct)
            ?? throw new NotFoundException("Không tìm thấy sản phẩm trong cửa hàng của bạn.");
        return new SellerProductEditDto(new ProductInput
        {
            Id = p.Id, Name = p.Name, Description = p.Description, Price = p.Price, Stock = p.Stock,
            CategoryId = p.CategoryId, Emoji = p.Emoji, IsActive = p.IsActive, IsAvailable = p.IsAvailable,
        }, p.Images.OrderBy(i => i.SortOrder).Select(i => new ProductImageDto(i.Id, i.Url)).ToList());
    }

    public async Task<int> SaveProductAsync(int storeId, ProductInput input, CancellationToken ct = default)
    {
        InputValidation.Validate(input);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (uid, _) = await AccessAsync(db, storeId, false, ct);

        if (input.CategoryId is { } cid && !await db.Categories.AnyAsync(c => c.Id == cid && (c.StoreId == null || c.StoreId == storeId), ct))
            throw new ForbiddenException("Danh mục không hợp lệ cho cửa hàng này.");

        Product p;
        if (input.Id == 0)
        {
            p = new Product { StoreId = storeId, Name = "" };
            db.Products.Add(p);
        }
        else
        {
            // Store ownership enforced by filtering on StoreId: a product of another store is simply "not found".
            p = await db.Products.FirstOrDefaultAsync(x => x.Id == input.Id && x.StoreId == storeId, ct)
                ?? throw new NotFoundException("Không tìm thấy sản phẩm trong cửa hàng của bạn.");
        }
        p.Name = input.Name.Trim();
        p.Description = input.Description.Clean();
        p.Price = input.Price;
        p.Stock = input.Stock;
        p.CategoryId = input.CategoryId;
        p.Emoji = string.IsNullOrWhiteSpace(input.Emoji) ? "🍽️" : input.Emoji.Trim();
        p.IsActive = input.IsActive;
        p.IsAvailable = input.IsAvailable;
        await db.SaveAsync(ct);
        db.Audit(uid, input.Id == 0 ? "Product.Created" : "Product.Updated", nameof(Product), p.Id, $"{p.Name} · {p.Price}đ · kho {p.Stock}");
        await db.SaveChangesAsync(ct);
        return p.Id;
    }

    public async Task DeleteProductAsync(int storeId, int productId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (uid, _) = await AccessAsync(db, storeId, false, ct);
        var p = await db.Products.Include(x => x.Images).FirstOrDefaultAsync(x => x.Id == productId && x.StoreId == storeId, ct)
            ?? throw new NotFoundException("Không tìm thấy sản phẩm.");
        var hasOrders = await db.OrderItems.AnyAsync(i => i.ProductId == productId, ct);
        if (hasOrders)
        {
            p.IsActive = false; // keep for order history integrity
            await db.CartItems.Where(c => c.ProductId == productId).ExecuteDeleteAsync(ct);
        }
        else
        {
            foreach (var img in p.Images) await files.DeleteAsync(img.Url, ct);
            db.Products.Remove(p);
        }
        db.Audit(uid, hasOrders ? "Product.Deactivated" : "Product.Deleted", nameof(Product), productId, p.Name);
        await db.SaveAsync(ct);
    }

    public async Task SetStockAsync(int storeId, int productId, int stock, CancellationToken ct = default)
    {
        if (stock is < 0 or > 100_000) throw new DomainException("Tồn kho không hợp lệ.");
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (uid, _) = await AccessAsync(db, storeId, false, ct);
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == productId && x.StoreId == storeId, ct) ?? throw new NotFoundException();
        p.Stock = stock;
        db.Audit(uid, "Product.StockSet", nameof(Product), productId, stock.ToString());
        await db.SaveAsync(ct);
    }

    public async Task SetAvailabilityAsync(int storeId, int productId, bool isAvailable, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (uid, _) = await AccessAsync(db, storeId, false, ct);
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == productId && x.StoreId == storeId, ct) ?? throw new NotFoundException();
        p.IsAvailable = isAvailable;
        db.Audit(uid, "Product.Availability", nameof(Product), productId, isAvailable ? "available" : "unavailable");
        await db.SaveAsync(ct);
    }

    public async Task<ProductImageDto> AddProductImageAsync(int storeId, int productId, Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (uid, _) = await AccessAsync(db, storeId, false, ct);
        var p = await db.Products.Include(x => x.Images).FirstOrDefaultAsync(x => x.Id == productId && x.StoreId == storeId, ct) ?? throw new NotFoundException();
        if (p.Images.Count >= MaxImagesPerProduct) throw new DomainException($"Mỗi sản phẩm tối đa {MaxImagesPerProduct} ảnh.");
        var url = await files.SaveImageAsync(content, fileName, contentType, $"products/{storeId}", ct);
        var img = new ProductImage { ProductId = p.Id, Url = url, SortOrder = p.Images.Count == 0 ? 0 : p.Images.Max(i => i.SortOrder) + 1 };
        db.ProductImages.Add(img);
        db.Audit(uid, "Product.ImageAdded", nameof(Product), productId, url);
        await db.SaveAsync(ct);
        return new ProductImageDto(img.Id, img.Url);
    }

    public async Task RemoveProductImageAsync(int storeId, int productId, int imageId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (uid, _) = await AccessAsync(db, storeId, false, ct);
        var img = await db.ProductImages.FirstOrDefaultAsync(i => i.Id == imageId && i.ProductId == productId && i.Product.StoreId == storeId, ct)
            ?? throw new NotFoundException();
        db.ProductImages.Remove(img);
        db.Audit(uid, "Product.ImageRemoved", nameof(Product), productId, img.Url);
        await db.SaveAsync(ct);
        await files.DeleteAsync(img.Url, ct);
    }

    // ------------------------------------------------------------ Categories

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(int storeId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await AccessAsync(db, storeId, false, ct);
        return await db.Categories.AsNoTracking().Where(c => c.StoreId == null || c.StoreId == storeId)
            .OrderBy(c => c.StoreId == null ? 0 : 1).ThenBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Emoji, c.StoreId, c.SortOrder, c.IsActive,
                db.Products.Count(p => p.CategoryId == c.Id && p.StoreId == storeId)))
            .ToListAsync(ct);
    }

    public async Task<int> SaveCategoryAsync(int storeId, CategoryInput input, CancellationToken ct = default)
    {
        InputValidation.Validate(input);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (uid, _) = await AccessAsync(db, storeId, false, ct);
        var name = input.Name.Trim();
        if (await db.Categories.AnyAsync(c => c.StoreId == storeId && c.Name == name && c.Id != input.Id, ct))
            throw new DomainException("Danh mục đã tồn tại.");
        Category c;
        if (input.Id == 0) db.Categories.Add(c = new Category { StoreId = storeId, Name = name });
        else c = await db.Categories.FirstOrDefaultAsync(x => x.Id == input.Id && x.StoreId == storeId, ct)
                 ?? throw new ForbiddenException("Chỉ được sửa danh mục của cửa hàng mình.");
        c.Name = name;
        c.Emoji = string.IsNullOrWhiteSpace(input.Emoji) ? "🍽️" : input.Emoji.Trim();
        c.SortOrder = input.SortOrder;
        c.IsActive = input.IsActive;
        db.Audit(uid, "Category.Saved", nameof(Category), input.Id == 0 ? name : input.Id);
        await db.SaveAsync(ct);
        return c.Id;
    }

    public async Task DeleteCategoryAsync(int storeId, int categoryId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (uid, _) = await AccessAsync(db, storeId, false, ct);
        var c = await db.Categories.FirstOrDefaultAsync(x => x.Id == categoryId && x.StoreId == storeId, ct)
            ?? throw new ForbiddenException("Chỉ được xóa danh mục của cửa hàng mình.");
        db.Categories.Remove(c);
        db.Audit(uid, "Category.Deleted", nameof(Category), categoryId, c.Name);
        await db.SaveAsync(ct);
    }

    // ------------------------------------------------------------ Orders

    public async Task<PagedResult<OrderSummaryDto>> GetOrdersAsync(int storeId, OrderFilter filter, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await AccessAsync(db, storeId, false, ct);
        filter.StoreId = storeId;
        return await db.Orders.AsNoTracking().Where(o => o.StoreId == storeId).ApplyFilter(filter).ToSummaries(db).ToPagedAsync(filter.Page, filter.PageSize, ct);
    }

    public async Task<OrderDetailDto> GetOrderAsync(int storeId, int orderId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await AccessAsync(db, storeId, false, ct);
        if (!await db.Orders.AnyAsync(o => o.Id == orderId && o.StoreId == storeId, ct)) throw new NotFoundException("Không tìm thấy đơn hàng của cửa hàng.");
        return await OrderQueries.LoadDetailAsync(db, orderId, OrderActor.Seller, ct);
    }

    public async Task ChangeOrderStatusAsync(int storeId, int orderId, OrderStatus target, string? reason, CancellationToken ct = default)
    {
        if (!SellerTargets.Contains(target)) throw new ForbiddenException();
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (uid, _) = await AccessAsync(db, storeId, false, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await OrderWorkflow.WithWorkflowGraph(db.Orders).FirstOrDefaultAsync(o => o.Id == orderId && o.StoreId == storeId, ct)
            ?? throw new NotFoundException("Không tìm thấy đơn hàng của cửa hàng.");
        await workflow.ApplyAsync(db, order, target, OrderActor.Seller, uid, reason, ct);
        await db.SaveAsync(ct);
        await tx.CommitAsync(ct);
        await notifier.OrderStatusChangedAsync(orderId, ct);
        // Store accepted → start looking for the nearest free driver right away.
        if (target == OrderStatus.Confirmed) await dispatch.DispatchOrderAsync(orderId, ct);
    }

    public async Task MarkFoodReadyAsync(int storeId, int orderId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (uid, _) = await AccessAsync(db, storeId, false, ct);
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.StoreId == storeId, ct)
            ?? throw new NotFoundException("Không tìm thấy đơn hàng của cửa hàng.");
        if (!OrderStateMachine.IsKitchenOpen(order.Status)) throw new DomainException("Đơn hàng không còn ở bước chuẩn bị món.");
        if (order.PreparationState == PreparationState.Ready) return;
        order.PreparationState = PreparationState.Ready;
        order.FoodReadyAtUtc = clock.UtcNow;
        db.Audit(uid, "Order.FoodReady", nameof(Order), orderId, order.Code);
        await db.SaveAsync(ct);
        var targets = order.DriverId is null ? new List<string>() : [order.DriverId];
        await notifier.NotifyAsync(targets, "order", "🍱 Món đã sẵn sàng", $"Đơn {order.Code} tại {order.PickupStoreName} đã sẵn sàng để lấy.", $"/driver/deliveries/{orderId}", orderId, ct);
        await notifier.NotifyAsync([order.CustomerId], "order", "🍱 Món đã sẵn sàng", $"{order.PickupStoreName} đã chuẩn bị xong đơn {order.Code}.", $"/orders/{order.Code}", orderId, ct);
    }

    // ------------------------------------------------------------ Reports & settlements

    public async Task<SalesReportDto> GetSalesReportAsync(int storeId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber > 92) from = to.AddDays(-92);
        await using var db = await dbf.CreateDbContextAsync(ct);
        await AccessAsync(db, storeId, false, ct);
        var fromUtc = VnTime.LocalDateToUtc(from);
        var toUtc = VnTime.LocalDateToUtc(to.AddDays(1));
        var orders = await db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.StoreId == storeId && o.PlacedAtUtc >= fromUtc && o.PlacedAtUtc < toUtc).ToListAsync(ct);
        var delivered = orders.Where(o => o.Status == OrderStatus.Delivered).ToList();
        var daily = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i =>
        {
            var d = from.AddDays(i);
            var rows = delivered.Where(o => DateOnly.FromDateTime(VnTime.ToLocal(o.PlacedAtUtc)) == d).ToList();
            return new DailyPoint(d, rows.Count, rows.Sum(o => o.ProductTotal));
        }).ToList();
        var top = delivered.SelectMany(o => o.Items).GroupBy(i => new { i.ProductId, i.ProductName, i.Emoji })
            .Select(g => new ProductSalesDto(g.Key.ProductId, g.Key.ProductName, g.Key.Emoji, g.Sum(i => i.Quantity), g.Sum(i => i.LineTotal)))
            .OrderByDescending(x => x.Revenue).Take(10).ToList();
        var settled = delivered.Where(o => o.StoreSettlementState == OrderSettlementState.Settled).Sum(o => o.StoreReceivable);
        return new SalesReportDto(from, to, delivered.Count, orders.Count(o => o.Status == OrderStatus.Cancelled),
            delivered.Sum(o => o.ProductTotal), delivered.Sum(o => o.PlatformFee), delivered.Sum(o => o.StoreReceivable),
            settled, delivered.Sum(o => o.StoreReceivable) - settled, daily, top);
    }

    public async Task<IReadOnlyList<SettlementDto>> GetSettlementsAsync(int storeId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await AccessAsync(db, storeId, true, ct);
        return await FinanceService.SettlementDtos(db, db.Settlements.Where(s => s.Type == SettlementType.Store && s.StoreId == storeId))
            .ToListAsync(ct);
    }

    // ------------------------------------------------------------ Members

    public async Task<IReadOnlyList<StoreMemberDto>> GetMembersAsync(int storeId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await AccessAsync(db, storeId, false, ct);
        return await (from m in db.StoreMembers.AsNoTracking()
                      where m.StoreId == storeId
                      join u in db.Users on m.UserId equals u.Id
                      join p in db.UserProfiles on m.UserId equals p.UserId into ps
                      from p in ps.DefaultIfEmpty()
                      orderby m.Role
                      select new StoreMemberDto(m.Id, m.UserId, p == null ? u.Email! : p.FullName, u.Email!, m.Role)).ToListAsync(ct);
    }

    public async Task AddMemberAsync(int storeId, string email, StoreMemberRole role, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (uid, _) = await AccessAsync(db, storeId, true, ct);
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct)
            ?? throw new DomainException("Không tìm thấy tài khoản với email này. Nhân viên cần đăng ký tài khoản trước.");
        if (!user.IsActive) throw new DomainException("Tài khoản này đang bị vô hiệu hóa.");
        if (await db.StoreMembers.AnyAsync(m => m.StoreId == storeId && m.UserId == user.Id, ct)) throw new DomainException("Người này đã là thành viên cửa hàng.");
        db.StoreMembers.Add(new StoreMember { StoreId = storeId, UserId = user.Id, Role = role });

        // Grant the Seller role (scoped by store membership) if missing.
        var sellerRoleId = await db.Roles.Where(r => r.Name == Roles.Seller).Select(r => r.Id).FirstAsync(ct);
        if (!await db.UserRoles.AnyAsync(r => r.UserId == user.Id && r.RoleId == sellerRoleId, ct))
        {
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = sellerRoleId });
            user.SecurityStamp = Guid.NewGuid().ToString(); // forces re-login so the new role takes effect
        }
        db.Audit(uid, "Store.MemberAdded", nameof(Store), storeId, $"{email} as {role}");
        await db.SaveAsync(ct);
        await notifier.NotifyAsync([user.Id], "store", "Bạn được thêm vào cửa hàng", "Bạn vừa được thêm làm thành viên cửa hàng trên Sky 17.", "/seller", null, ct);
    }

    public async Task RemoveMemberAsync(int storeId, int memberId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var (uid, _) = await AccessAsync(db, storeId, true, ct);
        var m = await db.StoreMembers.FirstOrDefaultAsync(x => x.Id == memberId && x.StoreId == storeId, ct) ?? throw new NotFoundException();
        if (m.Role == StoreMemberRole.Owner && await db.StoreMembers.CountAsync(x => x.StoreId == storeId && x.Role == StoreMemberRole.Owner, ct) <= 1)
            throw new DomainException("Cửa hàng phải có ít nhất một chủ sở hữu.");
        db.StoreMembers.Remove(m);
        db.Audit(uid, "Store.MemberRemoved", nameof(Store), storeId, m.UserId);
        await db.SaveAsync(ct);
    }
}
