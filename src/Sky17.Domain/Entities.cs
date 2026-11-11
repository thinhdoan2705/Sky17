namespace Sky17.Domain;

// ---------------------------------------------------------------- Users

/// <summary>Business profile for an identity user (1:1 with ApplicationUser via UserId).</summary>
public class UserProfile : AuditableEntity
{
    public required string UserId { get; set; }
    public required string FullName { get; set; }
    public string? PhoneNumber { get; set; }
    public string? AvatarUrl { get; set; }
}

public class Address : AuditableEntity
{
    public required string UserId { get; set; }
    public string Label { get; set; } = "Nhà";
    public required string RecipientName { get; set; }
    public required string PhoneNumber { get; set; }
    /// <summary>House number and street name.</summary>
    public required string Street { get; set; }
    /// <summary>Ward/commune code from the national administrative catalog (2025, two-level: province → ward).</summary>
    public string? WardCode { get; set; }
    /// <summary>Ward/commune full name snapshot, e.g. "Phường Thủ Dầu Một".</summary>
    public string? Ward { get; set; }
    public string? ProvinceCode { get; set; }
    /// <summary>Province/municipality full name snapshot, e.g. "Thành phố Hồ Chí Minh".</summary>
    public string City { get; set; } = "";
    /// <summary>Address details: building, floor, alley, landmark…</summary>
    public string? Note { get; set; }
    public bool IsDefault { get; set; }
    /// <summary>WGS84 coordinates picked on the map (optional).</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public string FullText => AddressText.Compose(Street, Ward, City);
}

/// <summary>Formats "Số nhà đường, Phường/Xã, Tỉnh/Thành phố".</summary>
public static class AddressText
{
    public static string Compose(params string?[] parts) =>
        string.Join(", ", parts.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
}

// ---------------------------------------------------------------- Stores & catalog

public class Store : AuditableEntity, IVersioned
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    /// <summary>Short cuisine / type label, e.g. "Đồ uống".</summary>
    public string? Tagline { get; set; }
    public required string PhoneNumber { get; set; }
    public required string Address { get; set; }
    public string Emoji { get; set; } = "🏪";
    public string? LogoUrl { get; set; }
    /// <summary>Pickup location (WGS84). Required for distance-based pricing.</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public StoreStatus Status { get; set; } = StoreStatus.PendingApproval;
    public string? StatusReason { get; set; }
    /// <summary>Manual open/closed switch ("tạm nghỉ").</summary>
    public bool IsAcceptingOrders { get; set; } = true;
    public TimeOnly OpenTime { get; set; } = new(7, 0);
    public TimeOnly CloseTime { get; set; } = new(22, 0);
    public DateTime? ApprovedAtUtc { get; set; }
    public long Version { get; set; }

    public ICollection<StoreMember> Members { get; set; } = [];
    public ICollection<Product> Products { get; set; } = [];
    public ICollection<Category> Categories { get; set; } = [];

    /// <summary>True when the store is within business hours at the given local (Vietnam) time.</summary>
    public bool IsWithinHours(TimeOnly localTime)
    {
        if (OpenTime == CloseTime) return true; // 24h
        return OpenTime < CloseTime
            ? localTime >= OpenTime && localTime < CloseTime
            : localTime >= OpenTime || localTime < CloseTime; // overnight
    }

    public bool CanReceiveOrders(TimeOnly localTime) =>
        Status == StoreStatus.Active && IsAcceptingOrders && IsWithinHours(localTime);
}

public class StoreMember : AuditableEntity
{
    public int StoreId { get; set; }
    public Store Store { get; set; } = null!;
    public required string UserId { get; set; }
    public StoreMemberRole Role { get; set; }
}

