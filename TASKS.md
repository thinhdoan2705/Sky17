# TASKS — Ship Hoả Tốc Sky 17

Legend: ✅ done & verified · 🟡 partial (see note) · ⬜ not done

Verification used: `dotnet build` (0 errors), `dotnet test` (50/50), migrations applied on a fresh DB, demo seed, HTTP smoke test logging in as Admin/Seller/Driver/Customer and rendering every page (all 200, no server errors), auth rate-limit probe (429 after 10 POSTs/min), SignalR hub auth probe (anonymous 401, signed-in 200).
**Not verified:** clicking through the interactive UI in a real browser and visual checks at 375–1440 px (the browser automation extension was unavailable in this session) — do this with the manual guide in README.md.

## Phase 1 — Solution, Identity, SQLite, UI foundation
- ✅ Inspect SDKs (.NET 10.0.401) and “Blazor Swarm” (not a real package → design kit, D1)
- ✅ Solution `Sky17.sln`: Domain / Application / Infrastructure / Web / Tests
- ✅ EF Core SQLite `AppDbContext`, 22 business entities + Identity, FKs, indexes, unique & check constraints, explicit delete behaviours, UTC audit fields, `long` money, concurrency tokens
- ✅ Migration `InitialCreate`, `MigrateAsync` at startup (no `EnsureCreated`)
- ✅ Seeder: roles, rate rule, settings, platform categories; dev demo data from config password; production admin bootstrap via env vars
- ✅ Identity: login, logout (POST + antiforgery), register (customer / seller→pending store / driver→pending profile), forgot & reset password, change password, profile, role-based redirect, lockout, activation/deactivation, cookie hardening, 1-min security-stamp revalidation
- ✅ Design system: design-kit CSS + extensions; components: KpiCard, StatusBadge, OrderStatusBadge, OrderTimeline, ProductCard, StoreCard, DeliveryCard, Modal (bottom sheet on mobile), ConfirmDialog, Drawer, Toast, Pagination, Skeleton, EmptyState, BarChart, PageHeader, NotificationBell, UserMenu, tabs, responsive table→cards
- ✅ Four layouts (sidebar + topbar desktop, bottom tabs mobile; driver mobile-first)

## Phase 2 — Seller, Customer, checkout
- ✅ Customer: home (address, search, category chips, featured stores, products), search, stores, store detail, product detail, cart (qty/remove/clear, single-store switch), checkout (saved addresses, note, COD), order confirmation & live tracking timeline, order history, cancel while Pending, addresses CRUD, profile, notifications
- ✅ Checkout: server-side prices, atomic stock reservation, price/address snapshots, idempotency key, service-availability & store-hours checks
- ✅ Seller: dashboard (live KPIs, action list, 7-day chart, top products), orders (tabs, search, paging, quick actions), order detail (accept/reject with reason, preparing, ready), product CRUD + stock + availability + soft delete, product images (validated upload), categories (store + platform), sales report (date range), settlements (owner only), store profile & business hours & open/closed switch, members (owner/staff)
- ✅ Multi-store seller switcher; membership checked on every service call

## Phase 3 — Driver, dispatch, order lifecycle
- ✅ State machine with actor rules; history + audit on every transition
- ✅ **GrabFood dispatch** (docs/DISPATCH_FLOW.md): store accept → nearest free driver gets a ringing 30 s popup (sound, vibration, system notification), decline/timeout → next driver, GPS/manual location heartbeat, trip steps đến quán → lấy món → đến nơi → giao, driver cancel trip → re-dispatch, store “Món đã sẵn sàng”, live driver marker for customers, admin offer history, auto-cancel unanswered orders, no-driver alerts, dispatch settings. Migration `GrabDispatchFlow`. 14 dispatch tests.
- ✅ Driver: online/offline, dashboard (radar, acceptance rate), max concurrent deliveries setting, failure report with reason, navigation links, delivery detail, history, earnings & COD wallet, profile/vehicle/location
- ✅ Customer phone/name hidden from drivers until assigned; other drivers' deliveries are private
- ✅ Admin manual assignment, reassignment before pickup, unassign, failed-delivery review (retry / return), cancel with reason, manual distance override (re-prices order, notifies customer)

