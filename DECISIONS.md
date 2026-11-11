# DECISIONS — Ship Hoả Tốc Sky 17

Architectural decisions log. Newest decisions at the bottom of each section.

## D1. UI stack: the supplied design kit, no third-party component library
- **“Blazor Swarm” is not an identifiable package.** NuGet search (`BlazorSwarm`, `Blazor.Swarm`, `swarm blazor`) only returns unrelated `BeSwarm.*` packages. The repository had no such dependency or convention.
- The supplied `sky17-html/` design (`assets/style.css`) is the visual source of truth. It already defines tokens, layouts and components (sidebar, KPI cards, tables, dialogs, delivery cards, mobile tabs, etc.).
- Decision: **copy the design kit verbatim** to `wwwroot/css/sky17.css` (tokens updated to the master palette: primary `#1769E0`, accent `#F97316`, success `#16A34A`, danger `#DC2626`, bg `#F4F7FB`, text `#0F172A`) and add `wwwroot/css/app.css` for the missing pieces (timeline, drawer, pagination, skeleton, responsive table→card, auth pages). Build our own Razor components on top (`Components/UI`).
- Not adopting Blazor Blueprint / NeoUI: they would bring a second visual language that conflicts with the supplied design, and the design kit already covers our needs. No React/Vue/second frontend stack.
- Form controls (Input/Select/Checkbox/Radio/Textarea) use Blazor's built-in `Input*` components styled by the kit's `.field` classes, so validation integrates with `EditForm`.

## D2. Modular monolith
`Sky17.Domain` (entities, enums, state machine, money math) ← `Sky17.Application` (DTOs, input models with validation, service interfaces) ← `Sky17.Infrastructure` (EF Core, Identity user, service implementations, seeding, file storage) ← `Sky17.Web` (Blazor, SignalR hub, Identity endpoints). No generic repository; services use `IDbContextFactory<AppDbContext>` and create a short-lived context per operation (safe for long-lived Blazor circuits).

## D3. Identity
- ASP.NET Core Identity with cookies; `/Account/*` pages are **static SSR** (`[ExcludeFromInteractiveRouting]`) so they can set cookies; all other pages run **Interactive Server**.
- `ApplicationUser` lives in Infrastructure (Domain has no Identity dependency); business data in `UserProfile` (1:1).
- Deactivation = `IsActive=false` + lockout until `MaxValue` + security-stamp rotation. Security stamp revalidated every minute (cookie validator and circuit revalidation), so role changes/deactivations take effect quickly.
- **Email confirmation is disabled** (no email provider in the Lite MVP). Password-reset links are written to the server log in Development (`LoggingEmailSender`). Production needs a real `IEmailSender<ApplicationUser>` — see TASKS.md.
- Public registration can create Customer / Seller (pending store) / Driver (pending profile) only. Admin is never publicly registrable; Admin accounts come from the dev seed or `Seed:AdminEmail/AdminPassword` env vars.
- **Login names**: customers sign in with their **phone number** (Identity `UserName` = normalized VN number, e.g. `+84 904 000 001` → `0904000001`, unique); a customer-only account cannot sign in with e-mail. Sellers, drivers and admins sign in with e-mail. Customers still give an e-mail at registration because password reset is e-mail based (no SMS/OTP provider yet). Changing the phone in the profile changes the login name (uniqueness checked). Existing customer accounts with e-mail login names are converted at startup when their phone number is valid and free. Resolution logic: `AccountService.FindLoginAsync`.
- **Separate login page per portal** (`LoginPortals` in `Components/Account/LoginPortals.cs`, one `Login.razor` with several routes): `/login` (customers, phone number only), `/seller/login`, `/driver/login`, `/admin/login` (e-mail only). A page signs in **only accounts holding that portal's role**; the password is checked first (with lockout) so the role check never reveals which accounts exist, and a wrong-portal account gets a link to its own login page. `/Account/Login` (the cookie `LoginPath`) forwards to the portal that owns `returnUrl`; `returnUrl` is honoured only inside the same portal. Partners return to their portal login after logout. All five routes share the strict auth rate limit.

## D4. Authorization is enforced in services, not only in the UI
Every service method resolves `CurrentUser` via `ICurrentUserAccessor` and checks role + resource ownership:
- Seller: `StoreMember` lookup for (storeId, userId) on every call; Owner-only for store profile, members, settlements. Product/category queries are always filtered by `StoreId`, so a foreign id is "not found".
- Driver: order must have `DriverId == me` for any action; available jobs hide customer phone/name/note until accepted.
- Customer: orders filtered by `CustomerId`.
- Pages additionally carry `[Authorize(Roles=…)]` for routing.
Client-supplied prices, totals, store ids of products, driver ids, etc. are never trusted.

## D5. Order state machine
`OrderStateMachine` (Domain) is the single table of `(from, to) → allowed actors`. `OrderWorkflow` applies a transition with its side effects (stock restore, assignment close, COD record) and always writes `OrderStatusHistory` + `AuditLog`. UI buttons are derived from `AllowedNextStatuses`, but the server re-validates.

