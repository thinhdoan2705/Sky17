// Driver app helpers: GPS heartbeat; ringing offer alert re-exported from ringer.js.
let watchId = null;
let heartbeat = null;
let last = null;            // last known position {lat,lng,acc}
let ref = null;
const HEARTBEAT_MS = 20000;

function send() {
    if (ref && last) ref.invokeMethodAsync('OnLocation', last.lat, last.lng, last.acc ?? 0).catch(() => { });
}

/**
 * mode: 'gps' (browser geolocation) or 'manual' (fixed point pinned by the driver).
 * Sends the position immediately and then every 20 s while the app is open (presence + location).
 */
export function startTracking(dotnetRef, mode, manualLat, manualLng) {
    stopTracking();
    ref = dotnetRef;
    if (mode === 'manual' && manualLat != null && manualLng != null) {
        last = { lat: manualLat, lng: manualLng, acc: 0 };
        send();
    } else if (navigator.geolocation) {
        watchId = navigator.geolocation.watchPosition(
            p => {
                const first = last === null;
                last = { lat: p.coords.latitude, lng: p.coords.longitude, acc: p.coords.accuracy };
                if (first) send();
            },
            err => ref?.invokeMethodAsync('OnLocationError', err.code === 1 ? 'denied' : 'unavailable').catch(() => { }),
            { enableHighAccuracy: true, maximumAge: 15000, timeout: 20000 });
    } else {
        ref?.invokeMethodAsync('OnLocationError', 'unsupported').catch(() => { });
    }
    heartbeat = setInterval(send, HEARTBEAT_MS);
}

export function stopTracking() {
    if (watchId !== null && navigator.geolocation) navigator.geolocation.clearWatch(watchId);
    watchId = null;
    if (heartbeat) clearInterval(heartbeat);
    heartbeat = null;
    last = null;
}

// Sound/vibration/notification alert lives in ringer.js (shared with the seller incoming-orders panel).
export { unlock, ring, stopRing } from "./ringer.js";

export function getMode() {
    try { return localStorage.getItem('sky17-driver-location-mode') || 'gps'; } catch { return 'gps'; }
}

export function setMode(mode) {
    try { localStorage.setItem('sky17-driver-location-mode', mode); } catch { }
}
