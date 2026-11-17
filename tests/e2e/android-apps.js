// Drives the real Android apps (emulator/phone) through their WebView with Playwright over CDP.
//
// Proves: the apps load the hosted site, login works, the driver app rings with the NATIVE ringtone +
// notification when a trip is offered — also while the app is in the BACKGROUND — the trip can be
// completed in the app, and a customer can order from the customer app.
//
// Requirements: emulator/phone connected (adb), test builds with -p:Sky17WebDebug=true and
// -p:Sky17BaseUrl=http://localhost:<port>, `adb reverse tcp:<port> tcp:<port>`, server with demo data.
// Usage: node tests/e2e/android-apps.js http://localhost:5299
const { execFileSync } = require('child_process');
const path = require('path');
const fs = require('fs');
const { chromium, _android } = require(process.env.PLAYWRIGHT_CORE || 'playwright-core');
let device; // Playwright Android device (adb)

const BASE = (process.argv[2] || 'http://localhost:5299').replace(/\/$/, '');
const PASSWORD = JSON.parse(fs.readFileSync(path.join(__dirname, '../../src/Sky17.Web/appsettings.Development.json'), 'utf8')).Seed.DemoPassword;
const ADB = process.env.ADB || path.join(process.env.LOCALAPPDATA || '', 'Android/Sdk/platform-tools/adb.exe');
const adb = (...args) => execFileSync(ADB, args, { encoding: 'utf8' }).trim();
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
let step = 0;
const log = (m) => console.log(`${String(++step).padStart(2, '0')}. ${m}`);
const fail = (m) => { throw new Error(m); };

/** Launch an app fresh and attach Playwright to its WebView. */
async function openApp(pkg) {
    adb('shell', 'am', 'force-stop', pkg);
    adb('shell', 'pm', 'clear', pkg); // fresh cookies (logged out)
    for (const p of ['android.permission.ACCESS_FINE_LOCATION', 'android.permission.ACCESS_COARSE_LOCATION', 'android.permission.POST_NOTIFICATIONS'])
        adb('shell', 'pm', 'grant', pkg, p);
    adb('shell', 'am', 'start', '-n', `${pkg}/sky17.app.MainActivity`);
    let pid = '';
    for (let i = 0; i < 30 && !pid; i++) { await sleep(500); try { pid = adb('shell', 'pidof', pkg); } catch { } }
    if (!pid) fail(`${pkg} did not start`);
    // Playwright attaches to the app WebView (needs -p:Sky17WebDebug=true).
    const webview = await device.webView({ pkg }, { timeout: 30000 });
    const page = await webview.page();
    const browser = { close: async () => { } };
    await page.waitForLoadState('domcontentloaded');
    return { browser, page, pid };
}

async function interactive(page) {
    await page.waitForFunction(() => !!window.Blazor, null, { timeout: 20000 });
    await page.waitForTimeout(1500);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    if (overflow > 1) fail(`horizontal overflow (${overflow}px) on ${page.url()}`);
}

async function login(page, user) {
    await interactive(page);
    await page.fill('#login', user);
    await page.fill('#password', PASSWORD);
    await Promise.all([page.waitForNavigation({ timeout: 20000 }), page.click('button[type=submit]')]);
    if (/\/login/.test(page.url())) fail(`login failed for ${user}`);
    await interactive(page);
}

const notificationShown = () => /sky17_incoming/.test(adb('shell', 'dumpsys', 'notification', '--noredact'));

/** Desktop seller (Playwright in Chrome) accepts every waiting order so the dispatcher offers trips. */
async function sellerAcceptAll(desktop) {
    const page = desktop.page;
    await page.goto(BASE + '/seller');
    await page.waitForFunction(() => !!window.Blazor);
    await page.waitForTimeout(2500);
    const n = await page.locator('.incoming-card').count();
    if (n === 0) return 0;
    if (n > 1) await page.click('button:has-text("Nhận tất cả")');
    else await page.click('.incoming-accept');
    await page.waitForSelector('.incoming-card', { state: 'detached', timeout: 20000 });
    return n;
}

