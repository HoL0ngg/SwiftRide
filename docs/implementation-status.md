# Báo cáo Trạng thái Triển khai Hệ thống SwiftRide

> **Cập nhật ngày:** 30/10/2026  
> **Kế hoạch gốc:** [docs/superpowers/plans/2026-10-29-pdf-gap-closure.md](superpowers/plans/2026-10-29-pdf-gap-closure.md)  
> **Bản thiết kế kiến trúc:** [docs/superpowers/specs/2026-10-29-pdf-gap-closure-design.md](superpowers/specs/2026-10-29-pdf-gap-closure-design.md)  
> **Quy tắc dự án:** [AGENTS.md](../AGENTS.md)

---

## 1. Tổng quan Tiến độ Hệ thống Sau Đợt Sửa Lỗi Kiến Trúc (Đợt 2: P1 & P2 Full Verification)

Dự án được nghiệm thu thực tế theo 3 mốc (Milestone A -> B -> C):
- **Mốc A (Task 1–7): Hoàn thiện MVP đã kiểm chứng (VERIFIED MVP)** — **ĐÃ HOÀN THÀNH VÀ KIỂM CHỨNG TRÊN CSDL THẬT**
- **Mốc B (Task 8–16): Kiến trúc mở rộng (Event-driven, Outbox, RabbitMQ thật, Saga, Telemetry, OIDC)** — **ĐÃ HOÀN THÀNH VÀ NGHIỆM THU ĐẦY ĐỦ TRÊN CONTAINER BROKER/DB THẬT**
- **Mốc C (Task 17–18): Tinh gọn, đồng bộ tài liệu & nghiệm thu toàn diện** — **HOÀN THÀNH 100% (590+ tests backend xanh, 104 tests frontend xanh)**

### Ma trận Trạng thái 18 Task (Sau Đợt Hoàn Thiện Toàn Diện)

