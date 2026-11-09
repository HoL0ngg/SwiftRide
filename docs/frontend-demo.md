# Giao diện SwiftRide — hai ứng dụng Rider / Driver kết nối Backend Gateway

WebApp tại `src/WebApp` gồm **hai giao diện tách biệt** cho Hành khách (Rider) và Tài xế (Driver),
xây dựng bằng Vite (multi-page), React, TypeScript và Tailwind CSS v4. Ngôn ngữ thiết kế "Sky Calm"
được mô tả trong [`frontend-design.md`](frontend-design.md).

| URL | Nội dung |
| --- | --- |
| `/` | Trang chọn vai trò (Tôi cần đi xe / Tôi lái xe) |
| `/rider/` | Giao diện hành khách: đặt xe, theo dõi chuyến, lịch sử |
| `/driver/` | Giao diện tài xế: bật online, nhận chuyến, thu nhập |

Link cũ `/?role=rider` và `/?role=driver` vẫn dùng được: trang `/` chuyển hướng sang `/rider/` hoặc `/driver/`.

## 1. Cách vận hành

- Kết nối trực tiếp qua API Gateway (`http://localhost:5080`); trình duyệt luôn gọi đường dẫn tương đối, Vite proxy chuyển tiếp.
- Mỗi trang chỉ khởi tạo **một store và một vòng polling** cho đúng vai trò của nó:
  - Hành khách thăm dò `GET /trips/current`.
  - Tài xế thăm dò `GET /trips/feed`.
  - Polling mỗi 3 giây, tự lùi 10 giây khi gặp 429.
- Xử lý tiền tệ bằng BigInt, đơn vị VND nguyên.
- Mọi lệnh thay đổi trạng thái gửi kèm `Idempotency-Key` ổn định theo hành động.
- Tài xế đang Online gửi tọa độ (`PUT /drivers/{id}/location`) mỗi 30 giây, và gửi ngay khi đổi vị trí.
  Vị trí mặc định là trung tâm TP.HCM; tài xế **bấm lên bản đồ** để chọn vị trí khi đang Online và chưa nhận chuyến.
- Thu nhập tài xế tính từ lịch sử (20 chuyến gần nhất), chỉ cộng các chuyến `Paid`; được tải lại khi vào trang và khi chuyến hiện tại đổi trạng thái.
- Tự động thanh toán và đối chiếu sau khi trả khách, không có nút thanh toán thủ công.
- Khi chuyến đang theo dõi không còn trong `/trips/current`, ứng dụng khách đọc lại `/trips/{id}` để xác nhận trạng thái cuối. Chỉ hiển thị thanh toán thành công khi backend trả `Paid`.
- Giữ màn hình hoàn tất, giá tiền và thông báo thanh toán thành công cho đến khi khách bấm **Đặt một chuyến mới**; các lượt polling tiếp theo không tự đóng màn hình này.

Không còn chế độ Demo Fallback bằng `localStorage`; giao diện luôn cần backend (hoặc Gateway) đang chạy.

---

## 2. Cách chạy WebApp

Cần Node.js 20+ và npm:

```powershell
cd src/WebApp
npm install
npm run dev
```

Mở trình duyệt, mỗi vai trò một tab:

- Hành khách: `http://localhost:5173/rider/`
- Tài xế: `http://localhost:5173/driver/`

Vite proxy chuyển tiếp các route `/trips`, `/drivers`, `/locations`, `/dev/token` về API Gateway
`http://localhost:5080` (đổi bằng biến môi trường `GATEWAY_URL`). Gateway chưa cấu hình CORS, nên hai trang
phải được phục vụ cùng nguồn với proxy này.

Nếu `npm run dev` báo `EPERM` khi xóa `node_modules/.vite`, hãy đóng các tiến trình Node đang chạy rồi xóa
thư mục `node_modules/.vite` (chỉ là cache, được tạo lại tự động).

### Đăng nhập OIDC (tùy chọn)

Khi dùng `VITE_AUTH_MODE=oidc`, `VITE_OIDC_REDIRECT_URI` phải trỏ về trang tương ứng
(`http://localhost:5173/rider/` hoặc `http://localhost:5173/driver/`) và được đăng ký như redirect URI hợp lệ ở IdP.

---

## 3. Kịch bản trải nghiệm hai tab với backend thật

1. **Tab Tài xế (`/driver/`):**
   - Bật công tắc "Trực tuyến" trên thanh trên cùng.
   - Bấm lên bản đồ để đặt vị trí gần điểm đón (ví dụ khu vực Chợ Bến Thành). Ứng dụng gửi tọa độ tới Gateway.
2. **Tab Hành khách (`/rider/`):**
   - Tìm kiếm điểm đón (ví dụ Chợ Bến Thành) và điểm đến (ví dụ Landmark 81) hoặc chọn trên bản đồ.
   - Nhấn "Xem giá chuyến đi" → `POST /trips`, nhận báo giá từ Matching Service.
   - Nhấn "Xác nhận giá & đặt xe" → `POST /trips/{id}/confirm-quote`, chuyến chuyển sang `DriverOffered`.
3. **Tab Tài xế:**
   - Driver Feed nhận lời mời chuyến (`GET /trips/feed`).
   - Nhấn "Nhận chuyến" → `POST /trips/{id}/driver-accept`.
4. **Hành trình chuyến đi:**
   - "Bắt đầu đến đón" (`POST /trips/{id}/en-route`).
   - "Đã đón khách" (`POST /trips/{id}/pickup`).
   - "Đã trả khách" (`POST /trips/{id}/dropoff`).
5. **Thanh toán tự động:**
   - Ngay khi dropoff, Trip Service gửi yêu cầu thanh toán sang Payment Service.
   - Payment Service xác thực idempotency, trừ tiền và ghi sổ cái kép cân bằng.
   - Hai tab chuyển sang trạng thái "Hoàn tất / Đã thanh toán" (`Paid`); thu nhập của tài xế cập nhật.

---

## 4. Kiểm tra chất lượng (Verification)

Trong thư mục `src/WebApp`:

```powershell
npm run typecheck   # Kiểm tra kiểu TypeScript
npm test            # Vitest unit tests
npm run build       # Build production: index.html, rider/index.html, driver/index.html
```

Bản build có ba trang tĩnh; môi trường production cần một reverse proxy cùng nguồn chuyển tiếp
`/trips`, `/drivers`, `/locations` về Gateway (Gateway không phục vụ file tĩnh và chưa có CORS).