/// <summary>Product category. StoreId == null means a platform-wide category (shown as chips to customers).</summary>
public class Category : AuditableEntity
{
    public int? StoreId { get; set; }
    public Store? Store { get; set; }
    public required string Name { get; set; }
    public string Emoji { get; set; } = "🍽️";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Product : AuditableEntity, IVersioned
{
    public int StoreId { get; set; }
    public Store Store { get; set; } = null!;
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    /// <summary>Price in VND (smallest unit).</summary>
    public long Price { get; set; }
    public int Stock { get; set; }
    public string Emoji { get; set; } = "🍽️";
    /// <summary>Visible in the catalog. Inactive products are hidden (soft delete).</summary>
    public bool IsActive { get; set; } = true;
    /// <summary>Temporarily available / unavailable for ordering.</summary>
    public bool IsAvailable { get; set; } = true;
    public long Version { get; set; }

    public ICollection<ProductImage> Images { get; set; } = [];
}

public class ProductImage : AuditableEntity
{
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public required string Url { get; set; }
    public int SortOrder { get; set; }
}

// ---------------------------------------------------------------- Cart

/// <summary>Single-store cart (Lite MVP). One cart per user.</summary>
public class Cart : AuditableEntity
{
    public required string UserId { get; set; }
    public int? StoreId { get; set; }
    public Store? Store { get; set; }
    public ICollection<CartItem> Items { get; set; } = [];
}

public class CartItem : AuditableEntity
{
    public int CartId { get; set; }
    public Cart Cart { get; set; } = null!;
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int Quantity { get; set; }
}

// ---------------------------------------------------------------- Orders

public class Order : AuditableEntity, IVersioned
{
    public required string Code { get; set; }
    public required string CustomerId { get; set; }
    public int StoreId { get; set; }
    public Store Store { get; set; } = null!;
    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    // Delivery address snapshot
    public required string RecipientName { get; set; }
    public required string RecipientPhone { get; set; }
    public required string DeliveryAddress { get; set; }
    /// <summary>Address details for the driver: building, floor, alley, landmark…</summary>
    public string? DeliveryAddressDetail { get; set; }
    public string? DeliveryWardCode { get; set; }
    public string? DeliveryProvinceCode { get; set; }
    public string? DeliveryNote { get; set; }

    // Pickup snapshot
    public required string PickupStoreName { get; set; }
    public required string PickupAddress { get; set; }
    public required string PickupPhone { get; set; }

    // Coordinate snapshots (null when unknown)
    public double? PickupLatitude { get; set; }
    public double? PickupLongitude { get; set; }
    public double? DropoffLatitude { get; set; }
    public double? DropoffLongitude { get; set; }
    public DistanceSource DistanceSource { get; set; }

    /// <summary>Distance in meters, only when known (set manually by admin). Null = not measured.</summary>
    public int? DistanceMeters { get; set; }

    // Money (VND, long)
    public long ProductTotal { get; set; }
    public long DeliveryFee { get; set; }
    public long DiscountAmount { get; set; }
    public long GrandTotal { get; set; }
    public long CollectedCodAmount { get; set; }
    public long PlatformFee { get; set; }
    public long StoreReceivable { get; set; }
    public long DriverEarning { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.CashOnDelivery;
    public OrderSettlementState StoreSettlementState { get; set; }
    public OrderSettlementState DriverSettlementState { get; set; }

    /// <summary>Current (or last) driver user id.</summary>
    public string? DriverId { get; set; }
    public string? CancelReason { get; set; }
    public string? FailureReason { get; set; }
    public required string IdempotencyKey { get; set; }
    public DateTime PlacedAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }

