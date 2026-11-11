using Sky17.Application;
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
        return list;
    }

    public static IReadOnlyList<MapView.MapMarker> ForJob(DeliveryJobDto j)
    {
        var list = new List<MapView.MapMarker>();
        if (j.PickupLatitude is { } pl && j.PickupLongitude is { } pg) list.Add(new(pl, pg, $"🏪 Lấy hàng: {j.StoreName}", "store"));
        if (j.DropoffLatitude is { } dl && j.DropoffLongitude is { } dg) list.Add(new(dl, dg, $"🏠 Giao đến: {j.DropoffAddress}", "dropoff"));
        return list;
    }

    /// <summary>Overview for the driver: pickup points of open jobs and own active deliveries.</summary>
    public static IReadOnlyList<MapView.MapMarker> ForDriver(IEnumerable<DeliveryJobDto> active, IEnumerable<DeliveryJobDto> open)
    {
        var list = new List<MapView.MapMarker>();
        foreach (var j in active)
        {
            if (j.PickupLatitude is { } pl && j.PickupLongitude is { } pg && j.Status == Domain.OrderStatus.Assigned)
                list.Add(new(pl, pg, $"{j.Code} · lấy tại {j.StoreName}", "store"));
            if (j.DropoffLatitude is { } dl && j.DropoffLongitude is { } dg)
                list.Add(new(dl, dg, $"{j.Code} · giao {j.DropoffAddress}", "dropoff"));
        }
        foreach (var j in open)
            if (j.PickupLatitude is { } pl && j.PickupLongitude is { } pg)
                list.Add(new(pl, pg, $"📦 Đơn mở {j.Code} · {j.StoreName}", "job"));
        return list;
    }
}
