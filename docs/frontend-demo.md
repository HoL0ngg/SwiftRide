# Giao diện SwiftRide — phase mock API

Hai giao diện rider và driver nằm trong một ứng dụng tại `src/WebApp`, dùng Vite, React, TypeScript và Tailwind CSS. Phong cách Swift Emerald dùng nền sáng, xanh ngọc và font Be Vietnam Pro được phục vụ từ package cục bộ, hỗ trợ tiếng Việt.

## Chạy thử

Cần Node.js 22.14 trở lên và npm. Từ thư mục gốc:

```powershell
cd src/WebApp
npm ci
npm run dev
```

Mở địa chỉ Vite in ra. Mặc định là:

- Hành khách: `http://127.0.0.1:5173/?role=rider`
- Tài xế: `http://127.0.0.1:5173/?role=driver`

Mở cả hai tab trong cùng trình duyệt và cùng origin, kể cả hostname/port. Không trộn `localhost` với `127.0.0.1`: localStorage tách theo origin. Cũng có thể đổi vai trò bằng thanh chọn trên giao diện.

## Kịch bản demo

1. Tài xế bật trạng thái trực tuyến (mặc định đã bật).
2. Hành khách chọn điểm đón và điểm đến khác nhau trong sáu địa điểm TP.HCM, chọn ví hoặc thẻ demo, nhấn **Xem giá chuyến đi**.
3. Xem báo giá, nhấn **Xác nhận giá & đặt xe**. Giá giữ nguyên từ đây.
4. Trong tab tài xế, nhấn **Nhận chuyến** → **Bắt đầu đến đón** → **Đã đón khách** → **Đã trả khách**.
5. Chuyến ở **Đang xử lý thanh toán**. Nhấn **Mô phỏng thanh toán thành công** ở một trong hai giao diện để chuyển sang **Đã thanh toán**.
6. Mở **Lịch sử chuyến đi** để xem kết quả. Hành khách có thể nhấn **Đặt một chuyến mới** mà vẫn giữ lịch sử.

Hủy chỉ được phép trước khi tài xế nhận chuyến. Từ chối đến ba lần tiếp tục mô phỏng ghép lại; lần thứ tư chuyển chuyến sang không thành công. Chỉ có một tài xế demo, không mô phỏng nhiều danh tính tài xế khi ghép lại. Khi tài xế ngoại tuyến, tạo báo giá sẽ báo chưa có tài xế.

## Phạm vi dữ liệu và bản đồ

- `src/demo.ts` là lớp API giả lập có độ trễ, kiểm tra chuyển trạng thái và lưu dữ liệu bằng localStorage key `swiftride-demo-v1`. Các tab cập nhật qua sự kiện `storage`. Dữ liệu còn sau khi tải lại trang; có thể xóa site data của origin để bắt đầu lại hoàn toàn.
- Giá, khoảng cách, thời gian, tài xế, phương thức thanh toán đều là dữ liệu mô phỏng. Khoảng cách ước lượng từ tọa độ; giá không phải báo giá backend. Không có giao dịch, ledger, JWT hoặc thông tin thẻ thật.
- Bản đồ tương tác dùng Leaflet/React Leaflet, tải tile thật từ `https://tile.openstreetmap.org/{z}/{x}/{y}.png`, giữ ghi công OpenStreetMap và cơ chế cache thông thường của trình duyệt. Không tải trước bản đồ hay cung cấp bản đồ ngoại tuyến. Tham khảo [chính sách tile OpenStreetMap](https://operations.osmfoundation.org/policies/tiles/).
- Chỉ đánh dấu điểm đón và điểm đến; chưa tìm địa chỉ trực tuyến, gọi Nominatim, tính tuyến đường theo đường phố, theo dõi GPS hoặc có vị trí tài xế trực tiếp. Cần mạng để tải bản đồ; tile bị treo được dừng sau 15 giây, UI báo lỗi và có nút **Thử lại**, không tự retry vô hạn.
- localStorage phù hợp cho bản demo cùng trình duyệt; không có đồng bộ nhiều thiết bị, phân quyền thật hay bảo đảm giao dịch giữa các tab thao tác đồng thời. Khi nối backend, thay lớp API giả lập bằng lời gọi có timeout qua YARP Gateway; giá, phân quyền, idempotency và trạng thái chuẩn do backend xác minh. Không gọi trực tiếp các business service.

## Kiểm tra

Chạy trong `src/WebApp`:

```powershell
npm run typecheck
npm test
npm run build
```

Vitest kiểm tra báo giá, chuyển trạng thái, giới hạn từ chối, thanh toán lặp, lịch sử, dữ liệu lưu lỗi và phục hồi trạng thái sau khi tải lại. Test render giao diện xác nhận các nút theo trạng thái và cách hiển thị chờ thanh toán; test bản đồ kiểm tra timeout và dọn timer. Các kiểm tra này không thay thế kiểm thử tương tác trên trình duyệt, thiết bị thật hay backend.