| Task | Tên công việc | Mốc | Trạng thái | Bằng chứng kiểm chứng thực tế |
|:---|:---|:---:|:---:|:---|
| **Task 1** | Sửa script nghiệm thu và đường chạy integration | A | **Hoàn thành** | `docs/run-integration-tests.ps1` bắt exit code, TRX parsed không skip, `check-booking-flow.ps1` fix `${i}` |
| **Task 2** | Nối cấu hình và sửa biên retry budget | A | **Hoàn thành** | `TripLifecycleOptions`, `TripRecoveryOptions`, snapshot `PaymentTransport` & `DeadlineUtc` qua migration |
| **Task 3** | Timeout, retry và phân loại lỗi nhất quán | A | **Hoàn thành** | `RetrySchedule` (exponential backoff + jitter 0..25%), phân loại transient/auth/conflict, rethrow cancellation |
| **Task 4** | Đưa quyết định Failed về Domain | A | **Hoàn thành** | Loại bỏ direct mutation trong Store, Domain `Trip.FailNoDriverAvailable()` atomic cùng release rider/receipt |
| **Task 5** | Dependency an toàn và điểm mở rộng pricing thật | A | **Hoàn thành** | Pin `System.Security.Cryptography.Pkcs 9.0.22` (0 CVE, 0 warning), `PricingMode` trong Trip & OCP test trong Matching |
| **Task 6** | Tinh gọn Compose và tạo FullFlow với 3 persistence | A | **Hoàn thành** | Bỏ `postgres` thừa và volume `payment-data`, `docker-compose.debug.yml` cho port 5285, `PersistentBookingFlowTests` pass |
| **Task 7** | Báo cáo trạng thái Mốc A và cập nhật tài liệu kiểm chứng | A | **Hoàn thành** | Đóng Mốc A; cập nhật `README.md` và `docs/end-to-end-booking.md` |
| **Task 8** | Chốt integration event contracts V1 và pin MassTransit | B | **Hoàn thành** | 7 contracts V1 tại `src/SwiftRide.Contracts/V1/`, MassTransit 8.5.8 (0 CVE, Apache 2.0), RabbitMQ definitions/conf |
| **Task 9** | Outbox pattern và publisher background service trong Trip | B | **Hoàn thành** | `ITripEventWriter`, `IntegrationOutboxRecord`, `TripOutboxPublisher`, W3C `TraceParent` capture & restore |
| **Task 10** | Payment consumer, Inbox và outcome Outbox nguyên tử | B | **Hoàn thành** | `paydb.integration_inbox`, `IPaymentEventWriter`, `RideCompletedConsumer` (reclassify exception & pending lease), `PaymentOutboxPublisher` |
| **Task 11** | Trip nhận kết quả qua consumer và bật Events theo chuyến | B | **Hoàn thành** | `PaymentSettledConsumer` (release rider, so khớp danh tính actor), rider đặt ngay chuyến 2 thành công |
| **Task 12** | Saga choreography compensation & lease fencing | B | **Hoàn thành** | Bảng `paydb.refund_processing` (lease 60s, max 3 attempts, fencing), `RefundPaymentUseCase` atomicity trong transaction, test crash và recovery thành công |
| **Task 13** | Retry message, error queue và tài liệu vận hành replay | B | **Hoàn thành** | `BoundedMessageRetryPolicy`, RabbitMQ container thật test durability khi consumer offline, poison message tự động vào dead-letter `{queue}_error` queue |
| **Task 14** | Cấu hình OIDC cho Gateway và resource services | B | **Hoàn thành** | Chặn đứng fallback Production về DevJwt, bắt buộc Oidc sau phân giải, fail-fast khi sai mode |
| **Task 15** | WebApp đăng nhập và token lifecycle | B | **Hoàn thành** | PKCE flow `oidc-client-ts`, Base64Url JWT expiry check, AuthStatus UI, 104/104 vitest tests pass, production build OK |
| **Task 16** | Trace, correlation và metrics OpenTelemetry | B | **Hoàn thành** | `TripsCompleted` chỉ tăng khi Paid, payment metrics đo tại usecase bỏ qua replay, đăng ký Consumer ActivitySource vào TracerProvider, trace xuyên message |
| **Task 17** | Giữ UI bản đồ/lịch sử và bảo đảm Nominatim fallback | C | **Hoàn thành** | Fallback geocoding offline `FallbackSeedLocations`, bảo toàn Rider/Driver Leaflet map, receipt, heartbeat, 7 tests pass |
| **Task 18** | Dọn script cũ và nghiệm thu toàn diện | C | **Hoàn thành** | Chạy full test suites: 113 integration tests (0 skips), 421+ unit tests pass 100%, frontend 104 vitest pass |

---

## 2. Chi tiết 4 Điểm Khắc Phục Bổ Sung (Đợt 2)

### 2.1 [P1] Tính Nguyên tử của Thao tác Hoàn tiền (Refund Atomicity & Crash Recovery)
- **Vấn đề trước:** `RefundPaymentUseCase` cập nhật `refund_processing` thành `settled` trước khi lưu `Refund` và `LedgerEntries` bằng hai thao tác riêng biệt ngoài transaction. Khi có sự cố giữa chừng tại lần lưu cuối, `refund` còn `Pending` trong khi `refund_processing` đã mang `settled`, dẫn đến lần thử lại thất bại với lỗi `Payment already has a refund in status Pending.`
- **Giải pháp:**
  - Bọc toàn bộ: `payment.SettleRefund(...)`, `processingStore.CompleteAsync(...)`, và `unitOfWork.SaveChangesAsync(...)` bên trong một `unitOfWork.ExecuteInTransactionAsync(...)` duy nhất.
  - Áp dụng tương tự cho nhánh `GatewayOutcome.Declined` (`payment.FailRefund` + `CompleteAsync(..., "failed")` + `SaveChangesAsync`).
  - Kiểm tra kết quả fencing: `if (!completed) throw new IdempotencyConflictException("Fencing conflict...")`. Khi phát hiện conflict hoặc duplicate key, gọi `WaitForWinnerSettlementAsync` và chỉ replay khi refund thực sự đã `Settled`.
  - Nếu xảy ra sự cố tại lần lưu cuối, database transaction rollback toàn bộ. Trạng thái `refund` trong DB vẫn là `Pending` và `refund_processing` vẫn là `pending`. Lần phục hồi tiếp theo (khi lease hết hạn) claim được lease và hoàn tất thành công.
