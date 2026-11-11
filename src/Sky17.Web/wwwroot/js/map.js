// Free maps: Leaflet + OpenStreetMap tiles, Nominatim geocoding, OSRM routing.
// Usage policies: OSM tiles & Nominatim are for light use (≤1 search/s, searches only on explicit user action).
const TILE_URL = 'https://tile.openstreetmap.org/{z}/{x}/{y}.png';
const ATTRIBUTION = '&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a>';
const OSRM_URL = 'https://router.project-osrm.org';
const DEFAULT_CENTER = [10.9804, 106.6519]; // Thủ Dầu Một, Bình Dương

const KINDS = {
    store: { emoji: '🏪', color: '#1769e0' },
    dropoff: { emoji: '🏠', color: '#f97316' },
    me: { emoji: '🛵', color: '#16a34a' },
    job: { emoji: '📦', color: '#1769e0' },
    pin: { emoji: '📍', color: '#dc2626' },
};

function icon(kind) {
    const k = KINDS[kind] || KINDS.pin;
    return L.divIcon({
        className: 'sky-pin',
        html: `<div class="sky-pin-inner" style="background:${k.color}"><span>${k.emoji}</span></div>`,
        iconSize: [36, 36], iconAnchor: [18, 36], popupAnchor: [0, -32],
    });
}

function escapeHtml(s) {
    return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function ensureMap(el, opts) {
    if (el._sky) return el._sky;
    const map = L.map(el, { zoomControl: true, attributionControl: true, scrollWheelZoom: opts?.scrollZoom ?? false })
        .setView(DEFAULT_CENTER, 13);
    L.tileLayer(TILE_URL, { maxZoom: 19, attribution: ATTRIBUTION }).addTo(map);
    const layer = L.layerGroup().addTo(map);
    el._sky = { map, layer, me: null };
    // Maps created inside dialogs/tabs need a size recalculation once visible.
    setTimeout(() => map.invalidateSize(), 250);
    return el._sky;
}

async function routeGeometry(a, b) {
    try {
        const url = `${OSRM_URL}/route/v1/driving/${a[1]},${a[0]};${b[1]},${b[0]}?overview=full&geometries=geojson`;
        const res = await fetch(url);
        if (!res.ok) return null;
        const data = await res.json();
        if (data.code !== 'Ok' || !data.routes?.length) return null;
        return data.routes[0].geometry.coordinates.map(c => [c[1], c[0]]);
    } catch { return null; }
}

/** Render markers (and optionally the road route between the first two) on a read-only map. */
export async function render(el, data) {
    if (!el || !window.L) return;
    const s = ensureMap(el, data);
    s.layer.clearLayers();
    const pts = [];
    for (const m of data.markers || []) {
        const ll = [m.lat, m.lng];
        pts.push(ll);
        const mk = L.marker(ll, { icon: icon(m.kind) }).addTo(s.layer);
        if (m.label) mk.bindPopup(escapeHtml(m.label));
    }
    if (s.me) pts.push(s.me.getLatLng());
    if (pts.length === 0) { s.map.setView(DEFAULT_CENTER, 12); return; }
    if (pts.length === 1) s.map.setView(pts[0], 15); else s.map.fitBounds(L.latLngBounds(pts), { padding: [36, 36] });
    if (data.route && data.markers?.length >= 2) {
        const a = [data.markers[0].lat, data.markers[0].lng], b = [data.markers[1].lat, data.markers[1].lng];
        const geo = await routeGeometry(a, b);
        if (geo) {
            L.polyline(geo, { color: '#1769e0', weight: 5, opacity: .8 }).addTo(s.layer);
        } else {
            // Routing unavailable: show a dashed straight line, clearly not a road route.
            L.polyline([a, b], { color: '#7c879d', weight: 3, dashArray: '6 8' }).addTo(s.layer);
        }
    }
}

/** Show the browser's current position as a 🛵 marker. Returns {lat,lng} or null. */
export async function showMe(el) {
    const pos = await locate();
    if (!pos || !el?._sky) return pos;
    const s = el._sky;
    if (s.me) s.me.setLatLng([pos.lat, pos.lng]);
    else s.me = L.marker([pos.lat, pos.lng], { icon: icon('me') }).bindPopup('Vị trí của bạn').addTo(s.map);
    s.map.setView([pos.lat, pos.lng], 15);
    return pos;
}

/** Interactive picker: click the map or drag the pin. Calls dotnet.OnPicked(lat, lng). */
export function picker(el, dotnet, lat, lng) {
    if (!el || !window.L) return;
    const s = ensureMap(el, { scrollZoom: true });
    s.dotnet = dotnet;
    s.map.on('click', e => place(el, e.latlng.lat, e.latlng.lng, true));
    if (lat != null && lng != null) place(el, lat, lng, false);
}

function place(el, lat, lng, notify) {
    const s = el._sky;
    if (!s) return;
    if (!s.pin) {
        s.pin = L.marker([lat, lng], { icon: icon('pin'), draggable: true }).addTo(s.map);
        s.pin.on('dragend', () => { const p = s.pin.getLatLng(); s.dotnet?.invokeMethodAsync('OnPicked', p.lat, p.lng); });
    } else {
        s.pin.setLatLng([lat, lng]);
    }
    s.map.setView([lat, lng], Math.max(s.map.getZoom(), 16));
    if (notify) s.dotnet?.invokeMethodAsync('OnPicked', lat, lng);
}

export function setPoint(el, lat, lng) {
    if (!el?._sky) return;
    if (lat == null || lng == null) {
        if (el._sky.pin) { el._sky.map.removeLayer(el._sky.pin); el._sky.pin = null; }
        return;
    }
    place(el, lat, lng, false);
}

/** Address search (Nominatim). Only called when the user presses "Tìm". */
export async function search(query) {
    if (!query || query.trim().length < 3) return [];
    const url = 'https://nominatim.openstreetmap.org/search?format=jsonv2&limit=5&countrycodes=vn&accept-language=vi&q=' + encodeURIComponent(query.trim());
    try {
        const res = await fetch(url, { headers: { 'Accept': 'application/json' } });
        if (!res.ok) return [];
        const data = await res.json();
        return data.map(d => ({ lat: parseFloat(d.lat), lng: parseFloat(d.lon), label: d.display_name }));
    } catch { return []; }
}

/** Browser geolocation (requires HTTPS or localhost). */
export function locate() {
    return new Promise(resolve => {
        if (!navigator.geolocation) return resolve(null);
        navigator.geolocation.getCurrentPosition(
            p => resolve({ lat: p.coords.latitude, lng: p.coords.longitude }),
            () => resolve(null),
            { enableHighAccuracy: true, timeout: 10000, maximumAge: 60000 });
    });
}

export function dispose(el) {
    if (el?._sky) { el._sky.map.remove(); el._sky = null; }
}
