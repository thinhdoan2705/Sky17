using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using AppNotification = Android.App.Notification;

namespace Sky17.Mobile;

/// <summary>Notification channels and builders.</summary>
internal static class Notifications
{
    public const string ChannelIncoming = "sky17_incoming";   // ringing: new trip / new order
    public const string ChannelInfo = "sky17_info";           // ordinary updates
    public const string ChannelService = "sky17_service";     // "đang trực tuyến" (foreground service)
    public const string ExtraUrl = "sky17_url";
    public const int IncomingId = 1701;
    public const int ServiceId = 1702;
    private static int infoId = 2000;

    public static void EnsureChannels(Context ctx)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var nm = (NotificationManager)ctx.GetSystemService(Context.NotificationService)!;
        // The ringtone and vibration are played by NativeRinger (looping), so this channel is silent itself.
        var incoming = new NotificationChannel(ChannelIncoming, "Đơn / chuyến mới", NotificationImportance.High)
        {
            Description = "Chuông báo khi có chuyến mới (tài xế) hoặc đơn mới (cửa hàng)",
            LockscreenVisibility = NotificationVisibility.Public,
        };
        incoming.SetSound(null, null);
        incoming.EnableVibration(false);
        nm.CreateNotificationChannel(incoming);
        nm.CreateNotificationChannel(new NotificationChannel(ChannelInfo, "Cập nhật đơn hàng", NotificationImportance.Default));
        nm.CreateNotificationChannel(new NotificationChannel(ChannelService, "Trạng thái kết nối", NotificationImportance.Low)
        {
            Description = "Giữ ứng dụng kết nối để nhận chuyến / đơn ngay lập tức",
        });
    }

    public static PendingIntent OpenApp(Context ctx, string? url, int requestCode)
    {
        var intent = new Intent(ctx, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop | ActivityFlags.NewTask);
        if (url is not null) intent.PutExtra(ExtraUrl, url);
        return PendingIntent.GetActivity(ctx, requestCode, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    public static AppNotification BuildIncoming(Context ctx, string title, string body, string? url)
    {
        var open = OpenApp(ctx, url, IncomingId);
        return new AppNotification.Builder(ctx, ChannelIncoming)
            .SetSmallIcon(Resource.Drawable.ic_stat_sky17)!
            .SetContentTitle(title)!
            .SetContentText(body)!
            .SetStyle(new AppNotification.BigTextStyle().BigText(body))!
            .SetCategory(AppNotification.CategoryCall)!
            .SetVisibility(NotificationVisibility.Public)!
            .SetOngoing(true)!
            .SetAutoCancel(true)!
            .SetContentIntent(open)!
            // Lock screen: open the app straight away (like an incoming call); otherwise a heads-up banner.
            .SetFullScreenIntent(open, true)!
            .Build()!;
    }

    public static void ShowInfo(Context ctx, string title, string body, string? url)
    {
        var n = new AppNotification.Builder(ctx, ChannelInfo)
            .SetSmallIcon(Resource.Drawable.ic_stat_sky17)!
            .SetContentTitle(title)!
            .SetContentText(body)!
            .SetAutoCancel(true)!
            .SetContentIntent(OpenApp(ctx, url, ++infoId))!
            .Build()!;
        Manager(ctx).Notify(infoId, n);
    }

    public static NotificationManager Manager(Context ctx) => (NotificationManager)ctx.GetSystemService(Context.NotificationService)!;
}

/// <summary>Loops the phone ringtone + vibration and shows the incoming notification until stopped.</summary>
internal static class NativeRinger
{
    private static readonly object Gate = new();
    private static Ringtone? tone;
    private static Timer? replay;

    public static bool IsRinging { get; private set; }

    public static void Start(Context ctx, string title, string body, string? url)
    {
        lock (Gate)
        {
            StopCore(ctx, cancelNotification: false);
            IsRinging = true;
            try
            {
                var uri = RingtoneManager.GetDefaultUri(RingtoneType.Ringtone) ?? RingtoneManager.GetDefaultUri(RingtoneType.Notification);
                tone = uri is null ? null : RingtoneManager.GetRingtone(ctx, uri);
                if (tone is not null)
                {
                    tone.AudioAttributes = new AudioAttributes.Builder()
                        .SetUsage(AudioUsageKind.NotificationRingtone)!
                        .SetContentType(AudioContentType.Sonification)!
                        .Build();
                    if (OperatingSystem.IsAndroidVersionAtLeast(28)) tone.Looping = true;
                    tone.Play();
                    if (!OperatingSystem.IsAndroidVersionAtLeast(28))
                        replay = new Timer(_ => { lock (Gate) { if (IsRinging && tone is { IsPlaying: false }) tone.Play(); } }, null, 1500, 1500);
                }
            }
            catch (Exception) { /* no ringtone available: vibration + notification still work */ }

            try
            {
                Vibrator(ctx)?.Vibrate(VibrationEffect.CreateWaveform([0, 700, 500, 700, 1200], 0));
            }
            catch (Exception) { }

            try { Notifications.Manager(ctx).Notify(Notifications.IncomingId, Notifications.BuildIncoming(ctx, title, body, url)); }
            catch (Exception) { /* notifications disabled by the user */ }
        }
    }

    public static void Stop(Context ctx)
    {
        lock (Gate) StopCore(ctx, cancelNotification: true);
    }

    private static void StopCore(Context ctx, bool cancelNotification)
    {
        IsRinging = false;
        replay?.Dispose();
        replay = null;
        try { tone?.Stop(); } catch (Exception) { }
        tone = null;
        try { Vibrator(ctx)?.Cancel(); } catch (Exception) { }
        if (cancelNotification)
        {
            try { Notifications.Manager(ctx).Cancel(Notifications.IncomingId); } catch (Exception) { }
        }
    }

    private static Vibrator? Vibrator(Context ctx)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            return (ctx.GetSystemService(Context.VibratorManagerService) as VibratorManager)?.DefaultVibrator;
#pragma warning disable CA1422
        return ctx.GetSystemService(Context.VibratorService) as Vibrator;
#pragma warning restore CA1422
    }
}

