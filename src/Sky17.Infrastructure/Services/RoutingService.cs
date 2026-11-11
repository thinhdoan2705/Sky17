using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Sky17.Application;
using Sky17.Domain;
using Sky17.Infrastructure.Data;

namespace Sky17.Infrastructure.Services;

/// <summary>
/// Road distance from an OSRM server (free; default is the public demo server router.project-osrm.org,
/// which is rate-limited and meant for light use — self-host OSRM for production volume).
/// Any failure returns null so the caller falls back to the base fee instead of guessing.
/// </summary>
public sealed class OsrmRoutingService(string baseUrl, ILogger<OsrmRoutingService> logger) : IRoutingService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Sky17-Delivery/1.0");
        return c;
    }

    public async Task<int?> GetRoadDistanceMetersAsync(GeoPoint from, GeoPoint to, CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;
        var url = $"{baseUrl.TrimEnd('/')}/route/v1/driving/{from.Longitude.ToString(inv)},{from.Latitude.ToString(inv)};{to.Longitude.ToString(inv)},{to.Latitude.ToString(inv)}?overview=false&alternatives=false&steps=false";
        try
        {
            using var res = await Http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.GetProperty("code").GetString() != "Ok") return null;
            var routes = doc.RootElement.GetProperty("routes");
            if (routes.GetArrayLength() == 0) return null;
            var meters = routes[0].GetProperty("distance").GetDouble();
            return meters is > 0 and < 1_000_000 ? (int)Math.Round(meters) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("OSRM routing unavailable: {Message}", ex.Message);
            return null;
        }
    }
}

internal static class DeliveryQuote
{
    /// <summary>Distance and fee between a store and a drop-off point. No coordinates or no route → base fee.</summary>
    public static async Task<(int? Meters, long Fee, DistanceSource Source)> CalculateAsync(
        IRoutingService routing, DeliveryRateRule rule, Store store, GeoPoint? dropoff, CancellationToken ct)
    {
        var pickup = store.Latitude is { } la && store.Longitude is { } lo ? new GeoPoint(la, lo) : (GeoPoint?)null;
        if (pickup is null || dropoff is null) return (null, DeliveryPricing.CalculateFee(rule, null), DistanceSource.None);
        var meters = await routing.GetRoadDistanceMetersAsync(pickup.Value, dropoff.Value, ct);
        return meters is null
            ? (null, DeliveryPricing.CalculateFee(rule, null), DistanceSource.None)
            : (meters, DeliveryPricing.CalculateFee(rule, meters), DistanceSource.Route);
    }
}
