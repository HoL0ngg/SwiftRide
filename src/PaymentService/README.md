# Payment Service (SwiftRide)

Service **thanh toán** trong hệ thống SwiftRide.

- Thu tiền cho một chuyến đi, **không tính lại giá** (dùng đúng số tiền Trip gửi sang).
- Ghi **sổ cái kép**: mỗi giao dịch có 1 dòng Nợ và 1 dòng Có, luôn cân bằng, chỉ ghi thêm.
- Chống thu tiền 2 lần bằng **Idempotency-Key**.
- Hoàn tiền bằng bút toán đảo.

Công nghệ: .NET 10, Minimal API, EF Core 10, PostgreSQL 17 (database `PayDB`), Docker.

> Đây là service **nội bộ**: API Gateway không route `/payments`.
> Khi test, ta gọi thẳng vào cổng `5285`.

> Mẹo: mở file này bằng chế độ xem trước (VS Code: `Ctrl+Shift+V`) để thấy hình và định dạng đẹp.

---

## 1. Cấu trúc thư mục

![Cấu trúc Clean Architecture của Payment Service](./payment-clean-architecture.svg)

Service chia 4 lớp, mỗi lớp là một project. Phụ thuộc **chỉ đi vào trong**:

```text
Api             ──►  Application  ──►  Domain
Infrastructure  ──►  Application  ──►  Domain
```

Vai trò từng lớp:

- **Domain**: nghiệp vụ thuần (`Payment`, `LedgerEntry`, `Money`, enum, exception).
  Không biết EF hay HTTP.
- **Application**: use case `Process`, `Refund`, `Get`; DTO; các interface cần dùng.
  Chỉ biết Domain và interface.
- **Infrastructure**: hiện thực interface bằng EF Core + PostgreSQL, cổng thanh toán giả,
  Migrations.
- **Api** (Presentation): endpoint, nhận request, trả response, bắt lỗi, `Program.cs`.
  Chỉ gọi use case.

Cây thư mục:

```text
src/PaymentService/
├── Domain/
│   ├── Entities/        Payment.cs, LedgerEntry.cs
│   ├── Enums/           PaymentStatus.cs, PaymentMethod.cs,
│   │                    EntryDirection.cs, LedgerEntryType.cs
│   ├── Exceptions/      DomainException.cs, InvalidPaymentStateException.cs
│   ├── Repositories/    IPaymentReader.cs, IPaymentWriter.cs
│   ├── Services/        PaymentIdempotency.cs
│   └── ValueObjects/    Money.cs
│
├── Application/
│   ├── Abstractions/    IPaymentGateway.cs, IPaymentGatewayFactory.cs,
│   │                    IUnitOfWork.cs, PaymentGatewaySettings.cs
│   ├── Exceptions/      PaymentNotFoundException.cs
│   │                    IdempotencyConflictException.cs
│   │                    DuplicateIdempotencyKeyException.cs
│   │                    ConcurrentPaymentUpdateException.cs
│   │                    PaymentGatewayUnavailableException.cs
│   ├── Payments/
│   │   ├── Dtos/        PaymentDto.cs, PaymentResult.cs, ProcessPaymentInput.cs
│   │   ├── UseCases/    ProcessPaymentUseCase.cs, RefundPaymentUseCase.cs,
│   │   │                GetPaymentUseCase.cs
│   │   ├── GatewayCallPolicy.cs
│   │   └── PaymentMapper.cs
│   └── DependencyInjection.cs
│
├── Infrastructure/
│   ├── Gateways/        PaymentGatewayFactory.cs, MockPaymentGatewayBase.cs,
│   │                    MockCardGateway.cs, MockWalletGateway.cs
│   ├── Persistence/
│   │   ├── Configurations/   PaymentConfiguration.cs, LedgerEntryConfiguration.cs
│   │   ├── PaymentDbContext.cs
│   │   ├── PaymentRepository.cs
│   │   ├── PaymentUnitOfWork.cs
│   │   └── MigrationExtensions.cs
│   ├── Migrations/      ..._InitialCreate.cs   (sinh bằng dotnet ef, phải commit)
│   └── DependencyInjection.cs
│
└── Api/
    ├── Requests/        CreatePaymentRequest.cs, RefundPaymentRequest.cs
    ├── PaymentEndpoints.cs
    ├── GlobalExceptionHandler.cs
    ├── Program.cs
    └── Dockerfile, appsettings*.json

tests/SwiftRide.PaymentService.Tests/
    PaymentTests.cs, ProcessPaymentUseCaseTests.cs
```

---

## 2. Lệnh cần chạy sau khi pull về

Chạy từ **thư mục gốc solution** (nơi có `docker-compose.yml`). Lệnh dưới là PowerShell.

