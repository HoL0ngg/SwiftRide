# Hướng dẫn kiểm thử & Vận hành luồng đặt xe End-to-End (SwiftRide)

Tài liệu này hướng dẫn cách chạy và kiểm chứng trọn vẹn kịch bản đặt xe xuyên suốt hệ thống SwiftRide (Gateway -> Trip Service -> Matching Service -> Payment Service -> Database/Ledger).

---

## 1. Kiến trúc hệ thống khi vận hành

SwiftRide tuân thủ nghiêm ngặt các quy tắc kiến trúc trong `AGENTS.md`:
- **3 Services duy nhất:**
  1. `Trip Service` (port nội bộ 8080) — lưu trữ trên SQL Server (`TripDB`), quản lý vòng đời chuyến đi theo State Pattern.
  2. `Matching & Pricing Service` (port nội bộ 8080) — lưu trữ trên Supabase PostgreSQL schema `matchdb`, tìm tài xế gần nhất & tính cước theo Strategy Pattern.
  3. `Payment Service` (port nội bộ 8080) — lưu trữ trên Supabase PostgreSQL schema `paydb`, xử lý thanh toán bất biến, sổ cái kép cân bằng (`paydb.ledger_entries`).
- **Edge API Gateway (YARP):** Tiếp nhận toàn bộ lưu lượng client tại port `5080`, xác thực JWT Bearer, rate limiting, định tuyến tới các service nội bộ. Các route nội bộ (`/match`, `/quote`, `/payments/**`) được bảo vệ bằng header bí mật `X-Internal-Api-Key`.
- **Frontend WebApp:** Single-Page Application (Vite + React + TypeScript) kết nối qua API Gateway.

---

## 2. Chuẩn bị môi trường

Tạo file `.env` từ `.env.example`:
```bash
cp .env.example .env
```

Cập nhật các giá trị trong `.env`:
- `SQLSERVER_PASSWORD`: Mật khẩu quản trị SA cho SQL Server container.
- `JWT_SIGNING_KEY`: Khóa bí mật ký JWT (tối thiểu 32 ký tự).
- `ConnectionStrings__MatchDB`: Connection string kết nối Supabase `matchdb` (`SSL Mode=VerifyFull`).
- `ConnectionStrings__PaymentDB`: Connection string kết nối Supabase `paydb` (`SSL Mode=Require;Trust Server Certificate=true`).
- `INTERNAL_MATCHING_API_KEY` & `INTERNAL_PAYMENT_API_KEY`: API keys nội bộ kết nối giữa Trip Service với Matching & Payment.

---

## 3. Khởi động toàn bộ hệ thống bằng Docker Compose

Khởi động các dịch vụ:
```bash
docker compose up --build -d
```

Kiểm tra trạng thái các container:
```bash
docker compose ps
```

---

## 4. Chạy kiểm tra tự động luồng đặt xe (Automated Smoke Test & Integration Tests)

### 4.1 Automated Smoke Test (Gateway)
Chạy script PowerShell kiểm thử luồng hoàn chỉnh qua API Gateway:
```powershell
# Chạy với dev token tự động lấy từ Gateway (chỉ trong Development environment):
powershell -ExecutionPolicy Bypass -File docs/check-booking-flow.ps1 -GatewayUrl "http://localhost:5080"

# Hoặc truyền token ngoài (trong môi trường Staging/Production):
powershell -ExecutionPolicy Bypass -File docs/check-booking-flow.ps1 `
    -GatewayUrl "http://localhost:5080" `
    -RiderAccessToken "<jwt-token-rider>" `
    -DriverAccessToken "<jwt-token-driver>" `
    -ExpectedTransport Rest
```

Kịch bản script tự động thực hiện:
1. Lấy token hợp lệ qua Gateway `/dev/token` (nếu chạy local Development) hoặc nhận token được truyền vào.
2. Driver gửi tọa độ online tại Chợ Bến Thành (`PUT /drivers/{id}/location`).
3. Kiểm tra bảo vệ actor: Rider gọi thử endpoint của driver bị từ chối 403 Forbidden.
4. Rider gửi yêu cầu đặt xe từ Chợ Bến Thành đi Landmark 81 (`POST /trips`).
   - Kiểm tra replay tạo chuyến với cùng `Idempotency-Key` trả về cùng `tripId` và giá cước.
