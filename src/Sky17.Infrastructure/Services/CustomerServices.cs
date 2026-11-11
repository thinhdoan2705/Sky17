using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure.Data;

namespace Sky17.Infrastructure.Services;

internal static class InputValidation
{
    public static void Validate(object input)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(input, new ValidationContext(input), results, validateAllProperties: true))
            throw new DomainException(string.Join(" ", results.Select(r => r.ErrorMessage)));
    }
}

public sealed class CatalogService(IDbContextFactory<AppDbContext> dbf, IClock clock) : ICatalogService
{
    private TimeOnly LocalNow => TimeOnly.FromDateTime(VnTime.ToLocal(clock.UtcNow));

    public async Task<IReadOnlyList<CategoryDto>> GetPlatformCategoriesAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Categories.AsNoTracking()
            .Where(c => c.StoreId == null && c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Emoji, c.StoreId, c.SortOrder, c.IsActive,
                db.Products.Count(p => p.CategoryId == c.Id && p.IsActive && p.Store.Status == StoreStatus.Active)))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StoreCardDto>> GetStoresAsync(string? search = null, int take = 30, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var q = db.Stores.AsNoTracking().Where(s => s.Status == StoreStatus.Active);
        if (search.Clean() is { } s)
        {
            var like = $"%{s}%";
            q = q.Where(x => EF.Functions.Like(x.Name, like) || EF.Functions.Like(x.Tagline ?? "", like) || EF.Functions.Like(x.Address, like));
        }
        var stores = await q.OrderBy(x => x.Name).Take(take)
            .Select(x => new { Store = x, Count = x.Products.Count(p => p.IsActive) })
            .ToListAsync(ct);
        var now = LocalNow;
        return stores.Select(x => ToCard(x.Store, x.Count, now)).OrderByDescending(x => x.IsOpenNow).ToList();
    }

    internal static StoreCardDto ToCard(Store s, int productCount, TimeOnly now) =>
        new(s.Id, s.Name, s.Emoji, s.LogoUrl, s.Tagline, s.Address, s.CanReceiveOrders(now), s.OpenTime, s.CloseTime, productCount)
        {
            Latitude = s.Latitude,
            Longitude = s.Longitude,
        };

    public async Task<StoreDetailDto?> GetStoreAsync(int storeId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId && s.Status == StoreStatus.Active, ct);
        if (store is null) return null;
        var products = (await ProductCards(db.Products.Where(p => p.StoreId == storeId)).ToListAsync(ct))
            .OrderBy(p => p.CategoryName).ThenBy(p => p.Name).ToList();
        var categories = products.Where(p => p.CategoryName is not null).GroupBy(p => p.CategoryName!)
            .Select((g, i) => new CategoryDto(i, g.Key, "", storeId, i, true, g.Count())).ToList();
        return new StoreDetailDto(ToCard(store, products.Count, LocalNow), store.Description, store.PhoneNumber, categories, products);
    }

    private static IQueryable<ProductCardDto> ProductCards(IQueryable<Product> q) =>
        q.AsNoTracking()
            .Where(p => p.IsActive && p.Store.Status == StoreStatus.Active)
            .Select(p => new ProductCardDto(p.Id, p.StoreId, p.Store.Name, p.Name, p.Emoji,
                p.Images.OrderBy(i => i.SortOrder).Select(i => i.Url).FirstOrDefault(),
                p.Price, p.Stock, p.IsAvailable && p.Stock > 0, p.Category == null ? null : p.Category.Name));

    public async Task<IReadOnlyList<ProductCardDto>> SearchProductsAsync(ProductQuery query, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var q = db.Products.AsQueryable();
        if (query.StoreId is { } sid) q = q.Where(p => p.StoreId == sid);
        if (query.CategoryId is { } cid) q = q.Where(p => p.CategoryId == cid);
        if (query.Search.Clean() is { } s)
        {
            var like = $"%{s}%";
            q = q.Where(p => EF.Functions.Like(p.Name, like) || EF.Functions.Like(p.Description ?? "", like) || EF.Functions.Like(p.Store.Name, like));
        }
        // Order on the entity (translatable), then project.
        q = q.OrderByDescending(p => p.IsAvailable && p.Stock > 0).ThenBy(p => p.Name);
        return await ProductCards(q).Take(Math.Clamp(query.Take, 1, 100)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StoreSearchGroupDto>> SearchByStoreAsync(StoreSearchQuery query, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var term = query.Search.Clean();
        var origin = GeoPoint.From(query.Latitude, query.Longitude);
        var perStore = Math.Clamp(query.MaxProductsPerStore, 1, 50);

        var stores = await db.Stores.AsNoTracking().Where(s => s.Status == StoreStatus.Active)
            .Select(s => new { Store = s, Count = s.Products.Count(p => p.IsActive) }).ToListAsync(ct);

        // Dishes matching the keyword (name, description or category) and/or the category filter.
        var pq = db.Products.AsQueryable();
        if (query.CategoryId is { } cid) pq = pq.Where(p => p.CategoryId == cid);
        if (term is not null)
        {
            var like = $"%{term}%";
            pq = pq.Where(p => EF.Functions.Like(p.Name, like) || EF.Functions.Like(p.Description ?? "", like)
                || (p.Category != null && EF.Functions.Like(p.Category.Name, like)));
        }
        var matched = term is null && query.CategoryId is null ? [] : await ProductCards(pq).ToListAsync(ct);

        // Stores whose name/tagline matches show their menu even when no single dish matched.
        var nameMatched = term is null
            ? (query.CategoryId is null ? stores.Select(s => s.Store.Id).ToHashSet() : [])
            : stores.Where(s => Contains(s.Store.Name, term) || Contains(s.Store.Tagline, term)).Select(s => s.Store.Id).ToHashSet();
        var menuStoreIds = nameMatched.Where(id => !matched.Any(p => p.StoreId == id)).ToList();
        var menus = menuStoreIds.Count == 0 ? [] : await ProductCards(db.Products.Where(p => menuStoreIds.Contains(p.StoreId))).ToListAsync(ct);

        var now = LocalNow;
        var groups = new List<StoreSearchGroupDto>();
        foreach (var s in stores)
        {
            var hits = matched.Where(p => p.StoreId == s.Store.Id).ToList();
            var fromName = nameMatched.Contains(s.Store.Id);
            if (hits.Count == 0 && !fromName) continue;
            var list = (hits.Count > 0 ? hits : menus.Where(p => p.StoreId == s.Store.Id).ToList())
                .OrderByDescending(p => p.IsAvailable).ThenBy(p => p.Name).ToList();
            double? km = origin is not null && GeoPoint.From(s.Store.Latitude, s.Store.Longitude) is { } sp
                ? Math.Round(origin.Value.StraightLineMetersTo(sp) / 1000.0, 1)
                : null;
            groups.Add(new StoreSearchGroupDto(ToCard(s.Store, s.Count, now), km, list.Take(perStore).ToList(), list.Count, term is not null && fromName && hits.Count == 0));
        }

        // Nearest store first; stores without a map pin (unknown distance) go last.
        return groups.OrderBy(g => g.DistanceKm is null).ThenBy(g => g.DistanceKm).ThenBy(g => g.Store.Name)
            .Take(Math.Clamp(query.MaxStores, 1, 100)).ToList();
    }

    private static bool Contains(string? text, string term) =>
        text is not null && System.Globalization.CultureInfo.GetCultureInfo("vi-VN").CompareInfo
            .IndexOf(text, term, System.Globalization.CompareOptions.IgnoreCase) >= 0;

    public async Task<ProductDetailDto?> GetProductAsync(int productId, CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var p = await db.Products.AsNoTracking().Include(x => x.Store).Include(x => x.Images).Include(x => x.Category)
            .FirstOrDefaultAsync(x => x.Id == productId && x.IsActive && x.Store.Status == StoreStatus.Active, ct);
        if (p is null) return null;
        return new ProductDetailDto(p.Id, p.StoreId, p.Store.Name, p.Store.Emoji, p.Store.CanReceiveOrders(LocalNow), p.Name, p.Description, p.Emoji,
            p.Images.OrderBy(i => i.SortOrder).Select(i => i.Url).ToList(), p.Price, p.Stock, p.IsAvailable && p.Stock > 0, p.Category?.Name);
    }

    public async Task<bool> IsServiceAvailableAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await Settings.ServiceAvailableAsync(db, ct);
    }

    public async Task<string> GetBrandNameAsync(CancellationToken ct = default)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await Settings.GetAsync(db, SettingKeys.BrandName, ct) ?? "Ship Hoả Tốc Sky 17";
    }
}