Cần cài: **Git** và **Docker Desktop** (bật lên, đợi sẵn sàng).
Không cần cài .NET hay PostgreSQL để chạy service.

### Lần đầu

Bước 1. Tạo file cấu hình (`.env` bị gitignore nên không có trong repo):

```powershell
Copy-Item .env.example .env
```

Bước 2. Mở cổng 5285 ra máy bạn. Bỏ qua nếu `docker-compose.yml` đã có `ports`
cho `payment-service`:

```powershell
@"
services:
  payment-service:
    ports:
      - "127.0.0.1:5285:8080"
"@ | Set-Content docker-compose.override.yml
```

Bước 3. Build và chạy (chỉ Payment + PostgreSQL, lần đầu hơi lâu):

```powershell
docker compose up -d --build payment-service
```

Bước 4. Kiểm tra:

```powershell
docker compose ps
docker compose logs payment-service --tail 30
```

Kết quả đúng:

- Hai container `payment-service-1` và `postgres-1` đều `Up`.
- Cột PORTS của payment có `127.0.0.1:5285->8080/tcp`.
- Log có `Applying migration '..._InitialCreate'` (lần đầu) và
  `Now listening on: http://[::]:8080`.
- `GET http://127.0.0.1:5285/health` trả **200**.

Database `PayDB` và các bảng `payments`, `ledger_entries` được **tự tạo khi service
khởi động** (compose đặt `Database__MigrateOnStartup=true`). Bạn không phải tạo DB.

### Hằng ngày

```powershell
docker compose up -d payment-service                 # bật service
docker compose logs payment-service --tail 50        # xem log
docker compose stop                                  # tắt (giữ dữ liệu)
docker compose up -d --build payment-service         # SAU KHI SỬA CODE C#
docker compose down -v                               # xóa sạch DB để làm lại
```

Sửa code mà quên `--build` thì container vẫn chạy bản code cũ.

### Xem dữ liệu trong DB

```powershell
docker compose exec postgres psql -U swiftride -d PayDB
```

(`swiftride` là `POSTGRES_USER` trong `.env`.) Gõ trong psql:

```sql
\dt
SELECT "Id","TripId","Status","Amount" FROM payments;
SELECT "PaymentId","Account","Direction","Type","Amount" FROM ledger_entries ORDER BY "CreatedAt";
\q
```

### Khi sửa cấu trúc bảng (thêm cột, sửa entity)

Cần .NET 10 SDK và `dotnet-ef` (cài một lần: `dotnet tool install --global dotnet-ef`).

```powershell
# Chuỗi kết nối chỉ để EF nạp được app, lệnh này không cần DB đang chạy
$env:ConnectionStrings__PaymentDB = "Host=localhost;Database=PayDB;Username=x;Password=x"
dotnet ef migrations add TenMigrationMoi --project src/PaymentService/Infrastructure --startup-project src/PaymentService/Api
docker compose up -d --build payment-service
```

Nhớ **commit thư mục `Migrations/`**. Thiếu nó thì người khác không có bảng
và request sẽ lỗi 500.

### Chạy unit test (cần .NET 10 SDK)

```powershell
dotnet test tests/SwiftRide.PaymentService.Tests
```

### Lỗi thường gặp

**`required variable POSTGRES_USER is missing`**
Chưa có `.env`. Chạy `Copy-Item .env.example .env`.

**Postman không kết nối được `127.0.0.1:5285`**
Chưa mở cổng. Nếu cột PORTS của `docker compose ps` chỉ có `8080/tcp`, làm lại bước 2.

**Cổng 5285 đã bị chương trình khác dùng**
Đổi thành `127.0.0.1:5286:8080` trong file override và dùng cổng 5286 trong Postman.

**`500 Internal Server Error`**
Nội dung lỗi bị giấu cố ý. Chạy `docker compose logs payment-service --tail 80`
và tìm dòng bắt đầu bằng `fail:`.

**`password authentication failed`**
Đã đổi mật khẩu trong `.env` sau khi volume được tạo.
Chạy `docker compose down -v` rồi chạy lại.

**Build lỗi `'Enums' does not exist in the namespace`**
File thiếu dòng `namespace`. Mỗi file trong `Domain/Enums` và `Application/Exceptions`
phải khai báo namespace tương ứng.

**`null value in column "Amount" of relation "ledger_entries"`**
Thiếu bản sao `Money`. `Payment.Post` phải dùng `Amount.Copy()` cho từng dòng ledger.

---

## 3. Endpoint và hướng dẫn test bằng Postman

### 3.1 Danh sách endpoint

Địa chỉ gốc khi test: `http://127.0.0.1:5285`

**GET /health**
Kiểm tra service sống. Trả 200. Xử lý ở `Api/Program.cs`.

