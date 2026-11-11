# Order & dispatch flow (GrabFood style)

This document describes how an order moves from the customer to the store, how the nearest free driver is found, and what each party sees. Code references point to the files that implement each rule.

## 1. Order states

| Status (enum) | Vietnamese label | Meaning | Who moves it forward |
|---|---|---|---|
| `Pending` | Chờ quán xác nhận | Order placed, waiting for the store | Store accepts / rejects. Customer may cancel. System auto-cancels after *N* minutes |
| `Confirmed` | Đang tìm tài xế | Store accepted. Kitchen is preparing, dispatcher is searching for a driver | Driver accepts an offer / admin assigns |
| `Assigned` | Tài xế đang đến quán | A driver accepted and is heading to the store | Driver: “Tôi đã đến quán” |
| `ArrivedAtStore` | Tài xế đã đến quán | Driver is at the store waiting for the food | Driver: “Đã lấy món · Bắt đầu giao” |
| `Delivering` | Đang giao | Food picked up, on the way | Driver: “Tôi đã đến nơi giao” |
| `ArrivedAtCustomer` | Tài xế đã đến nơi | Driver is at the drop-off point | Driver: “Đã giao & thu COD” |
| `Delivered` | Đã giao | Done, COD recorded | — |
| `Cancelled` / `DeliveryFailed` / `Returned` | Đã hủy / Giao thất bại / Đã hoàn trả | Exception states | Admin reviews failed deliveries |

Kitchen preparation runs **in parallel** and is tracked separately (`Order.PreparationState`: Preparing → Ready). The store presses **“Món đã sẵn sàng”** at any time between `Confirmed` and `ArrivedAtStore`; the driver and customer are notified. Picking up the food implies it is ready.

The full transition table (who may do what) lives in [`OrderStateMachine`](../src/Sky17.Domain/OrderStateMachine.cs). Every transition writes `OrderStatusHistory` and `AuditLog`.

```
Pending ──store accepts──▶ Confirmed ──driver accepts offer / admin assigns──▶ Assigned
   │                         ▲   │                                              │
   │ customer/store/         │   └─ cancel (store/admin)                        ▼
   │ admin/system cancel     │                                           ArrivedAtStore
   ▼                         │  driver cancels trip / admin unassigns           │
Cancelled                    └──────────────────────────────────────────────────┤
                                                                                ▼
                                Delivered ◀── ArrivedAtCustomer ◀────────── Delivering
                                                   │                            │
                                                   └──────▶ DeliveryFailed ◀────┘
                                                              │        │
                                         admin: retry (Confirmed)   admin: Returned
```

## 2. Dispatch — finding the nearest free driver

Implemented in [`DispatchService`](../src/Sky17.Infrastructure/Services/DispatchService.cs), run every 3 s by [`DispatchWorker`](../src/Sky17.Web/Infrastructure/DispatchWorker.cs) and also immediately when the store accepts, a driver declines, a driver cancels a trip, or an admin un-assigns.

1. **Searching orders**: `Status = Confirmed`, no driver, no ringing offer.
2. **Eligible drivers** — all must hold:
   - driver profile approved (`Active`), account active, **online**;
   - **free**: no trip in `Assigned / ArrivedAtStore / Delivering / ArrivedAtCustomer`;
   - not currently ringing for another order (one popup at a time);
   - **fresh location**: last GPS heartbeat within *LocationFreshMinutes* (default 15);
   - within *DispatchRadiusKm* (default 20 km) of the store pin;
   - not already asked for this order in the last 2 minutes (a new round starts after that).
3. **Ranking**: straight-line distance driver → store pin, nearest first. (Straight-line is only used for ranking; prices always use OSRM road distance.) If the store has no pin, the driver with the freshest heartbeat is chosen.
4. **Offer**: a `DispatchOffer` row with `ExpiresAtUtc = now + OfferTimeoutSeconds` (default **30 s**) and a realtime notification to that driver only.
5. **Outcomes**
   - **Accept** → atomic: the offer must still be pending and unexpired, then the order is claimed with a conditional update (`Status = Confirmed AND DriverId IS NULL`). Status → `Assigned`.
   - **Decline** or **going offline** → offer `Declined`, next driver immediately.
   - **No answer in 30 s** → offer `Expired`, the popup closes, next driver.
   - **Admin assigns / order cancelled** → ringing offers are `Withdrawn`.
