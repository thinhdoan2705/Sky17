using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure.Data;

namespace Sky17.Tests;

public class RegistrationTests
{
    [Fact]
    public async Task Customer_registration_creates_user_role_and_profile()
    {
        await using var app = await TestApp.CreateAsync();
        using var scope = app.Anonymous();
        var accounts = scope.Get<IAccountService>();
        var input = new RegisterInput { Kind = RegistrationKind.Customer, FullName = "Trần Gia Huy", Email = "huy@test.local", PhoneNumber = "0904000002", Password = "Password123", ConfirmPassword = "Password123" };

        var result = await accounts.RegisterAsync(input);

        Assert.True(result.Succeeded, string.Join(";", result.Errors));
        var um = app.Services.CreateScope().ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await um.FindByEmailAsync("huy@test.local");
        Assert.NotNull(user);
        Assert.Equal([Roles.Customer], await um.GetRolesAsync(user));
        await using var db = app.Db();
        Assert.Equal("Trần Gia Huy", (await db.UserProfiles.SingleAsync(p => p.UserId == user.Id)).FullName);
        Assert.Equal("/", await accounts.GetHomeUrlAsync(user.Id));

        var duplicate = await accounts.RegisterAsync(input);
        Assert.False(duplicate.Succeeded);
    }

    [Fact]
    public async Task Seller_and_driver_registration_create_pending_records_never_admin()
    {
        await using var app = await TestApp.CreateAsync();
        using var scope = app.Anonymous();
        var accounts = scope.Get<IAccountService>();
        var seller = await accounts.RegisterAsync(new RegisterInput { Kind = RegistrationKind.Seller, FullName = "Seller", Email = "s@test.local", PhoneNumber = "0904000003", Password = "Password123", ConfirmPassword = "Password123", StoreName = "Mây Bakery", StoreAddress = "12 Yersin" });
        var driver = await accounts.RegisterAsync(new RegisterInput { Kind = RegistrationKind.Driver, FullName = "Driver", Email = "d@test.local", PhoneNumber = "0904000004", Password = "Password123", ConfirmPassword = "Password123", LicensePlate = "61a1-12345" });

        Assert.True(seller.Succeeded && driver.Succeeded);
        await using var db = app.Db();
        var store = await db.Stores.Include(s => s.Members).SingleAsync();
        Assert.Equal(StoreStatus.PendingApproval, store.Status);
        Assert.Equal(StoreMemberRole.Owner, store.Members.Single().Role);
        var profile = await db.DriverProfiles.SingleAsync();
        Assert.Equal(DriverStatus.PendingApproval, profile.Status);
        Assert.Equal("61A1-12345", profile.LicensePlate);
        var adminRole = await db.Roles.SingleAsync(r => r.Name == Roles.Admin);
        Assert.False(await db.UserRoles.AnyAsync(r => r.RoleId == adminRole.Id));
    }
}

public class SellerAuthorizationTests
{
    [Fact]
    public async Task Seller_cannot_access_another_store()
    {
        await using var app = await TestApp.CreateAsync();
        var (ownerA, storeA, _, _) = await app.CreateStoreAsync("Store A");
        var (_, storeB, productB, _) = await app.CreateStoreAsync("Store B");
        using var a = app.As(ownerA);
        var seller = a.Get<ISellerService>();

        await Assert.ThrowsAsync<ForbiddenException>(() => seller.GetDashboardAsync(storeB));
        await Assert.ThrowsAsync<ForbiddenException>(() => seller.GetProductsAsync(storeB));
        await Assert.ThrowsAsync<ForbiddenException>(() => seller.SaveProductAsync(storeB, new ProductInput { Id = productB, Name = "Hack", Price = 1000, Stock = 1 }));
        // Passing my own storeId with someone else's product id is "not found", never an update.
        await Assert.ThrowsAsync<NotFoundException>(() => seller.SaveProductAsync(storeA, new ProductInput { Id = productB, Name = "Hack", Price = 1000, Stock = 1 }));
        await using var db = app.Db();
        Assert.NotEqual("Hack", (await db.Products.FindAsync(productB))!.Name);
    }

    [Fact]
    public async Task Non_seller_and_staff_restrictions()
    {
        await using var app = await TestApp.CreateAsync();
        var (_, storeId, _, _) = await app.CreateStoreAsync();
        var customer = await app.CreateUserAsync("Customer", Roles.Customer);
        var staff = await app.CreateUserAsync("Staff", Roles.Seller);
        await using (var db = app.Db())
        {
            db.StoreMembers.Add(new StoreMember { StoreId = storeId, UserId = staff.Id, Role = StoreMemberRole.Staff });
            await db.SaveChangesAsync();
        }

        using (var c = app.As(customer))
            await Assert.ThrowsAsync<ForbiddenException>(() => c.Get<ISellerService>().GetDashboardAsync(storeId));

        using var s = app.As(staff);
        var seller = s.Get<ISellerService>();
        await seller.GetDashboardAsync(storeId); // staff can operate
        await Assert.ThrowsAsync<ForbiddenException>(() => seller.UpdateStoreProfileAsync(storeId, new StoreProfileInput { Name = "x", PhoneNumber = "0911111111", Address = "x" }));
        await Assert.ThrowsAsync<ForbiddenException>(() => seller.GetSettlementsAsync(storeId));
    }