## Phase 4 — Admin, COD, reporting, tests
- ✅ Admin dashboard (all KPIs from DB, 7-day chart, today's status breakdown, recent orders & activities)
- ✅ Stores (approve/reject/suspend/reactivate, detail with members/products/sales), drivers (approve/reject/suspend, busy/available, deliveries, COD balance), customers (search, detail, activate/deactivate)
- ✅ Finance: overview, driver COD balances, store payables, create/confirm/cancel settlements (idempotent), COD ledger, settlement detail drawer
- ✅ Users & roles (multi-role), audit log, notification history, delivery/business settings with rate-rule history, platform categories
- ✅ Realtime: SignalR hub with per-user groups, persisted notifications, reconnect → reload from DB, toasts + live page refresh (new order, accepted, ready, driver assigned, picked up, delivered, cancelled, failed, settlement, approvals)
- ✅ Rate limiting (auth POST 10/min/IP, general 300/min/IP)
- ✅ Tests: registration, seller isolation, store ownership, product CRUD, checkout totals, snapshots, inventory, idempotency, state transitions, concurrent acceptance, COD, settlement idempotency, unauthorized access, end-to-end workflow
- ✅ Dockerfile + docker-compose, README, DECISIONS

## Known gaps / unfinished (deliberately not faked)
- ✅ **Customer login by phone number** (normalized VN number as login name, unique; partners keep e-mail login; existing customer accounts converted at startup). 11 tests.
- ✅ **Separate login pages** `/login`, `/seller/login`, `/driver/login`, `/admin/login` (role-checked per portal, `/Account/Login` forwards by return URL, partner logout returns to its login page).
- ✅ **Structured addresses** — Số nhà/đường + Phường/Xã + Tỉnh/Thành (34 provinces, 3,321 wards, 2025 catalog v5.2.0) + optional “Chi tiết địa chỉ”; province→ward picker with accent-insensitive filter in the address book and checkout; server validates the ward belongs to the province and composes the address. Migration `StructuredAddress`. 7 tests.
- ⬜ **Store addresses** — still a single free-text field (registration and store profile); could reuse `AdminUnitPicker`.
- ⬜ **SMS / OTP** — phone numbers are not verified by OTP and password reset still goes through e-mail; needs an SMS provider.
- ⬜ **Real e-mail delivery** — reset links are only logged in Development; implement `IEmailSender<ApplicationUser>` (SMTP / provider) for production. Email confirmation is therefore off.
- ✅ **Free maps** — Leaflet + OSM tiles; pin pickers for store location, saved addresses and checkout (Nominatim search, GPS, tap/drag); live fee quote from OSRM road distance; maps on order tracking, store page, driver dashboard/delivery (with “my location”) and admin order detail. Migration `AddMapCoordinates`. 6 tests (fake routing).
- 🟡 **Map providers for scale** — uses public OSM/Nominatim/OSRM demo servers (light-use policies); self-host or switch provider before production traffic. No live driver GPS tracking for customers.
- ⬜ **Online payments** — only COD. `PaymentMethod`/`Payment` entities are ready for more providers.
- ⬜ **Discounts/vouchers** — `DiscountAmount` is modelled (always 0) but there is no voucher UI.
- 🟡 **Business hours** — one daily open/close window per store (no per-weekday schedule).
- 🟡 **Store logo upload** — stores use an emoji; product images are uploadable.
- ⬜ **Browser E2E automation (Playwright)** and visual regression at the target widths.
- ✅ Background dispatch worker (every 3 s): offer expiry, re-dispatch, auto-cancel unanswered orders, admin alerts.
- ⬜ **Ring when the app is closed** — needs Web Push (service worker + VAPID) or a native app; today the driver page must stay open (foreground or background tab).
- ⬜ Driver ratings / batching several orders per trip / surge pricing — not implemented.
