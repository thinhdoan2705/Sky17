using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using AUri = Android.Net.Uri;

namespace Sky17.Mobile;

/// <summary>
/// Full-screen WebView on the hosted Sky 17 site. Navigation stays on the configured server;
/// phone calls, maps and other sites open in their own apps.
/// </summary>
[Activity(
    Name = "sky17.app.MainActivity",
    Label = AppConfig.Title,
    Icon = "@mipmap/ic_launcher_" + AppConfig.Flavor,
    RoundIcon = "@mipmap/ic_launcher_" + AppConfig.Flavor,
    Theme = "@style/Sky17Theme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.Portrait,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    private const int FileChooserRequest = 41;
    private const int LocationRequest = 42;
    private const int StartupPermissionsRequest = 43;

    internal static MainActivity? Current { get; private set; }

    private WebView web = null!;
    private FrameLayout root = null!;
    private IValueCallback? fileCallback;
    private (string Origin, GeolocationPermissions.ICallback Callback)? pendingGeo;
    private bool showingOffline;

    /// <summary>Driver and store apps must keep the realtime connection alive in the background.</summary>
    internal static readonly bool IsAlwaysOn = !string.Equals(AppConfig.Flavor, "customer", StringComparison.Ordinal);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;
        Notifications.EnsureChannels(this);

        root = new FrameLayout(this);
        root.SetBackgroundColor(Color.ParseColor(AppConfig.BrandColor));
        web = new WebView(this);
        root.AddView(web, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        SetContentView(root);
        ApplyEdgeToEdgeInsets();

        ConfigureWebView();
        if (IsAlwaysOn)
        {
            // Drivers/stores keep the screen on while the app is in front and stay connected in the background.
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            KeepAliveService.Start(this);
        }
        AskStartupPermissions();

        var target = Intent?.GetStringExtra(Notifications.ExtraUrl);
        if (Settings.BaseUrl is null) ShowServerDialog(firstRun: true);
        else web.LoadUrl(target ?? Settings.StartUrl);
    }

    private void ConfigureWebView()
    {
        WebView.SetWebContentsDebuggingEnabled(AppConfig.WebDebug);
        var s = web.Settings;
        s.JavaScriptEnabled = true;
        s.DomStorageEnabled = true;
        s.DatabaseEnabled = true;
        s.SetGeolocationEnabled(true);
        // The ringing alert uses Web Audio; allow it without a tap first.
        s.MediaPlaybackRequiresUserGesture = false;
        s.MixedContentMode = MixedContentHandling.CompatibilityMode;
        s.AllowFileAccess = false;
        s.AllowContentAccess = true;
        s.LoadWithOverviewMode = true;
        s.UseWideViewPort = true;
        s.SetSupportZoom(false);
        s.TextZoom = 100;
        // Lets the site know it runs inside the app (and which one).
        s.UserAgentString = $"{s.UserAgentString} Sky17App/{AppConfig.Flavor}/{AppConfig.Version}";

        var cookies = CookieManager.Instance!;
        cookies.SetAcceptCookie(true);
        cookies.SetAcceptThirdPartyCookies(web, false);

        web.AddJavascriptInterface(new NativeBridge(this), "Sky17Native");
        web.SetWebViewClient(new ShellClient(this));
        web.SetWebChromeClient(new ShellChromeClient(this));
        web.OverScrollMode = OverScrollMode.Never;
    }

    /// <summary>Android 15+ draws apps edge-to-edge: pad the content so the site is not under the status/navigation bars or keyboard.</summary>
    private void ApplyEdgeToEdgeInsets()
    {
        root.SetOnApplyWindowInsetsListener(new InsetsListener());
        // Android 14 and older still draw their own bars: colour them like the app (15+ shows the root background).
        if (!OperatingSystem.IsAndroidVersionAtLeast(35))
        {
#pragma warning disable CA1422
            Window?.SetStatusBarColor(Color.ParseColor(AppConfig.BrandColor));
            Window?.SetNavigationBarColor(Color.White);
#pragma warning restore CA1422
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            Window?.InsetsController?.SetSystemBarsAppearance(0, (int)WindowInsetsControllerAppearance.LightStatusBars);
    }

    private sealed class InsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.Ime());
                v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            }
            else
            {
#pragma warning disable CA1422 // pre-API-30 path
                v.SetPadding(insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop, insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
#pragma warning restore CA1422
            }
            return insets;
        }
    }

    private void AskStartupPermissions()
    {
        var wanted = new List<string>();
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            wanted.Add(Android.Manifest.Permission.PostNotifications);
        // The driver app needs GPS from the start (dispatch only offers trips to drivers with a fresh position).
        if (AppConfig.Flavor == "driver" && CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) != Permission.Granted)
        {
            wanted.Add(Android.Manifest.Permission.AccessFineLocation);
            wanted.Add(Android.Manifest.Permission.AccessCoarseLocation);
        }
        if (wanted.Count > 0) RequestPermissions(wanted.ToArray(), StartupPermissionsRequest);
    }

    // ------------------------------------------------------------------ navigation helpers

    internal void LoadStart()
    {
        showingOffline = false;
        web.LoadUrl(Settings.StartUrl);
    }

    internal void Retry()
    {
        RunOnUiThread(() =>
        {
            if (Settings.BaseUrl is null) { ShowServerDialog(firstRun: true); return; }
            LoadStart();
        });
    }

    internal void ShowOffline(string? detail)
    {
        if (showingOffline) return;
        showingOffline = true;
        var html = Offline.Html(Settings.BaseUrl ?? "(chưa cấu hình)", detail);
        web.LoadDataWithBaseURL("about:blank", html, "text/html", "utf-8", "about:blank");
    }

    internal void OpenExternal(AUri uri)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, uri);
            intent.AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            Toast.MakeText(this, "Không có ứng dụng để mở liên kết này.", ToastLength.Short)?.Show();
        }
    }

    internal void ShowServerDialog(bool firstRun = false)
    {
        RunOnUiThread(() =>
        {
            var input = new EditText(this)
            {
                Text = Settings.BaseUrl ?? (string.IsNullOrEmpty(AppConfig.DefaultBaseUrl) ? "https://" : AppConfig.DefaultBaseUrl),
                Hint = "https://sky17.ten-mien.vn",
                InputType = Android.Text.InputTypes.TextVariationUri | Android.Text.InputTypes.ClassText,
            };
            input.SetSingleLine(true);
            var pad = (int)(20 * Resources!.DisplayMetrics!.Density);
            var box = new FrameLayout(this);
            box.SetPadding(pad, pad / 2, pad, 0);
            box.AddView(input);

            var dialog = new AlertDialog.Builder(this)
                .SetTitle("Địa chỉ máy chủ Sky 17")!
                .SetMessage("Nhập địa chỉ trang web Sky 17 (ví dụ https://sky17.ten-mien.vn).")!
                .SetView(box)!
                .SetCancelable(!firstRun)!
                .SetPositiveButton("Lưu", (EventHandler<DialogClickEventArgs>)null!)!
                .SetNeutralButton("Mặc định", (_, _) => { Settings.BaseUrl = null; if (Settings.BaseUrl is null) ShowServerDialog(true); else LoadStart(); })!
                .Create()!;
            dialog.Show();
            // Validate before closing.
            dialog.GetButton((int)DialogButtonType.Positive)!.Click += (_, _) =>
            {
                var value = input.Text?.Trim().TrimEnd('/') ?? "";
                if (!Settings.IsValidBaseUrl(value))
                {
                    input.Error = "Địa chỉ phải bắt đầu bằng http:// hoặc https://";
                    return;
                }
                Settings.BaseUrl = value;
                dialog.Dismiss();
                LoadStart();
            };
        });
    }

    // ------------------------------------------------------------------ lifecycle

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        // Tapping the "new order / new trip" notification brings the app to the front on the right page.
        if (intent?.GetStringExtra(Notifications.ExtraUrl) is { } url && Settings.IsSameServer(url)) web.LoadUrl(url);
    }

    protected override void OnResume()
    {
        base.OnResume();
        web.OnResume();
        web.ResumeTimers();
        // Coming back after a long time in the background: reconnect Blazor if its circuit was lost.
        web.EvaluateJavascript(
            "(function(){var m=document.getElementById('components-reconnect-modal');" +
            "if(m && m.open && window.Blazor && Blazor.reconnect){Blazor.reconnect();}})()", null);
    }

    protected override void OnPause()
    {
        CookieManager.Instance?.Flush();
        // Customer app: pause to save battery. Driver/store apps keep running so alerts arrive immediately.
        if (!IsAlwaysOn)
        {
            web.OnPause();
        }
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        if (Current == this) Current = null;
        NativeRinger.Stop(this);
        if (IsAlwaysOn) KeepAliveService.Stop(this);
        web.Destroy();
        base.OnDestroy();
    }