    [Fact]
    public async Task Product_crud_and_soft_delete_when_ordered()
    {
        await using var app = await TestApp.CreateAsync();
        var (owner, storeId, productA, _) = await app.CreateStoreAsync();
        using var s = app.As(owner);
        var seller = s.Get<ISellerService>();

        var id = await seller.SaveProductAsync(storeId, new ProductInput { Name = "Trà đào", Price = 35_000, Stock = 5 });
        var edit = await seller.GetProductAsync(storeId, id);
        Assert.Equal("Trà đào", edit.Input.Name);
        edit.Input.Price = 39_000;
        await seller.SaveProductAsync(storeId, edit.Input);
        Assert.Equal(39_000, (await seller.GetProductAsync(storeId, id)).Input.Price);
        await Assert.ThrowsAsync<DomainException>(() => seller.SaveProductAsync(storeId, new ProductInput { Name = "", Price = 0, Stock = -1 }));

        await seller.DeleteProductAsync(storeId, id);
        await using (var db = app.Db()) Assert.Null(await db.Products.FindAsync(id));

        // A product with order history is hidden, not deleted.
        var customer = await app.CreateUserAsync("C", Roles.Customer);
        using (var c = app.As(customer))
        {
            await c.Get<ICartService>().AddAsync(productA);
            await c.Get<ICustomerOrderService>().PlaceOrderAsync(TestApp.Checkout());
        }
        await seller.DeleteProductAsync(storeId, productA);
        await using (var db = app.Db()) Assert.False((await db.Products.FindAsync(productA))!.IsActive);
    }
}

public class CheckoutTests
{
    [Fact]
    public async Task Checkout_totals_are_computed_server_side()
    {
        await using var app = await TestApp.CreateAsync();
        var (_, _, pa, pb) = await app.CreateStoreAsync(priceA: 29_000, priceB: 32_000);
        var customer = await app.CreateUserAsync("C", Roles.Customer);
        using var c = app.As(customer);
        await c.Get<ICartService>().AddAsync(pa, 2);
        var cart = (await c.Get<ICartService>().AddAsync(pb, 1)).Cart!;
        Assert.Equal(90_000, cart.Subtotal);
        Assert.Equal(15_000, cart.DeliveryFee);

        var placed = await c.Get<ICustomerOrderService>().PlaceOrderAsync(TestApp.Checkout());
        var order = await c.Get<ICustomerOrderService>().GetMyOrderAsync(placed.Code);

        Assert.NotNull(order);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(90_000, order.ProductTotal);
        Assert.Equal(15_000, order.DeliveryFee);
        Assert.Equal(105_000, order.GrandTotal);
        Assert.Equal(9_000, order.PlatformFee);
        Assert.Equal(81_000, order.StoreReceivable);
        Assert.Equal(12_000, order.DriverEarning);
        Assert.True((await c.Get<ICartService>().GetAsync()).IsEmpty);
    }

    [Fact]
    public async Task Order_keeps_price_and_address_snapshot()
    {
        await using var app = await TestApp.CreateAsync();
        var (owner, storeId, pa, _) = await app.CreateStoreAsync(priceA: 29_000);
        var customer = await app.CreateUserAsync("C", Roles.Customer);
        PlaceOrderResult placed;
        using (var c = app.As(customer))
        {
            await c.Get<ICartService>().AddAsync(pa, 1);
            placed = await c.Get<ICustomerOrderService>().PlaceOrderAsync(TestApp.Checkout());
        }
        using (var s = app.As(owner))
        {
            var seller = s.Get<ISellerService>();
            var p = await seller.GetProductAsync(storeId, pa);
            p.Input.Price = 99_000;
            p.Input.Name = "Tên mới";
            await seller.SaveProductAsync(storeId, p.Input);
            await seller.UpdateStoreProfileAsync(storeId, new StoreProfileInput { Name = "Đổi tên", PhoneNumber = "0911111111", Address = "Địa chỉ mới" });
        }
        await using var db = app.Db();
        var order = await db.Orders.Include(o => o.Items).SingleAsync(o => o.Id == placed.OrderId);
        Assert.Equal(29_000, order.Items.Single().UnitPrice);
        Assert.Equal("Cà phê sữa đá", order.Items.Single().ProductName);
        Assert.Equal("Test Store", order.PickupStoreName);
        Assert.Equal("1 Test St", order.PickupAddress);
        Assert.Equal(29_000 + 15_000, order.GrandTotal);
    }

