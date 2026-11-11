using Sky17.Domain;

namespace Sky17.Application;

/// <summary>Authenticated principal as seen by application services.</summary>
public sealed record CurrentUser(string? UserId, IReadOnlyCollection<string> Roles)
{
    public static readonly CurrentUser Anonymous = new(null, []);

    public bool IsAuthenticated => UserId is not null;
    public bool IsInRole(string role) => Roles.Contains(role);

    public string RequireUserId() =>
        UserId ?? throw new ForbiddenException("Vui lòng đăng nhập để tiếp tục.");

    /// <summary>Throws unless the user holds at least one of the roles.</summary>
    public string RequireRole(params string[] roles)
    {
        var id = RequireUserId();
        if (!roles.Any(Roles.Contains)) throw new ForbiddenException();
        return id;
    }
}

public interface ICurrentUserAccessor
{
    Task<CurrentUser> GetAsync(CancellationToken ct = default);
}

public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Vietnam time helpers (presentation & business-hours checks).</summary>
public static class VnTime
{
    public static readonly TimeZoneInfo Zone = Resolve();

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Asia/Ho_Chi_Minh", "SE Asia Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        }
        return TimeZoneInfo.CreateCustomTimeZone("ICT", TimeSpan.FromHours(7), "ICT", "ICT");
    }

    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>UTC instant at local midnight of the local date containing <paramref name="utc"/>.</summary>
    public static DateTime StartOfLocalDayUtc(DateTime utc)
    {
        var local = ToLocal(utc).Date;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);
    }

    public static DateTime LocalDateToUtc(DateOnly date) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Zone);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

/// <summary>Real-time push to connected users. Implemented with SignalR in the web project.</summary>
public interface IRealtimePublisher
{
    Task PublishAsync(IReadOnlyCollection<string> userIds, RealtimeMessage message, CancellationToken ct = default);
}

public sealed record RealtimeMessage(string Type, string Title, string Message, string? Link, int? OrderId);

public sealed class NullRealtimePublisher : IRealtimePublisher
{
    public Task PublishAsync(IReadOnlyCollection<string> userIds, RealtimeMessage message, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed record DailyPoint(DateOnly Date, int Orders, long Amount);
