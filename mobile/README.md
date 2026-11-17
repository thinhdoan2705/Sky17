# Ứng dụng Android Sky 17 (Khách hàng · Tài xế · Cửa hàng)

Ba ứng dụng Android "vỏ" bọc trang web Sky 17 đã được host (Blazor Server) trong một WebView. Cả 3 dùng chung một mã nguồn C# (.NET 10 `net10.0-android`, thư mục `Sky17.Android/`). Ứng dụng nào được build do tham số `Sky17App` quyết định.

| App | `Sky17App` | Package | Mở trang | Màu |
|---|---|---|---|---|
| **Sky 17** (khách hàng) | `customer` | `vn.sky17.customer` | `/` | xanh dương |
| **Sky 17 Tài xế** | `driver` | `vn.sky17.driver` | `/driver` | xanh lá |
| **Sky 17 Cửa hàng** | `seller` | `vn.sky17.merchant` | `/seller` | cam |

Mọi tính năng nằm trên web, nên **cập nhật web là app có tính năng mới ngay, không cần cài lại**. Phần native chỉ làm những việc trang web không tự làm được:

- **Chuông báo gốc** (`window.Sky17Native.ring/stopRing`, gọi từ `wwwroot/js/ringer.js`): phát nhạc chuông của điện thoại lặp lại, rung, hiện thông báo dạng heads-up hoặc toàn màn hình trên màn hình khóa. Có tác dụng **cả khi app đang chạy nền**.
- **Luôn kết nối (Tài xế, Cửa hàng)**: một foreground service ("Đang trực tuyến · sẵn sàng nhận chuyến") cùng partial wake lock giữ kết nối realtime khi tắt màn hình hoặc chuyển app. WebView không bị tạm dừng. Nút Back ở trang gốc chỉ đưa app xuống nền chứ không thoát. Màn hình luôn sáng khi app đang mở.
- **Quyền**: vị trí (hỏi ngay khi mở app Tài xế; khách hàng được hỏi khi bấm "Vị trí hiện tại") và thông báo (Android 13+). Có chọn ảnh khi tải ảnh món, gọi điện qua `tel:`. Bản đồ và chỉ đường mở bằng app bản đồ.
- **Giữ trong app**: chỉ trang thuộc máy chủ đã cấu hình được mở trong app; các trang khác mở bằng trình duyệt hoặc app tương ứng.
- **Mất mạng**: hiện trang "Không kết nối được máy chủ" có nút *Thử lại* và *Đổi địa chỉ máy chủ*.
- Hiển thị tràn viền (Android 15+), màu thanh trạng thái theo màu app. User-Agent có thêm `Sky17App/<app>/<version>`.

## Build

Yêu cầu: .NET SDK 10, workload Android (`dotnet workload install android`, hoặc `maui-android`), Android SDK (API 36) và JDK 17 trở lên.

```powershell
# 3 APK, địa chỉ web thật của bạn (https khuyến nghị):
./mobile/build-android.ps1 -BaseUrl https://sky17.ten-mien.vn

# Kèm chép APK vào wwwroot/downloads để người dùng tải ở <web>/tai-app:
./mobile/build-android.ps1 -BaseUrl https://sky17.ten-mien.vn -CopyToWeb

# Ký bằng keystore phát hành (bắt buộc dùng CÙNG một keystore cho mọi bản cập nhật):
keytool -genkeypair -v -keystore sky17.keystore -alias sky17 -keyalg RSA -keysize 2048 -validity 10000
./mobile/build-android.ps1 -BaseUrl https://sky17.ten-mien.vn -KeyStore .\sky17.keystore -KeyAlias sky17 -KeyPassword ***
```

Kết quả nằm ở `artifacts/android/`: `sky17-customer.apk`, `sky17-driver.apk`, `sky17-merchant.apk`. Mỗi APK chứa sẵn bản cho arm, arm64 và x64.

Build lẻ một app: `dotnet publish mobile/Sky17.Android -c Release -f net10.0-android -p:Sky17App=driver -p:Sky17BaseUrl=https://...`

Lưu ý:
- **`https://` là bắt buộc trong thực tế**: điện thoại chỉ cho lấy GPS (vị trí tài xế, "vị trí hiện tại") trên trang https hoặc `localhost`. `http://` chỉ dùng để thử trong mạng nội bộ.
- Địa chỉ đã build sẵn chỉ là mặc định. Người dùng có thể đổi trong app (trang mất kết nối → *Đổi địa chỉ máy chủ*). Build với `Sky17BaseUrl` rỗng thì app hỏi địa chỉ ở lần mở đầu tiên.
- Không có `-KeyStore` thì APK được ký bằng khóa debug của máy build. Dùng để thử thì được, nhưng điện thoại chỉ cập nhật được app khi bản mới ký **cùng khóa** với bản cũ.

## Cài lên điện thoại

1. Mở `<web>/tai-app` trên điện thoại (hoặc gửi file APK) → *Tải APK*.
2. Mở file, cho phép *Cài ứng dụng không rõ nguồn gốc* cho trình duyệt hoặc trình quản lý file.
3. Mở app, cho phép **Thông báo** và **Vị trí**.
4. Tài xế và cửa hàng: vào *Cài đặt → Ứng dụng → Sky 17 Tài xế/Cửa hàng → Pin → Không hạn chế* để máy không tắt app khi tắt màn hình. Một số hãng (Xiaomi, Oppo, Vivo…) còn có mục *Tự khởi chạy* hoặc *Khóa app trong đa nhiệm*.

## Kiểm thử tự động

- `tests/e2e/full-flow.js`: toàn bộ quy trình trên trình duyệt (khách → cửa hàng nghe chuông → tài xế nghe chuông → giao, thu COD → admin đối soát) và kiểm tra không tràn ngang trên khổ điện thoại.
- `tests/e2e/android-apps.js`: điều khiển **chính các app trên máy ảo hoặc điện thoại** qua WebView (cần build với `-p:Sky17WebDebug=true` và `adb reverse tcp:<port> tcp:<port>`). Kiểm tra: app mở web, đăng nhập, chuông gốc khi có chuyến, **chuông khi app đang chạy nền**, hoàn thành chuyến trong app, khách đặt đơn bằng app.
- `tests/e2e/screenshots.js`: chụp các màn hình chính để duyệt giao diện.

Cả 3 script cần `playwright-core` (`npm i -g playwright-core`, hoặc biến `PLAYWRIGHT_CORE` trỏ tới gói) và Chrome đã cài.

## Giới hạn

- Khi app bị **đóng hẳn** (vuốt khỏi đa nhiệm và hệ điều hành dừng service), hoặc điện thoại tắt mạng, sẽ không có chuông. Muốn đánh thức app đã đóng hẳn thì cần Firebase Cloud Messaging (FCM). Phần này chưa làm, cần tài khoản Firebase.
- Chưa có bản iOS. Trên iPhone dùng trực tiếp trang web trong Safari; giao diện đã theo phong cách iOS. Bản iOS gốc cần máy Mac và tài khoản Apple Developer.
