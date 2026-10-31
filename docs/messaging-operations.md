# Hướng Dẫn Vận Hành Hệ Thống Messaging & Xử Lý Sự Cố (RabbitMQ & MassTransit) — SwiftRide

Tài liệu này hướng dẫn chi tiết quy trình vận hành, giám sát hàng đợi, xử lý poison message, replay thông điệp và khắc phục sự cố cho kiến trúc event-driven trong hệ thống SwiftRide (.NET 10, RabbitMQ 4.x, MassTransit 8.5.8).

---

## 1. Tổng Quan Kiến Trúc Topology & Hàng Đợi (Queues)

Hệ thống SwiftRide áp dụng kiến trúc Event-Driven giữa hai service nghiệp vụ chính: **TripService** và **PaymentService**.

Tất cả các exchange, queue bền vững (durable) và routing key được khởi tạo tự động thông qua cấu hình `infra/rabbitmq/definitions.json` và cấu hình receive endpoint trong code:

| Queue Chính | Hàng Đợi Lỗi (DLQ / Error Queue) | Consumer / Service Xử Lý | Event Tương Ứng |
|---|---|---|---|
| `payment-ride-completed` | `payment-ride-completed_error` | `RideCompletedConsumer` (PaymentService) | `RideCompletedV1` |
| `payment-compensation` | `payment-compensation_error` | `PaymentCompensationConsumer` (PaymentService) | `PaymentCompensationRequestedV1` |
| `trip-payment-settled` | `trip-payment-settled_error` | `PaymentSettledConsumer` (TripService) | `PaymentSettledV1` |
| `trip-payment-failed` | `trip-payment-failed_error` | `PaymentFailedConsumer` (TripService) | `PaymentFailedV1` |
| `trip-payment-refunded` | `trip-payment-refunded_error` | `PaymentRefundedConsumer` (TripService) | `PaymentRefundedV1` |
| `trip-payment-compensation-failed` | `trip-payment-compensation-failed_error` | `PaymentCompensationFailedConsumer` (TripService) | `PaymentCompensationFailedV1` |

Mỗi hàng đợi được gán kèm tự động một hàng đợi phụ với hậu tố `_error` (Dead Letter Queue do MassTransit quản lý). Khi một thông điệp gặp lỗi không thể khắc phục hoặc vượt quá hạn mức retry, MassTransit sẽ chuyển thông điệp sang hàng đợi `_error` mà không bao giờ âm thầm nuốt lỗi (silently drop).

---

## 2. Chính Sách Retry Có Giới Hạn (Bounded Retry) & Concurrency

Chính sách retry được đóng gói trong `BoundedMessageRetryPolicy` ở mỗi service:

```text
[Message Arrives]
       │
       ▼
 [Try Consume] ───────────────► [Success] ──► Ack & Commit Inbox
       │ (Exception)
       ├────────────────────────────────────────┐
       ▼ (Permanent Exception:                  ▼ (Transient Exception:
          DomainException, FormatException,        DB lock, Timeout, Network)
          ArgumentException, JsonException)     [Attempt < 4?]
       │                                        ├── Yes ──► Exponential Delay
       │ (Không retry)                          │           (30s, 60s, 120s)
       ▼                                        └── No ──► Retries Exhausted
[Move to <queue>_error] ◄───────────────────────────────────┘
(Kèm headers MT-Fault-Message, MT-Fault-StackTrace)
```

### 2.1. Cấu hình Production (Mặc định)
- **Số lần retry tối đa (`RetryCount`):** 3 lần sau lần thử đầu tiên (tổng cộng 4 lần thực thi).
- **Khoảng thời gian chờ (`Exponential Backoff`):**
  - Min Interval: 30 giây
  - Max Interval: 120 giây
  - Interval Delta: 30 giây
  - Tổng thời gian chờ tối đa: $\le 300$ giây.
- **Giới hạn đồng thời (`Concurrency & Prefetch`):**
  - `ConcurrentMessageLimit = 2`: Giới hạn tối đa 2 message được xử lý đồng thời trên mỗi worker instance để tránh cạn kiệt connection pool cơ sở dữ liệu.
  - `PrefetchCount = 4`: Lấy trước tối đa 4 message từ broker.

