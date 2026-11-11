using System.Globalization;
using Sky17.Application;
using Sky17.Domain;

namespace Sky17.Web.Infrastructure;

/// <summary>Vietnamese presentation helpers: money, time, status labels.</summary>
public static class Fmt
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static string Vnd(long amount) => amount.ToString("#,##0", Vi) + "đ";

    public static string Compact(long amount) => amount >= 1_000_000
        ? (amount / 1_000_000m).ToString("0.#", Vi) + " triệu"
        : Vnd(amount);

    public static string Time(DateTime utc) => VnTime.ToLocal(utc).ToString("dd/MM/yyyy HH:mm", Vi);
    public static string Time(DateTime? utc) => utc is { } t ? Time(t) : "—";
    public static string Date(DateTime utc) => VnTime.ToLocal(utc).ToString("dd/MM/yyyy", Vi);
    public static string Clock(DateTime utc) => VnTime.ToLocal(utc).ToString("HH:mm", Vi);

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (d.TotalMinutes < 1) return "vừa xong";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} phút trước";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} giờ trước";
        return Time(utc);
    }

    public static string Distance(int? meters) => meters is { } m
        ? (m >= 1000 ? (m / 1000m).ToString("0.#", Vi) + " km" : m + " m")
        : "Chưa xác định";

    public static string Percent(int bps) => (bps / 100m).ToString("0.##", Vi) + "%";

    public static string Weekday(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Monday => "T2", DayOfWeek.Tuesday => "T3", DayOfWeek.Wednesday => "T4", DayOfWeek.Thursday => "T5",
        DayOfWeek.Friday => "T6", DayOfWeek.Saturday => "T7", _ => "CN",
    };

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1 ? parts[0][..1].ToUpperInvariant() : (parts[^2][..1] + parts[^1][..1]).ToUpperInvariant();
    }

    public static string Tel(string? phone) => "tel:" + new string((phone ?? "").Where(c => char.IsDigit(c) || c == '+').ToArray());

    public static string MapsUrl(string address) => "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(address);
}

public static class Labels
{
    public static string Text(this OrderStatus s) => s switch
    {
        OrderStatus.Pending => "Chờ quán xác nhận",
        OrderStatus.Confirmed => "Đang tìm tài xế",
        OrderStatus.Assigned => "Tài xế đang đến quán",
        OrderStatus.ArrivedAtStore => "Tài xế đã đến quán",
        OrderStatus.Delivering => "Đang giao",
        OrderStatus.ArrivedAtCustomer => "Tài xế đã đến nơi",
        OrderStatus.Delivered => "Đã giao",
        OrderStatus.Cancelled => "Đã hủy",
        OrderStatus.DeliveryFailed => "Giao thất bại",
        OrderStatus.Returned => "Đã hoàn trả",
        _ => s.ToString(),
    };

    public static string Css(this OrderStatus s) => s switch
    {
        OrderStatus.Delivered => "green",
        OrderStatus.Cancelled or OrderStatus.DeliveryFailed or OrderStatus.Returned => "red",
        OrderStatus.Pending or OrderStatus.Confirmed => "orange",
        OrderStatus.ArrivedAtCustomer => "green",
        _ => "",
    };

    /// <summary>Button label for the actor performing the transition into <paramref name="s"/>.</summary>
    public static string ActionText(this OrderStatus s) => s switch
    {
        OrderStatus.Confirmed => "✓ Nhận đơn",
        OrderStatus.Cancelled => "Hủy / Từ chối",
        OrderStatus.Returned => "Hoàn trả cửa hàng",
        _ => s.Text(),
    };

    public static readonly OrderStatus[] HappyPath =
        [OrderStatus.Pending, OrderStatus.Confirmed, OrderStatus.Assigned, OrderStatus.ArrivedAtStore, OrderStatus.Delivering, OrderStatus.ArrivedAtCustomer, OrderStatus.Delivered];

