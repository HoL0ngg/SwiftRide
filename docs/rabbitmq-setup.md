# Hướng dẫn cấu hình RabbitMQ và tích hợp vào Service/Docker — SwiftRide

Tài liệu này hướng dẫn **bật và vận hành** RabbitMQ (MassTransit 8.5.8) cho luồng thanh toán hướng sự kiện giữa **Trip Service** và **Payment Service**. RabbitMQ là phần **mở rộng** (extension) theo `AGENTS.md` mục 4: mặc định hệ thống vẫn chạy bằng REST (`Messaging__Transport=None`).

> Tài liệu liên quan: [messaging-contracts-v1.md](messaging-contracts-v1.md) (contract + topology), [messaging-operations.md](messaging-operations.md) (retry, DLQ, replay, xử lý sự cố), [architecture.md](architecture.md).
>
> Cập nhật 09/11/2026: đã khôi phục chuyến bị kẹt PaymentPending trên broker thật; sửa thứ tự tạo tài khoản/import topology và đối chiếu tên exchange với MassTransit.

---

## 1. Tổng quan

```text
Trip Service                                   Payment Service
  Dropoff ──► IntegrationOutbox (SQL Server)     RideCompletedConsumer ──► charge + ledger
              │ TripOutboxPublisher                      │
              ▼                                          ▼
        RabbitMQ  ── RideCompletedV1 ──────────►  PaymentOutbox (paydb.integration_outbox)
                  ◄── PaymentSettledV1 / PaymentFailedV1 ── PaymentOutboxPublisher
  PaymentSettledConsumer ──► Trip = Paid
```

