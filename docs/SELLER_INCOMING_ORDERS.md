# Seller Incoming Orders & Ringing Panel (GrabFood Merchant Style)

Tài liệu mô tả chi tiết cơ chế nhận đơn hàng thời gian thực cho cửa hàng (Merchant/Seller), popup thông báo, chuông reo và xử lý nhiều đơn hàng đồng thời.

---

## 1. Tổng quan yêu cầu & Kiến trúc

Khi khách hàng bấm đặt đơn, cửa hàng phải:
1. **Nghe chuông reo liên tục và thấy popup toàn màn hình ngay lập tức** (giống GrabFood Merchant / ShopeeFood quán).
2. **Xử lý được nhiều khách cùng đặt**: tất cả đơn hàng chờ (`Pending`) của mọi cửa hàng mà tài khoản sở hữu/quản lý được tập hợp trên 1 bảng điều khiển duy nhất.
3. **Thao tác nhanh**:
   - Nhận đơn riêng lẻ hoặc nút **"✓ Nhận tất cả"** khi có nhiều đơn cùng lúc.
   - Từ chối kèm lý do nhanh (Hết món, Quán quá tải, Sắp đóng cửa...) hoặc lý do tùy chỉnh.
4. **An toàn tương tranh (Concurrency-safe)**:
   - Nhiều nhân viên cùng mở màn hình: người này nhận thì màn hình người kia tự động biến mất đơn đó ngay lập tức (`order_handled`).
   - Khách hủy đơn trước khi quán nhận: hệ thống chặn và thông báo rõ ràng, không làm đơ ứng dụng.

---

## 2. Luồng hoạt động chi tiết

```
Khách đặt đơn (CustomerOrderService.PlaceOrderAsync)
       │
       ▼
Notifier.OrderStatusChangedAsync (Status = Pending)
       │  (SignalR: /hubs/notifications)
       ▼
Gửi RealtimeMessage("order", ...) tới tất cả thành viên quán (storeUsers)
       │
       ▼
RealtimeListener (Blazor) ──▶ RealtimeState.RaiseAsync
       │
       ▼
SellerIncomingOrders.razor (nằm tại SellerLayout)
       │
  ┌────┴──────────────────────────────────────┐
  ▼                                           ▼
Mở Popup Modal (open = true)         Kích hoạt chuông (ringer.js)
Hiển thị danh sách đơn Pending       Web Audio API ding-dong lặp lại
Highlight thẻ đơn mới (.is-new)      + Rung (mobile) + Desktop Notification
Đếm ngược tự hủy (AutoCancelAtUtc)
```

---

## 3. Các thành phần mã nguồn

### 3.1. Frontend UI & Layout
- **[`SellerIncomingOrders.razor`](../src/Sky17.Web/Components/Layout/SellerIncomingOrders.razor)**:
  - Nằm trong [`SellerLayout.razor`](../src/Sky17.Web/Components/Layout/SellerLayout.razor), luôn hoạt động bất kể chủ quán đang ở trang nào (`/seller`, `/seller/orders`, `/seller/products`, `/seller/reports`,...).
  - Quản lý danh sách `IncomingOrderDto` từ `ISellerService.GetIncomingOrdersAsync()`.
  - Hiển thị danh sách dạng lưới thẻ đơn hàng; mỗi thẻ có:
    - Mã đơn (`#Code`), tên khách, thời gian đặt, khoảng cách (nếu có).
    - Thẻ tên quán + emoji (khi tài khoản quản lý nhiều quán).
    - Đồng hồ đếm ngược thời gian tự hủy (`AutoCancelAtUtc`).
    - Danh sách món ăn kèm số lượng và ghi chú của khách.
    - Tổng tiền món.
    - Nút thao tác: **✓ Nhận đơn**, **✕ Từ chối**, **Chi tiết**.
  - Nếu có từ 2 đơn trở lên: hiển thị nút **✓ Nhận tất cả (@orders.Count)** trên thanh tiêu đề.
  - Cho phép thu nhỏ (minimize) xuống nút tròn nổi góc dưới màn hình (`incoming-pill`) có hiệu ứng nhấp nháy; chuông vẫn tiếp tục reo để nhắc nhở. Khi có đơn mới tới, popup tự động bung lại toàn màn hình.

