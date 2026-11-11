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

## D4. Authorization is enforced in services, not only in the UI
Every service method resolves `CurrentUser` via `ICurrentUserAccessor` and checks role + resource ownership:
- Seller: `StoreMember` lookup for (storeId, userId) on every call; Owner-only for store profile, members, settlements. Product/category queries are always filtered by `StoreId`, so a foreign id is "not found".
- Driver: order must have `DriverId == me` for any action; available jobs hide customer phone/name/note until accepted.
- Customer: orders filtered by `CustomerId`.
- Pages additionally carry `[Authorize(Roles=…)]` for routing.
Client-supplied prices, totals, store ids of products, driver ids, etc. are never trusted.

## D5. Order state machine
`OrderStateMachine` (Domain) is the single table of `(from, to) → allowed actors`. `OrderWorkflow` applies a transition with its side effects (stock restore, assignment close, COD record) and always writes `OrderStatusHistory` + `AuditLog`. UI buttons are derived from `AllowedNextStatuses`, but the server re-validates.

Key rules: customer cancels only while `Pending`; seller rejects in `Pending/Confirmed`; admin may cancel until `Assigned`; after pickup only `DeliveryFailed`, which admin resolves to *retry* (`ReadyForPickup`) or `Returned`. Drivers accept only `ReadyForPickup` orders.

## D6. Concurrency & idempotency
- Stock reservation and driver acceptance are **conditional atomic UPDATEs** (`ExecuteUpdate … WHERE Stock >= qty` / `WHERE Status = ReadyForPickup AND DriverId IS NULL`) inside a transaction; 0 rows affected = lost the race.
- A **filtered unique index** (`OrderId WHERE IsActive = 1`) guarantees at most one active `DeliveryAssignment` per order at DB level.
- Optimistic concurrency via `Version` tokens on Order/Product/Store/DriverProfile/Settlement (SQLite has no rowversion; the DbContext increments it).
- Checkout idempotency: a client key per checkout page visit, unique `(CustomerId, IdempotencyKey)`.
- Settlement idempotency: unique `(OrderId, Type)` on `SettlementItem` and `CodTransaction`; confirmation is a conditional update `WHERE Status = Pending`, so a repeated confirm is a no-op.

## D7. Money
All money is `long` VND. Commission and driver share are basis points (`PlatformFeeBps`, `DriverShareBps`) with integer half-up rounding (`Money.ApplyBps`). For each order: `GrandTotal = ProductTotal + DeliveryFee − Discount`; `StoreReceivable = ProductTotal − PlatformFee`; `DriverOwesPlatform = COD − DriverEarning`; `PlatformRevenue = PlatformFee + DeliveryFee − DriverEarning − Discount`. These always sum to the amount the customer paid (unit-tested).

## D8. Delivery pricing without a map provider
No mapping service is integrated, so distance/ETA are **not fabricated**: at checkout the base fee applies and the order shows “Khoảng cách: Chưa xác định”. Admin can set a measured distance (manual override) before pickup, which recalculates the fee and notifies the customer. Driver "navigate" buttons open Google Maps search URLs.

## D9. Single-store cart
A cart belongs to one store. Adding a product from another store asks the customer to clear the cart first.

## D10. Realtime
A dedicated SignalR hub `/hubs/notifications` (`[Authorize]`); each connection joins only `user:{id}`. Messages contain only the recipient's own notification. All notifications are also persisted (`Notification` table), so after a reconnect the client reloads from the database (`onreconnected` → page reload of data). The SignalR JS client is vendored at `wwwroot/lib/signalr` (8.0.7) — no runtime CDN dependency.

## D11. SQLite & migrations
Single SQLite file (`Data/sky17.db`). Schema only via EF Core migrations (`Database.MigrateAsync()` at startup); `EnsureCreated` is never used. Check constraints on price/stock/quantity.

## D12. Seeding
Reference data (roles, rate rule, settings, platform categories) is always ensured. Demo data (stores, users, historical orders) is seeded only when `Seed:DemoData=true` (Development), with the password from configuration (`Seed:DemoPassword`), never from code.

## D13. Rate limiting
Global limiter: POSTs to `/Account/*` 10/min per IP (credential stuffing); other requests 300/min per IP; Blazor/framework traffic exempt.