**POST /payments**
Tạo payment và thu tiền. Trả 201 (mới) hoặc 200 (gửi lại cùng key).
Xử lý: `PaymentEndpoints.cs` → `ProcessPaymentUseCase.cs`.

**GET /payments/{id}**
Xem payment kèm sổ cái. Trả 200.
Xử lý: `PaymentEndpoints.cs` → `GetPaymentUseCase.cs`.

**POST /payments/{id}/refund**
Hoàn tiền. Trả 200.
Xử lý: `PaymentEndpoints.cs` → `RefundPaymentUseCase.cs`.

### 3.2 Quy tắc cần nhớ

- Header **Idempotency-Key** (bắt buộc với `POST /payments`) phải đúng dạng
  `trip-<tripId>-payment`, và `<tripId>` trùng với `tripId` trong body.
- `riderId` và `driverId` phải khác nhau, không được rỗng.
- Tiền chỉ nhận **VND**, là **số nguyên dương** (`85000` được; `1000.5` và `0` thì không).
- `method` là `"Card"` hoặc `"Wallet"`.
- `paymentToken` là token giả để chọn kịch bản. Service không lưu token.
- Mỗi chuyến cần `tripId` riêng. Muốn chạy lại một ca, đổi một chữ số trong `tripId`
  (ở cả header lẫn body).

Token giả:

| Cổng   | Thành công    | Bị từ chối              | Lỗi tạm (503)      |
| ------ | ------------- | ----------------------- | ------------------ |
| Card   | tok_card_ok   | tok_card_declined       | tok_card_timeout   |
| Wallet | tok_wallet_ok | tok_wallet_insufficient | tok_wallet_timeout |

### 3.3 Cài đặt Postman (không cần biến)

1. Bấm **New → HTTP Request**.
2. Chọn method và dán URL (ví dụ `POST` + `http://127.0.0.1:5285/payments`).
3. Với `POST /payments`:
   - Tab **Headers**: thêm `Idempotency-Key` với giá trị theo từng ca.
   - Tab **Body**: chọn **raw**, bên phải chọn **JSON**, rồi dán body.
4. Bấm **Send**. Xem mã trạng thái (góc phải, dưới ô response) và nội dung trả về.

Trước khi test, gọi `GET http://127.0.0.1:5285/health` và phải nhận **200**.

### 3.4 Luồng chính (chạy theo thứ tự)

#### Ca 1. Thanh toán thành công

`POST http://127.0.0.1:5285/payments`

Header: `Idempotency-Key: trip-11111111-1111-1111-1111-111111111111-payment`

```json
{
  "tripId": "11111111-1111-1111-1111-111111111111",
  "riderId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "driverId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "amount": 85000,
  "currency": "VND",
  "method": "Card",
  "paymentToken": "tok_card_ok"
}
```

Mong đợi: **201 Created**. Response dạng:

```json
{
  "id": "0199f3a2-....",
  "tripId": "11111111-1111-1111-1111-111111111111",
  "amount": 85000,
  "currency": "VND",
  "method": "Card",
  "status": "Settled",
  "ledgerEntries": [
    {
      "account": "rider:aaaaaaaa-...",
      "direction": "Debit",
      "type": "Charge",
      "amount": 85000
    },
    {
      "account": "driver:bbbbbbbb-...",
      "direction": "Credit",
      "type": "Charge",
      "amount": 85000
    }
  ]
}
```

**Copy giá trị `id`** (mã payment do server sinh ra). Các ca 4 đến 7 gọi nó là `<PAYMENT_ID>`.

#### Ca 2. Gửi lại y hệt (kiểm tra không thu tiền 2 lần)

Bấm **Send** lại ở request Ca 1, không đổi gì.
Mong đợi: **200 OK**, cùng `id` như Ca 1. Trong DB vẫn chỉ có 1 payment.

#### Ca 3. Cùng key nhưng đổi số tiền

Giữ nguyên URL, header, body của Ca 1, chỉ sửa `"amount": 99000`.
Mong đợi: **409 Conflict**.
`detail`: "The idempotency key was already used with a different request payload."

#### Ca 4. Xem chi tiết

`GET http://127.0.0.1:5285/payments/<PAYMENT_ID>`
Mong đợi: **200**, đủ thông tin và 2 dòng ledger.

#### Ca 5. Hoàn tiền thiếu lý do

`POST http://127.0.0.1:5285/payments/<PAYMENT_ID>/refund`, body `{}`
Mong đợi: **400**.
`detail`: "Refund reason is required (max 500 characters)."

#### Ca 6. Hoàn tiền

Cùng URL, body:

```json
{ "reason": "khach huy" }
```