### 2.2. Phân loại ngoại lệ (Fault Classification)
- **Lỗi vĩnh viễn (Permanent / Non-retryable):**
  - Các ngoại lệ quy tắc nghiệp vụ: `TripDomainException`, `PaymentDomainException`, `InvalidPaymentStateException`.
  - Các lỗi định dạng dữ liệu đầu vào: `ArgumentException`, `FormatException`, `System.Text.Json.JsonException`.
  - **Hành vi:** Được khai báo trong danh sách `r.Ignore<TException>()`. Khi xảy ra, consumer **không thực hiện retry** mà chuyển ngay lập tức sang hàng đợi `_error` ở lần thử đầu tiên để tiết kiệm tài nguyên và không chặn consumer slot.
- **Lỗi tạm thời (Transient / Retryable):**
  - Ngoại lệ kết nối cơ sở dữ liệu gián đoạn (`NpgsqlException`, `SqlException`), timeout mạng, deadlock hoặc optimistic concurrency conflict.
  - **Hành vi:** Tự động retry theo lũy thừa với thời gian giãn cách tăng dần.

### 2.3. Cấu hình kiểm thử nhanh (`FastRetryForTests`)
Khi chạy test tự động, thiết lập biến môi trường hoặc cấu hình:
```json
"RabbitMQ": {
  "FastRetryForTests": "true"
}
```
Khi bật cờ này, các khoảng chờ được rút ngắn xuống `50ms, 100ms, 150ms`, giúp bộ kiểm thử hoàn tất trong vòng vài trăm mili-giây mà không phải chờ đợi hàng phút.

---

## 3. Quy Trình Vận Hành & Khắc Phục Sự Cố

### 3.1. Kịch bản 1: Broker RabbitMQ bị gián đoạn (Broker Down)
- **Cơ chế bảo vệ (Transactional Outbox):**
  - Khi rider dropoff hoặc payment settle, sự kiện tích hợp **không gửi trực tiếp qua mạng** mà được ghi nguyên tử cùng trạng thái nghiệp vụ vào bảng `IntegrationOutbox` (SQL Server của TripService hoặc Supabase PostgreSQL `paydb.integration_outbox` của PaymentService) trong cùng một local transaction.
  - Background publisher (`TripOutboxPublisher`, `PaymentOutboxPublisher`) định kỳ quét bảng outbox với cơ chế distributed lease (30s) và owner fencing.
  - Nếu RabbitMQ bị sập hoặc không thể kết nối, publisher sẽ ghi log cảnh báo và lùi bước (backoff) có jitter, giữ nguyên dữ liệu trong outbox.
  - **Khắc phục:** Khởi động lại container RabbitMQ (`docker compose --profile messaging up -d rabbitmq`). Khi broker hoạt động trở lại, publisher tự động claim lease và đẩy tiếp các sự kiện chưa gửi. Không có thông điệp nào bị mất.

### 3.2. Kịch bản 2: Poison Message trong Hàng Đợi Lỗi (`_error`)
Khi một message bị lỗi định dạng hoặc lỗi logic không thể xử lý, MassTransit sẽ di chuyển message đó vào hàng đợi `<endpoint>_error`.

#### Bước 1: Kiểm tra danh sách hàng đợi lỗi
Truy cập RabbitMQ Management Web UI tại `http://localhost:15672` (tài khoản operator) hoặc dùng lệnh `rabbitmqadmin`:
```bash
docker compose --profile messaging exec rabbitmq rabbitmqctl list_queues name messages messages_ready | grep _error
```

#### Bước 2: Đọc thông tin lỗi chi tiết
Mở message trong hàng đợi lỗi qua UI hoặc CLI. MassTransit tự động đính kèm các message header chuẩn đoán:
- `MT-Fault-Message`: Nội dung thông báo lỗi nguyên nhân gốc (e.g., `Payment not found for trip ...`).
- `MT-Fault-StackTrace`: Toàn bộ stack trace tại điểm phát sinh lỗi.
- `MT-Host-MachineName`, `MT-Host-ProcessName`: Máy chủ và tiến trình gây lỗi.
- `MT-Reason`: Chuỗi `fault`.

#### Bước 3: Khắc phục lỗi gốc
- Nếu lỗi do bug mã nguồn: Triển khai bản vá (hotfix) lên service tương ứng.
- Nếu lỗi do dữ liệu ngoại lai hoặc phụ thuộc bên thứ ba (e.g., mock token sai cấu hình): Điều chỉnh cấu hình hoặc cập nhật dữ liệu liên quan.

#### Bước 4: Replay thông điệp (Chuyển ngược lại queue chính)
Sử dụng tính năng **Shovel** của RabbitMQ Management UI hoặc công cụ dòng lệnh:
1. Trong RabbitMQ Management UI:
   - Vào tab **Admin** $\rightarrow$ **Shovel Management**.
   - Tạo Dynamic Shovel:
     - Source queue: `payment-ride-completed_error`
     - Destination queue: `payment-ride-completed`
     - Delete after: `queue-length`
