using Sky17.Domain;

namespace Sky17.Application;

public interface ICatalogService
{
    Task<IReadOnlyList<CategoryDto>> GetPlatformCategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<StoreCardDto>> GetStoresAsync(string? search = null, int take = 30, CancellationToken ct = default);
    Task<StoreDetailDto?> GetStoreAsync(int storeId, CancellationToken ct = default);
    Task<IReadOnlyList<ProductCardDto>> SearchProductsAsync(ProductQuery query, CancellationToken ct = default);
    Task<ProductDetailDto?> GetProductAsync(int productId, CancellationToken ct = default);
    Task<bool> IsServiceAvailableAsync(CancellationToken ct = default);
    Task<string> GetBrandNameAsync(CancellationToken ct = default);
}

public interface ICartService
{
    Task<CartDto> GetAsync(CancellationToken ct = default);
    Task<AddToCartResult> AddAsync(int productId, int quantity = 1, bool replaceOtherStore = false, CancellationToken ct = default);
    Task<CartDto> SetQuantityAsync(int productId, int quantity, CancellationToken ct = default);
    Task<CartDto> ClearAsync(CancellationToken ct = default);
    /// <summary>Delivery fee preview for the current cart to the given drop-off point (road distance via routing service).</summary>
    Task<DeliveryQuoteDto> QuoteAsync(double? latitude, double? longitude, CancellationToken ct = default);
}

public interface ICustomerOrderService
{
    Task<PlaceOrderResult> PlaceOrderAsync(CheckoutInput input, CancellationToken ct = default);
    Task<PagedResult<OrderSummaryDto>> GetMyOrdersAsync(int page = 1, int pageSize = 10, CancellationToken ct = default);
    Task<OrderDetailDto?> GetMyOrderAsync(string code, CancellationToken ct = default);
    Task CancelAsync(string code, string reason, CancellationToken ct = default);
}

public interface IAddressService
{
    Task<IReadOnlyList<AddressDto>> GetMineAsync(CancellationToken ct = default);
    Task<AddressInput?> GetForEditAsync(int id, CancellationToken ct = default);
    Task<int> SaveAsync(AddressInput input, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task SetDefaultAsync(int id, CancellationToken ct = default);
}

public interface IProfileService
{
    Task<ProfileDto> GetMineAsync(CancellationToken ct = default);
    Task UpdateMineAsync(ProfileInput input, CancellationToken ct = default);
}

public interface INotificationService
{
    Task<IReadOnlyList<NotificationDto>> GetMineAsync(int take = 30, CancellationToken ct = default);
    Task<int> GetUnreadCountAsync(CancellationToken ct = default);
    Task MarkReadAsync(int id, CancellationToken ct = default);
    Task MarkAllReadAsync(CancellationToken ct = default);
}

public interface ISellerService
{
    Task<IReadOnlyList<MyStoreDto>> GetMyStoresAsync(CancellationToken ct = default);
    Task<SellerDashboardDto> GetDashboardAsync(int storeId, CancellationToken ct = default);

    Task<StoreProfileInput> GetStoreProfileAsync(int storeId, CancellationToken ct = default);
    Task UpdateStoreProfileAsync(int storeId, StoreProfileInput input, CancellationToken ct = default);

    Task<IReadOnlyList<SellerProductDto>> GetProductsAsync(int storeId, string? search = null, bool includeInactive = true, CancellationToken ct = default);
    Task<SellerProductEditDto> GetProductAsync(int storeId, int productId, CancellationToken ct = default);
    Task<int> SaveProductAsync(int storeId, ProductInput input, CancellationToken ct = default);
    Task DeleteProductAsync(int storeId, int productId, CancellationToken ct = default);
    Task SetStockAsync(int storeId, int productId, int stock, CancellationToken ct = default);
    Task SetAvailabilityAsync(int storeId, int productId, bool isAvailable, CancellationToken ct = default);
    Task<ProductImageDto> AddProductImageAsync(int storeId, int productId, Stream content, string fileName, string contentType, CancellationToken ct = default);
    Task RemoveProductImageAsync(int storeId, int productId, int imageId, CancellationToken ct = default);

    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(int storeId, CancellationToken ct = default);
    Task<int> SaveCategoryAsync(int storeId, CategoryInput input, CancellationToken ct = default);
    Task DeleteCategoryAsync(int storeId, int categoryId, CancellationToken ct = default);

