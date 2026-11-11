namespace Sky17.Domain;

/// <summary>Entity with creation / update timestamps (UTC). Set automatically by the DbContext.</summary>
public interface IAuditable
{
    DateTime CreatedAtUtc { get; set; }
    DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>Entity protected by an optimistic concurrency token (incremented on every update).</summary>
public interface IVersioned
{
    long Version { get; set; }
}

public abstract class AuditableEntity : IAuditable
{
    public int Id { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Seller = "Seller";
    public const string Customer = "Customer";
    public const string Driver = "Driver";

    public static readonly IReadOnlyList<string> All = [Admin, Seller, Customer, Driver];
}

/// <summary>Business rule violation. Message is safe to show to end users (Vietnamese).</summary>
public class DomainException(string message) : Exception(message);

public class ForbiddenException(string message = "Bạn không có quyền thực hiện thao tác này.") : Exception(message);

public class NotFoundException(string message = "Không tìm thấy dữ liệu yêu cầu.") : Exception(message);

public class ConcurrencyConflictException(string message = "Dữ liệu vừa được người khác cập nhật. Vui lòng tải lại và thử lại.") : Exception(message);
