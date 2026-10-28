# Payment Service + Supabase: hướng dẫn và luồng hoạt động

## 1. Đã sửa gì và vì sao

Code cũ viết cho một DB tự dựng (EF Migration, schema `public`). DB thật trên Supabase khác, nên phải sửa code cho khớp DB (không sửa DB).

| DB Supabase (`paydb`)                                                   | Code cũ                                | Đã sửa                                                                            |
| ----------------------------------------------------------------------- | -------------------------------------- | --------------------------------------------------------------------------------- |
| Schema `paydb`, tên bảng/cột `snake_case`                               | Schema mặc định, `PascalCase`          | `HasDefaultSchema("paydb")` + `HasColumnName(...)` trong `*Configuration`         |
| `method/status/side` là chữ thường: `'card'`, `'settled'`, `'debit'`    | Enum lưu `"Card"`, `"Settled"`         | `LowerCaseEnumConverter<T>`                                                       |
| `request_fingerprint` phải khớp `^[0-9a-f]{64}$`                        | Hash ra **chữ HOA** (sẽ bị DB từ chối) | `Convert.ToHexStringLower`                                                        |
| `payments.status` chỉ có `pending/settled/failed`                       | Có thêm `Refunded`                     | Bỏ `Refunded`. Hoàn tiền là dòng riêng ở bảng `refunds`                           |
| `settled_at` **chỉ** có khi `status='settled'` (CHECK)                  | `Failed` cũng gán `CompletedAt`        | Chỉ `MarkSettled` gán `SettledAt`                                                 |
| `failure_code`                                                          | `FailureReason` (câu dài)              | `FailureCode` (mã ngắn, vd `payment_method_declined`)                             |
| `rider_id` là `text`                                                    | `Guid`                                 | `string`                                                                          |
| **Không có cột `driver_id`**                                            | `DriverId` lưu trong Payment           | Lưu trong `account_code` của sổ cái (`driver:<id>`), đọc lại từ dòng Có           |
| `ledger_entries` khoá chính `(payment_id, side)`, không có `id`, `type` | Có `Id`, `Type`                        | Bỏ `Id`, `Type`. Khoá ghép `(PaymentId, Direction)`                               |
| `refunds` + `refund_ledger_entries` là bảng riêng; `UNIQUE(payment_id)` | Refund là dòng sổ cái trong cùng bảng  | Thêm entity `Refund`, `RefundLedgerEntry` (nằm trong aggregate `Payment`)         |
| `refunds.idempotency_key = 'payment-<id>-refund'` (CHECK)               | `refund-<id>`                          | `PaymentIdempotency.KeyForRefund`                                                 |
| Có `UNIQUE(trip_id)` và `UNIQUE(idempotency_key)`                       | Chỉ bắt 1 tên index                    | `DbNames` liệt kê 4 constraint, đều dịch thành `DuplicateIdempotencyKeyException` |
| Trigger `DEFERRABLE INITIALLY DEFERRED` kiểm sổ cái lúc COMMIT          | Không biết                             | Ghi payment + 2 dòng sổ cái trong **một** `SaveChanges` (một transaction)         |
| DB đã tạo sẵn                                                           | EF Migrations + `MigrateOnStartup`     | **Xoá** Migrations, `MigrationExtensions`, `xmin`, gói `EF.Design`                |

Vì sao bỏ `xmin` (khoá lạc quan): payment và refund giờ **chỉ INSERT một lần**, không UPDATE. Chặn trùng do `UNIQUE` của DB lo.

## 2. Kiến trúc (Clean Architecture)

```
Api  ───►  Application  ───►  Domain
Infrastructure  ───►  Application  ───►  Domain      (mũi tên = "biết đến")
```

| Lớp            | Có gì                                                                              | Không được biết    |
| -------------- | ---------------------------------------------------------------------------------- | ------------------ |
| Domain         | `Payment`, `Refund`, `LedgerEntry`, `Money`, enum, `IPaymentReader/Writer`         | EF, HTTP, Supabase |
| Application    | 3 use case, DTO, `IUnitOfWork`, `IPaymentGateway(Factory)`                         | EF, HTTP           |
| Infrastructure | EF Core + Npgsql (Supabase), `PaymentRepository`, `PaymentUnitOfWork`, gateway giả | HTTP               |
| Api            | Endpoint, `GlobalExceptionHandler`, `Program.cs` (nối DI)                          | Chi tiết DB        |

SOLID trong code:

| Nguyên tắc | Chỗ thấy                                                                                                                      |
| ---------- | ----------------------------------------------------------------------------------------------------------------------------- |
| S          | `GatewayCallPolicy` chỉ lo timeout. `PaymentUnitOfWork` chỉ lo lưu + dịch lỗi DB. `PaymentIdempotency` chỉ lo key/fingerprint |
| O          | Thêm cổng mới = thêm 1 class + 1 dòng DI, `PaymentGatewayFactory` không sửa                                                   |
| L          | `MockCardGateway`/`MockWalletGateway` thay nhau được qua `IPaymentGateway`                                                    |
| I          | `IPaymentReader` và `IPaymentWriter` tách riêng. `GetPaymentUseCase` chỉ cần Reader                                           |
| D          | Use case chỉ nhận interface. `Program.cs` mới gắn class thật (Supabase) vào                                                   |

