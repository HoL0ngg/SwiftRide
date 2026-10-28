# Giao diện SwiftRide — tìm địa chỉ thật, chuyến đi demo

Hai giao diện rider và driver nằm trong một ứng dụng tại `src/WebApp`, dùng Vite, React, TypeScript và Tailwind CSS. Phong cách Swift Emerald dùng nền sáng, xanh ngọc và font Be Vietnam Pro được phục vụ từ package cục bộ, hỗ trợ tiếng Việt.

## Chạy thử

Cần Node.js 22.14 trở lên và npm. Từ thư mục gốc:

```powershell
cd src/WebApp
npm ci
npm run dev
```

Tìm địa chỉ cần Gateway và Matching đang chạy (xem cấu hình backend). Vite chuyển `/locations` và `/dev/token` tới Gateway `http://localhost:5080`; đổi đích bằng biến `GATEWAY_URL` hoặc file `.env.local` trong `src/WebApp`. Ví dụ PowerShell: `$env:GATEWAY_URL = 'http://localhost:5080'` trước `npm run dev`. Không cấu hình frontend gọi thẳng Matching.

Ở chế độ Vite Development, mỗi lượt tìm không có JWT nhập tay sẽ lấy token qua `POST /dev/token?userId=demo-rider&role=rider`; Gateway cũng phải chạy Development và có khóa JWT qua môi trường/user-secrets. Chỉ endpoint tìm địa chỉ dùng token này, không biến chuyến demo thành chuyến backend. Khi build production, nhập JWT hợp lệ trong mục **JWT để tìm địa chỉ**; token chỉ giữ trong bộ nhớ tab, không ghi localStorage hoặc bundle. Hạ tầng phục vụ bản build phải chuyển `/locations` cùng origin tới Gateway; Vite dev proxy không tồn tại trong bản build.

Mở địa chỉ Vite in ra. Mặc định là:

- Hành khách: `http://127.0.0.1:5173/?role=rider`
- Tài xế: `http://127.0.0.1:5173/?role=driver`

Mở cả hai tab trong cùng trình duyệt và cùng origin, kể cả hostname/port. Không trộn `localhost` với `127.0.0.1`: localStorage tách theo origin. Cũng có thể đổi vai trò bằng thanh chọn trên giao diện.

## Kịch bản demo

1. Tài xế bật trạng thái trực tuyến (mặc định đã bật).
2. Hành khách nhập địa chỉ điểm đón/điểm đến (3–200 ký tự), nhấn **Tìm** rồi chọn một kết quả. Không tự gọi khi gõ. Hai điểm ban đầu là địa điểm mẫu; có thể chủ động mở **Dùng địa điểm mẫu** để chọn một trong sáu địa điểm TP.HCM.
3. Kiểm tra hai điểm trên bản đồ trước báo giá: xanh là điểm đón, vàng là điểm đến. Nhấn **Chọn trên bản đồ** ở trường tương ứng rồi nhấn bản đồ để điều chỉnh tọa độ; điểm này hiển thị tọa độ, không tự tìm ngược địa chỉ. Có thể đổi chỗ hai điểm bằng **Đổi hai điểm**. Chọn ví/thẻ demo rồi nhấn **Xem giá chuyến đi**.
4. Xem báo giá demo, nhấn **Xác nhận giá & đặt xe**. Giá giữ nguyên từ đây.
5. Trong tab tài xế, nhấn **Nhận chuyến** → **Bắt đầu đến đón** → **Đã đón khách** → **Đã trả khách**.
6. Chuyến ở **Đang xử lý thanh toán**. Nhấn **Mô phỏng thanh toán thành công** ở một trong hai giao diện để chuyển sang **Đã thanh toán**.
7. Mở **Lịch sử chuyến đi** để xem kết quả. Hành khách có thể nhấn **Đặt một chuyến mới** mà vẫn giữ lịch sử.

Hủy chỉ được phép trước khi tài xế nhận chuyến. Từ chối đến ba lần tiếp tục mô phỏng ghép lại; lần thứ tư chuyển chuyến sang không thành công. Chỉ có một tài xế demo, không mô phỏng nhiều danh tính tài xế khi ghép lại. Khi tài xế ngoại tuyến, tạo báo giá sẽ báo chưa có tài xế.