/// <summary>
/// Foreground service for the driver and store apps: keeps the process (and the WebView's realtime
/// connection) alive in the background so a new trip/order rings immediately.
/// </summary>
[Android.Runtime.Register("sky17.app.KeepAliveService")]
internal sealed class KeepAliveService : Service
{
    private PowerManager.WakeLock? wakeLock;

    public static void Start(Context ctx)
    {
        var intent = new Intent(ctx, typeof(KeepAliveService));
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) ctx.StartForegroundService(intent); else ctx.StartService(intent);
        }
        catch (Exception) { /* background start restrictions: the app still works while open */ }
    }

    public static void Stop(Context ctx) => ctx.StopService(new Intent(ctx, typeof(KeepAliveService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        Notifications.EnsureChannels(this);
        var text = AppConfig.Flavor == "driver" ? "Đang trực tuyến · sẵn sàng nhận chuyến" : "Đang mở cửa · sẵn sàng nhận đơn";
        var n = new Notification.Builder(this, Notifications.ChannelService)
            .SetSmallIcon(Resource.Drawable.ic_stat_sky17)!
            .SetContentTitle(AppConfig.Title)!
            .SetContentText(text)!
            .SetOngoing(true)!
            .SetContentIntent(Notifications.OpenApp(this, null, Notifications.ServiceId))!
            .Build()!;
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(Notifications.ServiceId, n, Android.Content.PM.ForegroundService.TypeSpecialUse);
        else
            StartForeground(Notifications.ServiceId, n);

        if (wakeLock is null)
        {
            var pm = (PowerManager)GetSystemService(PowerService)!;
            wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "sky17:realtime");
            wakeLock?.SetReferenceCounted(false);
            wakeLock?.Acquire();
        }
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        try { if (wakeLock?.IsHeld == true) wakeLock.Release(); } catch (Exception) { }
        wakeLock = null;
        base.OnDestroy();
    }
}