Muốn đổi Supabase sang DB khác: chỉ viết lại `Infrastructure/Persistence`, Domain và Application không đổi.

## 3. Nối Supabase

1. Supabase Dashboard → **Connect** → lấy chuỗi **Session pooler** (cổng 5432, chạy được trên IPv4).
2. Lưu chuỗi kết nối bằng user-secrets (không ghi vào file, không commit):

```powershell
cd Api
dotnet user-secrets set "ConnectionStrings:PaymentDB" "Host=aws-0-<region>.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.<project-ref>;Password=<mật-khẩu>;SSL Mode=Require;Trust Server Certificate=true"
dotnet run
```

Trong Docker/production dùng biến môi trường: `ConnectionStrings__PaymentDB=...`

3. Test: `GET http://localhost:5285/health` → 200. Rồi `POST /payments` như mục 4.

Lưu ý:

- Nếu dùng **Transaction pooler** (cổng 6543) thì thêm `;No Reset On Close=true`. Session pooler thì không cần.
- Không bật EF `EnsureCreated/Migrate`. Bảng đã có trên Supabase, bật sẽ lỗi hoặc tạo bảng thừa.
- Đừng cho `paydb` lộ ra Data API (PostgREST) của Supabase. Service này nối thẳng bằng Npgsql.

## 4. Luồng hoạt động

### 4.1 `POST /payments` (thu tiền)

Ví dụ cụ thể: rider `rider-1` trả `85000` VND cho tài xế `drv-1`, chuyến `11111111-...`.

```
POST /payments
Idempotency-Key: trip-11111111-1111-1111-1111-111111111111-payment
{ "tripId":"11111111-...", "riderId":"rider-1", "driverId":"drv-1",
  "amount":85000, "currency":"VND", "method":"Card", "paymentToken":"tok_card_ok" }
```

**Bước 0. `Api/Program.cs`** (chạy lúc khởi động)

- `AddPaymentApplication()` đăng ký 3 use case.
- `AddInfrastructure(config)` đọc `ConnectionStrings:PaymentDB`, đăng ký `PaymentDbContext` (Npgsql → Supabase), gắn `IPaymentReader/Writer → PaymentRepository`, `IUnitOfWork → PaymentUnitOfWork`, 2 gateway giả.
- `MapPaymentEndpoints()` đăng ký 3 route.

**Bước 1. `Api/PaymentEndpoints.cs` → `CreatePaymentAsync`**

- ASP.NET đọc JSON thành `CreatePaymentRequest`, đọc header `Idempotency-Key`, tạo `ProcessPaymentUseCase` (DI).
- Gói thành `ProcessPaymentInput` rồi gọi `useCase.ExecuteAsync(...)`.
- Kết quả: `IsReplay` = true → `200`, ngược lại → `201`.

**Bước 2. `Application/.../ProcessPaymentUseCase.ExecuteAsync`**

1. `Money.Create(85000, "VND")` (Domain): kiểm tiền > 0, số nguyên, đúng VND, < 1e18.
2. `Payment.Create(...)` (Domain): kiểm trip/rider/driver, rider ≠ driver, method hợp lệ, key phải đúng `trip-{tripId}-payment`. Tính `RequestFingerprint` (SHA-256, hex thường). Ra `candidate` trạng thái `Pending`, **chỉ nằm trong RAM**.
3. `reader.GetByIdempotencyKeyAsync(key)` → `PaymentRepository` (Infrastructure) → EF chạy `SELECT ... FROM paydb.payments WHERE idempotency_key = ...` (kèm sổ cái, refund).
   - Có rồi, cùng fingerprint → trả bản cũ (`IsReplay=true`). **Không thu tiền lần 2.**
   - Có rồi, khác fingerprint → `IdempotencyConflictException` → **409**.
4. Kiểm `paymentToken` không rỗng (đặt sau bước 3 để request lặp không cần gửi lại token).
5. `gateways.Create(Card)` (`PaymentGatewayFactory`) → `MockCardGateway`. `callPolicy.RunAsync(...)` gọi `ChargeAsync` có timeout 10 giây.
6. Xử lý kết quả cổng:
   - `Succeeded` (`tok_card_ok`) → `candidate.MarkSettled(ref, now)`: `Status=Settled`, `SettledAt=now`, thêm 2 dòng sổ cái: **Nợ** `rider:rider-1` 85000, **Có** `driver:drv-1` 85000, rồi kiểm cân bằng.
   - `Declined` (`tok_card_declined`) → `MarkFailed(code)`: `Status=Failed`, `SettledAt=null`, không có sổ cái.
   - `TransientFailure` (`tok_card_timeout`) → ném `PaymentGatewayUnavailableException` → **503**, **không lưu gì**, Trip được retry cùng key.
