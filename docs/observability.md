# Kiến trúc Giám sát & Đo lường — SwiftRide Observability

Tài liệu này mô tả chi tiết giải pháp **OpenTelemetry (Distributed Tracing, Metrics, Structured Logging)** và **Correlation ID Lifecycle** được triển khai trên toàn bộ hệ thống SwiftRide.

---

## 1. Nguyên tắc kiến trúc & Tiêu chuẩn

Hệ thống tuân thủ các quy tắc cốt lõi:
- **Chuẩn W3C Distributed Tracing (`traceparent`)**: Tự động liên kết các span giữa API Gateway, Trip Service, Matching Service và Payment Service.
- **Correlation ID chuẩn hóa (`X-Correlation-Id`)**: Hỗ trợ tra cứu nhanh trong log tập trung và kết nối xuyên suốt các tầng HTTP REST và bất đồng bộ (Outbox/RabbitMQ).
- **Khử trùng dữ liệu nhạy cảm (Data Redaction)**: Tuân thủ quy định bảo mật `AGENTS.md` (không bao giờ log JWT Bearer token thô, số thẻ, CVV hay payment token).
- **Chống tấn công Log Injection (CWE-117)**: Tự động loại bỏ ký tự ngắt dòng `\r`, `\n` và các ký tự đặc biệt khỏi header correlation ID.

---

## 2. Service Registry & Telemetry Sources

Mỗi microservice sở hữu một `ActivitySource` và `Meter` độc lập:

| Dịch vụ | Tên nguồn (`ActivitySource` & `Meter`) | Cổng nội bộ | Trách nhiệm giám sát |
|---|---|---|---|
| **API Gateway** | `SwiftRide.Gateway` | 5080 | Tiếp nhận request từ client, sinh/sanitize `X-Correlation-Id`, forward traceparent. |
| **Trip Service** | `SwiftRide.TripService` | 8080 (int) | Theo dõi vòng đời chuyến đi, đo lường số chuyến tạo, hoàn tất, thất bại. |
| **Matching & Pricing** | `SwiftRide.MatchingService` | 8080 (int) | Đo lường độ trễ (latency histogram) thuật toán tìm tài xế và tính giá. |
| **Payment Service** | `SwiftRide.PaymentService` | 8080 (int) | Đo lường thanh toán thành công, thất bại, bồi hoàn (refund). |

---

## 3. Vòng đời Correlation ID & Lan truyền vết (Propagation)

```
[ Client / WebApp ]
        │  (HTTP Request, optional X-Correlation-Id)
        ▼
[ YARP API Gateway ]
  ├─ 1. CorrelationSanitizer: kiểm tra độ dài (<= 128 chars), ký tự hợp lệ [a-zA-Z0-9_\-\.]
  │     Nếu rỗng hoặc chứa ký tự đặc biệt (CRLF/XSS) -> Tự sinh GUID an toàn mới.
  ├─ 2. Gán vào HttpContext.Items["CorrelationId"] và Response Header "X-Correlation-Id".
  ├─ 3. Gắn Activity Tag "correlation.id".
  └─ 4. Forward "X-Correlation-Id" và W3C "traceparent" sang backend services.
        │
        ├─────────────────────────────┬─────────────────────────────┐
        ▼                             ▼                             ▼
[ Trip Service ]            [ Matching Service ]          [ Payment Service ]
  ├─ CorrelationIdMiddleware   ├─ CorrelationIdMiddleware    ├─ CorrelationIdMiddleware
  └─ CorrelationPropagation    └─ Ghi log kèm scope          └─ Ghi log kèm scope
     Handler tự động inject
     vào HttpClient downstream
```

### Quy tắc làm sạch Correlation ID:
- **Độ dài tối đa**: 128 ký tự. Nếu chuỗi đầu vào dài hơn 128 ký tự, hệ thống cắt lấy 128 ký tự đầu.
- **Ký tự cho phép**: `[a-zA-Z0-9_\-\.]`.
- **Nếu chứa ký tự lạ (khoảng trắng, ký tự điều khiển, ngắt dòng CRLF, tag HTML)**: Lập tức bị thay thế bằng một GUID ngẫu nhiên (`Guid.NewGuid().ToString("D")`).

---

## 4. Danh mục Chỉ số Đo lường (Core Metrics)

Hệ thống cung cấp các metric nghiệp vụ chuẩn xác:

