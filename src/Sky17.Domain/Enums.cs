namespace Sky17.Domain;

/// <summary>
/// GrabFood-style order flow. Kitchen preparation runs in parallel and is tracked by <see cref="PreparationState"/>.
/// Values 2, 3 and 5 belonged to a previous flow and are migrated away (see migration GrabDispatchFlow).
/// </summary>
public enum OrderStatus
{
    /// <summary>Waiting for the store to accept.</summary>
    Pending = 0,
    /// <summary>Store accepted (kitchen preparing); dispatch is searching for a driver.</summary>
    Confirmed = 1,
    /// <summary>A driver accepted and is heading to the store.</summary>
    Assigned = 4,
    /// <summary>Driver is at the store.</summary>
    ArrivedAtStore = 8,
    /// <summary>Driver picked up the food and is on the way to the customer.</summary>
    Delivering = 6,
    /// <summary>Driver is at the drop-off point.</summary>
    ArrivedAtCustomer = 9,
    Delivered = 7,
    Cancelled = 20,
    DeliveryFailed = 21,
    Returned = 22,
}

/// <summary>Kitchen state, independent from the delivery flow.</summary>
public enum PreparationState
{
    Preparing = 0,
    Ready = 1,
}

public enum DispatchOfferStatus
{
    Pending = 0,
    Accepted = 1,
    Declined = 2,
    Expired = 3,
    /// <summary>Withdrawn because the order was assigned/cancelled meanwhile.</summary>
    Withdrawn = 4,
}

/// <summary>Who performs an order transition.</summary>
public enum OrderActor
{
    Customer,
    Seller,
    Driver,
    Admin,
    System,
}

/// <summary>Where an order's distance comes from. Distances are never estimated/fabricated.</summary>
public enum DistanceSource
{
    /// <summary>Unknown: base delivery fee applies.</summary>
    None = 0,
    /// <summary>Road distance from the routing service (OSRM) between the two map points.</summary>
    Route = 1,
    /// <summary>Entered manually by an admin.</summary>
    Manual = 2,
}

public enum StoreStatus
{
    PendingApproval = 0,
    Active = 1,
    Suspended = 2,
    Rejected = 3,
}

public enum StoreMemberRole
{
    Owner = 0,
    Staff = 1,
}

public enum DriverStatus
{
    PendingApproval = 0,
    Active = 1,
    Suspended = 2,
    Rejected = 3,
}

public enum AssignmentStatus
{
    Assigned = 0,
    PickedUp = 1,
    Delivering = 2,
    Delivered = 3,
    Failed = 4,
    Cancelled = 5,
    Released = 6,
    ArrivedAtStore = 7,
    ArrivedAtCustomer = 8,
}

public enum PaymentMethod
{
    CashOnDelivery = 0,
}

public enum PaymentStatus
{
    Pending = 0,
    Collected = 1,
    Cancelled = 2,
}

public enum CodTransactionType
{
    /// <summary>Driver collected cash from the customer.</summary>
    CollectedFromCustomer = 0,
    /// <summary>Driver remitted cash to the platform (driver settlement confirmed).</summary>
    RemittedToPlatform = 1,
}

public enum SettlementType
{
    Store = 0,
    Driver = 1,
}

public enum SettlementStatus
{
    Pending = 0,
    Confirmed = 1,
    Cancelled = 2,
}

/// <summary>Settlement progress of one order for one party (store or driver).</summary>
public enum OrderSettlementState
{
    Unsettled = 0,
    InSettlement = 1,
    Settled = 2,
}
