using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sky17.Domain;

namespace Sky17.Infrastructure.Data;

public sealed class SeedOptions
{
    /// <summary>Create demo stores/users/orders. Development only.</summary>
    public bool DemoData { get; set; }
    /// <summary>Password for all demo accounts. Must come from configuration (appsettings.Development.json / user-secrets), never code.</summary>
    public string? DemoPassword { get; set; }
    /// <summary>Optional bootstrap admin for real deployments (set via environment variables).</summary>
    public string? AdminEmail { get; set; }
    public string? AdminPassword { get; set; }
}

public static class DbSeeder
{
    public static async Task MigrateAndSeedAsync(IServiceProvider services, SeedOptions options, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Sky17.Seed");
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);

        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
            if (!await roleManager.RoleExistsAsync(role)) await roleManager.CreateAsync(new IdentityRole(role));

        await EnsureReferenceDataAsync(db, ct);

        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        await EnsureCustomerPhoneLoginsAsync(userManager, db, logger, ct);
        if (!string.IsNullOrWhiteSpace(options.AdminEmail) && !string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            await EnsureUserAsync(userManager, db, options.AdminEmail, options.AdminPassword, "Quản trị viên", "0900000000", Roles.Admin);
            logger.LogInformation("Bootstrap admin ensured for {Email}", options.AdminEmail);
        }

