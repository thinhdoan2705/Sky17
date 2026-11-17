// Captures the main screens of every portal at phone size (and a few at desktop size) for design review.
// Usage: node tests/e2e/screenshots.js http://localhost:5299 <outDir>
const path = require('path');
const fs = require('fs');
const { chromium } = require(process.env.PLAYWRIGHT_CORE || 'playwright-core');

const BASE = (process.argv[2] || 'http://localhost:5271').replace(/\/$/, '');
const OUT = process.argv[3] || path.join(__dirname, 'screens');
const PASSWORD = JSON.parse(fs.readFileSync(path.join(__dirname, '../../src/Sky17.Web/appsettings.Development.json'), 'utf8')).Seed.DemoPassword;
fs.mkdirSync(OUT, { recursive: true });

const phone = { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, locale: 'vi-VN',
    geolocation: { latitude: 10.9831, longitude: 106.6531 }, permissions: ['geolocation'] };

async function shoot(page, name, url, { full = false, before } = {}) {
    if (url) await page.goto(BASE + url);
    await page.waitForFunction(() => !!window.Blazor, null, { timeout: 20000 }).catch(() => { });
    await page.waitForTimeout(1800);
    if (before) await before(page);
    await page.screenshot({ path: path.join(OUT, name + '.png'), fullPage: full });
    console.log('  ' + name);
}

async function login(page, url, user) {
    await page.goto(BASE + url);
    await page.fill('#login', user);
    await page.fill('#password', PASSWORD);
    await Promise.all([page.waitForNavigation(), page.click('button[type=submit]')]);
}

(async () => {
    const browser = await chromium.launch({ channel: 'chrome' });
    const only = (process.env.ONLY || 'customer,driver,seller,admin').split(',');

    if (only.includes('customer')) {
        const page = await (await browser.newContext(phone)).newPage();
        await shoot(page, 'c01-login', '/login');
        await login(page, '/login', '0904000001');
        await shoot(page, 'c02-home', '/', { full: true });
        await shoot(page, 'c03-search', '/search', { full: true });
        await shoot(page, 'c04-store', '/stores/1', { full: true });
        await shoot(page, 'c05-product', '/products/1');
        await page.goto(BASE + '/search');
        await page.waitForTimeout(2000);
        await page.getByRole('button', { name: /Thêm .* vào giỏ/ }).first().click().catch(() => { });
        await page.waitForTimeout(1000);
        await shoot(page, 'c06-cart', '/cart', { full: true });
        await shoot(page, 'c07-checkout', '/checkout', { full: true });
        await shoot(page, 'c08-orders', '/orders', { full: true });
        const first = await page.locator('a[href^="/orders/SKY"]').first().getAttribute('href').catch(() => null);
        if (first) await shoot(page, 'c09-tracking', first, { full: true });
        await shoot(page, 'c10-addresses', '/addresses', { full: true });
        await shoot(page, 'c11-profile', '/profile', { full: true });
    }
    if (only.includes('driver')) {
        const page = await (await browser.newContext(phone)).newPage();
        await shoot(page, 'd01-login', '/driver/login');
        await login(page, '/driver/login', 'driver.lehoang@sky17.local');
        await shoot(page, 'd02-dashboard', '/driver', { full: true });
        const trip = await page.locator('a[href^="/driver/deliveries/"]').first().getAttribute('href').catch(() => null);
        if (trip) await shoot(page, 'd03-trip', trip, { full: true });
        await shoot(page, 'd04-earnings', '/driver/earnings', { full: true });
        await shoot(page, 'd05-history', '/driver/history', { full: true });
        await shoot(page, 'd06-profile', '/driver/profile', { full: true });
        const p2 = await (await browser.newContext(phone)).newPage();
        await login(p2, '/driver/login', 'driver.minhtri@sky17.local');
        await shoot(p2, 'd07-offer', '/driver', { before: async p => { await p.waitForSelector('.offer-card', { timeout: 60000 }).catch(() => { }); } });
    }
    if (only.includes('seller')) {
        const page = await (await browser.newContext(phone)).newPage();
        await login(page, '/seller/login', 'owner.skycoffee@sky17.local');
        await shoot(page, 's01-incoming', '/seller');
        await page.click('.incoming-head .close').catch(() => { });
        await shoot(page, 's02-dashboard', null, { full: true });
        await shoot(page, 's03-orders', '/seller/orders', { full: true });
        await shoot(page, 's04-products', '/seller/products', { full: true });
        const desk = await (await browser.newContext({ viewport: { width: 1366, height: 860 }, locale: 'vi-VN' })).newPage();
        await login(desk, '/seller/login', 'owner.bepnhamay@sky17.local');
        await shoot(desk, 's05-dashboard-desktop', '/seller');
    }
    if (only.includes('admin')) {
        const desk = await (await browser.newContext({ viewport: { width: 1366, height: 860 }, locale: 'vi-VN' })).newPage();
        await login(desk, '/admin/login', 'admin@sky17.local');
        await shoot(desk, 'a01-dashboard', '/admin');
        await shoot(desk, 'a02-orders', '/admin/orders');
        const page = await (await browser.newContext(phone)).newPage();
        await login(page, '/admin/login', 'admin@sky17.local');
        await shoot(page, 'a03-dashboard-phone', '/admin', { full: true });
    }
    await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