public sealed class CartService(IDbContextFactory<AppDbContext> dbf, ICurrentUserAccessor users, IClock clock, IRoutingService routing) : ICartService
{
    private async Task<string> UserAsync(CancellationToken ct) => (await users.GetAsync(ct)).RequireRole(Roles.Customer);

    private static IQueryable<Cart> CartGraph(AppDbContext db) =>
        db.Carts.Include(c => c.Store).Include(c => c.Items).ThenInclude(i => i.Product).ThenInclude(p => p.Images);

    private static CartDto Empty(int? storeId, bool serviceOn) => new(storeId, null, false, [], 0, 0, 0, serviceOn, []);

    public async Task<IReadOnlyList<CartDto>> GetAllAsync(CancellationToken ct = default)
    {
        var current = await users.GetAsync(ct);
        if (!current.IsInRole(Roles.Customer)) return [];
        await using var db = await dbf.CreateDbContextAsync(ct);
        var carts = await CartGraph(db).AsNoTracking().Where(c => c.UserId == current.UserId && c.Items.Any()).ToListAsync(ct);
        var result = new List<CartDto>();
        foreach (var c in carts.OrderByDescending(c => c.UpdatedAtUtc ?? c.CreatedAtUtc)) result.Add(await ToDtoAsync(db, c, ct));
        return result;
    }

