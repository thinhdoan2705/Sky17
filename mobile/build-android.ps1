<#
.SYNOPSIS
  Builds the Sky 17 Android apps (customer, driver, store) as installable APKs.

.EXAMPLE
  ./mobile/build-android.ps1 -BaseUrl https://sky17.ten-mien.vn -CopyToWeb
  ./mobile/build-android.ps1 -BaseUrl https://sky17.ten-mien.vn -KeyStore C:\keys\sky17.keystore -KeyAlias sky17 -KeyPassword ***

.NOTES
  -BaseUrl     The address where the Sky 17 website is hosted (baked in as default; changeable inside the app).
               Leave empty to build apps that ask for the address on first launch.
  -Apps        Which apps to build: customer, driver, seller (default: all three).
  -CopyToWeb   Also copy the APKs to src/Sky17.Web/wwwroot/downloads so /tai-app offers them for download.
  -KeyStore    Release keystore. Without it the APKs are signed with this machine's debug key, which is fine
               for testing but phones can only update an app signed with the SAME key, so use one keystore forever.
#>
param(
    [string]$BaseUrl = '',
    [string[]]$Apps = @('customer', 'driver', 'seller'),
    [string]$Version = '1.0.0',
    [int]$VersionCode = 1,
    [switch]$CopyToWeb,
    [switch]$WebDebug,
    [string]$KeyStore,
    [string]$KeyAlias,
    [string]$KeyPassword
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'Sky17.Android/Sky17.Android.csproj'
$out = Join-Path $root 'artifacts/android'
$names = @{ customer = 'sky17-customer.apk'; driver = 'sky17-driver.apk'; seller = 'sky17-merchant.apk' }

if ($BaseUrl -and $BaseUrl -notmatch '^https?://') { throw "BaseUrl must start with http:// or https:// (got '$BaseUrl')" }
if (-not $BaseUrl) { Write-Warning 'No -BaseUrl: the apps will ask for the server address the first time they are opened.' }
if ($BaseUrl -like 'http://*') { Write-Warning 'http:// works for testing, but phones only allow GPS (driver location, current-location search) on https:// sites.' }
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($app in $Apps) {
    if (-not $names.ContainsKey($app)) { throw "Unknown app '$app' (customer | driver | seller)" }
    Write-Host "==> Building $app ($BaseUrl)" -ForegroundColor Cyan
    $publishDir = Join-Path $out "publish/$app"
    $dotnetArgs = @('publish', $project, '-c', 'Release', '-f', 'net10.0-android', '-o', $publishDir, '-nologo', '-v', 'q',
        "-p:Sky17App=$app", "-p:Sky17BaseUrl=$BaseUrl", "-p:ApplicationDisplayVersion=$Version", "-p:ApplicationVersion=$VersionCode",
        "-p:Sky17WebDebug=$($WebDebug.IsPresent.ToString().ToLower())")
    if ($KeyStore) {
        $dotnetArgs += @('-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$KeyStore", "-p:AndroidSigningKeyAlias=$KeyAlias",
            "-p:AndroidSigningKeyPass=$KeyPassword", "-p:AndroidSigningStorePass=$KeyPassword")
    }
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $app" }
    $apk = Get-ChildItem $publishDir -Filter '*-Signed.apk' | Select-Object -First 1
    if (-not $apk) { throw "No signed APK produced for $app" }
    $target = Join-Path $out $names[$app]
    Copy-Item $apk.FullName $target -Force
    Write-Host ("    {0} ({1:N1} MB)" -f $target, ((Get-Item $target).Length / 1MB)) -ForegroundColor Green
    if ($CopyToWeb) {
        $web = Join-Path $root 'src/Sky17.Web/wwwroot/downloads'
        New-Item -ItemType Directory -Force $web | Out-Null
        Copy-Item $target (Join-Path $web $names[$app]) -Force
    }
}
Write-Host "Done. APKs in $out" -ForegroundColor Cyan
if ($CopyToWeb) { Write-Host 'Also copied to wwwroot/downloads -> download page: <BaseUrl>/tai-app' -ForegroundColor Cyan }
