namespace Sky17.Domain;

/// <summary>
/// Single source of truth for allowed order status transitions and who may perform them (GrabFood-style flow):
/// Pending → Confirmed (store accepts, driver search starts) → Assigned (driver accepted offer) → ArrivedAtStore
/// → Delivering (food picked up) → ArrivedAtCustomer → Delivered.
/// </summary>
public static class OrderStateMachine
{
    private static readonly Dictionary<(OrderStatus From, OrderStatus To), OrderActor[]> Rules = new()
    {
        [(OrderStatus.Pending, OrderStatus.Confirmed)] = [OrderActor.Seller],
        // Store did not answer in time → system auto-cancel.
        [(OrderStatus.Pending, OrderStatus.Cancelled)] = [OrderActor.Customer, OrderActor.Seller, OrderActor.Admin, OrderActor.System],

        // Searching for a driver.
        [(OrderStatus.Confirmed, OrderStatus.Assigned)] = [OrderActor.Driver, OrderActor.Admin],
        [(OrderStatus.Confirmed, OrderStatus.Cancelled)] = [OrderActor.Seller, OrderActor.Admin],

        // Driver heading to the store. Driver may cancel the trip (order goes back to searching).
        [(OrderStatus.Assigned, OrderStatus.ArrivedAtStore)] = [OrderActor.Driver],
        [(OrderStatus.Assigned, OrderStatus.Confirmed)] = [OrderActor.Driver, OrderActor.Admin],
        [(OrderStatus.Assigned, OrderStatus.Cancelled)] = [OrderActor.Admin],

        [(OrderStatus.ArrivedAtStore, OrderStatus.Delivering)] = [OrderActor.Driver],
        [(OrderStatus.ArrivedAtStore, OrderStatus.Confirmed)] = [OrderActor.Driver, OrderActor.Admin],
        [(OrderStatus.ArrivedAtStore, OrderStatus.Cancelled)] = [OrderActor.Admin],

        [(OrderStatus.Delivering, OrderStatus.ArrivedAtCustomer)] = [OrderActor.Driver],
        [(OrderStatus.Delivering, OrderStatus.DeliveryFailed)] = [OrderActor.Driver, OrderActor.Admin],

        [(OrderStatus.ArrivedAtCustomer, OrderStatus.Delivered)] = [OrderActor.Driver],
        [(OrderStatus.ArrivedAtCustomer, OrderStatus.DeliveryFailed)] = [OrderActor.Driver, OrderActor.Admin],

        // Failed deliveries are reviewed by admin: retry (search a new driver) or return to store.
        [(OrderStatus.DeliveryFailed, OrderStatus.Confirmed)] = [OrderActor.Admin],
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

    /// <summary>A driver is working on the order.</summary>
    public static bool IsInDelivery(OrderStatus s) =>
        s is OrderStatus.Assigned or OrderStatus.ArrivedAtStore or OrderStatus.Delivering or OrderStatus.ArrivedAtCustomer;

    /// <summary>Food has not left the store yet, so cancelling restores inventory.</summary>
    public static bool IsBeforePickup(OrderStatus s) =>
        s is OrderStatus.Pending or OrderStatus.Confirmed or OrderStatus.Assigned or OrderStatus.ArrivedAtStore;

    /// <summary>The store may still mark the food as ready.</summary>
    public static bool IsKitchenOpen(OrderStatus s) =>
        s is OrderStatus.Confirmed or OrderStatus.Assigned or OrderStatus.ArrivedAtStore;

    public static readonly OrderStatus[] DriverActiveStatuses =
        [OrderStatus.Assigned, OrderStatus.ArrivedAtStore, OrderStatus.Delivering, OrderStatus.ArrivedAtCustomer];
}
