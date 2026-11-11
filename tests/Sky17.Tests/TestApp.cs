using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure;
using Sky17.Infrastructure.Data;

namespace Sky17.Tests;

public sealed class TestUserAccessor : ICurrentUserAccessor
{
    public CurrentUser User { get; set; } = CurrentUser.Anonymous;
    public Task<CurrentUser> GetAsync(CancellationToken ct = default) => Task.FromResult(User);
}

/// <summary>
/// Real service graph over a temporary SQLite file database with migrations applied.
/// Each test gets an isolated database.
/// </summary>
public sealed class TestApp : IAsyncDisposable
{
    private readonly string dir;
    public ServiceProvider Services { get; }

    private TestApp(string dir, ServiceProvider services)
    {
        this.dir = dir;
        Services = services;
    }

    public static async Task<TestApp> CreateAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sky17-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var cs = $"Data Source={Path.Combine(dir, "test.db")};Default Timeout=30";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<FakeClock>();
        services.AddSingleton<IClock>(sp => sp.GetRequiredService<FakeClock>());
        services.AddSingleton<FakeRoutingService>();
        services.AddSingleton<IRoutingService>(sp => sp.GetRequiredService<FakeRoutingService>());
        services.AddSky17Infrastructure(cs, Path.Combine(dir, "wwwroot"));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 8;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireUppercase = false;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>();
        services.AddScoped<TestUserAccessor>();
        services.AddScoped<ICurrentUserAccessor>(sp => sp.GetRequiredService<TestUserAccessor>());
        var provider = services.BuildServiceProvider();
        await DbSeeder.MigrateAndSeedAsync(provider, new SeedOptions());
        return new TestApp(dir, provider);
    }

    /// <summary>A DI scope acting as the given user.</summary>
    public AsUser As(string? userId, params string[] roles)
    {
        var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TestUserAccessor>().User = userId is null ? CurrentUser.Anonymous : new CurrentUser(userId, roles);
        return new AsUser(scope);
    }

    public AsUser As(TestUser u) => As(u.Id, u.Roles);
    public AsUser Anonymous() => As((string?)null);

    public AppDbContext Db() => Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();

    public async Task<TestUser> CreateUserAsync(string name, params string[] roles)
    {
        using var scope = Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"user-{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var r = await um.CreateAsync(user, "Password123");
        Assert.True(r.Succeeded, string.Join(";", r.Errors.Select(e => e.Description)));
        foreach (var role in roles) await um.AddToRoleAsync(user, role);
        await using var db = Db();
        db.UserProfiles.Add(new UserProfile { UserId = user.Id, FullName = name, PhoneNumber = "0900000000" });
        await db.SaveChangesAsync();
        return new TestUser(user.Id, email, roles);
    }

    /// <summary>An approved store with an owner and two products.</summary>
    public async Task<(TestUser Owner, int StoreId, int ProductA, int ProductB)> CreateStoreAsync(string name = "Test Store", long priceA = 29_000, long priceB = 32_000, int stock = 10)
    {
        var owner = await CreateUserAsync(name + " Owner", Roles.Seller);
        await using var db = Db();
        var store = new Store { Name = name, Address = "1 Test St", PhoneNumber = "0911111111", Status = StoreStatus.Active, OpenTime = new(0, 0), CloseTime = new(0, 0), Latitude = 10.9804, Longitude = 106.6531 };
        store.Members.Add(new StoreMember { UserId = owner.Id, Role = StoreMemberRole.Owner });
        var a = new Product { Name = "Cà phê sữa đá", Price = priceA, Stock = stock };
        var b = new Product { Name = "Bánh mì", Price = priceB, Stock = stock };
        store.Products.Add(a);
        store.Products.Add(b);
        db.Stores.Add(store);
        await db.SaveChangesAsync();
        return (owner, store.Id, a.Id, b.Id);
    }

    /// <summary>Store pin used by <see cref="CreateStoreAsync"/>.</summary>
    public static readonly GeoPoint StorePoint = new(10.9804, 106.6531);

    /// <summary>Driver with a fresh GPS position <paramref name="metersNorth"/> meters north of the test store.</summary>
    public async Task<TestUser> CreateDriverAsync(string name, bool approved = true, bool online = true, int metersNorth = 300, bool freshLocation = true)
    {
        var u = await CreateUserAsync(name, Roles.Driver);
        await using var db = Db();
        db.DriverProfiles.Add(new DriverProfile
        {
            UserId = u.Id, LicensePlate = "59X1-" + Random.Shared.Next(10000, 99999),
            Status = approved ? DriverStatus.Active : DriverStatus.PendingApproval, IsOnline = online,
            LastLatitude = StorePoint.Latitude + metersNorth / 111_320.0, LastLongitude = StorePoint.Longitude,
            LastLocationAtUtc = freshLocation ? Clock.UtcNow : Clock.UtcNow.AddHours(-2),
        });
        await db.SaveChangesAsync();
        return u;
    }

    public FakeClock Clock => Services.GetRequiredService<FakeClock>();
    public IDispatchService Dispatch => Services.GetRequiredService<IDispatchService>();

    /// <summary>Runs dispatch; the driver must receive the offer for this order and accepts it.</summary>
    public async Task AcceptViaDispatchAsync(TestUser driver, int orderId)
    {
        await Dispatch.RunCycleAsync();
        using var d = As(driver);
        var svc = d.Get<IDriverService>();
        var offer = await svc.GetPendingOfferAsync();
        Assert.NotNull(offer);
        Assert.Equal(orderId, offer.OrderId);
        Assert.Equal(orderId, await svc.AcceptOfferAsync(offer.OfferId));
    }

    /// <summary>Arrive at store → pick up → arrive at customer → delivered.</summary>
    public async Task CompleteTripAsync(TestUser driver, int orderId)
    {
        using var d = As(driver);
        var svc = d.Get<IDriverService>();
        await svc.ArriveAtStoreAsync(orderId);
        await svc.ConfirmPickupAsync(orderId);
        await svc.ArriveAtCustomerAsync(orderId);
        await svc.ConfirmDeliveredAsync(orderId);
    }

    /// <summary>Customer places an order and the store accepts it (order is now searching for a driver).</summary>
    public async Task<(TestUser Customer, int StoreId, TestUser Owner, PlaceOrderResult Order)> CreateReadyOrderAsync()
    {
        var (owner, storeId, pa, pb) = await CreateStoreAsync();
        var customer = await CreateUserAsync("Customer", Roles.Customer);
        PlaceOrderResult placed;
        using (var c = As(customer))
        {
            await c.Get<ICartService>().AddAsync(pa, 2);
            await c.Get<ICartService>().AddAsync(pb, 1);
            placed = await c.Get<ICustomerOrderService>().PlaceOrderAsync(Checkout());
        }
        using (var s = As(owner))
        {
            var seller = s.Get<ISellerService>();
            await seller.ChangeOrderStatusAsync(storeId, placed.OrderId, OrderStatus.Confirmed, null);
        }
        return (customer, storeId, owner, placed);
    }

    public static CheckoutInput Checkout(Guid? key = null) => new()
    {
        RecipientName = "Nguyễn Văn A",
        RecipientPhone = "0901234567",
        DeliveryAddress = "118 Lê Hồng Phong, Thủ Dầu Một",
        IdempotencyKey = key ?? Guid.NewGuid(),
    };

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}

public sealed record TestUser(string Id, string Email, string[] Roles);

public sealed class AsUser(IServiceScope scope) : IDisposable
{
    public T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();
    public void Dispose() => scope.Dispose();
}

/// <summary>Deterministic routing for tests (no network). Null distance simulates an unavailable service.</summary>
public sealed class FakeRoutingService : IRoutingService
{
    public int? DistanceMeters { get; set; } = 5_000;
    public int Calls { get; private set; }

    public Task<int?> GetRoadDistanceMetersAsync(GeoPoint from, GeoPoint to, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(DistanceMeters);
    }
}

/// <summary>Real time plus a controllable offset (to simulate offer timeouts).</summary>
public sealed class FakeClock : IClock
{
    public TimeSpan Offset { get; set; }
    public DateTime UtcNow => DateTime.UtcNow + Offset;
    public void Advance(TimeSpan by) => Offset += by;
}
