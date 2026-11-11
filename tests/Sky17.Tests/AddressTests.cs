using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure.Data;

namespace Sky17.Tests;

/// <summary>Addresses are "số nhà/đường + phường/xã + tỉnh/thành" from the national catalog, plus an optional detail.</summary>
public class AddressTests
{
    [Fact]
    public void Catalog_contains_the_2025_administrative_units()
    {
        var units = new Sky17.Infrastructure.Services.AdministrativeUnitCatalog();
        Assert.Equal(34, units.Provinces.Count);
        Assert.Equal(3321, units.Provinces.Sum(p => units.WardsOf(p.Code).Count));
        Assert.Equal("Thành phố Hồ Chí Minh", units.FindProvince("79")!.FullName);
        var ward = units.FindWard("25747")!;
        Assert.Equal("Phường Thủ Dầu Một", ward.FullName);
        Assert.Equal("79", ward.ProvinceCode);
        Assert.Contains("v5.2.0", units.Version);
        Assert.Empty(units.WardsOf("00"));
    }

    [Fact]
    public async Task Order_address_is_composed_from_street_ward_and_province_and_keeps_the_detail()
    {
        await using var app = await TestApp.CreateAsync();
        var (owner, storeId, pa, _) = await app.CreateStoreAsync();
        var customer = await app.CreateUserAsync("Khách", Roles.Customer);
        using var c = app.As(customer);
        await c.Get<ICartService>().AddAsync(pa, 1);
        var input = TestApp.Checkout();
        input.Street = "  118 Lê Hồng Phong ";
        input.AddressDetail = "Tòa A, tầng 3";
        input.SaveAddress = true;
        var placed = await c.PlaceOrderAsync(input);

        await using var db = await app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        var order = await db.Orders.SingleAsync(o => o.Id == placed.OrderId);
        Assert.Equal("118 Lê Hồng Phong, Phường Thủ Dầu Một, Thành phố Hồ Chí Minh", order.DeliveryAddress);
        Assert.Equal("Tòa A, tầng 3", order.DeliveryAddressDetail);
        Assert.Equal("25747", order.DeliveryWardCode);
        Assert.Equal("79", order.DeliveryProvinceCode);

        var saved = Assert.Single(await c.Get<IAddressService>().GetMineAsync());
        Assert.Equal("118 Lê Hồng Phong", saved.Street);
        Assert.Equal("25747", saved.WardCode);
        Assert.Equal("Tòa A, tầng 3", saved.Note);
        Assert.True(saved.HasAdministrativeUnit);
    }

    [Theory]
    [InlineData("79", "00004")]   // Ba Đình belongs to Hà Nội, not HCMC
    [InlineData("79", null)]
    [InlineData(null, "25747")]
    [InlineData("99", "25747")]
    public async Task Ward_must_belong_to_the_selected_province(string? province, string? ward)
    {
        await using var app = await TestApp.CreateAsync();
        var (_, _, pa, _) = await app.CreateStoreAsync();
        var customer = await app.CreateUserAsync("Khách", Roles.Customer);
        using var c = app.As(customer);
        await c.Get<ICartService>().AddAsync(pa, 1);
        var input = TestApp.Checkout();
        input.ProvinceCode = province;
        input.WardCode = ward;
        await Assert.ThrowsAnyAsync<Exception>(() => c.PlaceOrderAsync(input));

        await Assert.ThrowsAnyAsync<Exception>(() => c.Get<IAddressService>().SaveAsync(new AddressInput
        {
            RecipientName = "A", PhoneNumber = "0901234567", Street = "1 Test", ProvinceCode = province, WardCode = ward,
        }));
    }

    [Fact]
    public async Task Saved_address_stores_codes_and_full_names()
    {
        await using var app = await TestApp.CreateAsync();
        var customer = await app.CreateUserAsync("Khách", Roles.Customer);
        using var c = app.As(customer);
        var id = await c.Get<IAddressService>().SaveAsync(new AddressInput
        {
            RecipientName = "A", PhoneNumber = "0901234567", Street = "5 Tràng Tiền", ProvinceCode = "01", WardCode = "00070", Note = "Cổng sau",
        });
        var a = Assert.Single(await c.Get<IAddressService>().GetMineAsync());
        Assert.Equal(id, a.Id);
        Assert.Equal("5 Tràng Tiền, Phường Hoàn Kiếm, Thành phố Hà Nội", a.FullText);
        Assert.Equal("Cổng sau", a.Note);
        var edit = await c.Get<IAddressService>().GetForEditAsync(id);
        Assert.Equal(("01", "00070"), (edit!.ProvinceCode, edit.WardCode));
    }
}