    public async Task<CartDto> GetAsync(int storeId, CancellationToken ct = default)
    {
        var current = await users.GetAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        if (!current.IsInRole(Roles.Customer)) return Empty(storeId, true);
        var cart = await CartGraph(db).AsNoTracking().FirstOrDefaultAsync(c => c.UserId == current.UserId && c.StoreId == storeId, ct);
        return cart is null ? Empty(storeId, await Settings.ServiceAvailableAsync(db, ct)) : await ToDtoAsync(db, cart, ct);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        var current = await users.GetAsync(ct);
        if (!current.IsInRole(Roles.Customer)) return 0;
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await CountAsync(db, current.UserId!, ct);
    }

    private static async Task<int> CountAsync(AppDbContext db, string uid, CancellationToken ct) =>
        await db.CartItems.Where(i => i.Cart.UserId == uid).SumAsync(i => (int?)i.Quantity, ct) ?? 0;

    internal async Task<CartDto> ToDtoAsync(AppDbContext db, Cart? cart, CancellationToken ct)
    {
        var rule = await Settings.GetRateRuleAsync(db, ct);
        var serviceOn = await Settings.ServiceAvailableAsync(db, ct);
        if (cart is null || cart.Items.Count == 0) return Empty(cart?.StoreId, serviceOn);

        var warnings = new List<string>();
        var lines = new List<CartLineDto>();
        foreach (var i in cart.Items.OrderBy(i => i.Id))
        {
            var p = i.Product;
            var available = p.IsActive && p.IsAvailable && p.Stock > 0;
            if (!available) warnings.Add($"“{p.Name}” hiện đã hết hoặc ngừng bán, vui lòng xóa khỏi giỏ.");
            else if (i.Quantity > p.Stock) warnings.Add($"“{p.Name}” chỉ còn {p.Stock} phần.");
            lines.Add(new CartLineDto(p.Id, p.Name, p.Emoji, p.Images.OrderBy(x => x.SortOrder).Select(x => x.Url).FirstOrDefault(),
                p.Price, i.Quantity, p.Price * i.Quantity, p.Stock, available));
        }
        var subtotal = lines.Sum(l => l.LineTotal);
        var fee = DeliveryPricing.CalculateFee(rule, null);
        var storeOk = cart.Store.CanReceiveOrders(TimeOnly.FromDateTime(VnTime.ToLocal(clock.UtcNow)));
        if (!storeOk) warnings.Add("Cửa hàng hiện đang đóng cửa hoặc tạm ngưng nhận đơn.");
        if (!serviceOn) warnings.Add("Dịch vụ giao hàng đang tạm ngưng.");
        return new CartDto(cart.StoreId, cart.Store.Name, storeOk, lines, subtotal, fee, subtotal + fee, serviceOn, warnings);
    }