- **Trip**, **Payment** và **Matching** dùng RabbitMQ. Gateway **không** kết nối broker (client chỉ đi qua HTTP/YARP).
- Matching chỉ có consumer và trả lời trực tiếp bằng `Publish` (không có outbox riêng); hai bên Trip ↔ Matching trao đổi 6 event, xem [messaging-operations.md](messaging-operations.md#35-luồng-trip--matching-qua-event-matchingtransport--events).
- Để Trip ↔ Matching đi qua RabbitMQ, đặt thêm `TRIP_LIFECYCLE_MATCHING_TRANSPORT=Events` (mặc định `Rest`) và `MESSAGING_TRANSPORT=RabbitMQ` cho **cả** `trip-service` và `matching-service`.
- Không thêm service thứ 4: outbox publisher là `BackgroundService` chạy trong chính service sở hữu (`TripOutboxPublisher`, `PaymentOutboxPublisher`).
- Consumer là **at-least-once** và idempotent qua bảng inbox; publish đi qua **Transactional Outbox** nên broker sập không mất event.

### Các file liên quan

| File | Vai trò |
|---|---|
| `docker-compose.yml` (service `rabbitmq`, profile `messaging`) | Chạy broker, mount config |
| `infra/rabbitmq/rabbitmq.conf` | Cấu hình broker; không import definitions khi node còn trống |
| `infra/rabbitmq/definitions.json` | Khai báo sẵn exchange / queue / binding |
| `src/SwiftRide.Contracts/V1/EventTopology.cs` | Hằng số tên exchange & queue |
| `src/TripService/Infrastructure/Messaging/MessagingRegistration.cs` | Đăng ký MassTransit cho Trip |
| `src/PaymentService/Infrastructure/Messaging/MessagingRegistration.cs` | Đăng ký MassTransit cho Payment |
| `src/MatchingService/Infrastructure/Messaging/MessagingRegistration.cs` | Đăng ký MassTransit cho Matching (2 consumer) |
| `.../Messaging/BoundedMessageRetryPolicy.cs` | Retry có giới hạn, concurrency, prefetch |

---

## 2. Bước 1 — Cấu hình biến môi trường

Tạo `.env` từ mẫu (nếu chưa có):

```powershell
Copy-Item .env.example .env
```

Chỉnh các biến RabbitMQ trong `.env`:

```dotenv
# Bật RabbitMQ (mặc định None = chỉ REST)
MESSAGING_TRANSPORT=RabbitMQ
# Khi chạy trong docker compose: tên service của broker
RABBITMQ_HOST=rabbitmq
RABBITMQ_USER=swiftride
RABBITMQ_PASSWORD=<mat-khau-rieng-cua-ban>
```

Lưu ý:

- `docker-compose.yml` bắt buộc `RABBITMQ_USER` và `RABBITMQ_PASSWORD` cho container broker (`${RABBITMQ_USER:?...}`); thiếu sẽ báo lỗi khi `docker compose config`.
- **Không** commit `.env`, **không** dùng `guest/guest` ngoài máy local (AGENTS.md mục 3: không hard-code secrets).
- Compose ánh xạ biến sang cấu hình .NET như sau (cùng cho `trip-service`, `payment-service` và `matching-service`):

| Biến `.env` | Cấu hình .NET | Ghi chú |
|---|---|---|
| `MESSAGING_TRANSPORT` | `Messaging:Transport` | `None` / `InMemory` / `RabbitMQ` |
| `RABBITMQ_HOST` | `RabbitMQ:Host` | Bắt buộc khi transport = `RabbitMQ` |
| `RABBITMQ_USER` | `RabbitMQ:Username` | |
| `RABBITMQ_PASSWORD` | `RabbitMQ:Password` | |
| *(không map sẵn)* | `RabbitMQ:Port` | Mặc định `5672` |
| `TRIP_LIFECYCLE_MATCHING_TRANSPORT` | `TripLifecycle:MatchingTransport` | Chỉ `trip-service`. `Rest` (mặc định) hoặc `Events` |

Quy tắc chọn transport trong code (cả hai service giống nhau):

1. `Messaging:Transport` có giá trị → dùng giá trị đó (`None`/`Disabled` → `NoOp` event writer, không có outbox/consumer).
2. Để trống: nếu có `RabbitMQ:Host` → `RabbitMQ`, ngược lại → `None`.
3. `RabbitMQ` mà thiếu `RabbitMQ:Host` → **ném lỗi khi khởi động** (fail fast). Giá trị lạ cũng ném lỗi.

---

## 3. Bước 2 — Khởi động RabbitMQ bằng Docker Compose

Service `rabbitmq` nằm trong profile `messaging` nên **phải** truyền `--profile messaging`:

```powershell
docker compose config                       # kiểm tra file hợp lệ và đủ biến
docker compose --profile messaging up --build -d
docker compose --profile messaging ps
```

Healthcheck chỉ đạt sau khi broker phản hồi, xác thực được tài khoản từ environment và đã có đủ 12 binding V1 tới queue sau khi import definitions. Lệnh import trả về trước khi tác vụ hoàn tất, nên không đánh dấu sẵn sàng chỉ dựa vào exit code của lệnh này. Trip/Payment/Matching chờ broker healthy khi profile `messaging` được bật; dependency tùy chọn giữ cho luồng REST chạy được khi không bật profile.

Không dùng `load_definitions` lúc boot: [RabbitMQ bỏ qua việc tạo default user khi node trống import definitions](https://www.rabbitmq.com/docs/definitions#import-on-boot-nuances), trong khi file của dự án không chứa credential. Healthcheck import topology một lần sau khi RabbitMQ tạo tài khoản từ environment; marker nằm trong `/tmp` của container.

Kiểm tra broker đã healthy và đã nạp definitions:

```powershell
docker compose --profile messaging exec rabbitmq rabbitmq-diagnostics -q ping
docker compose --profile messaging exec rabbitmq rabbitmqctl list_queues name messages
docker compose --profile messaging exec rabbitmq rabbitmqctl list_exchanges name type
```

Kết quả mong đợi: 12 queue (6 queue thanh toán: `payment-ride-completed`, `payment-compensation`, `trip-payment-settled`, `trip-payment-failed`, `trip-payment-refunded`, `trip-payment-compensation-failed`; 6 queue matching: `matching-quote-requested`, `matching-driver-match-requested`, `trip-quote-ready`, `trip-quote-failed`, `trip-driver-matched`, `trip-driver-match-failed`) và các exchange dạng `SwiftRide.Contracts.V1:RideCompletedV1`, đúng tên MassTransit publish.

### Mở cổng quản trị / AMQP (chỉ khi cần, chỉ cho máy local)

`docker-compose.yml` hiện publish cổng 5672 và 15672. Khi chỉ demo trên máy local, có thể giới hạn về loopback bằng `docker-compose.override.yml` **local** (không commit):

```yaml
services:
  rabbitmq:
    ports: !override
      - "127.0.0.1:5672:5672"     # AMQP
      - "127.0.0.1:15672:15672"   # Management UI
```

Sau đó mở `http://localhost:15672` và đăng nhập bằng `RABBITMQ_USER` / `RABBITMQ_PASSWORD`.

> Tránh `docker compose up` không có profile khi `MESSAGING_TRANSPORT=RabbitMQ`: trip/payment sẽ cố nối tới host `rabbitmq` không tồn tại. Bus của MassTransit sẽ liên tục retry kết nối; outbox vẫn giữ event (xem mục 7).

---

## 4. Bước 3 — Cấu hình Service (đã tích hợp sẵn trong code)

Hai `MessagingRegistration` đã được gọi trong `DependencyInjection.cs` của mỗi Infrastructure (`AddTripMessaging`, `AddPaymentMessaging`). Bạn **không cần sửa code** để bật RabbitMQ, chỉ cần cấu hình. Cấu trúc đăng ký:

```csharp
services.AddScoped<ITripEventWriter, TripEventWriter>();   // ghi event vào outbox
services.AddHostedService<TripOutboxPublisher>();          // đẩy outbox lên broker

services.AddMassTransit(x =>
{
    x.AddConsumer<PaymentSettledConsumer>();               // ...các consumer của service
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitHost, rabbitPort, "/", h => { h.Username(user); h.Password(pass); });

        cfg.ReceiveEndpoint(EventTopology.Queues.TripPaymentSettled, e =>
        {
            BoundedMessageRetryPolicy.ConfigureEndpoint(e, configuration); // retry + limit
            e.ConfigureConsumer<PaymentSettledConsumer>(context);
        });
        // ...
    });
});
```

Phân chia consumer:

| Service | Consumer | Queue | Event |
|---|---|---|---|
| Payment | `RideCompletedConsumer` | `payment-ride-completed` | `RideCompletedV1` |
| Payment | `PaymentCompensationConsumer` | `payment-compensation` | `PaymentCompensationRequestedV1` |
| Trip | `PaymentSettledConsumer` | `trip-payment-settled` | `PaymentSettledV1` |
| Trip | `PaymentFailedConsumer` | `trip-payment-failed` | `PaymentFailedV1` |
| Trip | `PaymentRefundedConsumer` | `trip-payment-refunded` | `PaymentRefundedV1` |
| Trip | `PaymentCompensationFailedConsumer` | `trip-payment-compensation-failed` | `PaymentCompensationFailedV1` |

### Tinh chỉnh retry/concurrency (tuỳ chọn)

Đọc trong `BoundedMessageRetryPolicy` từ section `RabbitMQ`; truyền vào container bằng biến `RabbitMQ__<Key>` (dấu `__` thay cho `:`):

| Key | Mặc định | Ý nghĩa |
|---|---|---|
| `RabbitMQ:RetryCount` | 3 | Số lần retry sau lần đầu |
| `RabbitMQ:RetryMinIntervalSeconds` | 30 | Backoff tối thiểu |
| `RabbitMQ:RetryMaxIntervalSeconds` | 120 | Backoff tối đa |
| `RabbitMQ:RetryDeltaSeconds` | 30 | Bước tăng |
| `RabbitMQ:ConcurrentMessageLimit` | 2 | Message xử lý đồng thời / endpoint |
| `RabbitMQ:PrefetchCount` | 4 | Prefetch |
| `RabbitMQ:FastRetryForTests` | false | `true` → backoff 50/100/150 ms (chỉ cho test) |

Các biến này chưa được map trong `docker-compose.yml`; nếu cần, thêm vào mục `environment` của service tương ứng, ví dụ `RabbitMQ__RetryCount: ${RABBITMQ_RETRY_COUNT:-3}`.

Lỗi nghiệp vụ/định dạng (`TripDomainException`, `ArgumentException`, `FormatException`, `JsonException`...) **không retry** và đi thẳng vào `<queue>_error`. Lỗi tạm thời thì retry theo backoff trên rồi mới vào `_error`. Chi tiết xem `messaging-operations.md` mục 2.

### Chạy service trực tiếp bằng `dotnet run` (không qua compose)

Cần broker có publish cổng 5672 (xem override ở mục 3). Dùng user-secrets hoặc biến môi trường:

```powershell
$env:Messaging__Transport = "RabbitMQ"
$env:RabbitMQ__Host       = "localhost"
$env:RabbitMQ__Username   = "swiftride"
$env:RabbitMQ__Password   = "<mat-khau>"
dotnet run --project src/PaymentService/Api
```

(Làm tương tự cho `src/TripService/Api`; mỗi service cần cả connection string DB của nó.)

---

## 5. Bước 4 — Chọn transport thanh toán cho chuyến đi

Bật RabbitMQ **chưa** đồng nghĩa mọi chuyến đi dùng event. Mỗi chuyến lưu snapshot `PaymentTransport` lúc tạo (`Rest` hoặc `Events`):

- Mặc định theo cấu hình `TripLifecycle:PaymentTransport` (hiện `appsettings.json` của Trip là `"Rest"`).
- Có thể chỉ định theo từng chuyến qua trường `PaymentTransport` trong body `POST /trips` (`"Events"` hoặc `"Rest"`).
- `PendingPaymentWorker` chỉ gọi REST cho chuyến có transport khác `Events` → hai cơ chế không tranh nhau, không thu tiền kép.

Để mặc định cả hệ thống dùng event, đặt `TripLifecycle__PaymentTransport=Events` cho `trip-service`.

> **Lưu ý khớp docs/code:** `.env.example` có `TRIP_LIFECYCLE__PAYMENT_TRANSPORT=Rest`, nhưng `docker-compose.yml` hiện **không** truyền biến này vào `trip-service`, nên đổi nó trong `.env` chưa có tác dụng. Đề xuất thay đổi nhỏ nhất: thêm vào `environment` của `trip-service`:
>
> ```yaml
> TripLifecycle__PaymentTransport: ${TRIP_LIFECYCLE__PAYMENT_TRANSPORT:-Rest}
> ```
>
> Tôi chưa sửa `docker-compose.yml` vì file đang có thay đổi chưa commit của bạn.

---

## 6. Bước 5 — Kiểm tra luồng end-to-end qua RabbitMQ

1. Khởi động như mục 3 với `MESSAGING_TRANSPORT=RabbitMQ`, `RABBITMQ_HOST=rabbitmq`.
2. Xem log hai service có dòng kết nối bus thành công và không có lỗi `RabbitMQ:Host is missing`:
   ```powershell
   docker compose --profile messaging logs trip-service payment-service --tail 100
   ```
3. Chạy smoke test qua Gateway, yêu cầu transport event:
   ```powershell
   powershell -ExecutionPolicy Bypass -File docs/check-booking-flow.ps1 `
       -GatewayUrl "http://localhost:5080" -ExpectedTransport Events
   ```
   (Xem tham số thực tế trong `docs/check-booking-flow.ps1`; kịch bản `ExpectedTransport Rest` được mô tả trong `end-to-end-booking.md`. Để chuyến đi dùng `Events`, cần đặt mặc định như mục 5 hoặc gửi `PaymentTransport` khi tạo chuyến.)
4. Sau dropoff, kỳ vọng: `RideCompletedV1` → Payment charge → `PaymentSettledV1` → trip `Paid`; ledger có đúng 2 dòng cân bằng; các queue chính về `0`, các `_error` vẫn `0`:
   ```powershell
   docker compose --profile messaging exec rabbitmq rabbitmqctl list_queues name messages messages_ready
   ```
5. Test tự động liên quan: `tests/SwiftRide.Api.Tests/Messaging/` (`RabbitMqBrokerIntegrationTests`, `BrokerFailureTests`) và `tests/SwiftRide.Api.Tests/FullFlow/EventDrivenBookingFlowTests.cs`; chạy bằng `dotnet test SwiftRide.sln` (một số test cần broker/DB thật — xem điều kiện trong từng test).

---

## 7. Vận hành nhanh & sự cố thường gặp

| Triệu chứng | Nguyên nhân thường gặp | Cách xử lý |
|---|---|---|
| Service crash khi khởi động: `RabbitMQ:Host is missing or empty` | `MESSAGING_TRANSPORT=RabbitMQ` nhưng `RABBITMQ_HOST` rỗng | Đặt `RABBITMQ_HOST=rabbitmq` (compose) hoặc `localhost` (chạy ngoài Docker) |
| `Unsupported Messaging:Transport` | Sai chính tả giá trị | Dùng `None`, `InMemory`, `RabbitMQ` |
| `docker compose` báo `Set RABBITMQ_USER` | Thiếu biến trong `.env` | Điền `RABBITMQ_USER`/`RABBITMQ_PASSWORD` |
| `ACCESS_REFUSED` khi service kết nối | User chưa tồn tại hoặc password trong database broker khác environment | Kiểm tra `rabbitmqctl list_users`; cấp user/quyền nếu thiếu hoặc dùng `change_password` nếu cần. Không xóa volume để sửa lỗi thanh toán |
| Trip kẹt `DroppedOff`/`PaymentPending` ở chế độ `Events` | Broker/consumer lỗi hoặc topology bootstrap cũ sai tên exchange | Kiểm tra Outbox, Inbox, queue và payment. Event chưa publish còn retry budget sẽ tự gửi lại; event đã publish nhưng chưa có Inbox/payment cần operator replay nguyên EventId và khóa thanh toán |
| Message trong `<queue>_error` | Poison message / hết retry | Xem `messaging-operations.md` mục 3.2 (đọc header `MT-Fault-Message`, sửa gốc, replay) |

Reset sạch môi trường local (xoá volume, **mất dữ liệu RabbitMQ và SQL Server local**):

```powershell
docker compose --profile messaging down -v
```

---

## 8. Topology khi consumer chưa khởi động

`definitions.json` và `EventTopology.Exchanges` dùng tên message mặc định của MassTransit, dạng `SwiftRide.Contracts.V1:RideCompletedV1`. Healthcheck import các durable exchange/queue/binding trước khi service bắt đầu publish. Vì vậy sự kiện có thể chờ trong queue khi consumer chưa chạy.

Cấu hình cũ dùng tên `swiftride.v1.*` nhưng MassTransit không publish vào đó. Publisher confirm chỉ chứng minh broker nhận publish; không chứng minh đã có queue nhận message. Nếu Trip kết nối trước Payment trên broker mới, sự kiện có thể không được định tuyến dù Outbox đã đánh dấu published.

Với volume cũ, `RABBITMQ_DEFAULT_USER`/`RABBITMQ_DEFAULT_PASS` không tự cập nhật tài khoản đã tồn tại. Nếu broker thiếu user do cấu hình cũ, mở shell nội bộ (`docker compose --profile messaging exec rabbitmq sh`), cấp tài khoản bằng biến môi trường và quyền trên vhost `/`, rồi kiểm tra xác thực. Không ghi mật khẩu vào source hoặc xóa dữ liệu broker/ledger.

Các exchange cũ không bị tự động xóa. Khi đổi definitions trên container hiện có, import lại bằng `rabbitmqctl import_definitions /etc/rabbitmq/definitions.json`; không cần recreate broker để có binding mới.

---

## 9. Checklist

- [ ] `.env` có `MESSAGING_TRANSPORT=RabbitMQ`, `RABBITMQ_HOST=rabbitmq`, `RABBITMQ_USER`, `RABBITMQ_PASSWORD` (không commit)
- [ ] `docker compose config` hợp lệ
- [ ] `docker compose --profile messaging up --build -d` — `rabbitmq` healthy
- [ ] 12 queue hiện diện, `_error` queue = 0
- [ ] Chuyến đi `PaymentTransport=Events` đi tới `Paid`, ledger cân bằng, retry không thu kép
- [ ] Không mở cổng RabbitMQ ra ngoài máy local; không log token/thẻ
