using System.ComponentModel.DataAnnotations;
using Sky17.Domain;

namespace Sky17.Application;

// ---------------------------------------------------------------- Catalog

public sealed record CategoryDto(int Id, string Name, string Emoji, int? StoreId, int SortOrder, bool IsActive, int ProductCount);

public sealed record StoreCardDto(
    int Id, string Name, string Emoji, string? LogoUrl, string? Tagline, string Address,
    bool IsOpenNow, TimeOnly OpenTime, TimeOnly CloseTime, int ProductCount);

public sealed record ProductCardDto(
    int Id, int StoreId, string StoreName, string Name, string Emoji, string? ImageUrl,
    long Price, int Stock, bool IsAvailable, string? CategoryName);

public sealed record ProductDetailDto(
    int Id, int StoreId, string StoreName, string StoreEmoji, bool StoreOpenNow, string Name, string? Description,
    string Emoji, IReadOnlyList<string> Images, long Price, int Stock, bool IsAvailable, string? CategoryName);

public sealed record StoreDetailDto(
    StoreCardDto Store, string? Description, string PhoneNumber,
    IReadOnlyList<CategoryDto> Categories, IReadOnlyList<ProductCardDto> Products);

public sealed record ProductQuery(string? Search = null, int? CategoryId = null, int? StoreId = null, int Take = 40);

// ---------------------------------------------------------------- Cart & checkout

public sealed record CartLineDto(int ProductId, string Name, string Emoji, string? ImageUrl, long UnitPrice, int Quantity, long LineTotal, int Stock, bool IsAvailable);

public sealed record CartDto(
    int? StoreId, string? StoreName, bool StoreCanReceiveOrders, IReadOnlyList<CartLineDto> Lines,
    long Subtotal, long DeliveryFee, long Total, bool ServiceAvailable, IReadOnlyList<string> Warnings)
{
    public int Count => Lines.Sum(l => l.Quantity);
    public bool IsEmpty => Lines.Count == 0;
    public bool CanCheckout => !IsEmpty && StoreCanReceiveOrders && ServiceAvailable && Warnings.Count == 0;
}

public sealed record AddToCartResult(bool Success, bool RequiresStoreSwitch, string Message, CartDto? Cart);

public sealed class CheckoutInput
{
    [Required(ErrorMessage = "Vui lòng nhập tên người nhận")]
    [StringLength(100)]
    public string RecipientName { get; set; } = "";

    [Required(ErrorMessage = "Vui lòng nhập số điện thoại")]
    [RegularExpression(@"^(\+84|0)\d{9,10}$", ErrorMessage = "Số điện thoại không hợp lệ")]
    public string RecipientPhone { get; set; } = "";

    [Required(ErrorMessage = "Vui lòng nhập địa chỉ nhận hàng")]
    [StringLength(300, MinimumLength = 5, ErrorMessage = "Địa chỉ quá ngắn")]
    public string DeliveryAddress { get; set; } = "";

    [StringLength(300)]
    public string? DeliveryNote { get; set; }

    public bool SaveAddress { get; set; }

    /// <summary>Client-generated key per checkout attempt; the same key never creates two orders.</summary>
    public Guid IdempotencyKey { get; set; } = Guid.NewGuid();
}

public sealed record PlaceOrderResult(int OrderId, string Code, bool WasDuplicate);

// ---------------------------------------------------------------- Orders (shared)

public sealed record OrderSummaryDto(
    int Id, string Code, OrderStatus Status, string StoreName, string CustomerName, string RecipientPhone,
    string? DriverName, long GrandTotal, int ItemCount, DateTime PlacedAtUtc);

public sealed record OrderItemDto(int ProductId, string Name, string Emoji, long UnitPrice, int Quantity, long LineTotal);

public sealed record OrderHistoryDto(OrderStatus? From, OrderStatus To, string? ChangedBy, DateTime AtUtc, string? Reason);

public sealed record AssignmentDto(int Id, string DriverId, string DriverName, AssignmentStatus Status, bool IsActive, bool SelfAccepted, DateTime AssignedAtUtc, DateTime? CompletedAtUtc, string? Note);

