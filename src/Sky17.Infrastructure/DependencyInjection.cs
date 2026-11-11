using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sky17.Application;
using Sky17.Infrastructure.Data;
using Sky17.Infrastructure.Services;

namespace Sky17.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence and application services. Identity (UserManager etc.) is configured by the host.
    /// </summary>
    public static IServiceCollection AddSky17Infrastructure(this IServiceCollection services, string connectionString, string webRootPath)
    {
        // Factory for interactive Blazor services (short-lived context per operation)...
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connectionString));
        // ...and a scoped context for ASP.NET Core Identity stores.
        services.TryAddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IRealtimePublisher, NullRealtimePublisher>();
        services.AddSingleton<IFileStorage>(new LocalFileStorage(webRootPath));
        services.AddSingleton<OrderWorkflow>();
        services.AddSingleton<Notifier>();

        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<ICartService, CartService>();
        services.AddScoped<ICustomerOrderService, CustomerOrderService>();
        services.AddScoped<IAddressService, AddressService>();
        services.AddScoped<IProfileService, ProfileService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<ISellerService, SellerService>();
        services.AddScoped<IDriverService, DriverService>();
        services.AddScoped<IAdminService, AdminService>();
        services.AddScoped<IFinanceService, FinanceService>();
        services.AddScoped<IAccountService, AccountService>();
        return services;
    }
}
