using Android.Content;

namespace Sky17.Mobile;

/// <summary>Server address: baked in at build time (Sky17BaseUrl) and changeable inside the app.</summary>
internal static class Settings
{
    private const string Prefs = "sky17";
    private const string KeyBaseUrl = "base_url";

    private static ISharedPreferences Store => Android.App.Application.Context.GetSharedPreferences(Prefs, FileCreationMode.Private)!;

    /// <summary>Saved address, else the build default; null when neither is a valid URL.</summary>
    public static string? BaseUrl
    {
        get
        {
            var saved = Store.GetString(KeyBaseUrl, null);
            if (IsValidBaseUrl(saved)) return saved!.TrimEnd('/');
            return IsValidBaseUrl(AppConfig.DefaultBaseUrl) ? AppConfig.DefaultBaseUrl.TrimEnd('/') : null;
        }
        set
        {
            var edit = Store.Edit()!;
            if (value is null) edit.Remove(KeyBaseUrl); else edit.PutString(KeyBaseUrl, value.TrimEnd('/'));
            edit.Apply();
        }
    }

    public static string StartUrl => (BaseUrl ?? "") + AppConfig.StartPath;

    public static bool IsValidBaseUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) && !string.IsNullOrEmpty(u.Host);

    /// <summary>Same scheme-less host and port as the configured server.</summary>
    public static bool IsSameServer(string url)
    {
        if (BaseUrl is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || !Uri.TryCreate(BaseUrl, UriKind.Absolute, out var b)) return false;
        return string.Equals(u.Host, b.Host, StringComparison.OrdinalIgnoreCase) && u.Port == b.Port;
    }
}

/// <summary>Shown when the server cannot be reached.</summary>
internal static class Offline
{
    public static string Html(string server, string? detail) => $$"""
        <!doctype html><html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <style>
          body{margin:0;font-family:sans-serif;background:#f4f7fb;color:#0f172a;display:flex;min-height:100vh;align-items:center;justify-content:center}
          .card{background:#fff;border-radius:18px;padding:28px 22px;margin:16px;max-width:380px;text-align:center;box-shadow:0 10px 30px #1e355f14}
          .icon{font-size:52px} h1{font-size:20px;margin:10px 0} p{color:#64748b;line-height:1.6;font-size:14px;word-break:break-word}
          button{display:block;width:100%;border:0;border-radius:12px;padding:14px;font-size:15px;font-weight:700;margin-top:10px}
          .primary{background:{{AppConfig.BrandColor}};color:#fff} .ghost{background:#eef2f8;color:#334155}
        </style></head><body><div class="card">
          <div class="icon">📡</div><h1>Không kết nối được máy chủ</h1>
          <p>Kiểm tra kết nối mạng rồi thử lại.<br><small>{{Escape(server)}}{{(string.IsNullOrEmpty(detail) ? "" : " · " + Escape(detail))}}</small></p>
          <button class="primary" onclick="Sky17Native.retry()">Thử lại</button>
          <button class="ghost" onclick="Sky17Native.openSettings()">Đổi địa chỉ máy chủ</button>
        </div></body></html>
        """;

    private static string Escape(string s) => System.Net.WebUtility.HtmlEncode(s);
}