## Phạm vi dữ liệu và bản đồ

- `src/demo.ts` là lớp API giả lập có độ trễ, kiểm tra chuyển trạng thái và lưu dữ liệu bằng localStorage key `swiftride-demo-v1`. Các tab cập nhật qua sự kiện `storage`. Dữ liệu còn sau khi tải lại trang; có thể xóa site data của origin để bắt đầu lại hoàn toàn.
- Giá, khoảng cách, thời gian, tài xế, phương thức thanh toán đều là dữ liệu mô phỏng. Khoảng cách Haversine (làm tròn 0,1 km, tối thiểu 0,1 km) chỉ là ước lượng, không phải tuyến đường; giá không phải báo giá backend. Chưa có giao dịch, ledger hoặc thông tin thẻ thật. `/quote` vẫn là route nội bộ, frontend không gọi trực tiếp.
- Bản đồ tương tác dùng Leaflet/React Leaflet, tải tile thật từ `https://tile.openstreetmap.org/{z}/{x}/{y}.png`, giữ ghi công OpenStreetMap và cơ chế cache thông thường của trình duyệt. Không tải trước bản đồ hay cung cấp bản đồ ngoại tuyến. Tham khảo [chính sách tile OpenStreetMap](https://operations.osmfoundation.org/policies/tiles/).
- Tìm địa chỉ qua `GET /locations/search?query=...` trên Gateway, Matching gọi Nominatim. Không autocomplete, không gửi yêu cầu Nominatim trực tiếp từ trình duyệt, không cần API key trả phí. Dịch vụ công cộng có giới hạn sử dụng và không bảo đảm luôn sẵn sàng; ghi công OpenStreetMap được hiển thị cạnh ô tìm kiếm. Lỗi mạng, JWT, HTTP 429/503, kết quả rỗng đều được hiển thị; không tự thay bằng dữ liệu mẫu. Mỗi HTTP request frontend có timeout 12 giây và không tự retry.
- Bản đồ hiển thị điểm đón/điểm đến cả trước khi báo giá; chưa tính tuyến đường theo đường phố, theo dõi GPS hoặc có vị trí tài xế trực tiếp. Cần mạng để tải bản đồ; tile bị treo được dừng sau 15 giây, UI báo lỗi và có nút **Thử lại**, không tự retry vô hạn.
- Địa chỉ đã chọn cùng tọa độ được lưu trong chuyến demo bằng localStorage; chỉ dùng địa chỉ/địa danh công khai cho demo, không nhập dữ liệu riêng tư. Truy vấn tìm kiếm được gửi tới Nominatim công cộng. Dữ liệu được kiểm tra kiểu, độ dài, tọa độ hữu hạn trong khoảng vĩ độ ±90/kinh độ ±180 khi tạo và khi đọc lại; địa chỉ ngoài danh sách mẫu vẫn được khôi phục qua các bước chuyến đi.
- localStorage phù hợp cho bản demo cùng trình duyệt; không có đồng bộ nhiều thiết bị, phân quyền thật hay bảo đảm giao dịch giữa các tab thao tác đồng thời. Khi nối backend, thay lớp API giả lập bằng lời gọi có timeout qua YARP Gateway; giá, phân quyền, idempotency và trạng thái chuẩn do backend xác minh. Không gọi trực tiếp các business service.

## Kiểm tra

Chạy trong `src/WebApp`:

```powershell
npm run typecheck
npm test
npm run build
```

Vitest kiểm tra báo giá, chuyển trạng thái, giới hạn từ chối, thanh toán lặp, lịch sử, dữ liệu lưu lỗi và phục hồi địa chỉ tùy chọn sau khi tải lại. Test client tìm địa chỉ kiểm tra encode query, JWT Development/production, tọa độ lỗi, HTTP 401/429/503 và timeout. Test render giao diện xác nhận các nút theo trạng thái và chờ thanh toán; test bản đồ kiểm tra hai điểm preview, nhận tọa độ khi click, timeout tile và dọn timer. Các kiểm tra này không thay thế kiểm thử tương tác trên trình duyệt, thiết bị thật hay backend.