    private async Task<CartDto> FreshAsync(AppDbContext db, string uid, int storeId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var cart = await CartGraph(db).AsNoTracking().FirstOrDefaultAsync(c => c.UserId == uid && c.StoreId == storeId, ct);
        return await ToDtoAsync(db, cart, ct) with { StoreId = storeId };
    }

    public async Task<AddToCartResult> AddAsync(int productId, int quantity = 1, CancellationToken ct = default)
    {
        var uid = await UserAsync(ct);
        if (quantity is < 1 or > 99) throw new DomainException("Số lượng không hợp lệ.");
        await using var db = await dbf.CreateDbContextAsync(ct);
        var product = await db.Products.AsNoTracking().Include(p => p.Store)
            .FirstOrDefaultAsync(p => p.Id == productId && p.IsActive && p.Store.Status == StoreStatus.Active, ct)
            ?? throw new NotFoundException("Sản phẩm không tồn tại hoặc đã ngừng bán.");
        if (!product.IsAvailable || product.Stock <= 0) return new AddToCartResult(false, "Sản phẩm tạm hết hàng.", null);

        // Each store has its own cart; adding never touches the customer's other carts.
        var cart = await db.Carts.Include(c => c.Items).FirstOrDefaultAsync(c => c.UserId == uid && c.StoreId == product.StoreId, ct);
        if (cart is null)
        {
            cart = new Cart { UserId = uid, StoreId = product.StoreId };
            db.Carts.Add(cart);
        }
        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId);
        var newQty = (item?.Quantity ?? 0) + quantity;
        if (newQty > product.Stock) return new AddToCartResult(false, $"Chỉ còn {product.Stock} phần “{product.Name}”.", null);
        if (item is null) cart.Items.Add(new CartItem { ProductId = productId, Quantity = newQty });
        else item.Quantity = newQty;
        cart.UpdatedAtUtc = clock.UtcNow;
        try { await db.SaveAsync(ct); }
        catch (DbUpdateException) when (cart.Id == 0)
        {
            // Two concurrent first adds for the same store: the unique (user, store) index keeps one cart; retry once.
            db.ChangeTracker.Clear();
            return await AddAsync(productId, quantity, ct);
        }

        var dto = await FreshAsync(db, uid, product.StoreId, ct);
        return new AddToCartResult(true, $"Đã thêm “{product.Name}” vào giỏ {product.Store.Name}", dto) { TotalItems = await CountAsync(db, uid, ct) };
    }

    public async Task<CartDto> SetQuantityAsync(int productId, int quantity, CancellationToken ct = default)
    {
        var uid = await UserAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var item = await db.CartItems.Include(i => i.Product).Include(i => i.Cart).ThenInclude(c => c.Items)
            .FirstOrDefaultAsync(i => i.ProductId == productId && i.Cart.UserId == uid, ct)
            ?? throw new NotFoundException("Món này không có trong giỏ.");
        var cart = item.Cart;
        if (quantity <= 0)
        {
            db.CartItems.Remove(item);
            // An empty store cart disappears from the cart list.
            if (cart.Items.Count == 1) db.Carts.Remove(cart);
        }
        else
        {
            if (quantity > item.Product.Stock) throw new DomainException($"Chỉ còn {item.Product.Stock} phần “{item.Product.Name}”.");
            item.Quantity = Math.Min(quantity, 99);
            cart.UpdatedAtUtc = clock.UtcNow;
        }
        await db.SaveAsync(ct);
        return await FreshAsync(db, uid, cart.StoreId, ct);
    }

    public async Task ClearAsync(int storeId, CancellationToken ct = default)
    {
        var uid = await UserAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        await db.Carts.Where(c => c.UserId == uid && c.StoreId == storeId).ExecuteDeleteAsync(ct);
    }

    public async Task<DeliveryQuoteDto> QuoteAsync(int storeId, double? latitude, double? longitude, CancellationToken ct = default)
    {
        var uid = await UserAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var cart = await CartGraph(db).AsNoTracking().FirstOrDefaultAsync(c => c.UserId == uid && c.StoreId == storeId, ct);
        var rule = await Settings.GetRateRuleAsync(db, ct);
        var subtotal = cart?.Items.Sum(i => i.Product.Price * i.Quantity) ?? 0;
        if (cart is null) return new DeliveryQuoteDto(null, rule.BaseFee, subtotal, subtotal + rule.BaseFee, false);
        var (meters, fee, _) = await DeliveryQuote.CalculateAsync(routing, rule, cart.Store, GeoPoint.From(latitude, longitude), ct);
        return new DeliveryQuoteDto(meters, fee, subtotal, subtotal + fee, cart.Store.Latitude is not null);
    }
}