6. **Nobody found** for *NoDriverAlertMinutes* (default 5) → admins get “⚠️ Chưa tìm được tài xế” (once) and the customer gets a reassurance message. Admin can assign manually from the order page.
7. **Store never answers** within *StoreConfirmTimeoutMinutes* (default 10) → system cancels the order and restores stock.

**Database guards**: filtered unique indexes allow only one *pending* offer per order and one per driver; one active `DeliveryAssignment` per order. Tested in [`DispatchTests`](../tests/Sky17.Tests/DispatchTests.cs).

All parameters are editable in **Admin → Cài đặt → Điều phối tài xế**.

## 3. Driver app

- **Online switch** (top bar). Turning it on also unlocks sound and asks for notification permission (browsers only allow sound after a user gesture).
- **Location heartbeat** every 20 s while online ([`wwwroot/js/driver.js`](../src/Sky17.Web/wwwroot/js/driver.js)):
  - *GPS mode* (default): `navigator.geolocation.watchPosition` — needs HTTPS or localhost and permission;
  - *Manual mode*: the driver pins a waiting spot in **Hồ sơ & vị trí** (useful on desktops or when GPS is denied).
  A status bar shows when the last position was sent. Without a fresh position the driver receives no offers.
- **Offer popup** ([`DriverOfferPopup.razor`](../src/Sky17.Web/Components/Layout/DriverOfferPopup.razor)): full screen, 30 s countdown ring, earning, distance to store, trip distance, pickup and drop-off addresses, COD amount; **Nhận chuyến / Bỏ qua**. It rings (two-tone Web Audio loop), vibrates, and shows a system notification if permitted. It also polls every 5 s as a safety net if a realtime message is missed.
- **Trip screen**: one big action per step — Chỉ đường đến quán → Tôi đã đến quán → Đã lấy món · Bắt đầu giao → Chỉ đường/Gọi khách → Tôi đã đến nơi giao → Đã giao & thu COD. “Hủy chuyến” (before pickup) sends the order back to dispatch; “Báo giao thất bại” after pickup goes to admin review.
- Dashboard shows today's earnings, acceptance rate (7 days), COD wallet, a “searching” radar when free and online.

**Limitation**: a web app can ring only while the page is open (foreground or background tab). Ringing a locked phone with the app closed requires Web Push (service worker + VAPID) or a native app — not implemented.

## 4. Store (seller)

- New order: **Nhận đơn** (→ `Confirmed`, dispatch starts at once) or **Từ chối** with a reason.
- **Món đã sẵn sàng** button while the driver is on the way / at the store.
- Sees the driver's progress (đang đến / đã đến quán / đã lấy món) in realtime.

## 5. Customer

The tracking page shows a Grab-like status banner (Đang chờ quán xác nhận → Quán đang chuẩn bị · đang tìm tài xế → Tài xế đang đến quán → … → Tài xế đã đến! Vui lòng ra nhận hàng), the kitchen state, the progress bar, the map with store, drop-off and the **live driver marker** (refreshed every 15 s while a driver is on the trip), and the driver's name, plate and phone.

## 6. Admin

- Order page: “Đang tìm tài xế” banner with the number of offers sent, the **offer history** (driver, distance, accepted/declined/expired/withdrawn, response time), manual assign / reassign / unassign, cancel, failed-delivery review.
- Settings: offer timeout, radius, GPS freshness, store-confirmation timeout, no-driver alert delay, max concurrent trips.

## 7. Demo walkthrough (Development seed)

Password for all demo accounts: `Seed:DemoPassword` in `appsettings.Development.json`.

1. Log in at `/driver/login` as **`driver.minhtri@sky17.local`** (free, online, ~300 m from Sky Coffee). The seeded order *“đang tìm tài xế”* rings within a few seconds.
   - If more than 15 minutes passed since startup, the seeded position is stale: keep the driver page open (GPS) or pin a spot near Thủ Dầu Một under **Hồ sơ & vị trí → Ghim vị trí thủ công**.
2. In another browser (or a private window) log in at `/login` as customer **`0904000001`** (phone number), order from Sky Coffee and pin the drop-off on the map.
3. Log in at `/seller/login` as **`owner.skycoffee@sky17.local`** → **Đơn hàng → Nhận đơn**. The free driver's phone rings immediately.
4. Driver accepts → goes through the trip steps. The customer page updates live; the seller can press **Món đã sẵn sàng**.
5. Let an offer run out (30 s) or press **Bỏ qua** to see it move to the next driver (log in a second driver, e.g. `driver.vuanh@sky17.local`, after their current trip ends).