### 3.2. Âm thanh chuông báo ([`ringer.js`](../src/Sky17.Web/wwwroot/js/ringer.js))
- Sử dụng **Web Audio API** tạo chuỗi âm điệu 2 tông (880Hz / 660Hz) mô phỏng chuông ứng dụng giao hàng chuyên nghiệp, không phụ thuộc file MP3 tĩnh và không bị lỗi tải mạng.
- Tự động lặp lại mỗi 1.2 giây cho đến khi quán nhận/từ chối hết đơn hoặc chủ động bấm nút "Tắt chuông".
- Hỗ trợ **Vibration API** cho thiết bị di động (mô thức rung dồn dập).
- Hỗ trợ **Web Notifications API** hiển thị popup cấp hệ điều hành khi chủ quán đang chuyển sang tab khác.
- Tự động mở khóa audio (`unlock`) khi người dùng tương tác bất kỳ lần đầu trên trang để vượt qua chính sách *Autoplay Policy* của trình duyệt.

### 3.3. Dịch vụ Backend & Xử lý tương tranh
- **DTOs ([`PortalDtos.cs`](../src/Sky17.Application/PortalDtos.cs))**:
  - `IncomingOrderDto`: Chứa đầy đủ dữ liệu món, tiền, thông tin quán, thời hạn xác nhận.
- **Interface ([`Services.cs`](../src/Sky17.Application/Services.cs))**:
  - `Task<IReadOnlyList<IncomingOrderDto>> GetIncomingOrdersAsync()`: Lấy tất cả đơn `Pending` của các quán thuộc sở hữu người dùng, xếp theo thứ tự cũ nhất lên trước.
  - `Task RespondToIncomingAsync(int storeId, int orderId, bool accept, string? reason)`: Xử lý nhận/từ chối với ràng buộc trạng thái bắt buộc phải là `Pending`.
- **Thực thi ([`SellerService.cs`](../src/Sky17.Infrastructure/Services/SellerService.cs))**:
  - `ChangeStatusCoreAsync`: Dùng Transaction & Optimistic Concurrency. Nếu đơn đã bị khách hủy hoặc nhân viên khác xử lý trước, tung ngoại lệ `DomainException` thân thiện.
  - Sau khi nhận đơn thành công:
    - Trạng thái chuyển sang `Confirmed`.
    - Gọi `dispatch.DispatchOrderAsync(orderId)` để ngay lập tức tìm kiếm tài xế gần quán nhất.
    - Phát thông điệp `order_handled` tới các thành viên khác trong quán để đóng đơn đó trên màn hình của họ.

---

## 4. Kiểm thử tự động ([`IncomingOrdersTests.cs`](../tests/Sky17.Tests/IncomingOrdersTests.cs))

Bao gồm 4 bài kiểm thử tích hợp chuyên sâu:
1. `Panel_lists_pending_orders_of_all_my_stores_oldest_first`: Kiểm tra danh sách hiển thị đúng tất cả đơn hàng `Pending` của các quán mà tài khoản tham gia (không hiện đơn của quán người khác), sắp xếp đơn cũ nhất lên đầu. Nhận một đơn sẽ xóa đơn đó và giữ lại các đơn còn lại.
2. `Many_simultaneous_orders_can_all_be_accepted_or_rejected`: Đặt 6 đơn hàng đồng thời từ nhiều khách khác nhau; từ chối 1 đơn (kèm lý do hoàn lại tồn kho) và nhận 5 đơn còn lại. Xác nhận tất cả chuyển trạng thái chính xác.
3. `Two_staff_accepting_the_same_order_only_one_wins`: Giả lập 2 nhân viên cùng bấm nhận/từ chối 1 đơn cùng 1 thời điểm; chỉ có duy nhất 1 thao tác thành công, thao tác thứ 2 bị chặn lại an toàn.
4. `Accepting_an_order_the_customer_already_cancelled_fails_cleanly`: Khách hàng hủy đơn trong lúc đang chờ quán nhận; khi quán bấm nhận sẽ nhận thông báo lỗi nhẹ nhàng, không gây crash ứng dụng.
