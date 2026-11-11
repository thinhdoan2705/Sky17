using Microsoft.AspNetCore.Components;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Web.Components.UI;
using Sky17.Web.Infrastructure;

namespace Sky17.Web.Components.Pages.Seller;

/// <summary>Seller pages operate on the store selected in <see cref="SellerContext"/>; the server re-checks membership.</summary>
public class SellerPage : PortalPage
{
    [Inject] protected SellerContext Ctx { get; set; } = default!;
    [Inject] protected ISellerService Seller { get; set; } = default!;

    protected int StoreId => Ctx.StoreId ?? 0;
    protected bool HasStore => Ctx.StoreId is not null;
    protected bool IsOwner => Ctx.Current?.Role == StoreMemberRole.Owner;
    protected bool StoreActive => Ctx.Current?.Status == StoreStatus.Active;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Ctx.Changed += OnStoreChanged;
    }

    private Task OnStoreChanged() => InvokeAsync(async () => { await ReloadAsync(); StateHasChanged(); });

    protected sealed override async Task LoadAsync()
    {
        await Ctx.EnsureLoadedAsync(Seller);
        if (!HasStore) return;
        await LoadStoreAsync();
    }

    protected virtual Task LoadStoreAsync() => Task.CompletedTask;

    public override void Dispose()
    {
        Ctx.Changed -= OnStoreChanged;
        base.Dispose();
    }
}