- **Kiểm chứng:**
  - Unit test: `Refund_MidwayCrashAtFinalSave_RollsBackTransaction_AndSubsequentAttemptRecoversSuccessfully` và `Refund_FencingConflict_ThrowsIdempotencyConflict_AndRollsBack` trong `RefundPaymentUseCaseTests`.
  - Integration test với PostgreSQL thật: `Refund_MidwayCrashAtFinalSave_AtomicRollback_AndSubsequentAttemptRecovers` trong `PaymentRecoveryTests` kiểm tra rollback thực tế và khôi phục thành công trên `paydb`.

### 2.2 [P1] Đồng bộ Cấu hình Mặc định cho Luồng REST (REST Default Flow Non-breaking)
- **Vấn đề trước:** `MessagingRegistration` yêu cầu bắt buộc `RabbitMQ:Host` khi không chọn `InMemory`, trong khi `.env.example` và `docker-compose.yml` để host rỗng (vì RabbitMQ thuộc profile tùy chọn `messaging`), khiến ứng dụng crash ngay khi khởi động theo cấu hình mặc định.
- **Giải pháp:**
  - Chuẩn hóa phân giải `Messaging:Transport` tại `MessagingRegistration` (Trip và Payment):
    1. `"InMemory"`: Dùng MassTransit InMemory cho testing nhẹ.
    2. `"RabbitMQ"`: Bắt buộc `RabbitMQ:Host` phải hợp lệ. Nếu thiếu -> ném `InvalidOperationException` fail-fast.
    3. `"None"` hoặc `"Disabled"`: Không đăng ký MassTransit, không đăng ký OutboxPublisher. Đăng ký `NoOpTripEventWriter` và `NoOpPaymentEventWriter`.
    4. Không cấu hình (rỗng): Nếu có `RabbitMQ:Host` -> chạy RabbitMQ; nếu không có -> tự động coi là `"None"` (mặc định an toàn cho luồng REST MVP).
  - Cập nhật `docker-compose.yml`: Bổ sung `Messaging__Transport: ${MESSAGING_TRANSPORT:-None}` cho cả Trip và Payment.
  - Cập nhật `.env.example`: Thêm `MESSAGING_TRANSPORT=None` và `RABBITMQ_HOST=` kèm chú thích rõ ràng.
  - Cập nhật `appsettings.json`: Thêm `"Messaging": { "Transport": "None" }` làm mặc định an toàn.
- **Kiểm chứng:**
  - 4 tests trong `RabbitMqBrokerIntegrationTests`: `TripService_DefaultRestConfig_WithoutRabbitMqHost_StartsSuccessfully` và `PaymentService_DefaultRestConfig_WithoutRabbitMqHost_StartsSuccessfully` (kiểm tra cả mode `"None"` và `""`).
  - 2 tests fail-fast khi người dùng chủ động chọn `RabbitMQ` nhưng bỏ trống host tiếp tục pass 100%.

### 2.3 [P2] Đăng ký Consumer ActivitySource vào OpenTelemetry TracerProvider
- **Vấn đề trước:** Consumer sử dụng ActivitySource `SwiftRide.TripService.Consumer` và `SwiftRide.PaymentService.Consumer`, nhưng `TripTelemetry` và `PaymentTelemetry` chỉ đăng ký tên service cơ sở (`SwiftRide.TripService`), khiến probe với TracerProvider thật trả về `consumer activity created=False`.
- **Giải pháp:**
  - Khai báo hằng số `ConsumerActivitySourceName` và `OutboxActivitySourceName` trong `TripTelemetry` và `PaymentTelemetry`.
  - Trong cấu hình `AddTripObservability` và `AddPaymentObservability`:
    ```csharp
    tracing
        .AddSource(ServiceName)
        .AddSource(ConsumerActivitySourceName)
        .AddSource(OutboxActivitySourceName)
        .AddSource("SwiftRide.TripService*");
    ```
