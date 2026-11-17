using System.ComponentModel.DataAnnotations;
using Sky17.Domain;

namespace Sky17.Application;

// ---------------------------------------------------------------- Seller

/// <summary>An order waiting for the store to accept or reject (seller ringing panel).</summary>
public sealed record IncomingOrderDto(
    int OrderId, string Code, int StoreId, string StoreName, string StoreEmoji, string RecipientName,
    IReadOnlyList<OrderItemDto> Items, long ProductTotal, long GrandTotal, string DeliveryAddress, string? DeliveryNote,
    int? DistanceMeters, DateTime PlacedAtUtc, DateTime AutoCancelAtUtc)
{
    public int ItemCount => Items.Sum(i => i.Quantity);
}

public sealed record MyStoreDto(int StoreId, string Name, string Emoji, StoreStatus Status, StoreMemberRole Role);

public sealed record SellerDashboardDto(
    int OrdersToday, long RevenueToday, int PendingCount, int InProgressCount, int ActiveProducts, int LowStockProducts,
    int ConfirmRatePercent, IReadOnlyList<DailyPoint> Last7Days, IReadOnlyList<OrderSummaryDto> ActionOrders,
    IReadOnlyList<SellerProductDto> TopProducts);

public sealed record SellerProductDto(
    int Id, string Name, string Emoji, string? ImageUrl, string? CategoryName, int? CategoryId,
    long Price, int Stock, bool IsActive, bool IsAvailable, int SoldQuantity);

public sealed class ProductInput
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Vui lòng nhập tên sản phẩm"), StringLength(120)]
    public string Name { get; set; } = "";

    [StringLength(1000)] public string? Description { get; set; }

    [Range(1000, 100_000_000, ErrorMessage = "Giá từ 1.000đ đến 100.000.000đ")]
    public long Price { get; set; }

    [Range(0, 100_000, ErrorMessage = "Tồn kho không hợp lệ")]
    public int Stock { get; set; }

    public int? CategoryId { get; set; }

    [StringLength(8)] public string Emoji { get; set; } = "🍽️";
    public bool IsActive { get; set; } = true;
    public bool IsAvailable { get; set; } = true;
}

public sealed record ProductImageDto(int Id, string Url);

public sealed record SellerProductEditDto(ProductInput Input, IReadOnlyList<ProductImageDto> Images);

public sealed class CategoryInput
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập tên danh mục"), StringLength(60)] public string Name { get; set; } = "";
    [StringLength(8)] public string Emoji { get; set; } = "🍽️";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class StoreProfileInput
{
    [Required(ErrorMessage = "Nhập tên cửa hàng"), StringLength(100)] public string Name { get; set; } = "";
    [StringLength(500)] public string? Description { get; set; }
    [StringLength(60)] public string? Tagline { get; set; }

    [Required(ErrorMessage = "Nhập số điện thoại")]
    [VnPhone]
    public string PhoneNumber { get; set; } = "";

    [Required(ErrorMessage = "Nhập địa chỉ"), StringLength(200)] public string Address { get; set; } = "";
    [StringLength(8)] public string Emoji { get; set; } = "🏪";
    public TimeOnly OpenTime { get; set; } = new(7, 0);
    public TimeOnly CloseTime { get; set; } = new(22, 0);
    public bool IsAcceptingOrders { get; set; } = true;
    [Range(-90, 90)] public double? Latitude { get; set; }
    [Range(-180, 180)] public double? Longitude { get; set; }
}

public sealed record StoreMemberDto(int Id, string UserId, string FullName, string Email, StoreMemberRole Role);

public sealed record ProductSalesDto(int ProductId, string Name, string Emoji, int Quantity, long Revenue);

public sealed record SalesReportDto(
    DateOnly From, DateOnly To, int DeliveredOrders, int CancelledOrders, long ProductRevenue, long PlatformFees,
    long NetReceivable, long SettledAmount, long UnsettledAmount,
    IReadOnlyList<DailyPoint> Daily, IReadOnlyList<ProductSalesDto> TopProducts);

public sealed record SettlementDto(
    int Id, string Code, SettlementType Type, int? StoreId, string? DriverId, string PartyName,
    long Amount, long GrossCod, long DriverEarnings, SettlementStatus Status, int OrderCount, string? Note,
    DateTime CreatedAtUtc, DateTime? ConfirmedAtUtc, string? CancelReason);

public sealed record SettlementItemDto(int OrderId, string OrderCode, long Amount, DateTime? DeliveredAtUtc);

