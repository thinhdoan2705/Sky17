using Microsoft.AspNetCore.Components;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Web.Infrastructure;

namespace Sky17.Web.Components.UI;

/// <summary>
/// Base for interactive pages: loads data on navigation, reloads on realtime notifications,
/// and turns business exceptions into toasts instead of crashing the circuit.
/// </summary>
public abstract class PortalPage : ComponentBase, IDisposable
{
    [Inject] protected RealtimeState Realtime { get; set; } = default!;
    [Inject] protected ToastService Toast { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected bool Loading { get; private set; } = true;
    protected bool Busy { get; private set; }
    protected string? LoadError { get; private set; }
    private string? loadedKey;

    protected override void OnInitialized() => Realtime.Changed += HandleRealtimeAsync;

    protected override async Task OnParametersSetAsync()
    {
        var key = Nav.Uri;
        if (key == loadedKey) return;
        loadedKey = key;
        await ReloadAsync();
    }

    protected async Task ReloadAsync()
    {
        try
        {
            LoadError = null;
            await LoadAsync();
        }
        catch (Exception ex) when (IsUserError(ex))
        {
            LoadError = ex.Message;
        }
        finally
        {
            Loading = false;
        }
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>Whether a realtime notification (null = reconnect) should trigger a reload of this page.</summary>
    protected virtual bool ReloadOnRealtime(RealtimeMessage? message) => true;

    private Task HandleRealtimeAsync(RealtimeMessage? message) =>
        ReloadOnRealtime(message)
            ? InvokeAsync(async () => { await ReloadAsync(); StateHasChanged(); })
            : Task.CompletedTask;

    /// <summary>Runs a user action with busy state, error toast, success toast and optional reload.</summary>
    protected async Task<bool> RunAsync(Func<Task> action, string? success = null, bool reload = true)
    {
        if (Busy) return false;
        Busy = true;
        StateHasChanged();
        try
        {
            await action();
            if (success is not null) Toast.Success(success);
            if (reload) await ReloadAsync();
            return true;
        }
        catch (Exception ex) when (IsUserError(ex))
        {
            Toast.Error(ex.Message);
            return false;
        }
        finally
        {
            Busy = false;
            StateHasChanged();
        }
    }

    protected static bool IsUserError(Exception ex) =>
        ex is DomainException or ForbiddenException or NotFoundException or ConcurrencyConflictException;

    public virtual void Dispose()
    {
        Realtime.Changed -= HandleRealtimeAsync;
        GC.SuppressFinalize(this);
    }
}
