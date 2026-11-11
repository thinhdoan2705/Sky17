using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure.Data;

namespace Sky17.Tests;

/// <summary>Customers sign in with their phone number; partners (seller/driver/admin) with e-mail.</summary>
public class PhoneLoginTests
{
    private static RegisterInput Customer(string phone, string email) => new()
    {
        Kind = RegistrationKind.Customer, FullName = "Khách", Email = email, PhoneNumber = phone,
        Password = "Password123", ConfirmPassword = "Password123",
    };

    [Theory]
    [InlineData("0904000001", "0904000001")]
    [InlineData("+84904000001", "0904000001")]
    [InlineData("84 904 000 001", "0904000001")]
    [InlineData("0904.000.001", "0904000001")]
    [InlineData("12345", null)]
    [InlineData("a@b.vn", null)]
    public void Phone_numbers_are_normalized(string input, string? expected) => Assert.Equal(expected, PhoneNumbers.Normalize(input));

    [Fact]
    public async Task Customer_login_name_is_the_normalized_phone_number()
    {
        await using var app = await TestApp.CreateAsync();
        using var anon = app.Anonymous();
        var accounts = anon.Get<IAccountService>();
        var r = await accounts.RegisterAsync(Customer("+84 912 345 678", "kh@test.local"));
        Assert.True(r.Succeeded, string.Join(";", r.Errors));

        var um = app.Services.CreateScope().ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await um.FindByIdAsync(r.UserId!);
        Assert.Equal("0912345678", user!.UserName);
        Assert.Equal("0912345678", user.PhoneNumber);

        foreach (var login in new[] { "0912345678", "+84912345678", "0912 345 678" })
        {
            var found = await accounts.FindLoginAsync(login);
            Assert.Null(found.Error);
            Assert.Equal(user.Id, found.UserId);
            Assert.Equal("0912345678", found.UserName);
        }
        // A customer-only account cannot sign in with e-mail.
        Assert.NotNull((await accounts.FindLoginAsync("kh@test.local")).Error);
        Assert.NotNull((await accounts.FindLoginAsync("0999999999")).Error);
    }

    [Fact]
    public async Task The_same_phone_cannot_register_two_customer_accounts()
    {
        await using var app = await TestApp.CreateAsync();
        using var anon = app.Anonymous();
        var accounts = anon.Get<IAccountService>();
        Assert.True((await accounts.RegisterAsync(Customer("0912345678", "a@test.local"))).Succeeded);
        var dup = await accounts.RegisterAsync(Customer("+84912345678", "b@test.local"));
        Assert.False(dup.Succeeded);
        Assert.Contains(dup.Errors, e => e.Contains("Số điện thoại"));
    }

    [Fact]
    public async Task Partners_still_sign_in_with_email()
    {
        await using var app = await TestApp.CreateAsync();
        using var anon = app.Anonymous();
        var accounts = anon.Get<IAccountService>();
        var seller = await accounts.RegisterAsync(new RegisterInput
        {
            Kind = RegistrationKind.Seller, FullName = "Chủ quán", Email = "shop@test.local", PhoneNumber = "0911111111",
            Password = "Password123", ConfirmPassword = "Password123", StoreName = "Quán", StoreAddress = "1 Đường",
        });
        Assert.True(seller.Succeeded);
        var found = await accounts.FindLoginAsync("shop@test.local");
        Assert.Equal(seller.UserId, found.UserId);
        Assert.Equal("shop@test.local", found.UserName);
    }

    [Fact]
    public async Task Changing_the_profile_phone_changes_the_customer_login()
    {
        await using var app = await TestApp.CreateAsync();
        string a, b;
        using (var anon = app.Anonymous())
        {
            a = (await anon.Get<IAccountService>().RegisterAsync(Customer("0912345678", "a@test.local"))).UserId!;
            b = (await anon.Get<IAccountService>().RegisterAsync(Customer("0987654321", "b@test.local"))).UserId!;
        }
        using (var s = app.As(a, Roles.Customer))
        {
            await Assert.ThrowsAsync<DomainException>(() => s.Get<IProfileService>().UpdateMineAsync(new ProfileInput { FullName = "A", PhoneNumber = "0987654321" }));
            await Assert.ThrowsAsync<DomainException>(() => s.Get<IProfileService>().UpdateMineAsync(new ProfileInput { FullName = "A", PhoneNumber = null }));
            await s.Get<IProfileService>().UpdateMineAsync(new ProfileInput { FullName = "A", PhoneNumber = "0933333333" });
        }
        using var anon2 = app.Anonymous();
        Assert.Equal(a, (await anon2.Get<IAccountService>().FindLoginAsync("0933333333")).UserId);
        Assert.NotNull((await anon2.Get<IAccountService>().FindLoginAsync("0912345678")).Error);
        Assert.Equal(b, (await anon2.Get<IAccountService>().FindLoginAsync("0987654321")).UserId);
    }

    [Fact]
    public async Task Existing_email_based_customer_accounts_are_converted_at_startup()
    {
        await using var app = await TestApp.CreateAsync();
        string id;
        using (var scope = app.Services.CreateScope())
        {
            var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var old = new ApplicationUser { UserName = "old@test.local", Email = "old@test.local", PhoneNumber = "+84 905 555 555", EmailConfirmed = true };
            Assert.True((await um.CreateAsync(old, "Password123")).Succeeded);
            await um.AddToRoleAsync(old, Roles.Customer);
            id = old.Id;
        }
        await DbSeeder.MigrateAndSeedAsync(app.Services, new SeedOptions());

        using var anon = app.Anonymous();
        var found = await anon.Get<IAccountService>().FindLoginAsync("0905555555");
        Assert.Equal(id, found.UserId);
    }
}
