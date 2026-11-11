using Sky17.Domain;

namespace Sky17.Web.Components.Account;

/// <summary>One sign-in page per portal: /login (customers), /admin/login, /seller/login, /driver/login.</summary>
public sealed record LoginPortal(
    string Key, string Path, string Home, string Role, string Icon, string Title, string Subtitle,
    bool UsesPhone, string? RegisterUrl)
{
    public string IdentifierLabel => UsesPhone ? "Số điện thoại" : "Email";
}

public static class LoginPortals
{
    public static readonly LoginPortal Customer = new("customer", "/login", "/", Roles.Customer, "🛍️",
        "Đăng nhập", "Đặt món giao hỏa tốc từ các cửa hàng quanh bạn.", true, "/Account/Register?kind=customer");

    public static readonly LoginPortal Seller = new("seller", "/seller/login", "/seller", Roles.Seller, "🏪",
        "Đăng nhập người bán", "Quản lý cửa hàng, đơn hàng và doanh thu.", false, "/Account/Register?kind=seller");

    public static readonly LoginPortal Driver = new("driver", "/driver/login", "/driver", Roles.Driver, "🛵",
        "Đăng nhập tài xế", "Bật trực tuyến để nhận chuyến gần bạn.", false, "/Account/Register?kind=driver");

    public static readonly LoginPortal Admin = new("admin", "/admin/login", "/admin", Roles.Admin, "🛡️",
        "Đăng nhập quản trị", "Khu vực dành riêng cho quản trị viên Sky 17.", false, null);

    public static readonly IReadOnlyList<LoginPortal> All = [Customer, Seller, Driver, Admin];

    /// <summary>The portal a local path belongs to (/admin/... → Admin, everything else not partner → Customer).</summary>
    public static LoginPortal ForPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Customer;
        foreach (var p in new[] { Admin, Seller, Driver })
        {
            if (path.Equals(p.Home, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(p.Home + "/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(p.Home + "?", StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return Customer;
    }

    public static LoginPortal? ForRole(string role) => All.FirstOrDefault(p => p.Role == role);

    /// <summary>Login URL for the portal that owns <paramref name="returnPath"/>.</summary>
    public static string LoginUrlFor(string? returnPath)
    {
        var portal = ForPath(returnPath);
        return string.IsNullOrEmpty(returnPath) || returnPath == "/" || returnPath.Equals(portal.Path, StringComparison.OrdinalIgnoreCase)
            ? portal.Path
            : portal.Path + "?returnUrl=" + Uri.EscapeDataString(returnPath);
    }

    /// <summary>True for the sign-in form endpoints (used by the strict auth rate limit).</summary>
    public static bool IsLoginPath(PathString path) =>
        All.Any(p => path.Equals(p.Path, StringComparison.OrdinalIgnoreCase));

    public static bool IsSafeLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\");
}