public sealed record SettlementDetailDto(SettlementDto Settlement, IReadOnlyList<SettlementItemDto> Items);

// ---------------------------------------------------------------- Driver

public sealed record DriverProfileDto(
    string UserId, string FullName, string? PhoneNumber, string Email, string VehicleType, string LicensePlate,
    string? IdentityNumber, string ServiceArea, DriverStatus Status, string? StatusReason, bool IsOnline)
{
    public double? LastLatitude { get; init; }
    public double? LastLongitude { get; init; }
    public DateTime? LastLocationAtUtc { get; init; }
}

public sealed class DriverProfileInput
{
    [Required(ErrorMessage = "Nhập loại xe"), StringLength(30)] public string VehicleType { get; set; } = "Xe máy";
    [Required(ErrorMessage = "Nhập biển số"), StringLength(20)] public string LicensePlate { get; set; } = "";
    [StringLength(20)] public string? IdentityNumber { get; set; }
    [StringLength(60)] public string ServiceArea { get; set; } = "Bình Dương";
}

public sealed record DeliveryJobDto(
    int OrderId, string Code, OrderStatus Status, string StoreName, string PickupAddress, string PickupPhone,
    string RecipientName, string? CustomerPhone, string DropoffAddress, string? DeliveryNote,
    long DeliveryFee, long DriverEarning, long CodAmount, int? DistanceMeters, int ItemCount,
    DateTime PlacedAtUtc, DateTime? CompletedAtUtc, bool IsMine, string? FailureReason)
{
    public double? PickupLatitude { get; init; }
    public double? PickupLongitude { get; init; }
    /// <summary>Building / floor / alley / landmark (assigned driver only).</summary>
    public string? DropoffDetail { get; init; }
    /// <summary>Only revealed to the assigned driver (null for open jobs).</summary>
    public double? DropoffLatitude { get; init; }
    public double? DropoffLongitude { get; init; }
    public PreparationState PreparationState { get; init; }
}

public sealed record DriverDashboardDto(
    DriverStatus Status, bool IsOnline, long EarningToday, int ActiveCount, int CompletedToday,
    long CodHolding, long OwedToPlatform, int AvailableJobs, int MaxActive);

public sealed record CodRecordDto(int Id, int OrderId, string OrderCode, string DriverId, string DriverName, CodTransactionType Type, long Amount, int? SettlementId, DateTime CreatedAtUtc);

public sealed record DriverEarningsDto(
    long TotalEarnings, long UnsettledEarnings, long CodHolding, long OwedToPlatform, int CompletedTrips,
    IReadOnlyList<DailyPoint> Last7Days, IReadOnlyList<CodRecordDto> CodRecords, IReadOnlyList<SettlementDto> Settlements);

// ---------------------------------------------------------------- Admin

public sealed record AdminDashboardDto(
    int TotalOrders, int OrdersToday, int PendingOrders, int InDeliveryOrders, int DeliveredOrders, int FailedOrders,
    int ActiveStores, int PendingStores, int ActiveDrivers, int OnlineDrivers, int PendingDrivers,
    long GrossMerchandiseValue, long DeliveryFeeTotal, long OutstandingCod, long PlatformRevenue,
    IReadOnlyList<DailyPoint> Last7Days, IReadOnlyList<(OrderStatus Status, int Count)> TodayByStatus,
    IReadOnlyList<AuditLogDto> RecentActivities, IReadOnlyList<OrderSummaryDto> RecentOrders);

public sealed record AdminStoreDto(
    int Id, string Name, string Emoji, string? Tagline, string Address, string PhoneNumber, StoreStatus Status,
    string? StatusReason, string OwnerName, string OwnerEmail, int ProductCount, int DeliveredOrders, long Revenue, DateTime CreatedAtUtc);

public sealed record AdminStoreDetailDto(
    AdminStoreDto Store, string? Description, TimeOnly OpenTime, TimeOnly CloseTime, bool IsAcceptingOrders,
    IReadOnlyList<StoreMemberDto> Members, IReadOnlyList<SellerProductDto> Products,
    IReadOnlyList<OrderSummaryDto> RecentOrders, long PendingReceivable);

public sealed record AdminDriverDto(
    string UserId, string FullName, string Email, string? PhoneNumber, string VehicleType, string LicensePlate,
    DriverStatus Status, string? StatusReason, bool IsOnline, int ActiveDeliveries, int CompletedDeliveries,
    long CodHolding, long OwedToPlatform, bool AccountActive, DateTime CreatedAtUtc);

public sealed record AdminDriverDetailDto(AdminDriverDto Driver, string? IdentityNumber, string ServiceArea, IReadOnlyList<DeliveryJobDto> Deliveries);

