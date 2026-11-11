using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Sky17.Application;
using Sky17.Infrastructure.Data;

namespace Sky17.Web.Infrastructure;

/// <summary>Resolves the current user from the Blazor authentication state (works for SSR and interactive circuits).</summary>
public sealed class BlazorCurrentUserAccessor(AuthenticationStateProvider auth) : ICurrentUserAccessor
{
    public async Task<CurrentUser> GetAsync(CancellationToken ct = default)
    {
        var state = await auth.GetAuthenticationStateAsync();
        var principal = state.User;
        if (principal.Identity?.IsAuthenticated != true) return CurrentUser.Anonymous;
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null) return CurrentUser.Anonymous;
        return new CurrentUser(id, principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet());
    }
}

/// <summary>Authenticated hub. Each connection joins only its own user group.</summary>
[Authorize]
public sealed class NotificationHub : Hub
{
    public static string UserGroup(string userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        var id = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is not null) await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(id));
        await base.OnConnectedAsync();
    }
}

public sealed class SignalRRealtimePublisher(IHubContext<NotificationHub> hub) : IRealtimePublisher
{
    public Task PublishAsync(IReadOnlyCollection<string> userIds, RealtimeMessage message, CancellationToken ct = default) =>
        hub.Clients.Groups(userIds.Select(NotificationHub.UserGroup).ToList()).SendAsync("notify", message, ct);
}

/// <summary>
/// Development e-mail sender: writes the message to the server log. Replace with an SMTP/API sender for production.
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger, IHostEnvironment env) : IEmailSender<ApplicationUser>
{
    private Task Log(string email, string subject, string body)
    {
        if (env.IsDevelopment()) logger.LogInformation("[DEV EMAIL] To: {Email} | {Subject} | {Body}", email, subject, body);
        else logger.LogWarning("E-mail sender not configured; message '{Subject}' was not delivered.", subject);
        return Task.CompletedTask;
    }

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        Log(email, "Xác nhận tài khoản Sky 17", confirmationLink);

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        Log(email, "Đặt lại mật khẩu Sky 17", resetLink);

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        Log(email, "Mã đặt lại mật khẩu Sky 17", resetCode);
}

public enum ToastKind { Info, Success, Error }

public sealed record ToastMessage(string Text, ToastKind Kind);

/// <summary>Per-circuit toast queue.</summary>
public sealed class ToastService
{
    public event Action<ToastMessage>? Shown;
    public void Success(string text) => Shown?.Invoke(new ToastMessage(text, ToastKind.Success));
    public void Error(string text) => Shown?.Invoke(new ToastMessage(text, ToastKind.Error));
    public void Info(string text) => Shown?.Invoke(new ToastMessage(text, ToastKind.Info));
}

/// <summary>Per-circuit fan-out of realtime notifications to the pages currently displayed.</summary>
public sealed class RealtimeState
{
    public event Func<RealtimeMessage?, Task>? Changed;
    public int Version { get; private set; }

    public async Task RaiseAsync(RealtimeMessage? message)
    {
        Version++;
        if (Changed is null) return;
        foreach (var handler in Changed.GetInvocationList().Cast<Func<RealtimeMessage?, Task>>())
        {
            try { await handler(message); } catch { /* a disposed page must not break others */ }
        }
    }
}

/// <summary>Currently selected store in the seller portal (per circuit). Membership is still verified server-side on every call.</summary>
public sealed class SellerContext
{
    private Task? loading;
    public int? StoreId { get; private set; }
    public IReadOnlyList<MyStoreDto> Stores { get; private set; } = [];
    public MyStoreDto? Current => Stores.FirstOrDefault(s => s.StoreId == StoreId);
    public event Func<Task>? Changed;

    public Task EnsureLoadedAsync(ISellerService seller) => loading ??= LoadAsync(seller);

    public async Task RefreshAsync(ISellerService seller)
    {
        loading = null;
        await EnsureLoadedAsync(seller);
    }

    private async Task LoadAsync(ISellerService seller)
    {
        Stores = await seller.GetMyStoresAsync();
        if (StoreId is null || Stores.All(s => s.StoreId != StoreId)) StoreId = Stores.FirstOrDefault()?.StoreId;
    }

    public async Task SelectAsync(int storeId)
    {
        if (Stores.All(s => s.StoreId != storeId)) return;
        StoreId = storeId;
        if (Changed is not null) await Changed.Invoke();
    }
}

/// <summary>Cart badge state shared by the customer layout and pages (per circuit).</summary>
public sealed class CartState
{
    public int Count { get; private set; }
    public event Action? Changed;
    /// <summary>Total items across all store carts.</summary>
    public void Set(int count) { Count = count; Changed?.Invoke(); }
}

/// <summary>Point used to measure store distances in search (per circuit): a saved address or the device location.</summary>
public sealed class CustomerLocation
{
    public string? Label { get; private set; }
    public double? Latitude { get; private set; }
    public double? Longitude { get; private set; }
    /// <summary>Saved address id, 0 = device location, null = not chosen yet.</summary>
    public int? AddressId { get; private set; }
    public bool HasPoint => Latitude is not null && Longitude is not null;

    public void Set(int? addressId, string label, double? lat, double? lng)
    {
        AddressId = addressId;
        Label = label;
        Latitude = lat;
        Longitude = lng;
    }
}
