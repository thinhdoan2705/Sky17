using Sky17.Application;
using Sky17.Domain;
using Sky17.Web.Components.UI;

namespace Sky17.Web.Infrastructure;

/// <summary>Builds map markers (pickup first, drop-off second so the route is drawn store → customer).</summary>
public static class MapMarkers
{
    public static IReadOnlyList<MapView.MapMarker> ForOrder(OrderDetailDto o)
    {
        var list = new List<MapView.MapMarker>();
        if (o.PickupLatitude is { } pl && o.PickupLongitude is { } pg) list.Add(new(pl, pg, $"🏪 {o.StoreName}", "store"));
        if (o.DropoffLatitude is { } dl && o.DropoffLongitude is { } dg) list.Add(new(dl, dg, $"🏠 Giao đến: {o.DeliveryAddress}", "dropoff"));
        // Live driver position (only provided while the driver works on this order).
        if (o.DriverLatitude is { } rl && o.DriverLongitude is { } rg)
            list.Add(new(rl, rg, $"🛵 {o.DriverName} · cập nhật {Fmt.Ago(o.DriverLocationAtUtc ?? DateTime.UtcNow)}", "me"));
        return list;
    }

    public static IReadOnlyList<MapView.MapMarker> ForJob(DeliveryJobDto j)
    {
        var list = new List<MapView.MapMarker>();
        if (j.PickupLatitude is { } pl && j.PickupLongitude is { } pg) list.Add(new(pl, pg, $"🏪 Lấy món: {j.StoreName}", "store"));
        if (j.DropoffLatitude is { } dl && j.DropoffLongitude is { } dg) list.Add(new(dl, dg, $"🏠 Giao đến: {j.DropoffAddress}", "dropoff"));
        return list;
    }

    /// <summary>Driver overview: current trip points plus the driver's last reported position.</summary>
    public static IReadOnlyList<MapView.MapMarker> ForDriver(IEnumerable<DeliveryJobDto> active, DriverProfileDto profile)
    {
        var list = new List<MapView.MapMarker>();
        foreach (var j in active)
        {
            if (j.PickupLatitude is { } pl && j.PickupLongitude is { } pg && j.Status is OrderStatus.Assigned or OrderStatus.ArrivedAtStore)
                list.Add(new(pl, pg, $"{j.Code} · lấy món tại {j.StoreName}", "store"));
            if (j.DropoffLatitude is { } dl && j.DropoffLongitude is { } dg)
                list.Add(new(dl, dg, $"{j.Code} · giao {j.DropoffAddress}", "dropoff"));
        }
        if (profile.IsOnline && profile.LastLatitude is { } la && profile.LastLongitude is { } lo)
            list.Add(new(la, lo, "Vị trí của bạn (lần gửi gần nhất)", "me"));
        return list;
    }
}
