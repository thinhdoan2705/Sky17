namespace Sky17.Domain;

public enum OrderStatus
{
    Pending = 0,
    Confirmed = 1,
    Preparing = 2,
    ReadyForPickup = 3,
    Assigned = 4,
    PickedUp = 5,
    Delivering = 6,
    Delivered = 7,
    Cancelled = 20,
    DeliveryFailed = 21,
    Returned = 22,
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
