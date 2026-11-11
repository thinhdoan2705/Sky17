namespace Sky17.Domain;

/// <summary>
/// Single source of truth for allowed order status transitions and who may perform them.
/// </summary>
public static class OrderStateMachine
{
    private static readonly Dictionary<(OrderStatus From, OrderStatus To), OrderActor[]> Rules = new()
    {
        [(OrderStatus.Pending, OrderStatus.Confirmed)] = [OrderActor.Seller],
        [(OrderStatus.Pending, OrderStatus.Cancelled)] = [OrderActor.Customer, OrderActor.Seller, OrderActor.Admin],

        [(OrderStatus.Confirmed, OrderStatus.Preparing)] = [OrderActor.Seller],
        [(OrderStatus.Confirmed, OrderStatus.Cancelled)] = [OrderActor.Seller, OrderActor.Admin],

        [(OrderStatus.Preparing, OrderStatus.ReadyForPickup)] = [OrderActor.Seller],
        [(OrderStatus.Preparing, OrderStatus.Cancelled)] = [OrderActor.Admin],

        [(OrderStatus.ReadyForPickup, OrderStatus.Assigned)] = [OrderActor.Driver, OrderActor.Admin],
        [(OrderStatus.ReadyForPickup, OrderStatus.Cancelled)] = [OrderActor.Admin],

        // Admin may unassign (release back to the pool) before pickup.
        [(OrderStatus.Assigned, OrderStatus.ReadyForPickup)] = [OrderActor.Admin],
        [(OrderStatus.Assigned, OrderStatus.PickedUp)] = [OrderActor.Driver],
        [(OrderStatus.Assigned, OrderStatus.Cancelled)] = [OrderActor.Admin],

        [(OrderStatus.PickedUp, OrderStatus.Delivering)] = [OrderActor.Driver],
        [(OrderStatus.PickedUp, OrderStatus.DeliveryFailed)] = [OrderActor.Driver, OrderActor.Admin],

        [(OrderStatus.Delivering, OrderStatus.Delivered)] = [OrderActor.Driver],
        [(OrderStatus.Delivering, OrderStatus.DeliveryFailed)] = [OrderActor.Driver, OrderActor.Admin],

        // Failed deliveries are reviewed by admin: retry (back to pool) or return to store.
        [(OrderStatus.DeliveryFailed, OrderStatus.ReadyForPickup)] = [OrderActor.Admin],
        [(OrderStatus.DeliveryFailed, OrderStatus.Returned)] = [OrderActor.Admin],
    };

    public static bool CanTransition(OrderStatus from, OrderStatus to, OrderActor actor) =>
        Rules.TryGetValue((from, to), out var actors) && actors.Contains(actor);

    public static void EnsureCanTransition(OrderStatus from, OrderStatus to, OrderActor actor)
    {
        if (!Rules.TryGetValue((from, to), out var actors))
            throw new DomainException($"Không thể chuyển đơn từ trạng thái {from} sang {to}.");
        if (!actors.Contains(actor))
            throw new ForbiddenException("Bạn không được phép thực hiện bước chuyển trạng thái này.");
    }

    public static IReadOnlyList<OrderStatus> NextStatuses(OrderStatus from, OrderActor actor) =>
        Rules.Where(r => r.Key.From == from && r.Value.Contains(actor)).Select(r => r.Key.To).ToList();

    public static IReadOnlyCollection<(OrderStatus From, OrderStatus To, OrderActor[] Actors)> AllRules =>
        Rules.Select(r => (r.Key.From, r.Key.To, r.Value)).ToList();

    public static bool IsTerminal(OrderStatus s) =>
        s is OrderStatus.Delivered or OrderStatus.Cancelled or OrderStatus.Returned;

    /// <summary>Order is with a driver (not yet finished).</summary>
    public static bool IsInDelivery(OrderStatus s) =>
        s is OrderStatus.Assigned or OrderStatus.PickedUp or OrderStatus.Delivering;

    /// <summary>Stock has not left the store yet, so cancelling restores inventory.</summary>
    public static bool IsBeforePickup(OrderStatus s) =>
        s is OrderStatus.Pending or OrderStatus.Confirmed or OrderStatus.Preparing or OrderStatus.ReadyForPickup or OrderStatus.Assigned;
}