public sealed class CustomerOrderService(
    IDbContextFactory<AppDbContext> dbf, ICurrentUserAccessor users, IClock clock, OrderWorkflow workflow, Notifier notifier, IRoutingService routing, IAdministrativeUnits units) : ICustomerOrderService
{
    public async Task<PlaceOrderResult> PlaceOrderAsync(CheckoutInput input, CancellationToken ct = default)
    {
        var uid = (await users.GetAsync(ct)).RequireRole(Roles.Customer);
        InputValidation.Validate(input);
        if (input.IdempotencyKey == Guid.Empty) throw new DomainException("Yêu cầu đặt hàng không hợp lệ.");
        var key = input.IdempotencyKey.ToString("N");
        // Ward must belong to the province (national catalog); the address text is composed server-side.
        var (province, ward) = units.Resolve(input.ProvinceCode, input.WardCode);
        var street = input.Street.Trim();

        await using var db = await dbf.CreateDbContextAsync(ct);

        // Idempotency: the same checkout attempt never creates two orders.
        var existing = await db.Orders.AsNoTracking().Where(o => o.CustomerId == uid && o.IdempotencyKey == key)
            .Select(o => new { o.Id, o.Code }).FirstOrDefaultAsync(ct);
        if (existing is not null) return new PlaceOrderResult(existing.Id, existing.Code, true);

        if (!await Settings.ServiceAvailableAsync(db, ct)) throw new DomainException("Dịch vụ giao hàng đang tạm ngưng, vui lòng quay lại sau.");

        // One order per store cart: only the cart of the requested store is checked out.
        var cart = await db.Carts.Include(c => c.Items).Include(c => c.Store).FirstOrDefaultAsync(c => c.UserId == uid && c.StoreId == input.StoreId, ct);
        if (cart is null || cart.Items.Count == 0) throw new DomainException("Giỏ hàng của cửa hàng này đang trống.");
        var store = cart.Store;
        var now = clock.UtcNow;
        if (!store.CanReceiveOrders(TimeOnly.FromDateTime(VnTime.ToLocal(now))))
            throw new DomainException($"{store.Name} hiện không nhận đơn (ngoài giờ mở cửa hoặc tạm nghỉ).");
        var rule = await Settings.GetRateRuleAsync(db, ct);

        // Road distance (network call) is resolved before the write transaction starts.
        var dropoff = GeoPoint.From(input.Latitude, input.Longitude);
        var (meters, deliveryFee, distanceSource) = await DeliveryQuote.CalculateAsync(routing, rule, store, dropoff, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Atomically reserve stock; any shortage aborts the whole order.
        foreach (var item in cart.Items)
        {
            var qty = item.Quantity;
            var reserved = await db.Products
                .Where(p => p.Id == item.ProductId && p.StoreId == store.Id && p.IsActive && p.IsAvailable && p.Stock >= qty)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock - qty).SetProperty(p => p.Version, p => p.Version + 1), ct);
            if (reserved == 0)
            {
                var info = await db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => new { p.Name, p.Stock }).FirstOrDefaultAsync(ct);
                throw new DomainException(info is null ? "Một sản phẩm trong giỏ không còn tồn tại." : $"“{info.Name}” không đủ hàng (còn {info.Stock}).");
            }
        }

        // Prices are always read from the database, never from the client.
        var ids = cart.Items.Select(i => i.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var items = cart.Items.Select(i =>
        {
            var p = products[i.ProductId];
            return new OrderItem { ProductId = p.Id, ProductName = p.Name, Emoji = p.Emoji, UnitPrice = p.Price, Quantity = i.Quantity, LineTotal = p.Price * i.Quantity };
        }).ToList();

        var amounts = OrderFinancials.Calculate(items.Sum(i => i.LineTotal), deliveryFee, 0, rule);
        var order = new Order
        {
            Code = CodeGen.OrderCode(now),
            CustomerId = uid,
            StoreId = store.Id,
            Status = OrderStatus.Pending,
            RecipientName = input.RecipientName.Trim(),
            RecipientPhone = input.RecipientPhone.Trim(),
            DeliveryAddress = AddressText.Compose(street, ward.FullName, province.FullName),
            DeliveryAddressDetail = input.AddressDetail.Clean(),
            DeliveryWardCode = ward.Code,
            DeliveryProvinceCode = province.Code,
            DeliveryNote = input.DeliveryNote.Clean(),
            PickupStoreName = store.Name,
            PickupAddress = store.Address,
            PickupPhone = store.PhoneNumber,
            PickupLatitude = store.Latitude,
            PickupLongitude = store.Longitude,
            DropoffLatitude = dropoff?.Latitude,
            DropoffLongitude = dropoff?.Longitude,
            DistanceMeters = meters,
            DistanceSource = distanceSource,
            IdempotencyKey = key,
            PlacedAtUtc = now,
            Items = items,
        };
        OrderFinancials.Apply(order, amounts);
        order.Payment = new Payment { Method = PaymentMethod.CashOnDelivery, Amount = order.GrandTotal, Status = PaymentStatus.Pending };
        order.History.Add(new OrderStatusHistory { NewStatus = OrderStatus.Pending, ChangedByUserId = uid, ChangedAtUtc = now, Reason = "Khách hàng đặt đơn" });
        db.Orders.Add(order);

        // The checked-out store cart is removed; the customer's other store carts stay untouched.
        db.Carts.Remove(cart);

        if (input.SaveAddress && !await db.Addresses.AnyAsync(a => a.UserId == uid && a.Street == street && a.WardCode == ward.Code, ct))
        {
            var hasAny = await db.Addresses.AnyAsync(a => a.UserId == uid, ct);
            db.Addresses.Add(new Address { UserId = uid, Label = "Đã lưu", RecipientName = order.RecipientName, PhoneNumber = order.RecipientPhone, Street = street, WardCode = ward.Code, Ward = ward.FullName, ProvinceCode = province.Code, City = province.FullName, Note = order.DeliveryAddressDetail, IsDefault = !hasAny, Latitude = dropoff?.Latitude, Longitude = dropoff?.Longitude });
        }

        db.Audit(uid, "Order.Placed", nameof(Order), order.Code, $"store={store.Id} total={order.GrandTotal}");
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent request with the same idempotency key won the race.
            if (await IsDuplicateAsync(uid, key, ct) is { } dup) return dup;
            throw;
        }

        await notifier.OrderStatusChangedAsync(order.Id, ct);
        return new PlaceOrderResult(order.Id, order.Code, false);
    }

    private async Task<PlaceOrderResult?> IsDuplicateAsync(string uid, string key, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var o = await db.Orders.AsNoTracking().Where(x => x.CustomerId == uid && x.IdempotencyKey == key).Select(x => new { x.Id, x.Code }).FirstOrDefaultAsync(ct);
        return o is null ? null : new PlaceOrderResult(o.Id, o.Code, true);
    }

    public async Task<PagedResult<OrderSummaryDto>> GetMyOrdersAsync(int page = 1, int pageSize = 10, CancellationToken ct = default)
    {
        var uid = (await users.GetAsync(ct)).RequireUserId();
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Orders.AsNoTracking().Where(o => o.CustomerId == uid).ToSummaries(db).ToPagedAsync(page, pageSize, ct);
    }

    public async Task<OrderDetailDto?> GetMyOrderAsync(string code, CancellationToken ct = default)
    {
        var uid = (await users.GetAsync(ct)).RequireUserId();
        await using var db = await dbf.CreateDbContextAsync(ct);
        var id = await db.Orders.AsNoTracking().Where(o => o.Code == code && o.CustomerId == uid).Select(o => (int?)o.Id).FirstOrDefaultAsync(ct);
        return id is null ? null : await OrderQueries.LoadDetailAsync(db, id.Value, OrderActor.Customer, ct);
    }

    public async Task CancelAsync(string code, string reason, CancellationToken ct = default)
    {
        var uid = (await users.GetAsync(ct)).RequireUserId();
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await OrderWorkflow.WithWorkflowGraph(db.Orders).FirstOrDefaultAsync(o => o.Code == code && o.CustomerId == uid, ct)
            ?? throw new NotFoundException("Không tìm thấy đơn hàng.");
        await workflow.ApplyAsync(db, order, OrderStatus.Cancelled, OrderActor.Customer, uid, reason, ct);
        await db.SaveAsync(ct);
        await tx.CommitAsync(ct);
        await notifier.OrderStatusChangedAsync(order.Id, ct);
    }
}