Mong đợi: **200**, `status: "Refunded"`, `ledgerEntries` có **4** dòng
(2 dòng thu và 2 dòng đảo `type: "Refund"`).

#### Ca 7. Hoàn tiền lần 2

Gửi lại y hệt Ca 6.
Mong đợi: **200**, kết quả cũ, **không** thêm dòng ledger (không hoàn đôi).

### 3.5 Các ca lỗi

Với `POST /payments`, **copy request Ca 1** rồi đổi đúng những chỗ ghi dưới đây.
Nhớ đổi `tripId` ở cả header lẫn body.

#### Ca 8. Thẻ bị từ chối

- Đổi: `tripId` = `22222222-2222-2222-2222-222222222222`, `paymentToken` = `tok_card_declined`
- Mong đợi: **201**, `status: "Failed"`, có `failureReason`, `ledgerEntries: []`

#### Ca 9. Hoàn tiền payment đã Failed

- Gọi: `POST /payments/<id của Ca 8>/refund` với body `{ "reason": "thu" }`
- Mong đợi: **409**, "Payment must be Settled but is Failed."

#### Ca 10. Cổng thanh toán lỗi tạm

- Đổi: `tripId` = `33333333-3333-3333-3333-333333333333`, `paymentToken` = `tok_card_timeout`
- Mong đợi: **503**. DB **không** có payment nào cho chuyến này.

#### Ca 11. Idempotency-Key sai

- Đổi: `tripId` = `44444444-4444-4444-4444-444444444444`, header `Idempotency-Key` = `abc`
- Mong đợi: **400**, "Idempotency key must be 'trip-...-payment'."

#### Ca 12. Không tìm thấy payment

- Gọi: `GET /payments/99999999-9999-9999-9999-999999999999`
- Mong đợi: **404**

#### Ca 13. Thanh toán bằng ví

- Đổi: `tripId` = `55555555-5555-5555-5555-555555555555`, `method` = `Wallet`,
  `paymentToken` = `tok_wallet_ok`
- Mong đợi: **201**, `status: "Settled"`

#### Ca 14. Số tiền lẻ

- Đổi: `tripId` = `66666666-6666-6666-6666-666666666666`, `amount` = `1000.5`
- Mong đợi: **400**, "VND amounts must be whole numbers."

#### Ca 15. Số tiền bằng 0

- Đổi: `tripId` = `77777777-7777-7777-7777-777777777777`, `amount` = `0`
- Mong đợi: **400**, "Amount must be greater than zero."

#### Ca 16. Sai loại tiền

- Đổi: `tripId` = `88888888-8888-8888-8888-888888888888`, `currency` = `USD`
- Mong đợi: **400**, "Only VND is supported."

#### Ca 17. Thiếu token

- Đổi: `tripId` = `99999999-9999-9999-9999-999999999999`, xóa dòng `paymentToken`
- Mong đợi: **400**, "PaymentToken is required."

#### Ca 18. Rider trùng driver

- Đổi: `tripId` = `12121212-1212-1212-1212-121212121212`,
  `driverId` = `aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`
- Mong đợi: **400**, "Rider and driver must be different users."

### 3.6 Định dạng lỗi và mã trạng thái

Lỗi trả về theo dạng chuẩn:

```json
{
  "type": "...",
  "title": "Conflict",
  "status": 409,
  "detail": "...",
  "traceId": "00-..."
}
```

Ánh xạ lỗi (file `Api/GlobalExceptionHandler.cs`):

- **400**: vi phạm quy tắc nghiệp vụ.
- **404**: không có payment.
- **409**: sai trạng thái, key trùng nhưng khác payload, hoặc 2 request sửa cùng lúc.
- **503**: cổng thanh toán lỗi tạm hoặc quá thời gian (Trip được phép retry cùng key).
- **500**: lỗi không lường trước (xem log container để biết nguyên nhân).

### 3.7 Kiểm tra kết quả trong DB

Sau Ca 1 đến Ca 7, vào psql (xem mục 2) và chạy:

```sql
SELECT "Id","Status","Amount" FROM payments;
SELECT "Direction", SUM("Amount") FROM ledger_entries GROUP BY "Direction";
```

- Mỗi payment `Settled` có đúng 2 dòng ledger; tổng Nợ bằng tổng Có (hai số ở câu lệnh
  thứ hai phải bằng nhau).
- Payment `Refunded` có 4 dòng.
- Payment `Failed` (Ca 8) không có dòng ledger.
- Ca 10 (503) không tạo payment nào.

> Lưu ý: nếu một payment đã lưu `Failed`, gọi lại cùng key sẽ luôn nhận lại kết quả
> `Failed` (không thử lại được bằng token khác). Cần bàn với nhóm nếu đề muốn cho thử lại.