7. `writer.Add(candidate)`: chỉ báo EF "theo dõi payment này" (chưa ghi DB).
8. `unitOfWork.SaveChangesAsync()` → `PaymentUnitOfWork` → `PaymentDbContext.SaveChangesAsync`:
   - Rào chắn: cấm Modified/Deleted (dữ liệu tiền chỉ INSERT).
   - EF gửi lên Supabase, **một transaction**: `INSERT payments` → `INSERT ledger_entries` ×2.
   - Lúc COMMIT, các trigger deferred (`payment_ledger_check`, `ledger_payment_check`) kiểm sổ cái.
   - Nếu 2 request song song cùng trip: `UNIQUE` của DB cho 1 người thắng, người thua bị lỗi `23505`. `PaymentUnitOfWork` dịch thành `DuplicateIdempotencyKeyException`. Use case bắt, đọc lại bản thắng, trả như replay.
9. `PaymentMapper.ToDto(candidate)` → `PaymentResult(dto, IsReplay=false)`.

**Bước 3.** `201 Created` + JSON `PaymentDto`. Lỗi bất kỳ → `GlobalExceptionHandler` đổi thành 400/404/409/503/500.

### 4.2 `POST /payments/{id}/refund` (hoàn tiền)

Ví dụ: hoàn payment ở trên với `{ "reason": "Customer cancelled" }`.

1. `PaymentEndpoints.RefundPaymentAsync` → `RefundPaymentUseCase.ExecuteAsync(id, reason)`.
2. `reader.GetByIdAsync(id)` → có thể `PaymentNotFoundException` → **404**.
3. `payment.Refund != null` (đã hoàn)?
   - Cùng `reason` → trả lại kết quả cũ (200, không hoàn lần 2).
   - Khác `reason` → **409**.
4. `payment.RequestRefund(reason, now)` (Domain): chỉ `Settled` mới hoàn (không thì **409**), `reason` 1–500 ký tự. Tạo `Refund` `Pending` trong RAM, `idempotency_key = payment-{id}-refund`.
5. Gọi `gateway.RefundAsync` có timeout. Lỗi tạm → 503. Bị từ chối → 409. **Cả hai đều không lưu gì**: nếu lưu 1 refund `failed`, `UNIQUE(payment_id)` sẽ chặn hoàn lại mãi mãi.
6. `payment.SettleRefund(ref, now)`: refund `Settled`, `SettledAt=now`, thêm 2 dòng đảo: **Nợ** `driver:drv-1`, **Có** `rider:rider-1`. Dòng sổ cái cũ **không bị sửa**.
7. `SaveChangesAsync`: `INSERT refunds` + `INSERT refund_ledger_entries` ×2 trong một transaction. Trigger `refund_ledger_check`/`ledger_refund_check` kiểm lúc COMMIT.
8. Trả `200` + `PaymentDto` (có khối `refund`). `status` của payment vẫn là `Settled` (đúng với DB).

### 4.3 `GET /payments/{id}`

`GetPaymentUseCase` → `IPaymentReader.GetByIdAsync` (một lần nạp: payment + sổ cái + refund + sổ cái refund) → `PaymentMapper.ToDto`. Không có → 404.

## 5. Giả định cần bạn kiểm

- Mình **không thấy thân hàm trigger** (`payment_ledger_check`, `ledger_payment_check`, `refund_ledger_check`, `ledger_refund_check`). Mình giả định chúng chỉ kiểm: payment `settled` có đủ 2 dòng nợ/có, số tiền cân bằng và bằng `amount`. Nếu `POST` báo lỗi trigger, gửi mình nội dung các hàm đó.
- Không có `driver_id` trong DB nên `driverId` của payment `Failed` là `null` khi đọc lại. Muốn có luôn: thêm cột `driver_id text` vào `paydb.payments`, rồi map thêm trong `PaymentConfiguration`.
- Lớp EF (Infrastructure) chưa được chạy thử với Supabase thật. Phần Domain + Application đã chạy thử 26 phép kiểm (thu tiền, lặp, xung đột key, từ chối, lỗi tạm, hoàn tiền, đua song song).

## 6. Bảng test nhanh

| Ca                         | Token                                           | Kết quả                       |
| -------------------------- | ----------------------------------------------- | ----------------------------- |
| Thành công                 | `tok_card_ok` / `tok_wallet_ok`                 | 201, `Settled`, 2 dòng sổ cái |
| Gửi lại y hệt              | như trên                                        | 200, cùng `id`                |
| Cùng key, khác `amount`    |                                                 | 409                           |
| Từ chối                    | `tok_card_declined` / `tok_wallet_insufficient` | 201, `Failed`, `failureCode`  |
| Lỗi tạm                    | `tok_card_timeout` / `tok_wallet_timeout`       | 503, DB không đổi             |
| Key sai                    |                                                 | 400                           |
| Hoàn tiền                  |                                                 | 200, có `refund`              |
| Hoàn tiền payment `Failed` |                                                 | 409                           |