public sealed class AddressService(IDbContextFactory<AppDbContext> dbf, ICurrentUserAccessor users, IAdministrativeUnits units) : IAddressService
{
    public async Task<IReadOnlyList<AddressDto>> GetMineAsync(CancellationToken ct = default)
    {
        var uid = (await users.GetAsync(ct)).RequireUserId();
        await using var db = await dbf.CreateDbContextAsync(ct);
        var list = await db.Addresses.AsNoTracking().Where(a => a.UserId == uid).OrderByDescending(a => a.IsDefault).ThenBy(a => a.Id).ToListAsync(ct);
        return list.Select(a => new AddressDto(a.Id, a.Label, a.RecipientName, a.PhoneNumber, a.FullText, a.Note, a.IsDefault)
        {
            Street = a.Street,
            // Only expose codes that still exist in the catalog.
            ProvinceCode = units.FindProvince(a.ProvinceCode)?.Code,
            WardCode = units.FindWard(a.WardCode)?.Code,
            Latitude = a.Latitude,
            Longitude = a.Longitude,
        }).ToList();
    }

    public async Task<AddressInput?> GetForEditAsync(int id, CancellationToken ct = default)
    {
        var uid = (await users.GetAsync(ct)).RequireUserId();
        await using var db = await dbf.CreateDbContextAsync(ct);
        var a = await db.Addresses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct);
        return a is null ? null : new AddressInput
        {
            Id = a.Id, Label = a.Label, RecipientName = a.RecipientName, PhoneNumber = a.PhoneNumber, Street = a.Street,
            ProvinceCode = a.ProvinceCode, WardCode = a.WardCode, Note = a.Note, IsDefault = a.IsDefault,
            Latitude = a.Latitude, Longitude = a.Longitude,
        };
    }

    public async Task<int> SaveAsync(AddressInput input, CancellationToken ct = default)
    {
        var uid = (await users.GetAsync(ct)).RequireUserId();
        InputValidation.Validate(input);
        var (province, ward) = units.Resolve(input.ProvinceCode, input.WardCode);
        await using var db = await dbf.CreateDbContextAsync(ct);
        Address a;
        if (input.Id == 0)
        {
            a = new Address { UserId = uid, RecipientName = "", PhoneNumber = "", Street = "" };
            db.Addresses.Add(a);
        }
        else
        {
            a = await db.Addresses.FirstOrDefaultAsync(x => x.Id == input.Id && x.UserId == uid, ct) ?? throw new NotFoundException();
        }
        a.Label = input.Label.Trim();
        a.RecipientName = input.RecipientName.Trim();
        a.PhoneNumber = input.PhoneNumber.Trim();
        a.Street = input.Street.Trim();
        a.WardCode = ward.Code;
        a.Ward = ward.FullName;
        a.ProvinceCode = province.Code;
        a.City = province.FullName;
        a.Note = input.Note.Clean();
        var point = GeoPoint.From(input.Latitude, input.Longitude);
        a.Latitude = point?.Latitude;
        a.Longitude = point?.Longitude;
        var isFirst = !await db.Addresses.AnyAsync(x => x.UserId == uid && x.Id != a.Id, ct);
        if (input.IsDefault || isFirst)
        {
            await db.Addresses.Where(x => x.UserId == uid && x.Id != a.Id).ForEachAsync(x => x.IsDefault = false, ct);
            a.IsDefault = true;
        }
        await db.SaveAsync(ct);
        return a.Id;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var uid = (await users.GetAsync(ct)).RequireUserId();
        await using var db = await dbf.CreateDbContextAsync(ct);
        var a = await db.Addresses.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct) ?? throw new NotFoundException();
        db.Addresses.Remove(a);
        if (a.IsDefault)
        {
            var next = await db.Addresses.Where(x => x.UserId == uid && x.Id != id).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
            if (next is not null) next.IsDefault = true;
        }
        await db.SaveAsync(ct);
    }

    public async Task SetDefaultAsync(int id, CancellationToken ct = default)
    {
        var uid = (await users.GetAsync(ct)).RequireUserId();
        await using var db = await dbf.CreateDbContextAsync(ct);
        var list = await db.Addresses.Where(x => x.UserId == uid).ToListAsync(ct);
        if (list.All(x => x.Id != id)) throw new NotFoundException();
        foreach (var x in list) x.IsDefault = x.Id == id;
        await db.SaveAsync(ct);
    }
}

