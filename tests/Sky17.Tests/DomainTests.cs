using Sky17.Domain;

namespace Sky17.Tests;

public class OrderStateMachineTests
{
    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Confirmed, OrderActor.Seller)]
    [InlineData(OrderStatus.Pending, OrderStatus.Cancelled, OrderActor.Customer)]
    [InlineData(OrderStatus.Confirmed, OrderStatus.Preparing, OrderActor.Seller)]
    [InlineData(OrderStatus.Preparing, OrderStatus.ReadyForPickup, OrderActor.Seller)]
    [InlineData(OrderStatus.ReadyForPickup, OrderStatus.Assigned, OrderActor.Driver)]
    [InlineData(OrderStatus.ReadyForPickup, OrderStatus.Assigned, OrderActor.Admin)]
    [InlineData(OrderStatus.Assigned, OrderStatus.PickedUp, OrderActor.Driver)]
    [InlineData(OrderStatus.PickedUp, OrderStatus.Delivering, OrderActor.Driver)]
    [InlineData(OrderStatus.Delivering, OrderStatus.Delivered, OrderActor.Driver)]
    [InlineData(OrderStatus.Delivering, OrderStatus.DeliveryFailed, OrderActor.Driver)]
    [InlineData(OrderStatus.DeliveryFailed, OrderStatus.Returned, OrderActor.Admin)]
    public void Allowed_transitions(OrderStatus from, OrderStatus to, OrderActor actor) =>
        Assert.True(OrderStateMachine.CanTransition(from, to, actor));

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.ReadyForPickup, OrderActor.Seller)]   // cannot skip steps
    [InlineData(OrderStatus.Confirmed, OrderStatus.Cancelled, OrderActor.Customer)]    // customer cancels only while pending
    [InlineData(OrderStatus.Pending, OrderStatus.Confirmed, OrderActor.Customer)]
    [InlineData(OrderStatus.Delivering, OrderStatus.Delivered, OrderActor.Seller)]
    [InlineData(OrderStatus.Delivered, OrderStatus.Cancelled, OrderActor.Admin)]       // terminal
    [InlineData(OrderStatus.PickedUp, OrderStatus.Cancelled, OrderActor.Admin)]        // goods already left the store
    [InlineData(OrderStatus.ReadyForPickup, OrderStatus.Assigned, OrderActor.Seller)]
    public void Forbidden_transitions(OrderStatus from, OrderStatus to, OrderActor actor)
    {
        Assert.False(OrderStateMachine.CanTransition(from, to, actor));
        Assert.ThrowsAny<Exception>(() => OrderStateMachine.EnsureCanTransition(from, to, actor));
    }

    [Fact]
    public void Terminal_states_have_no_next_status()
    {
        foreach (var actor in Enum.GetValues<OrderActor>())
        {
            Assert.Empty(OrderStateMachine.NextStatuses(OrderStatus.Delivered, actor));
            Assert.Empty(OrderStateMachine.NextStatuses(OrderStatus.Cancelled, actor));
            Assert.Empty(OrderStateMachine.NextStatuses(OrderStatus.Returned, actor));
        }
    }
}

public class PricingTests
{
    private static readonly DeliveryRateRule Rule = new() { BaseFee = 15_000, BaseDistanceMeters = 2_000, FeePerKm = 5_000, PlatformFeeBps = 1_000, DriverShareBps = 8_000 };

    [Theory]
    [InlineData(null, 15_000)]
    [InlineData(1_500, 15_000)]
    [InlineData(2_000, 15_000)]
    [InlineData(2_001, 20_000)]   // a started kilometre is charged
    [InlineData(5_000, 30_000)]
    public void Delivery_fee(int? meters, long expected) => Assert.Equal(expected, DeliveryPricing.CalculateFee(Rule, meters));

    [Fact]
    public void Order_amounts_use_integer_math()
    {
        var a = OrderFinancials.Calculate(90_000, 15_000, 0, Rule);
        Assert.Equal(105_000, a.GrandTotal);
        Assert.Equal(9_000, a.PlatformFee);
        Assert.Equal(81_000, a.StoreReceivable);
        Assert.Equal(12_000, a.DriverEarning);
        Assert.Equal(93_000, a.DriverOwesPlatform);
        Assert.Equal(12_000, a.PlatformRevenue);
        // Every đồng is accounted for: store + driver + platform = what the customer paid.
        Assert.Equal(a.GrandTotal, a.StoreReceivable + a.DriverEarning + a.PlatformRevenue);
    }

    [Fact]
    public void Basis_points_round_half_up() => Assert.Equal(3, Money.ApplyBps(25, 1_000)); // 2.5 → 3

    [Fact]
    public void Store_hours_support_24h_and_overnight()
    {
        Assert.True(new Store { Name = "x", Address = "x", PhoneNumber = "x", OpenTime = new(0, 0), CloseTime = new(0, 0) }.IsWithinHours(new(3, 0)));
        var night = new Store { Name = "x", Address = "x", PhoneNumber = "x", OpenTime = new(18, 0), CloseTime = new(2, 0) };
        Assert.True(night.IsWithinHours(new(23, 0)));
        Assert.True(night.IsWithinHours(new(1, 0)));
        Assert.False(night.IsWithinHours(new(12, 0)));
    }
}