5. Hệ thống báo giá (`Quoted`) kèm tài xế ứng viên. Rider xác nhận giá (`POST /trips/{id}/confirm-quote`), chuyển sang `DriverOffered`.
6. Driver nhận chuyến (`POST /trips/{id}/driver-accept`), chuyển sang `DriverAccepted`.
7. Driver di chuyển (`POST /trips/{id}/en-route`), đón khách (`POST /trips/{id}/pickup`), và trả khách (`POST /trips/{id}/dropoff`).
   - Kiểm tra replay dropoff với cùng `Idempotency-Key` trả về kết quả nhất quán.
8. Hệ thống tự động thu tiền, kiểm tra trạng thái chuyển thành `Paid`, kiểm tra đúng `fareAmount`, `fareCurrency` (VND) và `paymentId`.

### 4.2 Integration Tests Runner
Chạy runner kiểm thử tích hợp trên các cơ sở dữ liệu test thực tế (SQL Server + PostgreSQL):
```powershell
# Chạy từng suite riêng:
powershell -ExecutionPolicy Bypass -File docs/run-integration-tests.ps1 -Suite Trip
powershell -ExecutionPolicy Bypass -File docs/run-integration-tests.ps1 -Suite Matching
powershell -ExecutionPolicy Bypass -File docs/run-integration-tests.ps1 -Suite Payment
powershell -ExecutionPolicy Bypass -File docs/run-integration-tests.ps1 -Suite FullFlow

# Hoặc chạy toàn bộ cả 4 suites:
powershell -ExecutionPolicy Bypass -File docs/run-integration-tests.ps1 -Suite All
```
Runner tự động khởi động test containers, khởi tạo schema, chạy các test suite bắt buộc (bao gồm `PersistentBookingFlowTests` kiểm thử end-to-end qua Gateway với cả 3 database thực tế: `swiftride_trip_test`, `matching_test`, và `swiftride_payment_test`), parse báo cáo TRX để đảm bảo không có test bắt buộc nào bị skip, và dọn dẹp biến môi trường sau khi hoàn tất.

---

## 5. Kiểm thử qua Giao diện Web (Dual-Tab Demo)

1. Khởi động frontend WebApp:
   ```bash
   cd src/WebApp
   npm install
   npm run dev
   ```
2. Mở trình duyệt tại `http://localhost:5173`.
3. Mở 2 tab hoặc 2 cửa sổ:
   - **Tab 1 (Rider):** Nhập điểm đón (Chợ Bến Thành) và điểm đến (Landmark 81). Nhấn "Đặt xe".
   - **Tab 2 (Driver):** Bật công tắc "Online". Tab sẽ tự động gửi tọa độ heartbeat mỗi 5 giây.
4. Tab Rider xác nhận cước -> Tab Driver nhận được chuyến trong Driver Feed -> Nhấn "Chấp nhận".
5. Driver tiến hành các bước "Đang đến đón", "Đã đón khách", "Hoàn thành chuyến đi".
6. Màn hình Rider lập tức cập nhật sang trạng thái "Đã thanh toán" kèm thông tin giao dịch mà không cần bấm thủ công.

---

## 6. Kiểm tra tính toàn vẹn dữ liệu (Idempotency & Double-Entry Ledger)

1. **SQL Server (`TripDB`):**
   Kiểm tra bảng `Trips` và `CommandReceipts`:
   ```sql
   SELECT Id, Status, RiderId, AssignedDriverId, FareAmount, PaymentId, PaymentIdempotencyKey FROM Trips;
   SELECT Scope, CommandKey, Status FROM CommandReceipts;
   ```
   - Mỗi lệnh gửi lại với cùng `Idempotency-Key` sẽ trả về kết quả cũ, không tạo bản ghi mới hay gây duplicate mutation.