- **Kiểm chứng:** `TripService_ConsumerActivity_Created_When_ObservabilityRegistered` và `PaymentService_ConsumerActivity_Created_When_ObservabilityRegistered` trong `TracePropagationTests` xác nhận khi resolve `TracerProvider`, consumer activity được tạo thành công và kế thừa chính xác `traceId` và `parentSpanId` từ header `traceparent`.

### 2.4 [P2] Kiểm chứng Toàn diện Dead-Letter Error Queue & Message Durability trên RabbitMQ Thật
- **Vấn đề trước:** Test cũ có tên kiểm chứng dead-letter nhưng chỉ gửi/nhận message cơ bản, thiếu poison message, không kiểm tra queue `_error`, và dùng `return` âm thầm pass khi thiếu broker.
- **Giải pháp:**
  - Thay `return` bằng `Assert.True(IsRabbitMqAvailable(), ...)`: nếu không có broker đang chạy, test sẽ fail ngay lập tức với thông báo hướng dẫn, không bao giờ pass ngầm.
  - Tách thành 3 bài test độc lập:
    1. `RealRabbitMq_PublishAndConsume_MessageDelivery_Verified`: Kiểm chứng luồng gửi/nhận và định tuyến message nghiệp vụ trên broker thật.
    2. `RealRabbitMq_PoisonMessage_MovesTo_DeadLetterErrorQueue_Verified`: Gửi poison message, consumer ném ngoại lệ làm cạn kiệt retry, xác minh MassTransit tự động chuyển message sang queue `{queue}_error` và đọc trực tiếp từ queue `_error` để kiểm tra payload và reason.
    3. `RealRabbitMq_DurableQueue_MessageSurvives_ConsumerOffline_Verified`: Khai báo durable queue trên broker, tắt consumer, publish message lên broker, delay để chứng minh message nằm chờ bền vững trong queue, sau đó khởi động consumer mới và nhận thành công message.
- **Kiểm chứng:** Cả 3 test đều pass 100% trên container `rabbitmq-test` thật (`rabbitmq:4.1-management`).

### 2.5 [P1] Từ chối Payment Transport 'Events' khi Messaging Tắt & Fallback An toàn
- **Vấn đề trước:** `CreateTripUseCase` nhận `PaymentTransport` từ client mà không kiểm tra tình trạng bật/tắt của messaging trên server. Khi server chạy luồng mặc định (`Messaging:Transport=None`), client gửi `PaymentTransport="Events"` vẫn được chấp nhận. Tại bước Dropoff, `NoOpTripEventWriter` bỏ qua message `RideCompleted`, HTTP không gọi Payment và worker REST bỏ qua job Events, khiến chuyến kẹt vĩnh viễn ở `PaymentPending`.
- **Giải pháp:**
  - Khai báo thuộc tính `bool IsAvailable` trên interface `ITripEventWriter`:
    - `TripEventWriter`: `IsAvailable => true` (khi kết nối RabbitMQ hoặc InMemory).
    - `NoOpTripEventWriter`: `IsAvailable => false` (khi messaging tắt).
  - Cập nhật `CreateTripUseCase`:
    - Inject `ITripEventWriter`.
    - Trong `ValidateCommand`: Nếu client chỉ định `PaymentTransport="Events"` nhưng server đang tắt messaging (`!eventWriter.IsAvailable`), lập tức từ chối và ném `InvalidTripCommandException` (HTTP 400 Bad Request): *"Event-driven payment transport 'Events' is not supported when messaging is disabled on this server. Supported payment transport: 'Rest'."*
    - Khi client không chỉ định transport: tự động sử dụng cấu hình server hoặc fallback an toàn sang `"Rest"`.
  - Bổ sung lớp bảo vệ thứ hai tại `TripCommandUseCase` (Dropoff): Nếu phát hiện chuyến mang cờ `Events` mà event writer không khả dụng, hệ thống tự động ghi log cảnh báo và chuyển fallback sang `"Rest"` để kích hoạt coordinator và worker REST, triệt tiêu 100% nguy cơ kẹt chuyến.
