using Sky17.Application;
using Sky17.Domain;

namespace Sky17.Tests;

public class CatalogTests
{
    [Fact]
    public async Task Catalog_queries_translate_and_hide_inactive_data()
    {
        await using var app = await TestApp.CreateAsync();
        var (owner, storeId, pa, pb) = await app.CreateStoreAsync();
        using (var s = app.As(owner)) await s.Get<ISellerService>().SetAvailabilityAsync(storeId, pb, false);
        using var anon = app.Anonymous();
        var catalog = anon.Get<ICatalogService>();

        var all = await catalog.SearchProductsAsync(new ProductQuery());
        Assert.Equal(2, all.Count);
        Assert.True(all[0].IsAvailable);           // available products first
        Assert.Single(await catalog.SearchProductsAsync(new ProductQuery(Search: "phê")));
        var store = await catalog.GetStoreAsync(storeId);
        Assert.NotNull(store);
        Assert.Equal(2, store.Products.Count);
        Assert.NotNull(await catalog.GetProductAsync(pa));
        Assert.Single(await catalog.GetStoresAsync("Test"));
        Assert.NotEmpty(await catalog.GetPlatformCategoriesAsync());
    }
}

/// <summary>The 13-step manual test guide, automated against the real services.</summary>
public class EndToEndTests
{
    [Fact]
    public async Task Full_marketplace_workflow()
    {
        await using var app = await TestApp.CreateAsync();
        const string pw = "Password123";
        // 1. Admin
        var admin = await app.CreateUserAsync("Admin", Roles.Admin);

        // 2. Seller registration
        RegisterResult sellerReg, customerReg, driverReg;
        using (var anon = app.Anonymous())
        {
            var acc = anon.Get<IAccountService>();
            sellerReg = await acc.RegisterAsync(new RegisterInput { Kind = RegistrationKind.Seller, FullName = "Chủ quán", Email = "seller@e2e.local", PhoneNumber = "0911000001", Password = pw, ConfirmPassword = pw, StoreName = "Quán E2E", StoreAddress = "1 Đường E2E" });
            customerReg = await acc.RegisterAsync(new RegisterInput { Kind = RegistrationKind.Customer, FullName = "Khách", Email = "customer@e2e.local", PhoneNumber = "0911000002", Password = pw, ConfirmPassword = pw });
            driverReg = await acc.RegisterAsync(new RegisterInput { Kind = RegistrationKind.Driver, FullName = "Tài xế", Email = "driver@e2e.local", PhoneNumber = "0911000003", Password = pw, ConfirmPassword = pw, LicensePlate = "61A1-00001" });
        }
        Assert.True(sellerReg.Succeeded && customerReg.Succeeded && driverReg.Succeeded);
        var seller = new TestUser(sellerReg.UserId!, "", [Roles.Seller]);
        var customer = new TestUser(customerReg.UserId!, "", [Roles.Customer]);
        var driver = new TestUser(driverReg.UserId!, "", [Roles.Driver]);

        // 3. Admin approves seller (store) — store is invisible before approval
        int storeId;
        using (var s = app.As(seller)) storeId = (await s.Get<ISellerService>().GetMyStoresAsync()).Single().StoreId;
        using (var anon = app.Anonymous()) Assert.Null(await anon.Get<ICatalogService>().GetStoreAsync(storeId));
        using (var a = app.As(admin)) await a.Get<IAdminService>().SetStoreStatusAsync(storeId, StoreStatus.Active, null);

        // 4. Seller creates products (store is open 24h for the test)
        int productId;
        using (var s = app.As(seller))
        {
            var svc = s.Get<ISellerService>();
            var profile = await svc.GetStoreProfileAsync(storeId);
            profile.OpenTime = profile.CloseTime = new TimeOnly(0, 0);
            await svc.UpdateStoreProfileAsync(storeId, profile);
            productId = await svc.SaveProductAsync(storeId, new ProductInput { Name = "Phở bò", Price = 60_000, Stock = 10 });
        }

        // 5-6. Customer orders
        PlaceOrderResult order;
        using (var c = app.As(customer))
        {
            await c.Get<ICartService>().AddAsync(productId, 2);
            order = await c.Get<ICustomerOrderService>().PlaceOrderAsync(TestApp.Checkout());
        }

        // 7. Seller confirms and prepares
        using (var s = app.As(seller))
        {
            var svc = s.Get<ISellerService>();
            await svc.ChangeOrderStatusAsync(storeId, order.OrderId, OrderStatus.Confirmed, null); // starts driver search
            await svc.MarkFoodReadyAsync(storeId, order.OrderId);
        }

        // 8. Driver approval; unapproved driver cannot go online
        using (var d = app.As(driver)) await Assert.ThrowsAsync<DomainException>(() => d.Get<IDriverService>().SetOnlineAsync(true));
        using (var a = app.As(admin)) await a.Get<IAdminService>().SetDriverStatusAsync(driver.Id, DriverStatus.Active, null);

        // 9. Driver goes online with GPS; dispatcher rings the nearest free driver; driver accepts within 30 s
        using (var d = app.As(driver))
        {
            var svc = d.Get<IDriverService>();
            await svc.SetOnlineAsync(true);
            await svc.UpdateLocationAsync(10.9810, 106.6530);
        }
        await app.AcceptViaDispatchAsync(driver, order.OrderId);
        using (var d = app.As(driver))
            Assert.NotNull((await d.Get<IDriverService>().GetJobAsync(order.OrderId)).CustomerPhone); // revealed to the assigned driver

        // 10-11. Arrive at store → picked up → arrive at customer → delivered
        await app.CompleteTripAsync(driver, order.OrderId);

        // 12. COD recorded; customer sees delivered + notifications persisted
        using (var c = app.As(customer))
        {
            var detail = await c.Get<ICustomerOrderService>().GetMyOrderAsync(order.Code);
            Assert.Equal(OrderStatus.Delivered, detail!.Status);
            Assert.Equal(135_000, detail.CollectedCodAmount);
            Assert.Contains(await c.Get<INotificationService>().GetMineAsync(), n => n.Title.Contains("Giao hàng thành công"));
        }

        // 13. Admin reviews and settles both sides
        using (var a = app.As(admin))
        {
            var finance = a.Get<IFinanceService>();
            var overview = await finance.GetOverviewAsync();
            Assert.Equal(135_000, overview.CodHeldByDrivers);
            Assert.Equal(108_000, overview.PayableToStores);   // 120.000 − 10% commission
            var ds = await finance.CreateDriverSettlementAsync(driver.Id, null);
            var ss = await finance.CreateStoreSettlementAsync(storeId, null);
            await finance.ConfirmSettlementAsync(ds);
            await finance.ConfirmSettlementAsync(ss);
            var after = await finance.GetOverviewAsync();
            Assert.Equal(0, after.CodHeldByDrivers);
            Assert.Equal(0, after.PayableToStores);
            var dash = await a.Get<IAdminService>().GetDashboardAsync();
            Assert.Equal(1, dash.DeliveredOrders);
            Assert.Equal(120_000, dash.GrossMerchandiseValue);
        }
    }
}