    Task<PagedResult<OrderSummaryDto>> GetOrdersAsync(int storeId, OrderFilter filter, CancellationToken ct = default);
    Task<OrderDetailDto> GetOrderAsync(int storeId, int orderId, CancellationToken ct = default);
    Task ChangeOrderStatusAsync(int storeId, int orderId, OrderStatus target, string? reason, CancellationToken ct = default);
    Task MarkFoodReadyAsync(int storeId, int orderId, CancellationToken ct = default);

    Task<SalesReportDto> GetSalesReportAsync(int storeId, DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<IReadOnlyList<SettlementDto>> GetSettlementsAsync(int storeId, CancellationToken ct = default);

    Task<IReadOnlyList<StoreMemberDto>> GetMembersAsync(int storeId, CancellationToken ct = default);
    Task AddMemberAsync(int storeId, string email, StoreMemberRole role, CancellationToken ct = default);
    Task RemoveMemberAsync(int storeId, int memberId, CancellationToken ct = default);
}

public interface IDriverService
{
    Task<DriverProfileDto> GetProfileAsync(CancellationToken ct = default);
    Task UpdateProfileAsync(DriverProfileInput input, CancellationToken ct = default);
    Task SetOnlineAsync(bool online, CancellationToken ct = default);
    Task<DriverDashboardDto> GetDashboardAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DeliveryJobDto>> GetMyActiveJobsAsync(CancellationToken ct = default);
    Task<DeliveryJobDto> GetJobAsync(int orderId, CancellationToken ct = default);
    /// <summary>Heartbeat from the driver app while online (GPS or manually pinned position).</summary>
    Task UpdateLocationAsync(double latitude, double longitude, CancellationToken ct = default);
    Task<DispatchOfferDto?> GetPendingOfferAsync(CancellationToken ct = default);
    /// <summary>Accepts the offer; returns the order id.</summary>
    Task<int> AcceptOfferAsync(int offerId, CancellationToken ct = default);
    Task DeclineOfferAsync(int offerId, CancellationToken ct = default);
    Task<DriverOfferStatsDto> GetOfferStatsAsync(CancellationToken ct = default);
    Task ArriveAtStoreAsync(int orderId, CancellationToken ct = default);
    /// <summary>Food picked up → Delivering.</summary>
    Task ConfirmPickupAsync(int orderId, CancellationToken ct = default);
    Task ArriveAtCustomerAsync(int orderId, CancellationToken ct = default);
    Task ConfirmDeliveredAsync(int orderId, CancellationToken ct = default);
    Task ReportFailedAsync(int orderId, string reason, CancellationToken ct = default);
    /// <summary>Driver gives up the trip before pickup; the order goes back to driver search.</summary>
    Task CancelTripAsync(int orderId, string reason, CancellationToken ct = default);
    Task<PagedResult<DeliveryJobDto>> GetHistoryAsync(int page = 1, int pageSize = 10, CancellationToken ct = default);
    Task<DriverEarningsDto> GetEarningsAsync(CancellationToken ct = default);
}

public interface IAdminService
{
    Task<AdminDashboardDto> GetDashboardAsync(CancellationToken ct = default);

    Task<PagedResult<OrderSummaryDto>> SearchOrdersAsync(OrderFilter filter, CancellationToken ct = default);
    Task<OrderDetailDto> GetOrderAsync(int orderId, CancellationToken ct = default);
    Task<IReadOnlyList<DriverOptionDto>> GetAssignableDriversAsync(CancellationToken ct = default);
    Task AssignDriverAsync(int orderId, string driverId, CancellationToken ct = default);
    Task UnassignDriverAsync(int orderId, string reason, CancellationToken ct = default);
    Task CancelOrderAsync(int orderId, string reason, CancellationToken ct = default);
    Task MarkDeliveryFailedAsync(int orderId, string reason, CancellationToken ct = default);
    Task ResolveFailedDeliveryAsync(int orderId, bool retry, string reason, CancellationToken ct = default);
    Task SetDistanceAsync(int orderId, int distanceMeters, string reason, CancellationToken ct = default);