2. **Supabase PostgreSQL (`paydb`):**
   Kiểm tra thanh toán và sổ cái kế toán kép:
   ```sql
   SELECT id, trip_id, amount, currency, status, idempotency_key FROM paydb.payments;
   SELECT id, payment_id, account, direction, amount, currency FROM paydb.ledger_entries;
   ```
   - Mỗi khoản thanh toán settled sinh chính xác 2 dòng ledger (1 `Debit`, 1 `Credit`) với tổng số tiền tuyệt đối bằng nhau.

---

## 7. Cơ chế phục hồi lỗi & Bảo vệ hệ thống (Hardening & Reliability)

Hệ thống đã được củng cố toàn diện với các quy tắc kỹ thuật sau:

1. **Bảo mật API nội bộ Fail-Closed:**
   - Cả `PaymentService` (`/payments/**`) và `MatchingService` (`/match`, `/quote`) kiểm tra `X-Internal-Api-Key` đối chiếu cấu hình `Security:InternalApiKey` (hoặc `Payment:InternalApiKey` / `Matching:InternalApiKey`).
   - Nếu thiếu cấu hình key hoặc client gửi key không khớp, API ngay lập tức từ chối với mã `401 Unauthorized` (không bao giờ bypass/fail-open).

2. **Tự động áp dụng Migration cho Database Trip khi khởi động:**
   - `TripService` hỗ trợ cờ cấu hình `Database:MigrateOnStartup=true` (được cấu hình mặc định trong `docker-compose.yml`).
   - Khi container khởi động, `InitializeDatabaseAsync` tự động chạy `MigrateAsync()` tạo bảng và schema đầy đủ trên SQL Server nếu chưa có.

3. **Background Worker & CAS Claim nguyên tử:**
   - `TripWorkStore` hỗ trợ background processing cho cả Payment và Matching tasks.
   - Thao tác claim job sử dụng cơ chế Compare-And-Swap (CAS) nguyên tử trên DB (`ExecuteUpdateAsync`), loại bỏ hoàn toàn race condition khi chạy nhiều worker song song.
   - Các tác vụ Matching bị gián đoạn hoặc timeout sẽ được worker tự động phục hồi và thử lại có giới hạn (exponential backoff, tối đa 5 lần).

4. **Phục hồi thanh toán đang treo (Pending Retry):**
   - Khi kiểm tra trạng thái thanh toán thấy `Pending`, coordinator kiểm tra `CanRetry == true` và lease đã hết hạn.
   - Hệ thống tự động gửi lại lệnh `ChargeAsync` với cùng Idempotency Key để đánh thức Payment Service tiếp tục quy trình gọi Gateway thay vì để chuyến xe kẹt vĩnh viễn ở `PaymentPending`.

5. **Loại trừ tài xế bận & Rematch linh hoạt:**
   - Trong bước `ConfirmQuote` và `DriverReject`, nếu tài xế ứng viên từ Matching đã bị chuyến khác giữ (`TryClaimDriver == false`), hệ thống tự động đưa vào danh sách loại trừ (`excludedDrivers`) và gọi lại Matching để tìm tài xế khác trong giới hạn $k$ lần (mặc định $k=3$). Chuyến xe chỉ chuyển sang `Failed` an toàn khi hết số lần thử, không gây lỗi 500.

6. **Đối chiếu danh tính chặt chẽ trước khi quyết toán (`Paid`):**
   - Trước khi cập nhật chuyến sang `Paid`, hệ thống đối chiếu chặt chẽ toàn bộ thông tin: `TripId`, `RiderId`, `AssignedDriverId`, `FareAmount`, `Currency`, và kiểm tra không có refund đã settled.

7. **Bảo mật Mock Receipt bằng SHA-256 Digest:**
   - Mock Gateway không lưu `PaymentToken` thô vào cột `RequestHash` mà tính toán mã băm SHA-256 hex digest chuẩn hóa.
   - Khi replay request, hệ thống kiểm tra đối chiếu hash digest để phát hiện xung đột idempotency.

