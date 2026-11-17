using Android.Webkit;
using Java.Interop;

namespace Sky17.Mobile;

/// <summary>
/// window.Sky17Native, used by wwwroot/js/ringer.js: inside the app the "new trip / new order" alert
/// rings with the phone's ringtone, vibrates and shows a heads-up / lock-screen notification,
/// which keeps working when the app is in the background (a web page alone cannot do that).
/// </summary>
internal sealed class NativeBridge(MainActivity activity) : Java.Lang.Object
{
    [JavascriptInterface, Export("info")]
    public string Info() =>
        $"{{\"app\":\"{AppConfig.Flavor}\",\"version\":\"{AppConfig.Version}\",\"alwaysOn\":{(MainActivity.IsAlwaysOn ? "true" : "false")}}}";

    [JavascriptInterface, Export("ring")]
    public void Ring(string? title, string? body) =>
        NativeRinger.Start(activity, title ?? AppConfig.Title, body ?? "", null);

    [JavascriptInterface, Export("stopRing")]
    public void StopRing() => NativeRinger.Stop(activity);

    [JavascriptInterface, Export("isRinging")]
    public bool IsRinging() => NativeRinger.IsRinging;

    [JavascriptInterface, Export("notify")]
    public void Notify(string? title, string? body) =>
        Notifications.ShowInfo(activity, title ?? AppConfig.Title, body ?? "", null);

    [JavascriptInterface, Export("retry")]
    public void Retry() => activity.Retry();

    [JavascriptInterface, Export("openSettings")]
    public void OpenSettings() => activity.ShowServerDialog();

}
