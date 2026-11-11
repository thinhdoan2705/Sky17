// Driver app helpers: GPS heartbeat, ringing offer alert (Web Audio + vibration + system notification).
let watchId = null;
let heartbeat = null;
let last = null;            // last known position {lat,lng,acc}
let ref = null;
let audioCtx = null;
let ringTimer = null;
let notification = null;
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

/** Must be called from a user gesture (e.g. the online switch) so the browser allows sound later. */
export async function unlock() {
    try {
        audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)();
        if (audioCtx.state === 'suspended') await audioCtx.resume();
    } catch { }
    try {
        if ('Notification' in window && Notification.permission === 'default') await Notification.requestPermission();
    } catch { }
}

function beep() {
    if (!audioCtx) return;
    const t = audioCtx.currentTime;
    // Two-tone "ding-dong" like a ride-hailing app.
    [[880, 0], [660, 0.22]].forEach(([freq, offset]) => {
        const o = audioCtx.createOscillator();
        const g = audioCtx.createGain();
        o.type = 'sine';
        o.frequency.value = freq;
        g.gain.setValueAtTime(0.0001, t + offset);
        g.gain.exponentialRampToValueAtTime(0.5, t + offset + 0.02);
        g.gain.exponentialRampToValueAtTime(0.0001, t + offset + 0.2);
        o.connect(g).connect(audioCtx.destination);
        o.start(t + offset);
        o.stop(t + offset + 0.22);
    });
}

/** Start ringing until stopRing() is called. */
export function ring(title, body) {
    stopRing();
    try { if (audioCtx?.state === 'suspended') audioCtx.resume(); } catch { }
    beep();
    ringTimer = setInterval(() => {
        beep();
        try { navigator.vibrate?.([300, 150, 300]); } catch { }
    }, 1200);
    try { navigator.vibrate?.([400, 200, 400, 200, 400]); } catch { }
    try {
        if ('Notification' in window && Notification.permission === 'granted') {
            notification = new Notification(title, { body, tag: 'sky17-offer', renotify: true, requireInteraction: true });
            notification.onclick = () => { window.focus(); notification?.close(); };
        }
    } catch { }
}

export function stopRing() {
    if (ringTimer) clearInterval(ringTimer);
    ringTimer = null;
    try { navigator.vibrate?.(0); } catch { }
    try { notification?.close(); } catch { }
    notification = null;
}

export function getMode() {
    try { return localStorage.getItem('sky17-driver-location-mode') || 'gps'; } catch { return 'gps'; }
}

export function setMode(mode) {
    try { localStorage.setItem('sky17-driver-location-mode', mode); } catch { }
}