    [Fact]
    public async Task Inventory_is_reserved_rejected_when_short_and_restored_on_cancel()
    {
        await using var app = await TestApp.CreateAsync();
        var (_, _, pa, _) = await app.CreateStoreAsync(stock: 3);
        var c1 = await app.CreateUserAsync("C1", Roles.Customer);
        var c2 = await app.CreateUserAsync("C2", Roles.Customer);

        PlaceOrderResult first;
        using (var c = app.As(c1))
        {
            await c.Get<ICartService>().AddAsync(pa, 2);
            first = await c.Get<ICustomerOrderService>().PlaceOrderAsync(TestApp.Checkout());
        }
        await using (var db = app.Db()) Assert.Equal(1, (await db.Products.FindAsync(pa))!.Stock);

        using (var c = app.As(c2))
        {
            var add = await c.Get<ICartService>().AddAsync(pa, 2);
            Assert.False(add.Success); // only 1 left
        }
        await using (var db = app.Db()) Assert.Equal(1, await db.Orders.CountAsync());

        using (var c = app.As(c1)) await c.Get<ICustomerOrderService>().CancelAsync(first.Code, "Đặt nhầm");
        await using (var db = app.Db())
        {
            Assert.Equal(3, (await db.Products.FindAsync(pa))!.Stock);
            var history = await db.OrderStatusHistories.Where(h => h.OrderId == first.OrderId).OrderBy(h => h.Id).ToListAsync();
            Assert.Equal([OrderStatus.Pending, OrderStatus.Cancelled], history.Select(h => h.NewStatus));
            Assert.Equal("Đặt nhầm", history.Last().Reason);
        }
    }

    [Fact]
    public async Task Checkout_fails_atomically_if_stock_ran_out_after_adding_to_cart()
    {
        await using var app = await TestApp.CreateAsync();
        var (owner, storeId, pa, pb) = await app.CreateStoreAsync(stock: 5);
        var customer = await app.CreateUserAsync("C", Roles.Customer);
        using var c = app.As(customer);
        await c.Get<ICartService>().AddAsync(pa, 1);
        await c.Get<ICartService>().AddAsync(pb, 4);
        using (var s = app.As(owner)) await s.Get<ISellerService>().SetStockAsync(storeId, pb, 2);

        await Assert.ThrowsAsync<DomainException>(() => c.Get<ICustomerOrderService>().PlaceOrderAsync(TestApp.Checkout()));
        await using var db = app.Db();
        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Equal(5, (await db.Products.FindAsync(pa))!.Stock); // first reservation rolled back
    }

    [Fact]
    public async Task Same_idempotency_key_never_creates_two_orders()
    {
        await using var app = await TestApp.CreateAsync();
        var (_, _, pa, _) = await app.CreateStoreAsync();
        var customer = await app.CreateUserAsync("C", Roles.Customer);
        using var c = app.As(customer);
        await c.Get<ICartService>().AddAsync(pa, 1);
        var key = Guid.NewGuid();
        var first = await c.Get<ICustomerOrderService>().PlaceOrderAsync(TestApp.Checkout(key));
        var second = await c.Get<ICustomerOrderService>().PlaceOrderAsync(TestApp.Checkout(key));
        Assert.Equal(first.OrderId, second.OrderId);
        Assert.True(second.WasDuplicate);
        await using var db = app.Db();
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task Customer_cannot_cancel_after_store_confirmed()
    {
        await using var app = await TestApp.CreateAsync();
        var (customer, _, _, placed) = await app.CreateReadyOrderAsync();
        using var c = app.As(customer);
        await Assert.ThrowsAnyAsync<Exception>(() => c.Get<ICustomerOrderService>().CancelAsync(placed.Code, "đổi ý"));
    }
}


public class SettlementTests
{
    private static async Task<(TestApp App, TestUser Admin, TestUser Driver, int StoreId, int OrderId)> DeliveredAsync()
    {
        var app = await TestApp.CreateAsync();
        var (_, storeId, _, placed) = await app.CreateReadyOrderAsync();
        var driver = await app.CreateDriverAsync("D");
        await app.AcceptViaDispatchAsync(driver, placed.OrderId);
        await app.CompleteTripAsync(driver, placed.OrderId);
        var admin = await app.CreateUserAsync("Admin", Roles.Admin);
        return (app, admin, driver, storeId, placed.OrderId);
    }

