using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Sky17.Application;
using Sky17.Infrastructure;
using Sky17.Infrastructure.Data;
using Sky17.Web.Components;
using Sky17.Web.Components.Account;
using Sky17.Web.Infrastructure;

var vi = new CultureInfo("vi-VN");
CultureInfo.DefaultThreadCurrentCulture = vi;
CultureInfo.DefaultThreadCurrentUICulture = vi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ---------------------------------------------------------------- Authentication / Identity
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.AccessDeniedPath = "/Account/AccessDenied";
    o.Cookie.Name = "sky17.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromDays(7);
    o.SlidingExpiration = true;
});
// Deactivated users / role changes take effect quickly (security stamp re-check).
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.AddAuthorization();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
var dbPath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
if (Path.GetDirectoryName(Path.GetFullPath(dbPath)) is { } dbDir) Directory.CreateDirectory(dbDir);

// Realtime publisher must be registered before infrastructure (which falls back to a no-op publisher).
builder.Services.AddSignalR();
builder.Services.AddSingleton<IRealtimePublisher, SignalRRealtimePublisher>();
// Free road routing for delivery pricing (OSRM). Empty value disables distance-based pricing.
builder.Services.AddSky17Infrastructure(connectionString, builder.Environment.WebRootPath, builder.Configuration["Maps:OsrmBaseUrl"]);

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, LoggingEmailSender>();
builder.Services.AddScoped<ICurrentUserAccessor, BlazorCurrentUserAccessor>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<RealtimeState>();
builder.Services.AddScoped<SellerContext>();
builder.Services.AddScoped<CartState>();
builder.Services.AddScoped<CustomerLocation>();
if (!builder.Configuration.GetValue<bool>("Dispatch:DisableWorker"))
    builder.Services.AddHostedService<DispatchWorker>();

var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath)).SetApplicationName("Sky17");

// ---------------------------------------------------------------- Rate limiting
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
    {
        // Keep the 429: otherwise status-code re-execution would turn it into a different page/status.
        if (ctx.HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;
        ctx.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.HttpContext.Response.WriteAsync("Bạn thao tác quá nhanh. Vui lòng thử lại sau ít phút.", ct);
    };
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        // Authentication form posts: strict limit to slow down credential stuffing.
        if (HttpMethods.IsPost(ctx.Request.Method) && (ctx.Request.Path.StartsWithSegments("/Account") || Sky17.Web.Components.Account.LoginPortals.IsLoginPath(ctx.Request.Path)))
            return RateLimitPartition.GetFixedWindowLimiter("auth:" + ip, _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) });
        if (ctx.Request.Path.StartsWithSegments("/_framework") || ctx.Request.Path.StartsWithSegments("/_blazor") || ctx.Request.Path.StartsWithSegments("/hubs"))
            return RateLimitPartition.GetNoLimiter("framework");
        // Static files (css/js/map library/images) are cheap and cached: never count them. Many phones share one
        // public IP (mobile CGNAT, a store's wifi), so counting every asset would block real users.
        if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)) && Path.HasExtension(ctx.Request.Path.Value))
            return RateLimitPartition.GetNoLimiter("static");
        return RateLimitPartition.GetFixedWindowLimiter("public:" + ip, _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) });
    });
});

var app = builder.Build();

// ---------------------------------------------------------------- Database (migrations, never EnsureCreated)
var seed = builder.Configuration.GetSection("Seed").Get<SeedOptions>() ?? new SeedOptions();
await DbSeeder.MigrateAndSeedAsync(app.Services, seed);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!builder.Configuration.GetValue<bool>("DisableHttpsRedirection"))
    app.UseHttpsRedirection();

app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(vi),
    SupportedCultures = [vi],
    SupportedUICultures = [vi],
});

// Runtime files: product images (uploads/) and Android installers (downloads/*.apk, see mobile/README.md).
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".apk"] = "application/vnd.android.package-archive";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();
app.MapHub<NotificationHub>("/hubs/notifications");

app.Run();

public partial class Program;
