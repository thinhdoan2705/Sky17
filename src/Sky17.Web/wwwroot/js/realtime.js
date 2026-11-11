// SignalR client for per-user notifications. The hub only ever sends to the signed-in user's own group.
let connection = null;
let dotnetRef = null;
let retryTimer = null;

async function connect() {
    if (!connection) return;
    try {
        await connection.start();
    } catch {
        clearTimeout(retryTimer);
        retryTimer = setTimeout(connect, 5000);
    }
}

export async function start(ref) {
    dotnetRef = ref;
    if (connection || !window.signalR) return;
    connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/notifications')
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .build();
    connection.on('notify', m => dotnetRef?.invokeMethodAsync('OnNotify', m));
    // After a reconnect, reload state from the database (messages may have been missed).
    connection.onreconnected(() => dotnetRef?.invokeMethodAsync('OnReconnected'));
    connection.onclose(() => { clearTimeout(retryTimer); retryTimer = setTimeout(async () => { await connect(); dotnetRef?.invokeMethodAsync('OnReconnected'); }, 5000); });
    await connect();
}

export function detach(ref) {
    if (dotnetRef === ref) dotnetRef = null;
}