Key rules (GrabFood flow, see D14): customer cancels only while `Pending`; the system auto-cancels `Pending` orders the store never answered; seller accepts/rejects in `Pending` (and may cancel while still searching); drivers get `Confirmed` orders only through dispatch offers; a driver may cancel the trip before pickup (back to `Confirmed`); admin may cancel until the food is picked up; after pickup only `DeliveryFailed`, which admin resolves to *retry* (`Confirmed`, re-dispatch) or `Returned`.

## D6. Concurrency & idempotency
- Stock reservation and driver acceptance are **conditional atomic UPDATEs** (`ExecuteUpdate … WHERE Stock >= qty` / `WHERE Status = ReadyForPickup AND DriverId IS NULL`) inside a transaction; 0 rows affected = lost the race.
- A **filtered unique index** (`OrderId WHERE IsActive = 1`) guarantees at most one active `DeliveryAssignment` per order at DB level.
- Optimistic concurrency via `Version` tokens on Order/Product/Store/DriverProfile/Settlement (SQLite has no rowversion; the DbContext increments it).
- Checkout idempotency: a client key per checkout page visit, unique `(CustomerId, IdempotencyKey)`.
- Settlement idempotency: unique `(OrderId, Type)` on `SettlementItem` and `CodTransaction`; confirmation is a conditional update `WHERE Status = Pending`, so a repeated confirm is a no-op.

## D7. Money
All money is `long` VND. Commission and driver share are basis points (`PlatformFeeBps`, `DriverShareBps`) with integer half-up rounding (`Money.ApplyBps`). For each order: `GrandTotal = ProductTotal + DeliveryFee − Discount`; `StoreReceivable = ProductTotal − PlatformFee`; `DriverOwesPlatform = COD − DriverEarning`; `PlatformRevenue = PlatformFee + DeliveryFee − DriverEarning − Discount`. These always sum to the amount the customer paid (unit-tested).

## D8. Free maps & distance-based pricing
- **Map display:** Leaflet 1.9.4 (vendored in `wwwroot/lib/leaflet`) with OpenStreetMap tiles — free, no API key. Attribution “© OpenStreetMap” is shown on every map.
- **Address search:** Nominatim (OSM), called from the browser only when the user presses “Tìm” (respects the ≤1 req/s policy, no autocomplete). Street-level Vietnamese addresses are sometimes missing in OSM, so users can always tap/drag the pin or use GPS (“Dùng vị trí hiện tại”).
- **Distance:** OSRM road distance between the store pin and the drop-off pin (`Maps:OsrmBaseUrl`, default public demo `https://router.project-osrm.org`). Computed server-side at quote and checkout; the client never sends a distance or fee.
- **No fabrication:** if either pin is missing or OSRM fails, the distance stays null, `DistanceSource = None`, and the base fee applies. Admin can still override the distance manually (`DistanceSource = Manual`).
- **Privacy:** store pickup pins are public; a customer's drop-off pin is only returned to that customer, the store, admin and the *assigned* driver (open jobs show pickup only).
- **Production note:** public OSM tile, Nominatim and OSRM demo servers are for light use. For real traffic, self-host OSRM/Nominatim or use a commercial tile/geocoding provider and change the URLs (`Maps:OsrmBaseUrl`, `wwwroot/js/map.js`).

## D9. One cart per store (supersedes "single-store cart")
- A customer has **one cart per store** (`Cart` unique on `(UserId, StoreId)`, `StoreId` NOT NULL, cascade-deleted with the store). Adding a dish from another store simply goes into that store's cart; nothing is replaced and there is no "switch store" prompt.
- **Checkout is per store**: `/checkout/{storeId}` and `CheckoutInput.StoreId`; placing an order removes only that store's cart. Each order still has exactly one store (pickup, settlement and dispatch stay simple). `/checkout` without a store id redirects to the only cart, or to `/cart` when there are several.
- `/cart` lists every store cart with its own summary, warnings (store closed, out of stock) and an "Đặt đơn tại …" button. Empty carts are deleted. The header badge is the total item count across carts.
- Migration `CartPerStore` deletes store-less (empty) carts and replaces the unique index on `UserId` with `(UserId, StoreId)`; existing carts with a store are kept.

## D16. Dish search grouped by store, nearest first
- `/search` (`ICatalogService.SearchByStoreAsync`) returns **stores, each with its matching dishes** (dish name, description or category; a store whose name/tagline matches shows its menu). Without a keyword it lists all stores with their menus.
- Stores are sorted by **straight-line distance, nearest first**, and the distance is shown next to the store name ("2,7 km"). Stores without a map pin come last with "? km". Straight-line (haversine) is used because routing every store through OSRM on each search would be slow and abuse the free server; the delivery fee still uses road distance at checkout (D8).
- Distance is measured from the customer's default **saved address with a pin**; they can switch to another saved address or to "Vị trí hiện tại" (browser geolocation). The choice is kept for the session (`CustomerLocation`, per circuit). Anonymous visitors can use the device location.