| Tên Metric | Loại | Đơn vị | Mô tả nghiệp vụ |
|---|---|---|---|
| `swiftride.trips.created` | Counter | `trips` | Tổng số chuyến đi được tạo thành công qua `POST /trips`. |
| `swiftride.trips.completed` | Counter | `trips` | Tổng số chuyến đi hoàn tất (khi tài xế trả khách thành công - Dropoff). |
| `swiftride.trips.failed` | Counter | `trips` | Tổng số chuyến đi bị hủy hoặc thất bại do không tìm thấy tài xế. |
| `swiftride.payments.settled` | Counter | `payments` | Tổng số giao dịch thanh toán thành công và ghi sổ kép (double-entry ledger). |
| `swiftride.payments.failed` | Counter | `payments` | Tổng số giao dịch thanh toán thất bại. |
| `swiftride.payments.refunded` | Counter | `payments` | Tổng số giao dịch bồi hoàn (compensation refund) được giải quyết. |
| `swiftride.matching.duration` | Histogram | `ms` | Thời gian thực thi thuật toán tìm kiếm tài xế gần nhất trong Matching Service. |

---

## 5. Bảo mật & Khử trùng Log (Sensitive Data Redaction)

Để bảo đảm an toàn dữ liệu và tuân thủ nguyên tắc bảo vệ quyền riêng tư:
1. **JWT Bearer Token**: Mọi chuỗi token `Bearer eyJ...` khi xuất hiện trong log hoặc exception context đều được chuyển đổi thành:
   ```text
   Authorization: Bearer [REDACTED]
   ```
2. **Thông tin thẻ & Thanh toán**: Các trường dữ liệu nhạy cảm bao gồm:
   - `cardNumber` / `pan`: thay bằng `"[REDACTED]"`
   - `cvv` / `cvc`: thay bằng `"[REDACTED]"`
   - `paymentToken`: thay bằng `"[REDACTED]"`
3. **Chống giả mạo log**: Ký tự `\r\n` bị triệt tiêu ở tầng `CorrelationSanitizer`, ngăn ngừa tấn công log injection giả mạo các dòng audit log của quản trị viên.

---

## 6. Hướng dẫn Khởi chạy & Kiểm chứng với Jaeger

Hệ thống đã chuẩn bị sẵn file `docker-compose.observability.yml` tích hợp Jaeger All-In-One:

### 6.1 Khởi động Stack kèm Jaeger
```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d
```

### 6.2 Truy cập giao diện Jaeger
Mở trình duyệt tại:
```
http://localhost:16686
```

### 6.3 Tìm kiếm Trace Chuyến đi
1. Chọn Service: `SwiftRide.Gateway` hoặc `SwiftRide.TripService`.
2. Trong ô **Tags**, nhập:
   ```text
   correlation.id=<Mã_Correlation_ID_của_bạn>
   ```
3. Nhấn **Find Traces** để xem toàn bộ sơ đồ phân tán:
   - Span 1: `Gateway: POST /trips`
   - Span 2: `TripService: POST /trips`
   - Span 3: `TripService -> MatchingService: POST /quote`
   - Span 4: `TripService -> MatchingService: POST /match`
   - Span 5: `TripService -> PaymentService: POST /payments` (nếu dùng REST) hoặc Outbox dispatch event.

---

## 7. Kiểm chứng tự động (Automated Verification)

Hệ thống được bảo vệ bởi bộ kiểm thử tự động tại `tests/SwiftRide.Api.Tests/Observability/`:
- `TracePropagationTests.cs`:
  - `Gateway_Echoes_Given_Valid_CorrelationId`: Kiểm tra Gateway phản hồi đúng Correlation ID của client.
  - `Gateway_Generates_Guid_When_CorrelationId_Missing`: Kiểm tra Gateway tự sinh GUID an toàn khi thiếu header.
  - `Gateway_Sanitizes_Excessive_Length_CorrelationId`: Kiểm tra cắt ngắn chuỗi vượt 128 ký tự.
  - `Gateway_Replaces_Invalid_Characters_In_CorrelationId`: Kiểm tra vô hiệu hóa ký tự đặc biệt / XSS.
  - `CorrelationPropagationHandler_Injects_CorrelationId_From_HttpContext`: Kiểm tra tự động chuyển tiếp Correlation ID qua HttpClient giữa các microservices.
- `SensitiveLoggingTests.cs`:
  - `Bearer_Token_Is_Redacted`: Kiểm tra ẩn Bearer token trong log.
  - `Card_Numbers_And_CVV_Are_Redacted`: Kiểm tra ẩn số thẻ và CVV.
  - `Payment_Token_Is_Redacted`: Kiểm tra ẩn payment token.
  - `CorrelationSanitizer_Neutralizes_CRLF_Log_Injection`: Kiểm tra phòng chống CRLF Log Injection.
  - `All_Services_Telemetry_Meters_And_Activities_Are_Registered`: Xác nhận toàn bộ Metrics và Tracing Sources được nạp thành công.