#pragma warning disable CS0672, CA1422 // OnBackPressed is still delivered without predictive-back opt-in.
    public override void OnBackPressed()
    {
        if (web.CanGoBack() && !showingOffline) { web.GoBack(); return; }
        if (IsAlwaysOn) { MoveTaskToBack(true); return; } // keep driver/store connected instead of closing
        base.OnBackPressed();
    }
#pragma warning restore CS0672, CA1422

    // ------------------------------------------------------------------ permissions & file upload

    internal void RequestGeolocation(string origin, GeolocationPermissions.ICallback callback)
    {
        if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation) == Permission.Granted)
        {
            callback.Invoke(origin, true, false);
            return;
        }
        pendingGeo = (origin, callback);
        RequestPermissions([Android.Manifest.Permission.AccessFineLocation, Android.Manifest.Permission.AccessCoarseLocation], LocationRequest);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == LocationRequest && pendingGeo is { } g)
        {
            var granted = grantResults.Length > 0 && grantResults.Any(r => r == Permission.Granted);
            g.Callback.Invoke(g.Origin, granted, false);
            pendingGeo = null;
            if (!granted) Toast.MakeText(this, "Chưa cấp quyền vị trí. Bạn có thể bật lại trong Cài đặt.", ToastLength.Long)?.Show();
        }
    }

    internal bool ShowFileChooser(IValueCallback callback, WebChromeClient.FileChooserParams? options)
    {
        fileCallback?.OnReceiveValue(null);
        fileCallback = callback;
        try
        {
            var intent = options?.CreateIntent() ?? new Intent(Intent.ActionGetContent).AddCategory(Intent.CategoryOpenable).SetType("image/*");
            StartActivityForResult(Intent.CreateChooser(intent, "Chọn ảnh"), FileChooserRequest);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            fileCallback = null;
            return false;
        }
    }

    protected override void OnActivityResult(int requestCode, [GeneratedEnum] Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != FileChooserRequest || fileCallback is null) return;
        var uris = WebChromeClient.FileChooserParams.ParseResult((int)resultCode, data);
        fileCallback.OnReceiveValue(uris is null ? null : new Java.Lang.Object(JNIEnv.NewArray(uris), JniHandleOwnership.TransferLocalRef));
        fileCallback = null;
    }

    // ------------------------------------------------------------------ WebView clients

    private sealed class ShellClient(MainActivity activity) : WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var uri = request?.Url;
            if (uri is null) return false;
            var scheme = uri.Scheme?.ToLowerInvariant();
            if (scheme is "http" or "https" && Settings.IsSameServer(uri.ToString()!)) return false; // stay in the app
            if (scheme is "about" or "data" or "blob") return false;
            // tel:, mailto:, geo:, Google Maps, OpenStreetMap, other sites → their own apps.
            activity.OpenExternal(uri);
            return true;
        }

        public override void OnReceivedError(WebView? view, IWebResourceRequest? request, WebResourceError? error)
        {
            base.OnReceivedError(view, request, error);
            if (request?.IsForMainFrame == true) activity.ShowOffline(error?.Description);
        }

        public override void OnReceivedHttpError(WebView? view, IWebResourceRequest? request, WebResourceResponse? errorResponse)
        {
            base.OnReceivedHttpError(view, request, errorResponse);
            if (request?.IsForMainFrame == true && errorResponse?.StatusCode is 502 or 503 or 504)
                activity.ShowOffline($"Máy chủ đang bận (HTTP {errorResponse.StatusCode}).");
        }
    }

    private sealed class ShellChromeClient(MainActivity activity) : WebChromeClient
    {
        public override void OnGeolocationPermissionsShowPrompt(string? origin, GeolocationPermissions.ICallback? callback)
        {
            if (origin is null || callback is null) return;
            if (!Settings.IsSameServer(origin)) { callback.Invoke(origin, false, false); return; }
            activity.RequestGeolocation(origin, callback);
        }

        public override bool OnShowFileChooser(WebView? webView, IValueCallback? filePathCallback, FileChooserParams? fileChooserParams) =>
            filePathCallback is not null && activity.ShowFileChooser(filePathCallback, fileChooserParams);
    }
}