## D10. Realtime
A dedicated SignalR hub `/hubs/notifications` (`[Authorize]`); each connection joins only `user:{id}`. Messages contain only the recipient's own notification. All notifications are also persisted (`Notification` table), so after a reconnect the client reloads from the database (`onreconnected` → page reload of data). The SignalR JS client is vendored at `wwwroot/lib/signalr` (8.0.7) — no runtime CDN dependency.

## D11. SQLite & migrations
Single SQLite file (`Data/sky17.db`). Schema only via EF Core migrations (`Database.MigrateAsync()` at startup); `EnsureCreated` is never used. Check constraints on price/stock/quantity.

## D12. Seeding
Reference data (roles, rate rule, settings, platform categories) is always ensured. Demo data (stores, users, historical orders) is seeded only when `Seed:DemoData=true` (Development), with the password from configuration (`Seed:DemoPassword`), never from code.

## D14. GrabFood-style dispatch (supersedes the "open jobs" board)
- **Flow change**: `Pending → Confirmed (store accepted, searching) → Assigned → ArrivedAtStore → Delivering → ArrivedAtCustomer → Delivered`. The old `Preparing`, `ReadyForPickup` and `PickedUp` statuses were removed; migration `GrabDispatchFlow` maps existing rows (2,3 → Confirmed; 5 → Delivering). Kitchen progress is a separate `PreparationState` so driver search starts as soon as the store accepts (as on GrabFood), not when the food is ready.
- **Push, not pull**: drivers no longer browse/claim open jobs. The dispatcher offers each order to one driver at a time (`DispatchOffer`), nearest free driver first, with a 30 s timeout. Rationale: matches the requested Grab experience and avoids drivers cherry-picking.
- **"Free" = no active trip**. Admins may still assign manually up to the max-concurrent-trips setting.
- **Location**: drivers send a heartbeat every 20 s while online (GPS or a manually pinned spot). Positions older than 15 min are ignored, so drivers who closed the app stop receiving offers. Straight-line distance is used only to rank drivers; prices use OSRM road distance (D8).
- **Timing** relies on `IClock` (tests use a fake clock) and a `BackgroundService` ticking every 3 s; DB filtered unique indexes guarantee one pending offer per order and per driver.
- **Ringing**: Web Audio tone loop + vibration + Notification API while the web app is open. True push to a locked phone with the app closed needs Web Push or a native app (not built).
- **Customer privacy**: an offered (not yet accepted) driver sees pickup info and the drop-off *address* but not the customer's name/phone/pin; live driver position is shown to the customer only while that driver works on the order.
- Full description: [docs/DISPATCH_FLOW.md](docs/DISPATCH_FLOW.md).

## D13. Rate limiting
Global limiter: POSTs to `/Account/*` 10/min per IP (credential stuffing); other requests 300/min per IP; Blazor/framework traffic exempt.

## D15. Structured addresses (2025 two-level administration)
- Since 1 July 2025 Vietnam has **two administrative levels**: province/centrally-run city (34) → ward/commune/special zone (3,321). There are no districts any more, so addresses are **Số nhà, tên đường · Phường/Xã · Tỉnh/Thành phố**, plus an optional **Chi tiết địa chỉ** (building, floor, room, alley, landmark).
- The catalog comes from [vietnamese-provinces-database](https://github.com/thanglequoc/vietnamese-provinces-database) **v5.2.0 (388/NQ-UBTVQH16)**. The supplied MSSQL import script was converted to a compact JSON file, `src/Sky17.Infrastructure/Data/Reference/vn-units.json`, embedded in the assembly and served by `AdministrativeUnitCatalog` (singleton, in memory). It is immutable reference data versioned with the code, so it needs no tables, seeding or admin UI; a new dataset version = replace the JSON.
- The client only sends codes (`ProvinceCode`, `WardCode`); the server checks that the ward belongs to the province (`IAdministrativeUnits.Resolve`) and **composes the text itself**: `Order.DeliveryAddress = "118 Lê Hồng Phong, Phường Thủ Dầu Một, Thành phố Hồ Chí Minh"`. Orders also snapshot `DeliveryWardCode`, `DeliveryProvinceCode` and `DeliveryAddressDetail`; saved addresses store both codes and full names.
- `DeliveryAddressDetail` (where exactly to go) is separate from `DeliveryNote` (instructions for the order: call first, less ice…). The detail, like the customer's phone, is shown to a driver only after they accept the trip.
- Migration `StructuredAddress` drops `Addresses.District` after appending its text to `Street`, so old addresses still read correctly; they have no codes and the UI asks the customer to re-select the ward (they cannot be mapped reliably because wards were merged in 2025).
- Store addresses are still free text (see TASKS.md).
