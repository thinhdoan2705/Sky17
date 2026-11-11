# Ship Hoả Tốc Sky 17

Multi-store delivery marketplace (Lite MVP) connecting **customers, sellers, drivers and administrators**.
ASP.NET Core **Blazor Web App (.NET 10, Interactive Server)** · EF Core + **SQLite** · ASP.NET Core **Identity** · **SignalR** realtime · Vietnamese UI · VND.

The visual design comes from the supplied HTML kit (`sky17-html/`); see [DECISIONS.md](DECISIONS.md) for architecture choices and [TASKS.md](TASKS.md) for the feature checklist.

## Requirements
- .NET SDK **10.0.x** (`dotnet --list-sdks`)
- Optional: `dotnet-ef` tool for creating new migrations (`dotnet tool install -g dotnet-ef`)
- Optional: Docker

## Run locally
```bash
dotnet restore
dotnet build
dotnet test
dotnet run --project src/Sky17.Web --launch-profile http
```
Open http://localhost:5271. On startup the app **applies EF Core migrations** to `src/Sky17.Web/Data/sky17.db` and, in Development, seeds demo data.

Reset the demo database: stop the app and delete `src/Sky17.Web/Data/sky17.db*`.

## Development accounts
Seeded only when `ASPNETCORE_ENVIRONMENT=Development`. The password is **not in code**; it is `Seed:DemoPassword` in `src/Sky17.Web/appsettings.Development.json` (default `Sky17@Dev2026`; override with user-secrets or the `Seed__DemoPassword` env var).

| Role | Email | Notes |
|---|---|---|
| Admin | `admin@sky17.local` | |
| Seller (owner) | `owner.skycoffee@sky17.local` | Sky Coffee (active) |
| Seller (staff) | `staff.skycoffee@sky17.local` | Staff of Sky Coffee |
| Seller (owner) | `owner.bepnhamay@sky17.local` | Bếp Nhà Mây (active) |
| Seller (owner) | `owner.maybakery@sky17.local` | Mây Bakery (**pending approval**) |
| Driver | `driver.lehoang@sky17.local` | Approved, online |
| Driver | `driver.vuanh@sky17.local` | Approved, offline |
| Driver | `driver.anhkhoa@sky17.local` | **Pending approval** |
| Customer | `customer.minhanh@sky17.local` | Has saved addresses |
| Customer | `customer.giahuy@sky17.local` | |

Demo data includes delivered orders over the last 7 days (dashboards, COD balances) and live orders in Pending / Preparing / ReadyForPickup / Delivering.

## Portals
| URL | Who | Highlights |
|---|---|---|
| `/` | Public / Customer | Home, search, stores, product, cart (single store), checkout (COD), tracking timeline, order history, addresses, profile, notifications |
| `/seller` | Seller (Owner/Staff) | Dashboard, incoming orders (accept/reject → preparing → ready), products CRUD + images + stock, categories, sales report, settlements, store profile & hours, members |
| `/driver` | Driver | Online/offline, open jobs, atomic accept, pickup → delivering → delivered/failed, Google Maps navigation, earnings & COD wallet, trip history, profile |
| `/admin` | Admin | Live KPIs, orders (filter/paging/timeline/assign/reassign/cancel/failed review/distance override), stores & drivers approval, customers, COD & settlements, users & roles, settings, audit log, notification history |
| `/Account/*` | All | Login, register (customer/seller/driver), forgot/reset password, change password, logout |

## Production / Docker
```bash
docker compose up --build            # http://localhost:8080 (Development mode with demo data by default)
```
For a real deployment set `ASPNETCORE_ENVIRONMENT=Production`, `Seed__DemoData=false`, and bootstrap the first admin with `Seed__AdminEmail` / `Seed__AdminPassword` (environment variables or secrets — never commit them). Data (SQLite DB + data-protection keys) is stored in the `sky17-data` volume, uploads in `sky17-uploads`. Put the container behind an HTTPS reverse proxy.

Configuration keys: `ConnectionStrings:DefaultConnection`, `Seed:*`, `DataProtection:KeysPath`, `DisableHttpsRedirection`.

## Migrations
```bash
dotnet ef migrations add <Name> -p src/Sky17.Infrastructure -s src/Sky17.Web -o Data/Migrations
```
Migrations are applied automatically at startup (`Database.MigrateAsync`).

## Tests
`dotnet test` runs 50 tests against real SQLite databases with the real migrations: state machine, pricing/money math, registration, seller isolation & store ownership, product CRUD, checkout totals, price/address snapshots, inventory reservation/rollback/restore, checkout idempotency, concurrent driver acceptance (5 drivers race, exactly one wins), COD recording, settlement idempotency, unauthorized access, and a full 13-step end-to-end workflow.

## Manual end-to-end test guide
1. **Admin login** — `/Account/Login` as `admin@sky17.local` → lands on `/admin` with live KPIs.
2. **Seller registration** — logout, `/Account/Register?kind=seller`, fill store name/address → lands on `/seller` with “chờ duyệt” banner.
3. **Admin approves seller** — admin `/admin/stores` → tab *Chờ duyệt* → *Phê duyệt*. The seller receives a realtime notification.
4. **Seller creates products** — `/seller/products/new`, save, then upload an image (JPG/PNG/WebP ≤ 2 MB). Set store hours in `/seller/store` if needed.
5. **Customer registration** — `/Account/Register` (Khách hàng).
6. **Customer order** — open the store, add products, `/cart` → *Tiến hành đặt hàng* → `/checkout` → *Đặt hàng ngay*. Tracking page opens; the seller's dashboard updates in realtime.
7. **Seller confirms** — `/seller/orders` → *Nhận đơn* → *Bắt đầu chuẩn bị* → *Sẵn sàng giao*. The customer timeline updates live.
8. **Driver registration & approval** — register with `?kind=driver`; admin `/admin/drivers` → *Duyệt*.
9. **Driver accepts** — driver turns the online switch on, `/driver/jobs` → *Nhận đơn* (try two drivers at once: only one succeeds).
10. **Pickup** — *Xác nhận lấy hàng* (Google Maps button opens the pickup address).
11. **Delivery** — *Bắt đầu giao* → *Đã giao & thu …đ* (or *Báo giao thất bại* with a reason → admin resolves in the order page).
12. **COD recorded** — driver `/driver/earnings` shows cash held and amount owed; admin `/admin/finance` → *Giao dịch COD*.
13. **Admin settlement** — `/admin/finance` → *Công nợ tài xế* → *Lập phiếu* → *Phiếu đối soát* → *Xác nhận*; same for *Thanh toán cửa hàng*. The seller sees it in `/seller/settlements`.

Responsive check: open DevTools device mode at 375 / 390 / 768 / 1024 / 1440 px. Below 800 px the sidebar becomes bottom tabs, tables become cards and dialogs become bottom sheets.