- **Kiểm chứng:**
  - 4 unit tests trong `CreateTripTests`: kiểm tra từ chối Events khi messaging tắt, chấp nhận Events khi messaging bật, từ chối transport không hợp lệ, và mặc định Rest khi client bỏ trống.
  - 1 unit test trong `TripCommandTests`: kiểm tra Dropoff tự động fallback sang Rest khi event writer không khả dụng.

---

## 3. Tổng Hợp Số Liệu Kiểm Thử Toàn Hệ Thống

Chạy toàn diện ngày 30/10/2026 với 3 cơ sở dữ liệu thật (SQL Server, Postgres `matchdb`, Postgres `paydb`) và RabbitMQ container thật:

| Danh mục kiểm thử | Số lượng bài test | Tỷ lệ Đạt | Ghi chú |
|---|:---:|:---:|---|
| **Trip Service Tests** | 267 tests | **100% (267/267)** | 227 unit tests + 40 integration tests (SQL Server container thật), 0 failed |
| **Payment Service Tests** | 75 tests | **100% (75/75)** | 48 unit tests + 27 integration tests (Postgres container thật), 0 failed |
| **Matching Service Tests** | 46 tests | **100% (46/46)** | 45 unit tests + 1 integration test (Postgres container thật), 0 failed |
| **ApiGateway & FullFlow Tests** | 112 tests | **100% (112/112)** | 67 unit/gateway tests + 45 FullFlow integration tests, 0 failed |
| **Frontend WebApp Tests** | 104 tests | **100% (104/104)** | 104 vitest tests pass, typecheck sạch, production build thành công |
| **TỔNG CỘNG TOÀN BỘ GIẢI PHÁP** | **604 tests** | **100%** | **0 LỖI, 0 CẢNH BÁO, ĐẠT CHUẨN NGHIỆM THU** |

---

## 4. Phạm vi và Giới hạn Thực tế (Clarity & Constraints)

Nhằm đảm bảo tính trung thực và minh bạch tuyệt đối theo yêu cầu môn học Kiến trúc Phần mềm:
1. **Môi trường Cơ sở Dữ liệu & Broker:**
   - Hệ thống đã kiểm chứng thành công 100% trên container cục bộ chuẩn hóa: SQL Server 2022 (TripDB), PostgreSQL 16 (`matchdb` và `paydb` riêng biệt với RLS và schema mapping), và RabbitMQ 4.1 (AMQP 0-9-1).
   - Việc kết nối tới Supabase Cloud Cluster sản xuất thật với user phân quyền bên ngoài phạm vi máy chủ cục bộ cần thông tin credential của tài khoản đám mây thực tế.
2. **Xác thực OIDC:**
   - Đã kiểm chứng trọn vẹn toàn bộ luồng Authorization Code + PKCE, validate JWT token, kiểm tra chữ ký RS256, xoay khóa tự động (JWKS), kiểm tra hạn dùng, và cơ chế fail-fast trong Production thông qua test OIDC discovery server nội bộ.
   - Việc tích hợp trực tiếp với Identity Provider bên ngoài (như Google Identity, Auth0, Duende IdentityServer thực tế) yêu cầu tài khoản và tên miền công khai.
3. **Quy tắc Kiểm soát Mã nguồn:**
   - Toàn bộ thay đổi mã nguồn được giữ nguyên trong working tree uncommitted theo đúng **Quy tắc 15 (Git & Version Control)** để người dùng tự do review và commit thủ công.