    public static string Text(this PreparationState p) => p == PreparationState.Ready ? "Món đã sẵn sàng" : "Quán đang chuẩn bị";

    public static string Text(this DispatchOfferStatus s) => s switch
    {
        DispatchOfferStatus.Pending => "Đang chờ phản hồi",
        DispatchOfferStatus.Accepted => "Đã nhận",
        DispatchOfferStatus.Declined => "Từ chối",
        DispatchOfferStatus.Expired => "Hết giờ",
        _ => "Đã thu hồi",
    };

    public static string Css(this DispatchOfferStatus s) => s switch
    {
        DispatchOfferStatus.Accepted => "green", DispatchOfferStatus.Pending => "orange", DispatchOfferStatus.Declined => "red", _ => "gray",
    };

    public static string Text(this StoreStatus s) => s switch
    {
        StoreStatus.PendingApproval => "Chờ duyệt",
        StoreStatus.Active => "Đang hoạt động",
        StoreStatus.Suspended => "Tạm ngưng",
        StoreStatus.Rejected => "Từ chối",
        _ => s.ToString(),
    };

    public static string Css(this StoreStatus s) => s switch
    {
        StoreStatus.Active => "green", StoreStatus.PendingApproval => "orange", StoreStatus.Suspended => "gray", _ => "red",
    };

    public static string Text(this DriverStatus s) => s switch
    {
        DriverStatus.PendingApproval => "Chờ duyệt",
        DriverStatus.Active => "Đã duyệt",
        DriverStatus.Suspended => "Tạm khóa",
        DriverStatus.Rejected => "Từ chối",
        _ => s.ToString(),
    };

    public static string Css(this DriverStatus s) => s switch
    {
        DriverStatus.Active => "green", DriverStatus.PendingApproval => "orange", DriverStatus.Suspended => "gray", _ => "red",
    };

    public static string Text(this SettlementStatus s) => s switch
    {
        SettlementStatus.Pending => "Chờ xác nhận", SettlementStatus.Confirmed => "Đã xác nhận", _ => "Đã hủy",
    };

    public static string Css(this SettlementStatus s) => s switch
    {
        SettlementStatus.Pending => "orange", SettlementStatus.Confirmed => "green", _ => "gray",
    };

    public static string Text(this SettlementType t) => t == SettlementType.Store ? "Thanh toán cửa hàng" : "Thu COD tài xế";

    public static string Text(this OrderSettlementState s) => s switch
    {
        OrderSettlementState.Unsettled => "Chưa đối soát", OrderSettlementState.InSettlement => "Đang đối soát", _ => "Đã đối soát",
    };

    public static string Text(this PaymentStatus s) => s switch
    {
        PaymentStatus.Pending => "Chờ thu COD", PaymentStatus.Collected => "Đã thu COD", _ => "Đã hủy",
    };

    public static string Text(this AssignmentStatus s) => s switch
    {
        AssignmentStatus.Assigned => "Đã nhận",
        AssignmentStatus.PickedUp => "Đã lấy hàng",
        AssignmentStatus.Delivering => "Đang giao",
        AssignmentStatus.Delivered => "Hoàn thành",
        AssignmentStatus.Failed => "Thất bại",
        AssignmentStatus.Cancelled => "Đã hủy",
        AssignmentStatus.ArrivedAtStore => "Đã đến quán",
        AssignmentStatus.ArrivedAtCustomer => "Đã đến nơi giao",
        _ => "Đã gỡ / hủy chuyến",
    };

    public static string Text(this CodTransactionType t) => t == CodTransactionType.CollectedFromCustomer ? "Thu từ khách" : "Nộp về nền tảng";

    public static string Text(this StoreMemberRole r) => r == StoreMemberRole.Owner ? "Chủ cửa hàng" : "Nhân viên";

    public static string RoleText(string role) => role switch
    {
        Roles.Admin => "Quản trị viên", Roles.Seller => "Người bán", Roles.Driver => "Tài xế", Roles.Customer => "Khách hàng", _ => role,
    };
}
