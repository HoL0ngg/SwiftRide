# Giao diện SwiftRide — Tìm địa chỉ thật, Đặt xe kết nối Backend Gateway

Giao diện WebApp của SwiftRide tại `src/WebApp` phục vụ cả hai vai trò Hành khách (Rider) và Tài xế (Driver), được xây dựng bằng Vite, React, TypeScript và Tailwind CSS (phong cách Swift Emerald).

## 1. Hai chế độ vận hành

Giao diện hỗ trợ 2 chế độ:
1. **Chế độ Backend API (Mặc định):**
   - Kết nối trực tiếp qua API Gateway (`http://localhost:5080`).
   - Xử lý tiền tệ chính xác bằng BigInt whole VND.
   - Idempotency key chuẩn UUID v4 cho mọi mutation command.
   - Tài xế tự động gửi nhịp tim tọa độ (`PUT /drivers/{id}/location`) mỗi 5 giây khi Online.
   - Hành khách và Tài xế thăm dò trạng thái chuyến đi (`GET /trips/{id}`) đơn chuyến với backoff tự động khi gặp 429.
   - Tự động thanh toán và đối chiếu sau khi trả khách, không cần nút bấm thủ công.
2. **Chế độ Demo Fallback (LocalStorage):**
   - Dành cho chạy độc lập khi không bật backend hoặc offline.
   - Lưu trữ dữ liệu trên `localStorage` qua key `swiftride-demo-v1`.

---

## 2. Cách chạy WebApp

Cần Node.js 20+ và npm:

```powershell
cd src/WebApp
npm install
npm run dev
```

Mở trình duyệt:
- Tab Hành khách: `http://localhost:5173/?role=rider`
- Tab Tài xế: `http://localhost:5173/?role=driver`

Vite proxy cấu hình chuyển tiếp các route `/trips`, `/drivers`, `/locations`, `/dev/token` về API Gateway `http://localhost:5080`.

---

## 3. Kịch bản trải nghiệm Dual-Tab với Backend thật

1. **Tab Tài xế (`role=driver`):**
   - Bật công tắc "Trực tuyến".
   - Ứng dụng tự động gửi tọa độ hiện tại tới Gateway (`PUT /drivers/{id}/location`).
2. **Tab Hành khách (`role=rider`):**
   - Tìm kiếm điểm đón (VD: Chợ Bến Thành) và điểm đến (VD: Landmark 81) hoặc chọn trên bản đồ.
   - Nhấn "Xem giá chuyến đi" -> Gọi `POST /trips` tới Gateway, nhận báo giá từ Matching Service.
   - Nhấn "Xác nhận giá & đặt xe" -> Gọi `POST /trips/{id}/confirm-quote`, chuyến chuyển sang `DriverOffered`.
3. **Tab Tài xế:**
   - Driver Feed nhận được chuyến đi được mời (`GET /trips/feed`).
   - Nhấn "Chấp nhận chuyến đi" -> Gọi `POST /trips/{id}/driver-accept`.
4. **Hành trình chuyến đi:**
   - Tài xế nhấn "Bắt đầu đến đón" (`POST /trips/{id}/en-route`).
   - Tài xế nhấn "Đã đón khách" (`POST /trips/{id}/pickup`).
   - Tài xế nhấn "Đã trả khách" (`POST /trips/{id}/dropoff`).
5. **Thanh toán tự động:**
   - Ngay khi dropoff, Trip Service tự động gửi yêu cầu thanh toán sang Payment Service.
   - Payment Service xác thực idempotency, trừ tiền và ghi 2 dòng sổ cái kép cân bằng.
   - Cả hai tab tự động chuyển sang trạng thái "Đã thanh toán" (`Paid`) với mã giao dịch hoàn chỉnh.

---

## 4. Kiểm tra chất lượng (Verification)

Trong thư mục `src/WebApp`:

```powershell
npm run typecheck   # Xác thực 0 lỗi TypeScript
npm test            # 96/96 Vitest unit tests pass
npm run build       # Build production thành công
```