public sealed class ProfileService(IDbContextFactory<AppDbContext> dbf, ICurrentUserAccessor users) : IProfileService
{
    public async Task<ProfileDto> GetMineAsync(CancellationToken ct = default)
    {
        var me = await users.GetAsync(ct);
        var uid = me.RequireUserId();
        await using var db = await dbf.CreateDbContextAsync(ct);
        var email = await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.Email).FirstOrDefaultAsync(ct) ?? "";
        var p = await db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid, ct);
        return new ProfileDto(uid, email, p?.FullName ?? email, p?.PhoneNumber, me.Roles.ToList());
    }

    public async Task UpdateMineAsync(ProfileInput input, CancellationToken ct = default)
    {
        var me = await users.GetAsync(ct);
        var uid = me.RequireUserId();
        InputValidation.Validate(input);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var p = await db.UserProfiles.FirstOrDefaultAsync(x => x.UserId == uid, ct);
        if (p is null) db.UserProfiles.Add(p = new UserProfile { UserId = uid, FullName = "" });
        p.FullName = input.FullName.Trim();
        var phone = PhoneNumbers.Normalize(input.PhoneNumber);
        if (input.PhoneNumber.Clean() is not null && phone is null) throw new DomainException("Số điện thoại không hợp lệ.");

        // Customer accounts log in with their phone number: keep the login name in sync.
        var user = await db.Users.FirstAsync(u => u.Id == uid, ct);
        var phoneLogin = user.UserName is not null && !user.UserName.Contains('@');
        if (phoneLogin && phone != user.UserName)
        {
            if (phone is null) throw new DomainException("Số điện thoại là tên đăng nhập của bạn, không thể để trống.");
            if (await db.Users.AnyAsync(u => u.NormalizedUserName == phone && u.Id != uid, ct))
                throw new DomainException("Số điện thoại này đã được tài khoản khác sử dụng.");
            user.UserName = phone;
            user.NormalizedUserName = phone.ToUpperInvariant();
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
            db.Audit(uid, "Account.LoginPhoneChanged", "User", uid, phone);
        }
        user.PhoneNumber = phone;
        p.PhoneNumber = phone;
        db.Audit(uid, "Profile.Updated", nameof(UserProfile), uid);
        await db.SaveAsync(ct);
    }
}

