// End-to-end test of the whole GrabFood-style flow in a real browser (Blazor Server UI).
//
// Customer (phone login) → search grouped by store → two store carts → checkout one store
//   → seller ringing panel (+ a second customer at the same time) → "Nhận tất cả"
//   → nearest driver ringing offer → accept → arrive store → pickup → arrive → deliver + COD
//   → customer sees "Đã giao" → admin creates & confirms the driver COD settlement.
//
// Requirements: a running app with Development demo data, Chrome installed, and playwright-core:
//   npm i -g playwright-core      (or set PLAYWRIGHT_CORE to the package folder)
// Usage:
//   node tests/e2e/full-flow.js [baseUrl] [demoPassword]
//   default baseUrl http://localhost:5271, password from src/Sky17.Web/appsettings.Development.json
const path = require('path');
const fs = require('fs');
const { chromium } = require(process.env.PLAYWRIGHT_CORE || 'playwright-core');

const BASE = (process.argv[2] || 'http://localhost:5271').replace(/\/$/, '');
const PASSWORD = process.argv[3] || JSON.parse(fs.readFileSync(path.join(__dirname, '../../src/Sky17.Web/appsettings.Development.json'), 'utf8')).Seed.DemoPassword;
const HEADED = !!process.env.HEADED;
const STORE_PIN = { latitude: 10.9804, longitude: 106.6531 }; // Sky Coffee (demo)

let step = 0;
const log = (msg) => console.log(`${String(++step).padStart(2, '0')}. ${msg}`);
const fail = (msg) => { throw new Error(msg); };
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const errors = [];

async function newRole(browser, name, opts = {}) {
    const ctx = await browser.newContext({ locale: 'vi-VN', ...opts });
    const page = await ctx.newPage();
    page.on('pageerror', e => errors.push(`[${name}] page error: ${e.message}`));
    page.on('console', m => { if (m.type() === 'error' && !/favicon|ERR_CONNECTION|WebSocket/.test(m.text())) errors.push(`[${name}] console: ${m.text()}`); });
    page.on('response', r => { if (r.status() >= 500) errors.push(`[${name}] HTTP ${r.status()} ${r.url()}`); });
    return { ctx, page, name };
}

/** Wait until the Blazor circuit is connected so @onclick handlers work. */
async function interactive(page) {
    await page.waitForFunction(() => window.Blazor && document.querySelector('[data-blazor-ready], body'), null, { timeout: 15000 });
    await page.waitForTimeout(1200);
    // Phone layouts must never scroll sideways (a too-wide element breaks the whole page in the app).
    const vw = page.viewportSize()?.width ?? 1000;
    if (vw < 500) {
        const wide = await page.evaluate(() => {
            const w = document.documentElement.clientWidth;
            if (document.documentElement.scrollWidth <= w + 1) return null;
            const el = [...document.querySelectorAll('body *')].find(e => e.getBoundingClientRect().right > w + 1 && getComputedStyle(e).position !== 'fixed');
            return `${document.documentElement.scrollWidth}px > ${w}px, e.g. <${el?.tagName.toLowerCase()} class="${el?.className}">`;
        });
        if (wide) errors.push(`horizontal overflow on ${new URL(page.url()).pathname}: ${wide}`);
    }
}

async function login(page, url, login) {
    await page.goto(BASE + url);
    await interactive(page); // also checks the login page layout on phones
    await page.fill('#login', login);
    await page.fill('#password', PASSWORD);
    await Promise.all([page.waitForNavigation(), page.click('button[type=submit]')]);
    if (/\/login/.test(page.url())) fail(`login failed for ${login}: ${await page.locator('.alert, .validation-message').allInnerTexts()}`);
    await interactive(page);
}

async function text(page) { return (await page.locator('body').innerText()).replace(/\s+/g, ' '); }

async function addToCart(page, dish) {
    const btn = page.getByRole('button', { name: `Thêm ${dish} vào giỏ` }).first();
    await btn.click();
    await page.waitForSelector(`text=/Đã thêm “${dish}”/`, { timeout: 10000 });
}