    Task<IReadOnlyList<AdminStoreDto>> GetStoresAsync(StoreStatus? status = null, string? search = null, CancellationToken ct = default);
    Task<AdminStoreDetailDto> GetStoreAsync(int storeId, CancellationToken ct = default);
    Task SetStoreStatusAsync(int storeId, StoreStatus status, string? reason, CancellationToken ct = default);

    Task<IReadOnlyList<AdminDriverDto>> GetDriversAsync(DriverStatus? status = null, string? search = null, CancellationToken ct = default);
    Task<AdminDriverDetailDto> GetDriverAsync(string userId, CancellationToken ct = default);
    Task SetDriverStatusAsync(string userId, DriverStatus status, string? reason, CancellationToken ct = default);

    Task<PagedResult<AdminCustomerDto>> GetCustomersAsync(string? search, int page = 1, CancellationToken ct = default);
    Task<AdminCustomerDetailDto> GetCustomerAsync(string userId, CancellationToken ct = default);

    Task<PagedResult<AdminUserDto>> GetUsersAsync(string? search, string? role, int page = 1, CancellationToken ct = default);
    Task SetUserActiveAsync(string userId, bool active, CancellationToken ct = default);
    Task SetUserRolesAsync(string userId, IReadOnlyCollection<string> roles, CancellationToken ct = default);

    Task<PagedResult<AuditLogDto>> GetAuditLogsAsync(string? search, int page = 1, CancellationToken ct = default);
    Task<PagedResult<AdminNotificationDto>> GetNotificationHistoryAsync(int page = 1, CancellationToken ct = default);

    Task<DeliverySettingsInput> GetSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(DeliverySettingsInput input, CancellationToken ct = default);
    Task<IReadOnlyList<CategoryDto>> GetPlatformCategoriesAsync(CancellationToken ct = default);
    Task<int> SavePlatformCategoryAsync(CategoryInput input, CancellationToken ct = default);
}

public interface IFinanceService
{
    Task<FinanceOverviewDto> GetOverviewAsync(CancellationToken ct = default);
    Task<PagedResult<CodRecordDto>> GetCodRecordsAsync(string? driverId, int page = 1, CancellationToken ct = default);
    Task<IReadOnlyList<PartyBalanceDto>> GetStoreBalancesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PartyBalanceDto>> GetDriverBalancesAsync(CancellationToken ct = default);
    Task<int> CreateStoreSettlementAsync(int storeId, string? note, CancellationToken ct = default);
    Task<int> CreateDriverSettlementAsync(string driverId, string? note, CancellationToken ct = default);
    Task ConfirmSettlementAsync(int settlementId, CancellationToken ct = default);
    Task CancelSettlementAsync(int settlementId, string reason, CancellationToken ct = default);
    Task<PagedResult<SettlementDto>> GetSettlementsAsync(SettlementType? type, SettlementStatus? status, int page = 1, CancellationToken ct = default);
    Task<SettlementDetailDto> GetSettlementAsync(int settlementId, CancellationToken ct = default);
}

public interface IAccountService
{
    Task<RegisterResult> RegisterAsync(RegisterInput input, CancellationToken ct = default);
    /// <summary>Landing URL for the user's primary role after login.</summary>
    Task<string> GetHomeUrlAsync(string userId, CancellationToken ct = default);
}

/// <summary>Validated image storage for product images.</summary>
public interface IFileStorage
{
    Task<string> SaveImageAsync(Stream content, string fileName, string contentType, string folder, CancellationToken ct = default);
    Task DeleteAsync(string url, CancellationToken ct = default);
}

/// <summary>
/// GrabFood-style dispatcher: offers each searching order to the nearest free driver, one at a time,
/// with a timeout; also auto-cancels orders the store never accepted and alerts admins when nobody is found.
/// </summary>
public interface IDispatchService
{
    /// <summary>Run one dispatch cycle (called every few seconds by a background worker).</summary>
    Task<DispatchCycleResult> RunCycleAsync(CancellationToken ct = default);
    /// <summary>Try to offer this order immediately (e.g. right after the store accepted it).</summary>
    Task DispatchOrderAsync(int orderId, CancellationToken ct = default);
}