        if (options.DemoData)
        {
            if (string.IsNullOrWhiteSpace(options.DemoPassword))
            {
                logger.LogWarning("Seed:DemoData is enabled but Seed:DemoPassword is empty; demo data skipped.");
                return;
            }
            if (!await db.Stores.AnyAsync(ct))
            {
                await SeedDemoAsync(userManager, db, options.DemoPassword, ct);
                logger.LogInformation("Demo data seeded.");
            }
        }
    }

    private static async Task EnsureReferenceDataAsync(AppDbContext db, CancellationToken ct)
    {
        if (!await db.DeliveryRateRules.AnyAsync(ct))
            db.DeliveryRateRules.Add(new DeliveryRateRule { Name = "Bảng giá mặc định", BaseFee = 15_000, BaseDistanceMeters = 2_000, FeePerKm = 5_000, PlatformFeeBps = 1_000, DriverShareBps = 8_000 });

        var defaults = new Dictionary<string, string>
        {
            [SettingKeys.BrandName] = "Ship Hoả Tốc Sky 17",
            [SettingKeys.ServiceAvailable] = "true",
            [SettingKeys.ServiceArea] = "Bình Dương",
            [SettingKeys.SupportPhone] = "1900 1717",
            [SettingKeys.MaxActiveDeliveriesPerDriver] = "2",
        };
        foreach (var (k, v) in defaults)
            if (!await db.SystemSettings.AnyAsync(s => s.Key == k, ct))
                db.SystemSettings.Add(new SystemSetting { Key = k, Value = v, UpdatedAtUtc = DateTime.UtcNow });

        if (!await db.Categories.AnyAsync(c => c.StoreId == null, ct))
        {
            db.Categories.AddRange(
                new Category { Name = "Đồ ăn", Emoji = "🍔", SortOrder = 1 },
                new Category { Name = "Đồ uống", Emoji = "🧋", SortOrder = 2 },
                new Category { Name = "Cơm & Mì", Emoji = "🍜", SortOrder = 3 },
                new Category { Name = "Bánh ngọt", Emoji = "🥐", SortOrder = 4 });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Customers log in with their phone number. Converts existing customer-only accounts whose login name
    /// is still an e-mail (created before this rule) when the phone number is valid and not taken.
    /// </summary>
    private static async Task EnsureCustomerPhoneLoginsAsync(UserManager<ApplicationUser> um, AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var customerRoleId = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (customerRoleId is null) return;
        var candidates = await db.Users.AsNoTracking()
            .Where(u => u.UserName!.Contains("@")
                        && db.UserRoles.Any(r => r.UserId == u.Id && r.RoleId == customerRoleId)
                        && !db.UserRoles.Any(r => r.UserId == u.Id && r.RoleId != customerRoleId))
            .Select(u => new { u.Id, u.PhoneNumber, ProfilePhone = db.UserProfiles.Where(p => p.UserId == u.Id).Select(p => p.PhoneNumber).FirstOrDefault() })
            .ToListAsync(ct);
        foreach (var c in candidates)
        {
            var phone = PhoneNumbers.Normalize(c.PhoneNumber ?? c.ProfilePhone);
            if (phone is null || await um.FindByNameAsync(phone) is not null)
            {
                logger.LogWarning("Customer {UserId} keeps an e-mail login: phone missing or already used.", c.Id);
                continue;
            }
            var user = await um.FindByIdAsync(c.Id);
            if (user is null) continue;
            user.PhoneNumber = phone;
            var r = await um.SetUserNameAsync(user, phone);
            if (r.Succeeded) logger.LogInformation("Customer {UserId} now logs in with phone {Phone}.", c.Id, phone);
        }
    }

    private static async Task<ApplicationUser> EnsureUserAsync(UserManager<ApplicationUser> um, AppDbContext db, string email, string password, string name, string phone, params string[] roles)
    {
        var user = await um.FindByEmailAsync(email);
        if (user is null)
        {
            user = new ApplicationUser { UserName = roles.Length == 1 && roles[0] == Roles.Customer ? PhoneNumbers.Normalize(phone) : email, Email = email, EmailConfirmed = true, PhoneNumber = phone, IsActive = true, LockoutEnabled = true };
            var result = await um.CreateAsync(user, password);
            if (!result.Succeeded) throw new InvalidOperationException("Seed user failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        }
        foreach (var r in roles)
            if (!await um.IsInRoleAsync(user, r)) await um.AddToRoleAsync(user, r);
        if (!await db.UserProfiles.AnyAsync(p => p.UserId == user.Id))
        {
            db.UserProfiles.Add(new UserProfile { UserId = user.Id, FullName = name, PhoneNumber = phone });
            await db.SaveChangesAsync();
        }
        return user;
    }

    private static async Task SeedDemoAsync(UserManager<ApplicationUser> um, AppDbContext db, string pw, CancellationToken ct)
    {
        await EnsureUserAsync(um, db, "admin@sky17.local", pw, "Sky 17 Admin", "0901000001", Roles.Admin);
        var ownerCoffee = await EnsureUserAsync(um, db, "owner.skycoffee@sky17.local", pw, "Trần Thu Hà", "0902000001", Roles.Seller);
        var staffCoffee = await EnsureUserAsync(um, db, "staff.skycoffee@sky17.local", pw, "Phạm Quốc Bảo", "0902000002", Roles.Seller);
        var ownerBep = await EnsureUserAsync(um, db, "owner.bepnhamay@sky17.local", pw, "Lê Minh Châu", "0902000003", Roles.Seller);
        var ownerBakery = await EnsureUserAsync(um, db, "owner.maybakery@sky17.local", pw, "Đỗ Thanh Tâm", "0902000004", Roles.Seller);
        var d1 = await EnsureUserAsync(um, db, "driver.lehoang@sky17.local", pw, "Lê Hoàng", "0903000001", Roles.Driver);
        var d2 = await EnsureUserAsync(um, db, "driver.vuanh@sky17.local", pw, "Vũ Anh", "0903000002", Roles.Driver);
        var d3 = await EnsureUserAsync(um, db, "driver.anhkhoa@sky17.local", pw, "Nguyễn Anh Khoa", "0903000003", Roles.Driver);
        var d4 = await EnsureUserAsync(um, db, "driver.minhtri@sky17.local", pw, "Phan Minh Trí", "0903000004", Roles.Driver);
        var c1 = await EnsureUserAsync(um, db, "customer.minhanh@sky17.local", pw, "Nguyễn Minh Anh", "0904000001", Roles.Customer);
        var c2 = await EnsureUserAsync(um, db, "customer.giahuy@sky17.local", pw, "Trần Gia Huy", "0904000002", Roles.Customer);

        var now = DateTime.UtcNow;
        db.DriverProfiles.AddRange(
            new DriverProfile { UserId = d1.Id, LicensePlate = "61A1-12345", VehicleType = "Xe máy", Status = DriverStatus.Active, IsOnline = true, ApprovedAtUtc = now.AddDays(-30), LastLatitude = 10.9741, LastLongitude = 106.6602, LastLocationAtUtc = now },
            new DriverProfile { UserId = d2.Id, LicensePlate = "59H1-99887", VehicleType = "Xe máy", Status = DriverStatus.Active, IsOnline = true, ApprovedAtUtc = now.AddDays(-20), LastLatitude = 10.9731, LastLongitude = 106.6579, LastLocationAtUtc = now },
            new DriverProfile { UserId = d4.Id, LicensePlate = "61C1-55555", VehicleType = "Xe máy", Status = DriverStatus.Active, IsOnline = true, ApprovedAtUtc = now.AddDays(-10), LastLatitude = 10.9822, LastLongitude = 106.6510, LastLocationAtUtc = now },
            new DriverProfile { UserId = d3.Id, LicensePlate = "61B2-24680", VehicleType = "Xe máy", Status = DriverStatus.PendingApproval });

        db.Addresses.AddRange(
            new Address { UserId = c1.Id, Label = "Nhà", RecipientName = "Nguyễn Minh Anh", PhoneNumber = "0904000001", Street = "118 Lê Hồng Phong", Ward = "Phú Hòa", District = "Thủ Dầu Một", City = "Bình Dương", IsDefault = true, Latitude = 10.9757, Longitude = 106.6688 },
            new Address { UserId = c1.Id, Label = "Công ty", RecipientName = "Nguyễn Minh Anh", PhoneNumber = "0904000001", Street = "Tòa Becamex, 230 Đại lộ Bình Dương", District = "Thủ Dầu Một", City = "Bình Dương", Latitude = 10.9845, Longitude = 106.6703 },
            new Address { UserId = c2.Id, Label = "Nhà", RecipientName = "Trần Gia Huy", PhoneNumber = "0904000002", Street = "10 Phú Lợi", Ward = "Phú Lợi", District = "Thủ Dầu Một", City = "Bình Dương", IsDefault = true, Latitude = 10.9932, Longitude = 106.6741 });

        var cats = await db.Categories.Where(c => c.StoreId == null).ToDictionaryAsync(c => c.Name, ct);
        var full = new TimeOnly(0, 0); // OpenTime == CloseTime means open 24h (demo convenience)

        var coffee = new Store { Name = "Sky Coffee", Emoji = "☕", Tagline = "Đồ uống · Ăn nhanh", Description = "Cà phê, đồ uống và đồ ăn nhanh tươi ngon mỗi ngày.", PhoneNumber = "0274 3888 001", Address = "21 Nguyễn Huệ, Phú Cường, Thủ Dầu Một, Bình Dương", Status = StoreStatus.Active, ApprovedAtUtc = now.AddDays(-40), OpenTime = full, CloseTime = full, Latitude = 10.9804, Longitude = 106.6531 };
        coffee.Members.Add(new StoreMember { UserId = ownerCoffee.Id, Role = StoreMemberRole.Owner });
        coffee.Members.Add(new StoreMember { UserId = staffCoffee.Id, Role = StoreMemberRole.Staff });
        var bep = new Store { Name = "Bếp Nhà Mây", Emoji = "🍜", Tagline = "Cơm & Mì", Description = "Cơm nhà, phở và mì nóng hổi.", PhoneNumber = "0274 3888 002", Address = "85 Cách Mạng Tháng Tám, Thủ Dầu Một, Bình Dương", Status = StoreStatus.Active, ApprovedAtUtc = now.AddDays(-35), OpenTime = full, CloseTime = full, Latitude = 10.9729, Longitude = 106.6577 };
        bep.Members.Add(new StoreMember { UserId = ownerBep.Id, Role = StoreMemberRole.Owner });
        var bakery = new Store { Name = "Mây Bakery", Emoji = "🥐", Tagline = "Bánh ngọt · Bánh mì", Description = "Bánh nướng mới mỗi sáng.", PhoneNumber = "0274 3888 003", Address = "12 Yersin, Thủ Dầu Một, Bình Dương", Status = StoreStatus.PendingApproval, OpenTime = new(6, 0), CloseTime = new(21, 0), Latitude = 10.9846, Longitude = 106.6563 };
        bakery.Members.Add(new StoreMember { UserId = ownerBakery.Id, Role = StoreMemberRole.Owner });

        Product P(Store s, string name, long price, int stock, string emoji, string cat, string? desc = null)
        {
            var p = new Product { Store = s, Name = name, Price = price, Stock = stock, Emoji = emoji, CategoryId = cats[cat].Id, Description = desc };
            s.Products.Add(p);
            return p;
        }
        var cfSua = P(coffee, "Cà phê sữa đá", 29_000, 42, "☕", "Đồ uống", "Cà phê phin truyền thống với sữa đặc.");
        var traDao = P(coffee, "Trà đào cam sả", 35_000, 28, "🍑", "Đồ uống", "Trà đào thơm mát, cam tươi và sả.");
        var matcha = P(coffee, "Matcha Latte", 49_000, 15, "🍵", "Đồ uống");
        var banhMi = P(coffee, "Bánh mì đặc biệt", 32_000, 35, "🥖", "Đồ ăn", "Pate, chả lụa, thịt nguội, rau thơm.");
        var burger = P(coffee, "Burger bò phô mai", 69_000, 20, "🍔", "Đồ ăn");
        var comGa = P(bep, "Cơm gà xối mỡ", 55_000, 18, "🍗", "Cơm & Mì");
        var pho = P(bep, "Phở bò tái", 60_000, 25, "🍜", "Cơm & Mì", "Nước dùng hầm xương 12 tiếng.");
        var miXao = P(bep, "Mì xào hải sản", 65_000, 15, "🍝", "Cơm & Mì");
        var traTac = P(bep, "Trà tắc", 15_000, 50, "🍋", "Đồ uống");
        P(bakery, "Croissant bơ", 28_000, 30, "🥐", "Bánh ngọt");
        P(bakery, "Bánh kem dâu", 45_000, 12, "🍰", "Bánh ngọt");

        db.Stores.AddRange(coffee, bep, bakery);
        await db.SaveChangesAsync(ct);

        var rule = await db.DeliveryRateRules.FirstAsync(r => r.IsActive, ct);
        var c1Addr = "118 Lê Hồng Phong, Phú Hòa, Thủ Dầu Một, Bình Dương";
        var c2Addr = "10 Phú Lợi, Phú Lợi, Thủ Dầu Một, Bình Dương";

        void AddOrder(ApplicationUser cust, string name, string phone, string addr, Store s, (Product P, int Q)[] lines, double daysAgo, OrderStatus status, ApplicationUser? driver, string? reason = null)
        {
            var placed = now.AddDays(-daysAgo);
            var o = new Order
            {
                Code = $"SKY{placed:yyMMdd}-{Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}",
                CustomerId = cust.Id, StoreId = s.Id, RecipientName = name, RecipientPhone = phone, DeliveryAddress = addr,
                PickupStoreName = s.Name, PickupAddress = s.Address, PickupPhone = s.PhoneNumber,
                PickupLatitude = s.Latitude, PickupLongitude = s.Longitude,
                DropoffLatitude = addr == c1Addr ? 10.9757 : 10.9932, DropoffLongitude = addr == c1Addr ? 106.6688 : 106.6741,
                IdempotencyKey = Guid.NewGuid().ToString("N"), PlacedAtUtc = placed, CreatedAtUtc = placed,
                Items = lines.Select(l => new OrderItem { ProductId = l.P.Id, ProductName = l.P.Name, Emoji = l.P.Emoji, UnitPrice = l.P.Price, Quantity = l.Q, LineTotal = l.P.Price * l.Q }).ToList(),
            };
            OrderFinancials.Apply(o, OrderFinancials.Calculate(o.Items.Sum(i => i.LineTotal), DeliveryPricing.CalculateFee(rule, null), 0, rule));
            o.Payment = new Payment { Amount = o.GrandTotal };

            OrderStatus[] path = status switch
            {
                OrderStatus.Cancelled => [OrderStatus.Pending, OrderStatus.Cancelled],
                _ => [OrderStatus.Pending, OrderStatus.Confirmed, OrderStatus.Assigned, OrderStatus.ArrivedAtStore, OrderStatus.Delivering, OrderStatus.ArrivedAtCustomer, OrderStatus.Delivered],
            };
            var steps = path.Take(Array.IndexOf(path, status) + 1).ToList();
            OrderStatus? prev = null;
            var t = placed;
            foreach (var st in steps)
            {
                var by = st switch
                {
                    OrderStatus.Pending or OrderStatus.Cancelled => cust.Id,
                    OrderStatus.Confirmed => s.Members.First().UserId,
                    _ => driver?.Id,
                };
                o.History.Add(new OrderStatusHistory { PreviousStatus = prev, NewStatus = st, ChangedAtUtc = t, ChangedByUserId = by, Reason = st == OrderStatus.Cancelled ? reason : null });
                prev = st;
                t = t.AddMinutes(6);
            }
            o.Status = status;
            if (steps.Contains(OrderStatus.Confirmed))
            {
                o.ConfirmedAtUtc = placed.AddMinutes(6);
                // Searching orders restart their driver search at startup so the dispatcher picks them up.
                o.DispatchStartedAtUtc = status == OrderStatus.Confirmed ? now : placed.AddMinutes(6);
                o.PreparationState = steps.Contains(OrderStatus.Delivering) ? PreparationState.Ready : PreparationState.Preparing;
                if (o.PreparationState == PreparationState.Ready) o.FoodReadyAtUtc = placed.AddMinutes(20);
            }
            if (status == OrderStatus.Cancelled) { o.CancelReason = reason; o.Payment.Status = PaymentStatus.Cancelled; }
            if (driver is not null && steps.Contains(OrderStatus.Assigned))
            {
                o.DriverId = driver.Id;
                var delivered = status == OrderStatus.Delivered;
                o.Assignments.Add(new DeliveryAssignment
                {
                    DriverId = driver.Id, AssignedAtUtc = placed.AddMinutes(24), IsActive = !delivered,
                    Status = status switch { OrderStatus.Delivered => AssignmentStatus.Delivered, OrderStatus.Delivering => AssignmentStatus.Delivering, OrderStatus.ArrivedAtStore => AssignmentStatus.ArrivedAtStore, OrderStatus.ArrivedAtCustomer => AssignmentStatus.ArrivedAtCustomer, _ => AssignmentStatus.Assigned },
                    PickedUpAtUtc = steps.Contains(OrderStatus.Delivering) ? placed.AddMinutes(30) : null,
                    CompletedAtUtc = delivered ? t : null,
                });
                if (delivered)
                {
                    o.DeliveredAtUtc = t;
                    o.CollectedCodAmount = o.GrandTotal;
                    o.Payment.Status = PaymentStatus.Collected;
                    o.Payment.CollectedAtUtc = t;
                    o.Payment.CollectedByUserId = driver.Id;
                }
            }
            db.Orders.Add(o);
        }

        AddOrder(c1, "Nguyễn Minh Anh", "0904000001", c1Addr, coffee, [(cfSua, 2), (banhMi, 1)], 6.2, OrderStatus.Delivered, d1);
        AddOrder(c2, "Trần Gia Huy", "0904000002", c2Addr, bep, [(pho, 1), (traTac, 2)], 5.4, OrderStatus.Delivered, d2);
        AddOrder(c1, "Nguyễn Minh Anh", "0904000001", c1Addr, bep, [(comGa, 2)], 4.6, OrderStatus.Delivered, d1);
        AddOrder(c2, "Trần Gia Huy", "0904000002", c2Addr, coffee, [(traDao, 2), (burger, 1)], 3.8, OrderStatus.Delivered, d1);
        AddOrder(c1, "Nguyễn Minh Anh", "0904000001", c1Addr, coffee, [(matcha, 1)], 3.1, OrderStatus.Cancelled, null, "Khách đổi ý");
        AddOrder(c2, "Trần Gia Huy", "0904000002", c2Addr, bep, [(miXao, 1), (traTac, 1)], 2.5, OrderStatus.Delivered, d2);
        AddOrder(c1, "Nguyễn Minh Anh", "0904000001", c1Addr, coffee, [(cfSua, 3), (matcha, 1)], 1.7, OrderStatus.Delivered, d1);
        AddOrder(c2, "Trần Gia Huy", "0904000002", c2Addr, coffee, [(banhMi, 2), (cfSua, 1)], 0.9, OrderStatus.Delivered, d2);
        AddOrder(c1, "Nguyễn Minh Anh", "0904000001", c1Addr, bep, [(pho, 2)], 0.08, OrderStatus.Delivering, d1);
        AddOrder(c2, "Trần Gia Huy", "0904000002", c2Addr, coffee, [(traDao, 1), (burger, 1)], 0.02, OrderStatus.Confirmed, null);
        AddOrder(c1, "Nguyễn Minh Anh", "0904000001", c1Addr, bep, [(comGa, 1), (traTac, 1)], 0.04, OrderStatus.ArrivedAtStore, d2);
        AddOrder(c2, "Trần Gia Huy", "0904000002", c2Addr, coffee, [(cfSua, 2)], 0.0005, OrderStatus.Pending, null);
        await db.SaveChangesAsync(ct);

        // COD ledger for delivered demo orders.
        var delivered = await db.Orders.Where(o => o.Status == OrderStatus.Delivered).ToListAsync(ct);
        foreach (var o in delivered)
            db.CodTransactions.Add(new CodTransaction { OrderId = o.Id, DriverId = o.DriverId!, Type = CodTransactionType.CollectedFromCustomer, Amount = o.GrandTotal, Note = $"Thu hộ đơn {o.Code}", CreatedAtUtc = o.DeliveredAtUtc!.Value });
        db.AuditLogs.Add(new AuditLog { Action = "System.Seeded", EntityType = "System", Details = "Dữ liệu mẫu môi trường phát triển", CreatedAtUtc = now });
        await db.SaveChangesAsync(ct);
    }
}