(async () => {
    const browser = await chromium.launch({ channel: 'chrome', headless: !HEADED });
    const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, geolocation: STORE_PIN, permissions: ['geolocation'] };
    const customer = await newRole(browser, 'customer', phone);
    const customer2 = await newRole(browser, 'customer2', phone);
    const seller = await newRole(browser, 'seller', { viewport: { width: 1280, height: 900 } });
    const driver = await newRole(browser, 'driver', { ...phone, geolocation: { latitude: STORE_PIN.latitude + 0.0027, longitude: STORE_PIN.longitude } });
    const admin = await newRole(browser, 'admin', { viewport: { width: 1280, height: 900 } });

    // ---------------------------------------------------------------- logins
    await login(seller.page, '/seller/login', 'owner.skycoffee@sky17.local');
    log('Seller logged in at /seller/login');
    const initialIncoming = await seller.page.locator('.incoming-card').count();
    log(`Seller panel shows ${initialIncoming} order(s) already waiting (demo data)`);

    await login(driver.page, '/driver/login', 'driver.minhtri@sky17.local');
    log('Driver logged in at /driver/login (online, GPS near Sky Coffee)');

    await login(customer.page, '/login', '0904000001');
    log('Customer logged in with phone number 0904000001');

    // Wrong-portal check: a customer phone on the driver login page is refused.
    {
        const p = await (await browser.newContext()).newPage();
        await p.goto(BASE + '/driver/login');
        await p.fill('#login', '0904000001'); await p.fill('#password', PASSWORD);
        await Promise.all([p.waitForLoadState(), p.click('button[type=submit]')]);
        if (!/\/driver\/login/.test(p.url())) fail('customer phone was accepted on /driver/login');
        log('Customer phone refused on /driver/login (portal separation OK)');
        await p.context().close();
    }

    // ---------------------------------------------------------------- search grouped by store
    await customer.page.goto(BASE + '/search');
    await interactive(customer.page);
    const groups = await customer.page.$$eval('.store-result', els => els.map(e => ({
        name: e.querySelector('.store-result-name b')?.textContent.trim(),
        km: e.querySelector('.km-badge')?.textContent.trim(),
        dishes: e.querySelectorAll('.product').length,
    })));
    if (groups.length < 2) fail('search should list at least 2 stores');
    const kms = groups.map(g => parseFloat((g.km || '').replace(',', '.')));
    if (kms.some(isNaN)) fail('every demo store should show a distance: ' + JSON.stringify(groups));
    if (kms.some((k, i) => i > 0 && k < kms[i - 1])) fail('stores are not sorted nearest first: ' + JSON.stringify(groups));
    log('Search: ' + groups.map(g => `${g.name} (${g.km}, ${g.dishes} món)`).join(' → '));

    await customer.page.goto(BASE + '/search?q=' + encodeURIComponent('trà'));
    await interactive(customer.page);
    const teaStores = await customer.page.$$eval('.store-result .store-result-name b', els => els.map(e => e.textContent.trim()));
    log(`Search “trà”: dishes grouped under ${teaStores.join(', ')}`);

    // ---------------------------------------------------------------- one cart per store
    await addToCart(customer.page, 'Trà đào cam sả');   // Sky Coffee
    await addToCart(customer.page, 'Trà tắc');          // Bếp Nhà Mây
    await customer.page.goto(BASE + '/cart');
    await interactive(customer.page);
    const cartStores = await customer.page.$$eval('.cart-store section h2', els => els.map(e => e.textContent.trim()));
    if (cartStores.length !== 2) fail('expected 2 store carts, got ' + JSON.stringify(cartStores));
    log('Cart page shows 2 separate store carts: ' + cartStores.join(' | '));

    // ---------------------------------------------------------------- checkout Sky Coffee only
    await customer.page.click('text=Đặt đơn tại Sky Coffee');
    await customer.page.waitForURL(/\/checkout\/\d+/);
    await interactive(customer.page);
    const ward = await customer.page.$eval('#co-ward', s => s.options[s.selectedIndex]?.textContent);
    const province = await customer.page.$eval('#co-province', s => s.options[s.selectedIndex]?.textContent);
    if (!/Phường/.test(ward || '') || !/Hồ Chí Minh/.test(province || '')) fail(`checkout address not prefilled: ${province} / ${ward}`);
    await customer.page.fill('#rdetail', 'Cổng xanh, gọi khi tới');
    await customer.page.fill('#rnote', 'Ít đá giúp mình');
    log(`Checkout prefilled from saved address: ${ward}, ${province}`);
    await Promise.all([customer.page.waitForURL(/\/orders\/SKY/, { timeout: 20000 }), customer.page.click('button:has-text("Đặt hàng ngay")')]);
    const orderCode = customer.page.url().match(/\/orders\/(SKY[^?]+)/)[1];
    log(`Order placed: ${orderCode}`);

    await customer.page.goto(BASE + '/cart');
    await interactive(customer.page);
    const left = await customer.page.$$eval('.cart-store section h2', els => els.map(e => e.textContent.trim()));
    if (left.length !== 1 || !/Bếp Nhà Mây/.test(left[0])) fail('the other store cart should remain: ' + JSON.stringify(left));
    log('Bếp Nhà Mây cart is untouched after checking out Sky Coffee');

    // Second customer orders at the same time.
    await login(customer2.page, '/login', '0904000002');
    await customer2.page.goto(BASE + '/search?q=' + encodeURIComponent('bánh mì'));
    await interactive(customer2.page);
    await addToCart(customer2.page, 'Bánh mì đặc biệt');
    await customer2.page.goto(BASE + '/cart');
    await interactive(customer2.page);
    await customer2.page.click('text=Đặt đơn tại Sky Coffee');
    await customer2.page.waitForURL(/\/checkout\/\d+/);
    await interactive(customer2.page);
    await Promise.all([customer2.page.waitForURL(/\/orders\/SKY/, { timeout: 20000 }), customer2.page.click('button:has-text("Đặt hàng ngay")')]);
    const orderCode2 = customer2.page.url().match(/\/orders\/(SKY[^?]+)/)[1];
    log(`Second customer placed ${orderCode2} at the same time`);

    // ---------------------------------------------------------------- seller ringing panel
    await seller.page.waitForSelector(`.incoming-card:has-text("${orderCode}")`, { timeout: 15000 });
    await seller.page.waitForSelector(`.incoming-card:has-text("${orderCode2}")`, { timeout: 15000 });
    const count = await seller.page.locator('.incoming-card').count();
    const ringing = await seller.page.evaluate(async () => (await import('/js/ringer.js')).isRinging());
    if (!ringing) fail('seller panel should be ringing');
    log(`Seller panel popped up with ${count} orders and is ringing (no page refresh)`);
    await seller.page.click('button:has-text("Nhận tất cả")');
    await seller.page.waitForSelector('.incoming-card', { state: 'detached', timeout: 20000 });
    const stillRinging = await seller.page.evaluate(async () => (await import('/js/ringer.js')).isRinging());
    if (stillRinging) fail('ringing should stop when no order is waiting');
    log('Seller accepted all orders with “Nhận tất cả”; panel closed and ringing stopped');

    await customer.page.goto(BASE + '/orders/' + orderCode);
    await interactive(customer.page);
    if (!/tìm tài xế|Tài xế/.test(await text(customer.page))) fail('customer should see driver search after acceptance');
    log('Customer tracking page: store accepted, searching for a driver');

    // ---------------------------------------------------------------- driver trips until our orders are delivered
    const pending = new Set([orderCode, orderCode2]);
    for (let trip = 1; pending.size > 0 && trip <= 8; trip++) {
        await driver.page.goto(BASE + '/driver');
        await interactive(driver.page);
        await driver.page.waitForSelector('.offer-card', { timeout: 60000 });
        const offerText = (await driver.page.locator('.offer-card').innerText()).replace(/\s+/g, ' ');
        const code = (offerText.match(/SKY\S+/) || [])[0];
        await Promise.all([driver.page.waitForURL(/\/driver\/deliveries\/\d+/, { timeout: 20000 }), driver.page.click('.offer-accept')]);
        await interactive(driver.page);
        log(`Trip ${trip}: driver accepted ringing offer ${code}`);
        const press = async (label) => {
            await driver.page.click(`button:has-text("${label}")`);
            await driver.page.waitForTimeout(1500);
        };
        await press('Tôi đã đến quán');
        await press('Đã lấy món');
        await press('Tôi đã đến nơi giao');
        await driver.page.click('button:has-text("Đã giao & thu")');
        await driver.page.click('.dialog button:has-text("Đã giao và thu tiền")');
        await driver.page.waitForTimeout(2000);
        log(`Trip ${trip}: arrived store → picked up → arrived → delivered & collected COD`);
        pending.delete(code);
    }
    if (pending.size > 0) fail('orders not delivered: ' + [...pending].join(', '));

    await customer.page.goto(BASE + '/orders/' + orderCode);
    await interactive(customer.page);
    if (!/Đã giao/.test(await text(customer.page))) fail('customer should see “Đã giao”');
    log(`Customer sees ${orderCode} delivered`);

    await driver.page.goto(BASE + '/driver/earnings');
    await interactive(driver.page);
    log('Driver earnings: ' + (await text(driver.page)).match(/(Tiền mặt|COD)[^.]{0,80}/)?.[0]);

    // ---------------------------------------------------------------- admin settles driver COD
    await login(admin.page, '/admin/login', 'admin@sky17.local');
    await admin.page.goto(BASE + '/admin/finance?tab=drivers');
    await interactive(admin.page);
    const row = admin.page.locator('tr', { hasText: 'Minh Trí' }).first();
    if (await row.count() === 0) fail('driver Minh Trí should owe COD');
    await row.locator('button:has-text("Lập phiếu")').click();
    await admin.page.click('.dialog button:has-text("Lập phiếu")');
    await admin.page.waitForTimeout(1500);
    await admin.page.locator('tr', { hasText: 'Minh Trí' }).first().locator('button:has-text("Xác nhận")').click();
    await admin.page.click('.dialog button:has-text("Xác nhận")');
    await admin.page.waitForTimeout(1500);
    if (!/Đã xác nhận|Hoàn tất|Confirmed/i.test(await text(admin.page))) fail('settlement should be confirmed');
    log('Admin created and confirmed the COD settlement for the driver');

    if (errors.length) fail('browser/server errors:\n  ' + errors.join('\n  '));
    console.log(`\n✅ FULL FLOW PASSED (${step} checks)`);
    await browser.close();
})().catch(async e => {
    console.error('\n❌ FAILED at step ' + (step + 1) + ': ' + e.message);
    if (errors.length) console.error(errors.join('\n'));
    process.exit(1);
});