    [Fact]
    public async Task Driver_settlement_is_idempotent()
    {
        var (app, admin, driver, _, orderId) = await DeliveredAsync();
        await using var _ = app;
        using var a = app.As(admin);
        var finance = a.Get<IFinanceService>();

        var balances = await finance.GetDriverBalancesAsync();
        Assert.Equal(93_000, balances.Single().Amount);

        var id = await finance.CreateDriverSettlementAsync(driver.Id, "tiền mặt");
        await Assert.ThrowsAsync<DomainException>(() => finance.CreateDriverSettlementAsync(driver.Id, null)); // no double settlement

        await finance.ConfirmSettlementAsync(id);
        await finance.ConfirmSettlementAsync(id); // second confirm is a no-op

        await using var db = app.Db();
        Assert.Single(await db.CodTransactions.Where(c => c.OrderId == orderId && c.Type == CodTransactionType.RemittedToPlatform).ToListAsync());
        Assert.Equal(OrderSettlementState.Settled, (await db.Orders.FindAsync(orderId))!.DriverSettlementState);
        Assert.Empty(await finance.GetDriverBalancesAsync());
        Assert.Equal(SettlementStatus.Confirmed, (await db.Settlements.FindAsync(id))!.Status);
    }

    [Fact]
    public async Task Cancelled_store_settlement_releases_orders()
    {
        var (app, admin, _, storeId, orderId) = await DeliveredAsync();
        await using var _ = app;
        using var a = app.As(admin);
        var finance = a.Get<IFinanceService>();
        var id = await finance.CreateStoreSettlementAsync(storeId, null);
        Assert.Empty(await finance.GetStoreBalancesAsync());
        await finance.CancelSettlementAsync(id, "Sai số tiền");
        var balances = await finance.GetStoreBalancesAsync();
        Assert.Equal(81_000, balances.Single().Amount);
        await Assert.ThrowsAsync<DomainException>(() => finance.ConfirmSettlementAsync(id));
        var again = await finance.CreateStoreSettlementAsync(storeId, null);
        await finance.ConfirmSettlementAsync(again);
        await using var db = app.Db();
        Assert.Equal(OrderSettlementState.Settled, (await db.Orders.FindAsync(orderId))!.StoreSettlementState);
    }

    [Fact]
    public async Task Only_admin_can_settle()
    {
        var (app, _, driver, storeId, _) = await DeliveredAsync();
        await using var _ = app;
        using var d = app.As(driver);
        await Assert.ThrowsAsync<ForbiddenException>(() => d.Get<IFinanceService>().CreateStoreSettlementAsync(storeId, null));
    }
}

public class UnauthorizedAccessTests
{
    [Fact]
    public async Task Customers_only_see_their_own_orders()
    {
        await using var app = await TestApp.CreateAsync();
        var (customer, _, _, placed) = await app.CreateReadyOrderAsync();
        var other = await app.CreateUserAsync("Other", Roles.Customer);
        using var o = app.As(other);
        Assert.Null(await o.Get<ICustomerOrderService>().GetMyOrderAsync(placed.Code));
        await Assert.ThrowsAsync<NotFoundException>(() => o.Get<ICustomerOrderService>().CancelAsync(placed.Code, "x"));
        Assert.Equal(0, (await o.Get<ICustomerOrderService>().GetMyOrdersAsync()).TotalCount);
    }

    [Fact]
    public async Task Anonymous_and_wrong_role_calls_are_rejected()
    {
        await using var app = await TestApp.CreateAsync();
        var (_, _, pa, _) = await app.CreateStoreAsync();
        var driver = await app.CreateDriverAsync("D");
        using (var anon = app.Anonymous())
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => anon.Get<ICartService>().AddAsync(pa));
            await Assert.ThrowsAsync<ForbiddenException>(() => anon.Get<ICustomerOrderService>().PlaceOrderAsync(TestApp.Checkout()));
            await Assert.ThrowsAsync<ForbiddenException>(() => anon.Get<IAdminService>().GetDashboardAsync());
        }
        using (var d = app.As(driver))
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => d.Get<IAdminService>().CancelOrderAsync(1, "x"));
            await Assert.ThrowsAsync<ForbiddenException>(() => d.Get<ICartService>().AddAsync(pa));
        }
    }

    [Fact]
    public async Task Admin_cannot_assign_unapproved_driver()
    {
        await using var app = await TestApp.CreateAsync();
        var (_, _, _, placed) = await app.CreateReadyOrderAsync();
        var admin = await app.CreateUserAsync("Admin", Roles.Admin);
        var pending = await app.CreateDriverAsync("P", approved: false);
        using var a = app.As(admin);
        await Assert.ThrowsAsync<DomainException>(() => a.Get<IAdminService>().AssignDriverAsync(placed.OrderId, pending.Id));
    }
}