    // Kitchen & dispatch
    public PreparationState PreparationState { get; set; }
    public DateTime? FoodReadyAtUtc { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    /// <summary>When the current driver search started (reset when the order goes back to searching).</summary>
    public DateTime? DispatchStartedAtUtc { get; set; }
    /// <summary>Set once admins were alerted that no driver could be found.</summary>
    public DateTime? DispatchAlertedAtUtc { get; set; }
    public long Version { get; set; }

    public ICollection<OrderItem> Items { get; set; } = [];
    public ICollection<OrderStatusHistory> History { get; set; } = [];
    public ICollection<DeliveryAssignment> Assignments { get; set; } = [];
    public ICollection<DispatchOffer> Offers { get; set; } = [];
    public Payment? Payment { get; set; }
}

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public int ProductId { get; set; }
    public required string ProductName { get; set; }
    public string Emoji { get; set; } = "🍽️";
    public long UnitPrice { get; set; }
    public int Quantity { get; set; }
    public long LineTotal { get; set; }
}

public class OrderStatusHistory
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public OrderStatus? PreviousStatus { get; set; }
    public OrderStatus NewStatus { get; set; }
    public string? ChangedByUserId { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public string? Reason { get; set; }
}

// ---------------------------------------------------------------- Delivery

public class DriverProfile : AuditableEntity, IVersioned
{
    public required string UserId { get; set; }
    public string VehicleType { get; set; } = "Xe máy";
    public required string LicensePlate { get; set; }
    public string? IdentityNumber { get; set; }
    public string ServiceArea { get; set; } = "Bình Dương";
    public DriverStatus Status { get; set; } = DriverStatus.PendingApproval;
    public string? StatusReason { get; set; }
    public bool IsOnline { get; set; }
    /// <summary>Last reported GPS position (heartbeat from the driver app while online).</summary>
    public double? LastLatitude { get; set; }
    public double? LastLongitude { get; set; }
    public DateTime? LastLocationAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public long Version { get; set; }
}

public class DeliveryAssignment : AuditableEntity
{
    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public required string DriverId { get; set; }
    public AssignmentStatus Status { get; set; } = AssignmentStatus.Assigned;
    /// <summary>Only one active assignment per order (enforced by a filtered unique index).</summary>
    public bool IsActive { get; set; } = true;
    /// <summary>Admin who assigned manually; null when the driver self-accepted.</summary>
    public string? AssignedByUserId { get; set; }
    public DateTime AssignedAtUtc { get; set; }
    public DateTime? PickedUpAtUtc { get; set; }
    public DateTime? DeliveringAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? Note { get; set; }
}

public class DeliveryRateRule : AuditableEntity
{
    public string Name { get; set; } = "Mặc định";
    public long BaseFee { get; set; } = 15_000;
    public int BaseDistanceMeters { get; set; } = 2_000;
    public long FeePerKm { get; set; } = 5_000;
    /// <summary>Platform commission on product total in basis points (1000 = 10%).</summary>
    public int PlatformFeeBps { get; set; } = 1_000;
    /// <summary>Driver share of the delivery fee in basis points (8000 = 80%).</summary>
    public int DriverShareBps { get; set; } = 8_000;
    public bool IsActive { get; set; } = true;
}

// ---------------------------------------------------------------- Finance

public class Payment : AuditableEntity
{
    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public PaymentMethod Method { get; set; } = PaymentMethod.CashOnDelivery;
    public long Amount { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public DateTime? CollectedAtUtc { get; set; }
    public string? CollectedByUserId { get; set; }
}

public class CodTransaction : AuditableEntity
{
    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public required string DriverId { get; set; }
    public CodTransactionType Type { get; set; }
    public long Amount { get; set; }
    public int? SettlementId { get; set; }
    public string? Note { get; set; }
}

public class Settlement : AuditableEntity, IVersioned
{
    public required string Code { get; set; }
    public SettlementType Type { get; set; }
    public int? StoreId { get; set; }
    public Store? Store { get; set; }
    public string? DriverId { get; set; }
    /// <summary>
    /// Store settlement: amount the platform pays the store.
    /// Driver settlement: net cash the driver remits to the platform (COD collected − driver earnings).
    /// </summary>
    public long Amount { get; set; }
    public long GrossCod { get; set; }
    public long DriverEarnings { get; set; }
    public SettlementStatus Status { get; set; } = SettlementStatus.Pending;
    public string? Note { get; set; }
    public required string CreatedByUserId { get; set; }
    public string? ConfirmedByUserId { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public string? CancelReason { get; set; }
    public long Version { get; set; }

    public ICollection<SettlementItem> Items { get; set; } = [];
}

/// <summary>Links an order to a settlement. Unique (OrderId, Type) prevents double settlement.</summary>
public class SettlementItem
{
    public int Id { get; set; }
    public int SettlementId { get; set; }
    public Settlement Settlement { get; set; } = null!;
    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public SettlementType Type { get; set; }
    public long Amount { get; set; }
}

// ---------------------------------------------------------------- System

public class Notification
{
    public int Id { get; set; }
    public required string UserId { get; set; }
    public required string Type { get; set; }
    public required string Title { get; set; }
    public required string Message { get; set; }
    public string? Link { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    public string? UserId { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class SystemSetting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedByUserId { get; set; }
}

public static class SettingKeys
{
    public const string BrandName = "Brand.Name";
    public const string SupportPhone = "Brand.SupportPhone";
    public const string ServiceAvailable = "Service.Available";
    public const string ServiceArea = "Service.Area";
    public const string MaxActiveDeliveriesPerDriver = "Driver.MaxActiveDeliveries";
    public const string OfferTimeoutSeconds = "Dispatch.OfferTimeoutSeconds";
    public const string DispatchRadiusKm = "Dispatch.RadiusKm";
    public const string LocationFreshMinutes = "Dispatch.LocationFreshMinutes";
    public const string StoreConfirmTimeoutMinutes = "Dispatch.StoreConfirmTimeoutMinutes";
    public const string NoDriverAlertMinutes = "Dispatch.NoDriverAlertMinutes";
}

/// <summary>
/// A time-limited job offer sent to one driver (GrabFood-style dispatch).
/// Only one pending offer per order and per driver at a time.
/// </summary>
public class DispatchOffer
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public required string DriverId { get; set; }
    public DispatchOfferStatus Status { get; set; } = DispatchOfferStatus.Pending;
    /// <summary>Straight-line distance driver → store at offer time (used for ranking/display only, never pricing).</summary>
    public int? DriverDistanceMeters { get; set; }
    public DateTime OfferedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RespondedAtUtc { get; set; }
}
