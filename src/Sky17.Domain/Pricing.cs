namespace Sky17.Domain;

/// <summary>Integer-only VND money calculations. Never use floating point for money.</summary>
public static class Money
{
    /// <summary>amount × bps / 10000, rounded half-up.</summary>
    public static long ApplyBps(long amount, int bps)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (bps is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(bps));
        return checked((amount * bps + 5_000) / 10_000);
    }
}

/// <summary>WGS84 coordinate.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    public bool IsValid => Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180 && !(Latitude == 0 && Longitude == 0);

    /// <summary>Great-circle (straight-line) distance in meters. For ranking (nearby drivers, store distance in search) — never for pricing.</summary>
    public int StraightLineMetersTo(GeoPoint other)
    {
        const double r = 6_371_000;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(other.Latitude - Latitude);
        var dLng = Rad(other.Longitude - Longitude);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(Latitude)) * Math.Cos(Rad(other.Latitude)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return (int)Math.Round(r * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)));
    }

    public static GeoPoint? From(double? lat, double? lng)
    {
        if (lat is null || lng is null) return null;
        var p = new GeoPoint(lat.Value, lng.Value);
        return p.IsValid ? p : throw new DomainException("Tọa độ bản đồ không hợp lệ.");
    }
}

public static class DeliveryPricing
{
    /// <summary>
    /// Delivery fee. When the distance is unknown (no mapping service), the base fee applies;
    /// an admin can later set the measured distance manually.
    /// </summary>
    public static long CalculateFee(DeliveryRateRule rule, int? distanceMeters)
    {
        if (distanceMeters is null or <= 0) return rule.BaseFee;
        var extraMeters = Math.Max(0, distanceMeters.Value - rule.BaseDistanceMeters);
        var extraKm = (extraMeters + 999) / 1000; // started kilometre is charged
        return checked(rule.BaseFee + extraKm * rule.FeePerKm);
    }
}

public sealed record OrderAmounts(
    long ProductTotal,
    long DeliveryFee,
    long DiscountAmount,
    long GrandTotal,
    long PlatformFee,
    long StoreReceivable,
    long DriverEarning)
{
    /// <summary>Platform revenue = commission + delivery fee kept − discount (platform funded).</summary>
    public long PlatformRevenue => PlatformFee + (DeliveryFee - DriverEarning) - DiscountAmount;
    /// <summary>Cash the driver owes the platform after collecting COD for this order.</summary>
    public long DriverOwesPlatform => GrandTotal - DriverEarning;
}

public static class OrderFinancials
{
    public static OrderAmounts Calculate(long productTotal, long deliveryFee, long discount, DeliveryRateRule rule)
    {
        if (productTotal < 0 || deliveryFee < 0 || discount < 0) throw new DomainException("Số tiền không hợp lệ.");
        if (discount > productTotal + deliveryFee) throw new DomainException("Giảm giá vượt quá giá trị đơn hàng.");
        var platformFee = Money.ApplyBps(productTotal, rule.PlatformFeeBps);
        var driverEarning = Money.ApplyBps(deliveryFee, rule.DriverShareBps);
        return new OrderAmounts(
            productTotal,
            deliveryFee,
            discount,
            GrandTotal: productTotal + deliveryFee - discount,
            PlatformFee: platformFee,
            StoreReceivable: productTotal - platformFee,
            DriverEarning: driverEarning);
    }

    public static void Apply(Order order, OrderAmounts a)
    {
        order.ProductTotal = a.ProductTotal;
        order.DeliveryFee = a.DeliveryFee;
        order.DiscountAmount = a.DiscountAmount;
        order.GrandTotal = a.GrandTotal;
        order.PlatformFee = a.PlatformFee;
        order.StoreReceivable = a.StoreReceivable;
        order.DriverEarning = a.DriverEarning;
    }
}