public sealed record DriverOptionDto(string UserId, string FullName, string LicensePlate, bool IsOnline, int ActiveDeliveries);

public sealed record AdminCustomerDto(string UserId, string FullName, string Email, string? PhoneNumber, bool IsActive, int OrderCount, long TotalSpent, DateTime CreatedAtUtc);

public sealed record AdminCustomerDetailDto(AdminCustomerDto Customer, IReadOnlyList<AddressDto> Addresses, IReadOnlyList<OrderSummaryDto> Orders);

public sealed record AdminUserDto(string UserId, string FullName, string Email, string? PhoneNumber, IReadOnlyList<string> Roles, bool IsActive, DateTime CreatedAtUtc);

public sealed record AuditLogDto(long Id, string? UserName, string Action, string EntityType, string? EntityId, string? Details, DateTime CreatedAtUtc);

public sealed record AdminNotificationDto(int Id, string UserName, string Type, string Title, string Message, bool IsRead, DateTime CreatedAtUtc);

public sealed record PartyBalanceDto(string PartyKey, string Name, int OrderCount, long Amount, long GrossCod, long Earnings);

public sealed record FinanceOverviewDto(
    long CodHeldByDrivers, long OwedByDrivers, long PayableToStores, long DriverEarningsUnsettled,
    long PlatformRevenue, long ConfirmedSettlements, int PendingSettlements);

public sealed class DeliverySettingsInput
{
    [Range(0, 1_000_000, ErrorMessage = "Phí cơ bản không hợp lệ")] public long BaseFee { get; set; }
    [Range(0, 100_000, ErrorMessage = "Khoảng cách cơ bản không hợp lệ")] public int BaseDistanceMeters { get; set; }
    [Range(0, 1_000_000, ErrorMessage = "Phí mỗi km không hợp lệ")] public long FeePerKm { get; set; }
    [Range(0, 5_000, ErrorMessage = "Hoa hồng tối đa 50%")] public int PlatformFeeBps { get; set; }
    [Range(0, 10_000, ErrorMessage = "Tỷ lệ 0–100%")] public int DriverShareBps { get; set; }
    public bool ServiceAvailable { get; set; } = true;
    [Required(ErrorMessage = "Nhập tên thương hiệu"), StringLength(80)] public string BrandName { get; set; } = "";
    [StringLength(20)] public string? SupportPhone { get; set; }
    [StringLength(80)] public string ServiceArea { get; set; } = "";
    [Range(1, 10, ErrorMessage = "Từ 1 đến 10 đơn")] public int MaxActiveDeliveriesPerDriver { get; set; } = 2;
    [Range(10, 120, ErrorMessage = "Từ 10 đến 120 giây")] public int OfferTimeoutSeconds { get; set; } = 30;
    [Range(1, 50, ErrorMessage = "Từ 1 đến 50 km")] public int DispatchRadiusKm { get; set; } = 20;
    [Range(1, 120, ErrorMessage = "Từ 1 đến 120 phút")] public int LocationFreshMinutes { get; set; } = 15;
    [Range(1, 60, ErrorMessage = "Từ 1 đến 60 phút")] public int StoreConfirmTimeoutMinutes { get; set; } = 10;
    [Range(1, 120, ErrorMessage = "Từ 1 đến 120 phút")] public int NoDriverAlertMinutes { get; set; } = 5;
}

// ---------------------------------------------------------------- Dispatch (GrabFood-style)

/// <summary>A job offer shown to a driver with a countdown.</summary>
public sealed record DispatchOfferDto(
    int OfferId, int OrderId, string Code, string StoreName, string PickupAddress, double? PickupLatitude, double? PickupLongitude,
    string DropoffAddress, int? DriverDistanceMeters, int? TripDistanceMeters, long DriverEarning, long CodAmount, int ItemCount,
    DateTime OfferedAtUtc, DateTime ExpiresAtUtc, int TimeoutSeconds);

public sealed record DispatchOfferHistoryDto(int OfferId, string DriverId, string DriverName, DispatchOfferStatus Status, int? DriverDistanceMeters, DateTime OfferedAtUtc, DateTime? RespondedAtUtc);

public sealed record DispatchCycleResult(int OffersExpired, int OffersSent, int OrdersAutoCancelled, int Alerts);

public sealed record DriverOfferStatsDto(int Offered, int Accepted, int Declined, int Expired)
{
    public int AcceptanceRatePercent => Offered == 0 ? 100 : Accepted * 100 / Offered;
}