async function completeTrip(page) {
    await Promise.all([page.waitForURL(/\/driver\/deliveries\/\d+/, { timeout: 20000 }), page.click('.offer-accept')]);
    await interactive(page);
    // Accepting stops the ringtone. Checked now: once the trip ends the driver is free again and the
    // next waiting order is offered (and rings) right away.
    if (await page.evaluate(() => window.Sky17Native.isRinging())) fail('ringing should stop after accepting');
    for (const label of ['Tôi đã đến quán', 'Đã lấy món', 'Tôi đã đến nơi giao']) {
        await page.click(`button:has-text("${label}")`);
        await page.waitForTimeout(1500);
    }
    await page.click('button:has-text("Đã giao & thu")');
    await page.click('.dialog button:has-text("Đã giao và thu tiền")');
    await page.waitForTimeout(2000);
}

(async () => {
    [device] = await _android.devices();
    if (!device) fail('no Android device/emulator (adb devices)');
    log(`Device: ${adb('shell', 'getprop', 'ro.product.model')} · Android ${adb('shell', 'getprop', 'ro.build.version.release')}`);
    const chrome = await chromium.launch({ channel: 'chrome' });
    const sellerCtx = await chrome.newContext({ viewport: { width: 1280, height: 900 } });
    const seller = { page: await sellerCtx.newPage() };
    await seller.page.goto(BASE + '/seller/login');
    await seller.page.fill('#login', 'owner.skycoffee@sky17.local');
    await seller.page.fill('#password', PASSWORD);
    await Promise.all([seller.page.waitForNavigation(), seller.page.click('button[type=submit]')]);

    // ---------------------------------------------------------------- driver app
    const driver = await openApp('vn.sky17.driver');
    const info = JSON.parse(await driver.page.evaluate(() => window.Sky17Native.info()));
    const ua = await driver.page.evaluate(() => navigator.userAgent);
    if (info.app !== 'driver' || !/Sky17App\/driver/.test(ua)) fail('driver app bridge/user agent missing');
    log(`Driver app opened ${driver.page.url()} (native bridge ${JSON.stringify(info)})`);
    if (!/KeepAliveService/.test(adb('shell', 'dumpsys', 'activity', 'services', 'vn.sky17.driver'))) fail('foreground service not running');
    log('Driver app runs its foreground service (stays connected in background)');

    await login(driver.page, 'driver.minhtri@sky17.local');
    log(`Driver logged in inside the app → ${new URL(driver.page.url()).pathname}`);

    const accepted = await sellerAcceptAll(seller);
    log(`Store (desktop) accepted ${accepted} waiting order(s) → dispatcher looks for the nearest driver`);

    await driver.page.waitForSelector('.offer-card', { timeout: 90000 });
    await sleep(1500);
    if (!await driver.page.evaluate(() => window.Sky17Native.isRinging())) fail('native ringer should ring for a new trip');
    if (!notificationShown()) fail('incoming notification should be shown');
    log('Offer popup in the app + NATIVE ringtone/vibration + heads-up notification');

    await completeTrip(driver.page);
    log('Accepting stopped the ringtone; trip completed in the app: đến quán → lấy món → đến nơi → giao & thu COD');

    // ---------------------------------------------------------------- background alert
    await driver.page.goto(BASE + '/driver');
    await interactive(driver.page);
    adb('shell', 'input', 'keyevent', 'KEYCODE_HOME');
    await sleep(2000);
    const focus = adb('shell', 'dumpsys', 'window', 'displays');
    if (/mCurrentFocus=.*vn\.sky17\.driver/.test(focus)) fail('driver app should be in the background');
    log('Driver app sent to the background (HOME)');

    // A new customer order → store accepts → the backgrounded driver must ring.
    const custCtx = await chrome.newContext({ viewport: { width: 390, height: 844 } });
    const cust = await custCtx.newPage();
    await cust.goto(BASE + '/login');
    await cust.fill('#login', '0904000002'); await cust.fill('#password', PASSWORD);
    await Promise.all([cust.waitForNavigation(), cust.click('button[type=submit]')]);
    await cust.goto(BASE + '/search?q=' + encodeURIComponent('cà phê'));
    await cust.waitForFunction(() => !!window.Blazor); await cust.waitForTimeout(1500);
    await cust.getByRole('button', { name: 'Thêm Cà phê sữa đá vào giỏ' }).first().click();
    await cust.waitForSelector('text=/Đã thêm/');
    await cust.goto(BASE + '/cart'); await cust.waitForTimeout(1500);
    await cust.click('text=Đặt đơn tại Sky Coffee');
    await cust.waitForURL(/\/checkout\/\d+/); await cust.waitForTimeout(1500);
    await Promise.all([cust.waitForURL(/\/orders\/SKY/, { timeout: 20000 }), cust.click('button:has-text("Đặt hàng ngay")')]);
    log(`Customer placed ${cust.url().match(/SKY[^?]+/)[0]} while the driver app is in the background`);
    await sellerAcceptAll(seller);

    let rang = false;
    for (let i = 0; i < 90 && !rang; i++) { await sleep(1000); rang = await driver.page.evaluate(() => window.Sky17Native.isRinging()).catch(() => false) && notificationShown(); }
    if (!rang) fail('backgrounded driver app did not ring');
    log('BACKGROUND: driver phone rings with native ringtone + notification while the app is not on screen');

    adb('shell', 'am', 'start', '-n', 'vn.sky17.driver/sky17.app.MainActivity');
    await sleep(2000);
    await driver.page.waitForSelector('.offer-card', { timeout: 30000 });
    await completeTrip(driver.page);
    log('Driver reopened the app from the notification and completed the trip');
    await driver.browser.close();

    // ---------------------------------------------------------------- customer app
    const customer = await openApp('vn.sky17.customer');
    if (!/Sky17App\/customer/.test(await customer.page.evaluate(() => navigator.userAgent))) fail('customer app user agent');
    log(`Customer app opened ${customer.page.url()}`);
    await customer.page.goto(BASE + '/login');
    await login(customer.page, '0904000001');
    await customer.page.goto(BASE + '/search');
    await interactive(customer.page);
    const kms = await customer.page.$$eval('.km-badge', els => els.map(e => e.textContent.trim()));
    if (kms.length < 2 || kms.some(k => k.startsWith('?'))) fail('customer app search should show distances: ' + kms);
    log(`Customer app search shows stores with distance: ${kms.join(', ')}`);
    await customer.page.getByRole('button', { name: 'Thêm Bánh mì đặc biệt vào giỏ' }).first().click();
    await customer.page.waitForSelector('text=/Đã thêm/');
    await customer.page.goto(BASE + '/cart'); await interactive(customer.page);
    await customer.page.click('text=Đặt đơn tại Sky Coffee');
    await customer.page.waitForURL(/\/checkout\/\d+/); await interactive(customer.page);
    await Promise.all([customer.page.waitForURL(/\/orders\/SKY/, { timeout: 20000 }), customer.page.click('button:has-text("Đặt hàng ngay")')]);
    log(`Customer app placed order ${customer.page.url().match(/SKY[^?]+/)[0]}`);
    const n = await sellerAcceptAll(seller);
    if (n < 1) fail('store should receive the order from the customer app');
    await customer.page.waitForTimeout(2500);
    log('Store received and accepted the order from the customer app; tracking page live in the app');
    await customer.browser.close();

    await chrome.close();
    await device.close();
    console.log(`\n✅ ANDROID APPS PASSED (${step} checks)`);
})().catch(e => { console.error(`\n❌ FAILED at step ${step + 1}: ${e.message}`); process.exit(1); });
