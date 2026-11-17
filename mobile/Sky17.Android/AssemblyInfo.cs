using Android.App;

// App-level label/icon (Settings → Apps, permission dialogs); the launcher entry comes from MainActivity.
[assembly: Application(
    Label = Sky17.Mobile.AppConfig.Title,
    Icon = "@mipmap/ic_launcher_" + Sky17.Mobile.AppConfig.Flavor,
    RoundIcon = "@mipmap/ic_launcher_" + Sky17.Mobile.AppConfig.Flavor,
    Theme = "@style/Sky17Theme")]