2. Hoặc sử dụng tính năng "Move messages" tích hợp sẵn trong RabbitMQ UI trên giao diện hàng đợi `_error`:
   - Chọn hàng đợi lỗi $\rightarrow$ mục **Move messages** $\rightarrow$ nhập đích đến `payment-ride-completed` $\rightarrow$ bấm **Move Messages**.
3. **Bảo toàn định danh (`MessageId`):**
   - Message được giữ nguyên toàn bộ payload, header và `MessageId` ban đầu.
   - Consumer khi nhận lại message sẽ kiểm tra bảng `IntegrationInbox`. Nếu message đã từng được xử lý thành công trước đó, cơ chế deduplication sẽ bỏ qua an toàn mà không thực hiện lại các thao tác tài chính hay ghi thêm sổ cái.
   - Nếu message chưa từng được xử lý (trước đó bị crash hoặc fail), consumer sẽ hoàn tất xử lý bình thường.

---

### 3.3. Kịch bản 3: Drain Hàng Đợi Trước Khi Bảo Trì
Trước khi bảo trì hoặc nâng cấp hệ thống:
1. Dừng nhận yêu cầu mới tại Gateway (hoặc chuyển sang chế độ bảo trì).
2. Chờ cho các message trong các hàng đợi chính (`payment-ride-completed`, `trip-payment-settled`, etc.) giảm về `0`.
3. Kiểm tra các hàng đợi lỗi: Nếu còn message trong hàng đợi `_error`, xuất file backup trước khi tiến hành nâng cấp.

---

### 3.4. Kịch bản 4: Chuyển Đổi (Rollback) Transport Thanh Toán
Hệ thống SwiftRide hỗ trợ snapshot cơ chế thanh toán độc lập trên từng chuyến đi qua thuộc tính `PaymentTransport` (`Rest` hoặc `Events`):
- Khi cấu hình `TripLifecycle:PaymentTransport = "Rest"`:
  - Các chuyến đi mới tạo sẽ sử dụng cơ chế REST đồng bộ (TripService gọi `IPaymentClient` tới PaymentService).
- Khi cấu hình `TripLifecycle:PaymentTransport = "Events"`:
  - Các chuyến đi mới tạo sẽ sử dụng cơ chế Event-Driven (TripService phát `RideCompletedV1`, PaymentService lắng nghe và phản hồi bằng `PaymentSettledV1`).
- **An toàn khi rollback:**
  - Mỗi chuyến đi lưu snapshot cứng phương thức vận chuyển ngay tại thời điểm tạo (`TripRecord.PaymentTransport`).
  - Worker `PendingPaymentWorker` chỉ claim và gọi REST cho các chuyến đi có `PaymentTransport != "Events"`.
  - Nhờ vậy, việc thay đổi cấu hình mặc định trong file cấu hình không bao giờ gây xung đột, gọi kép hay tranh chấp đường truyền trên các chuyến đi đang chạy dở dang.

---

## 4. An Toàn Thông Tin & Phân Quyền Vận Hành (Security)

1. **Phân quyền truy cập Broker:**
   - Cổng quản trị RabbitMQ (15672) và cổng giao thức AMQP (5672) chỉ được mở nội bộ cho đội ngũ DevOps/Operator và các container service trong cùng Docker network.
   - Tuyệt đối không expose cổng RabbitMQ ra Internet công cộng.
   - Client và ứng dụng di động/web của Rider/Driver tuyệt đối không được cấp quyền kết nối trực tiếp vào broker; mọi luồng người dùng bắt buộc phải đi qua API Gateway.
2. **Bảo mật dữ liệu tài chính trong thông điệp:**
   - Các contract sự kiện tích hợp (`src/SwiftRide.Contracts/V1/`) tuyệt đối không chứa thông tin thẻ ngân hàng thật, mã CVV hay mật khẩu. Chỉ truyền mã token thanh toán giả lập (`mock payment token`).
   - Các log lỗi đính kèm trong header `_error` không ghi thông tin bí mật hay dữ liệu cá nhân nhạy cảm.
3. **Cấu hình thông tin đăng nhập:**
   - Tài khoản `RABBITMQ_USER` và mật khẩu `RABBITMQ_PASSWORD` bắt buộc phải được nạp qua biến môi trường hoặc Secret Manager trong môi trường Production; không lưu trữ mật khẩu mặc định `guest/guest` trên hệ thống thật.