public sealed record OrderDetailDto(
    int Id, string Code, OrderStatus Status,
    int StoreId, string StoreName, string PickupAddress, string PickupPhone,
    string CustomerId, string CustomerName, string RecipientName, string RecipientPhone, string DeliveryAddress, string? DeliveryNote,
    int? DistanceMeters,
    long ProductTotal, long DeliveryFee, long DiscountAmount, long GrandTotal, long CollectedCodAmount,
    long PlatformFee, long StoreReceivable, long DriverEarning,
    PaymentStatus PaymentStatus, OrderSettlementState StoreSettlementState, OrderSettlementState DriverSettlementState,
    string? DriverId, string? DriverName, string? DriverPhone, string? DriverPlate,
    string? CancelReason, string? FailureReason, DateTime PlacedAtUtc, DateTime? DeliveredAtUtc,
    IReadOnlyList<OrderItemDto> Items, IReadOnlyList<OrderHistoryDto> History, IReadOnlyList<AssignmentDto> Assignments,
    IReadOnlyList<OrderStatus> AllowedNextStatuses);

public sealed class OrderFilter
{
    public string? Search { get; set; }
    public OrderStatus? Status { get; set; }
    /// <summary>"active" = not terminal, "done" = terminal, null = all.</summary>
    public string? Bucket { get; set; }
    public int? StoreId { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 15;
}

// ---------------------------------------------------------------- Addresses / profile / notifications

public sealed class AddressInput
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Nhập nhãn địa chỉ"), StringLength(40)]
    public string Label { get; set; } = "Nhà";

    [Required(ErrorMessage = "Nhập tên người nhận"), StringLength(100)]
    public string RecipientName { get; set; } = "";

    [Required(ErrorMessage = "Nhập số điện thoại")]
    [RegularExpression(@"^(\+84|0)\d{9,10}$", ErrorMessage = "Số điện thoại không hợp lệ")]
    public string PhoneNumber { get; set; } = "";

    [Required(ErrorMessage = "Nhập số nhà, tên đường"), StringLength(200)]
    public string Street { get; set; } = "";

    [StringLength(100)] public string? Ward { get; set; }
    [StringLength(100)] public string? District { get; set; }

    [Required(ErrorMessage = "Nhập tỉnh/thành phố"), StringLength(100)]
    public string City { get; set; } = "Bình Dương";

    [StringLength(200)] public string? Note { get; set; }
    public bool IsDefault { get; set; }
}

public sealed record AddressDto(int Id, string Label, string RecipientName, string PhoneNumber, string FullText, string? Note, bool IsDefault);

public sealed class ProfileInput
{
    [Required(ErrorMessage = "Vui lòng nhập họ tên"), StringLength(100)]
    public string FullName { get; set; } = "";

    [RegularExpression(@"^(\+84|0)\d{9,10}$", ErrorMessage = "Số điện thoại không hợp lệ")]
    public string? PhoneNumber { get; set; }
}

public sealed record ProfileDto(string UserId, string Email, string FullName, string? PhoneNumber, IReadOnlyList<string> Roles);

public sealed record NotificationDto(int Id, string Type, string Title, string Message, string? Link, bool IsRead, DateTime CreatedAtUtc);

// ---------------------------------------------------------------- Registration

public enum RegistrationKind { Customer, Seller, Driver }

public sealed class RegisterInput
{
    public RegistrationKind Kind { get; set; } = RegistrationKind.Customer;

    [Required(ErrorMessage = "Vui lòng nhập họ tên"), StringLength(100)]
    public string FullName { get; set; } = "";

    [Required(ErrorMessage = "Vui lòng nhập email"), EmailAddress(ErrorMessage = "Email không hợp lệ")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Vui lòng nhập số điện thoại")]
    [RegularExpression(@"^(\+84|0)\d{9,10}$", ErrorMessage = "Số điện thoại không hợp lệ")]
    public string PhoneNumber { get; set; } = "";

    [Required(ErrorMessage = "Vui lòng nhập mật khẩu")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "Mật khẩu tối thiểu 8 ký tự")]
    public string Password { get; set; } = "";

    [Compare(nameof(Password), ErrorMessage = "Mật khẩu xác nhận không khớp")]
    public string ConfirmPassword { get; set; } = "";

    // Seller
    [StringLength(100)] public string? StoreName { get; set; }
    [StringLength(200)] public string? StoreAddress { get; set; }
    [StringLength(60)] public string? StoreTagline { get; set; }

    // Driver
    [StringLength(20)] public string? LicensePlate { get; set; }
    [StringLength(30)] public string? VehicleType { get; set; }
    [StringLength(20)] public string? IdentityNumber { get; set; }
}

public sealed record RegisterResult(bool Succeeded, string? UserId, IReadOnlyList<string> Errors);
