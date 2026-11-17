// Shared ringing alert (Web Audio "ding-dong" loop + vibration + system notification).
// Used by the driver offer popup and the seller incoming-orders panel.
let audioCtx = null;
let ringTimer = null;
let notification = null;

/** Inside the Sky 17 Android app: window.Sky17Native rings with the phone ringtone, vibrates and shows a
 *  heads-up / lock-screen notification, which keeps working while the app is in the background. */
const native = () => (typeof window !== 'undefined' && window.Sky17Native) || null;
export function isNativeApp() { return !!native(); }

/** Browsers allow sound only after a user gesture: unlock on the first click/tap anywhere. */
if (typeof document !== 'undefined') {
    const auto = () => { unlock(); };
    document.addEventListener('pointerdown', auto, { once: true, capture: true });
    document.addEventListener('keydown', auto, { once: true, capture: true });
}

/** Must be called from a user gesture so the browser allows sound later. Also asks for notification permission. */
export async function unlock() {
    try {
        audioCtx = audioCtx || new (window.AudioContext || window.webkitAudioContext)();
        if (audioCtx.state === 'suspended') await audioCtx.resume();
    } catch { }
    try {
        if ('Notification' in window && Notification.permission === 'default') await Notification.requestPermission();
    } catch { }
    return isUnlocked();
}

/** True when sound can play right now. */
export function isUnlocked() {
    if (native()) return true; // the app plays the ringtone itself, no browser autoplay restriction
    return !!audioCtx && audioCtx.state === 'running';
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

/** Start ringing until stopRing() is called. A new call restarts the ring and re-shows the notification. */
export function ring(title, body, tag) {
    stopRing();
    if (native()) {
        try { native().ring(title || '', body || ''); ringTimer = 'native'; return; } catch { /* fall back to Web Audio */ }
    }
    try { if (audioCtx?.state === 'suspended') audioCtx.resume(); } catch { }
    beep();
    ringTimer = setInterval(() => {
        beep();
        try { navigator.vibrate?.([300, 150, 300]); } catch { }
    }, 1200);
    try { navigator.vibrate?.([400, 200, 400, 200, 400]); } catch { }
    try {
        if ('Notification' in window && Notification.permission === 'granted') {
            notification = new Notification(title, { body, tag: tag || 'sky17-alert', renotify: true, requireInteraction: true });
            notification.onclick = () => { window.focus(); notification?.close(); };
        }
    } catch { }
}

export function isRinging() {
    return ringTimer !== null;
}

export function stopRing() {
    if (native()) { try { native().stopRing(); } catch { } }
    if (ringTimer && ringTimer !== 'native') clearInterval(ringTimer);
    ringTimer = null;
    try { navigator.vibrate?.(0); } catch { }
    try { notification?.close(); } catch { }
    notification = null;
}