public sealed class NotificationService(IDbContextFactory<AppDbContext> dbf, ICurrentUserAccessor users) : INotificationService
{
    public async Task<IReadOnlyList<NotificationDto>> GetMineAsync(int take = 30, CancellationToken ct = default)
    {
        var me = await users.GetAsync(ct);
        if (!me.IsAuthenticated) return [];
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Notifications.AsNoTracking().Where(n => n.UserId == me.UserId)
            .OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id).Take(Math.Clamp(take, 1, 100))
            .Select(n => new NotificationDto(n.Id, n.Type, n.Title, n.Message, n.Link, n.IsRead, n.CreatedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<int> GetUnreadCountAsync(CancellationToken ct = default)
    {
        var me = await users.GetAsync(ct);
        if (!me.IsAuthenticated) return 0;
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.Notifications.CountAsync(n => n.UserId == me.UserId && !n.IsRead, ct);
    }

    public async Task MarkReadAsync(int id, CancellationToken ct = default)
    {
        var uid = (await users.GetAsync(ct)).RequireUserId();
        await using var db = await dbf.CreateDbContextAsync(ct);
        await db.Notifications.Where(n => n.Id == id && n.UserId == uid).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);
    }

    public async Task MarkAllReadAsync(CancellationToken ct = default)
    {
        var uid = (await users.GetAsync(ct)).RequireUserId();
        await using var db = await dbf.CreateDbContextAsync(ct);
        await db.Notifications.Where(n => n.UserId == uid && !n.IsRead).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);
    }
}
