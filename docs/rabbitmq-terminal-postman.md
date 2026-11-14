# Xem thông tin RabbitMQ trên terminal và Postman Console

Hướng dẫn cho SwiftRide trên Windows/PowerShell, đối chiếu cấu hình ngày 12/11/2026. Mục tiêu: khi chạy chuyến đi qua Gateway, xem được exchange, queue, binding, consumer và số lượng message chờ/đã giao.

RabbitMQ trong dự án không có partition/offset như Kafka. Không gọi queue là partition hoặc dùng delivery tag thay cho Kafka offset. Đây là thông tin quan sát tương ứng với kiến trúc RabbitMQ, không phải thay thế nguyên nghĩa một yêu cầu bắt buộc dùng Kafka.

## 1. Chuẩn bị môi trường

### Bước 1: Mở Docker Desktop và terminal

Mở Docker Desktop, chờ engine hoạt động. Mở PowerShell tại thư mục dự án:

```powershell
Set-Location D:\SWA
docker info
```

Nếu báo không kết nối được Docker engine, xử lý Docker Desktop trước khi tiếp tục. Cần các kết nối DB và schema của dự án đã sẵn sàng theo [hướng dẫn end-to-end](end-to-end-booking.md).

### Bước 2: Cấu hình `.env`

Chỉ sao chép file mẫu nếu chưa có `.env`:

```powershell
if (-not (Test-Path -LiteralPath .env)) {
    Copy-Item -LiteralPath .env.example -Destination .env
}
```

Mở `.env` bằng editor, cập nhật các dòng tương ứng, không tạo nhiều dòng cùng một key:

```dotenv
ASPNETCORE_ENVIRONMENT=Development
MESSAGING_TRANSPORT=RabbitMQ
TRIP_LIFECYCLE_MATCHING_TRANSPORT=Events
RABBITMQ_HOST=rabbitmq
RABBITMQ_USER=swiftride
RABBITMQ_PASSWORD=<mat-khau-local-cua-ban>
```

Thay placeholder mật khẩu bằng giá trị local. Giữ các JWT keys, internal API keys, SQL Server password và connection strings hợp lệ đang có. Không commit `.env` hoặc chụp ảnh chứa credentials.

`Development` dùng để lấy token demo qua `/dev/token`; không dùng cấu hình này cho môi trường thật. `rabbitmq` là hostname giữa các container, còn máy host truy cập Management API bằng `localhost:15672`.

### Bước 3: Khởi động đầy đủ profile messaging

```powershell
docker compose --profile messaging config --quiet
docker compose --profile messaging up --build -d
docker compose --profile messaging ps
docker compose --profile messaging exec rabbitmq rabbitmq-diagnostics -q ping
```

Mong đợi broker `healthy`, các service đang chạy, lệnh ping thành công. `config --quiet` kiểm tra cấu hình mà không in toàn bộ secrets đã resolve. Nếu lỗi thiếu biến môi trường, sửa `.env` rồi chạy lại.

**Không bỏ `--profile messaging`** khi bật `MESSAGING_TRANSPORT=RabbitMQ`. Với volume broker cũ, đổi mật khẩu trong `.env` không tự đổi mật khẩu user đã tồn tại; xem [xử lý sự cố RabbitMQ](rabbitmq-setup.md). Không xóa volume để sửa lỗi đăng nhập.

## 2. Xem thông tin trên terminal

### Bước 4: Xem queue và consumer

```powershell
docker compose --profile messaging exec rabbitmq rabbitmqctl list_queues -p / name messages_ready messages_unacknowledged messages consumers
```

Ví dụ minh họa, không phải kết quả cố định:

```text
name                       messages_ready  messages_unacknowledged  messages  consumers
payment-ride-completed      0               0                        0         1
trip-payment-settled        0               0                        0         1
```

| Cột | Ý nghĩa |
|---|---|
| `name` | Queue nhận message |
| `messages_ready` | Message đang chờ được giao |
| `messages_unacknowledged` | Message đã giao nhưng chưa được consumer ACK |
| `messages` | Tổng ready + unacknowledged |
| `consumers` | Số subscription đang đăng ký vào queue, không phải số service trong kiến trúc |

Các queue chính được khai báo trong `infra/rabbitmq/definitions.json`:

| Queue | Vai trò |
|---|---|
| `matching-quote-requested` | Matching nhận yêu cầu báo giá và tìm tài xế ứng viên |
| `trip-quote-ready` | Trip nhận báo giá thành công |
| `trip-quote-failed` | Trip nhận kết quả báo giá thất bại |
| `matching-driver-match-requested` | Matching nhận yêu cầu tìm tài xế sau xác nhận/từ chối |
| `trip-driver-matched` | Trip nhận tài xế được tìm thấy và giữ chỗ |
| `trip-driver-match-failed` | Trip nhận kết quả không tìm được tài xế |
| `payment-ride-completed` | Payment nhận sự kiện hoàn tất chuyến để thu tiền |
| `trip-payment-settled` | Trip nhận kết quả thanh toán thành công |
| `trip-payment-failed` | Trip nhận kết quả thanh toán thất bại |
| `payment-compensation` | Payment nhận yêu cầu bồi hoàn |
| `trip-payment-refunded` | Trip nhận kết quả hoàn tiền |
| `trip-payment-compensation-failed` | Trip nhận kết quả bồi hoàn thất bại |

Queue hậu tố `_error` là hàng đợi lỗi do MassTransit quản lý; có thể chưa tồn tại nếu chưa phát sinh lỗi. Không xóa/purge queue khi chỉ cần quan sát.

### Bước 5: Xem exchange, binding và chi tiết consumer

Chạy lần lượt:

```powershell
docker compose --profile messaging exec rabbitmq rabbitmqctl list_exchanges -p / name type durable
docker compose --profile messaging exec rabbitmq rabbitmqctl list_bindings -p / source_name source_kind destination_name destination_kind routing_key
docker compose --profile messaging exec rabbitmq rabbitmqctl list_consumers -p /
```

Tìm các exchange như `SwiftRide.Contracts.V1:RideCompletedV1` và `SwiftRide.Contracts.V1:PaymentSettledV1`, cùng đường binding tới queue tương ứng. Cấu hình hiện tại dùng **fanout**: routing key có thể rỗng và không đóng vai trò chọn đích như direct/topic exchange. Có thể xuất hiện thêm exchange/binding do MassTransit khai báo.

### Bước 6: Theo dõi liên tục trong một cửa sổ riêng

Mở PowerShell thứ hai tại `D:\SWA`, chạy:

```powershell
while ($true) {
    Get-Date -Format 'HH:mm:ss'
    docker compose --profile messaging exec -T rabbitmq rabbitmqctl list_queues -p / name messages_ready messages_unacknowledged consumers
    Start-Sleep -Seconds 2
}
```

Nhấn **Ctrl+C** để dừng vòng theo dõi; không dừng container. Lệnh CLI có latency nên không chắc bắt được message tồn tại chỉ vài mili giây. Để xem log service ở một terminal khác:

```powershell
docker compose --profile messaging logs --follow --tail 100 trip-service payment-service
```

Log phụ thuộc mức logging và code hiện tại, không bảo đảm mọi message đều in đủ MessageId/CorrelationId hoặc payload. Dùng log để tìm lỗi bus, outbox, kết nối hoặc consumer; không suy ra log HTTP thành công là consumer đã xử lý event.

## 3. Xem thông tin trên Postman Console

### Bước 7: Tạo collection quản trị riêng

Trong Postman, tạo collection tên **RabbitMQ Local Monitoring**. Tách khỏi collection gọi Gateway để không dùng nhầm Basic Auth của broker cho API nghiệp vụ.

Tạo các collection variables:

| Variable | Giá trị |
|---|---|
| `rabbitmq_api` | `http://localhost:15672` |
| `queue_name` | `payment-ride-completed` |

Trong **Authorization** của collection:

1. Chọn **Basic Auth**.
2. Username: giá trị `RABBITMQ_USER` bạn đặt trong `.env`.
3. Password: giá trị `RABBITMQ_PASSWORD`; nhập local, dùng Vault nếu bạn đã cấu hình Vault.
4. Không đưa credential thật vào collection chia sẻ/export hoặc ảnh chụp Console. Basic Auth này khác JWT của rider/driver và khác internal API key.

Đây là thao tác quản trị local vào broker, không phải client nghiệp vụ gọi trực tiếp service. Không thêm route quản trị RabbitMQ vào Gateway. Nếu dùng Postman web, chọn **Desktop Agent** để truy cập localhost.

### Bước 8: Tạo request đọc một queue

Tạo request tên **Inspect Queue**:

```http
GET {{rabbitmq_api}}/api/queues/%2F/{{queue_name}}
```

- Authorization: **Inherit auth from parent**.
- Body: không có.
- Request timeout trong Settings: **15000 ms**.
- `%2F` là URL-encoded vhost `/`, không thay thành dấu `/` thô trong URL.

Nhấn **Send**. Mong đợi `200 OK`, response JSON có `name`, `vhost`, `messages_ready`, `messages_unacknowledged`, `consumers` và các thông tin khác. Nếu chưa bật plugin/broker, request có thể không kết nối được.

### Bước 9: Thêm script in thống kê

Trong **Scripts → Post-response** (bản cũ có thể gọi là **Tests**), dán:

```javascript
pm.test('Queue metrics are available', function () {
    pm.response.to.have.status(200);
    const q = pm.response.json();

    pm.expect(q.name).to.eql(pm.variables.get('queue_name'));
    pm.expect(q.messages_ready).to.be.a('number');
    pm.expect(q.messages_unacknowledged).to.be.a('number');

    const stats = q.message_stats || {};
    console.log('RabbitMQ queue snapshot', {
        observedAt: new Date().toISOString(),
        queue: q.name,
        vhost: q.vhost,
        type: q.type,
        node: q.node,
        consumers: q.consumers,
        ready: q.messages_ready,
        unacknowledged: q.messages_unacknowledged,
        total: q.messages,
        published: stats.publish ?? null,
        deliveredOrFetched: stats.deliver_get ?? null,
        acknowledged: stats.ack ?? null
    });
});
```

Các counter trong `message_stats` có thể chưa có khi chưa phát sinh hoạt động hoặc không thu thập thống kê. Script in `null` để phân biệt “không có số liệu” với `0`; không dùng các counter này làm định danh message.

### Bước 10: Mở Console và kiểm tra

Chọn **View → Show Postman Console**, hoặc **Ctrl+Alt+C** trên Windows. Có phiên bản hiển thị nút **Console** ở thanh dưới. Nhấn **Send** lại request **Inspect Queue**.

Console sẽ có dòng `RabbitMQ queue snapshot` và object thống kê. Mở rộng object để đọc từng trường. Đây là kết quả API quản trị broker, không phải body trả về từ `POST /trips`.

Đổi `queue_name` thành `trip-payment-settled`, Send lại để xem phía Trip. Muốn lưu hai request riêng, duplicate **Inspect Queue**, đổi URL queue cụ thể và assertion tên queue tương ứng (hoặc giữ dùng biến và chỉ chạy một queue mỗi lần).

### Bước 11: In đường binding vào Console

Tạo request thứ hai tên **Inspect Bindings**, cũng kế thừa Basic Auth:

```http
GET {{rabbitmq_api}}/api/queues/%2F/{{queue_name}}/bindings
```

Dán vào **Post-response**, mở Console và Send:

```javascript
pm.test('Queue bindings are available', function () {
    pm.response.to.have.status(200);
    const bindings = pm.response.json();
    pm.expect(bindings).to.be.an('array');

    console.log('RabbitMQ bindings', bindings.map(b => ({
        exchange: b.source,
        queue: b.destination,
        destinationType: b.destination_type,
        routingKey: b.routing_key
    })));
});
```

Có thể thấy binding từ default exchange với `source` rỗng; đó là hành vi bình thường. Quan sát thêm binding liên quan event của SwiftRide, không yêu cầu routing key phải khác rỗng.

## 4. Chạy chuyến đi để quan sát message thật

### Bước 12: Tạo bản collection demo Events

1. Import lại [SwiftRide Gateway — Matching Events + Payment Events](../postman/SwiftRide.Gateway.Smoke.postman_collection.json), dùng bản mới có 20 request thay cho bản REST cũ.
2. Đặt `TRIP_LIFECYCLE_MATCHING_TRANSPORT=Events` cùng `MESSAGING_TRANSPORT=RabbitMQ`, `RABBITMQ_HOST=rabbitmq` trong `.env`; recreate ba service bằng `docker compose --profile messaging up --build -d`. Kiểm tra cả Trip, Matching và Payment đã nối RabbitMQ.
3. Collection đã gửi `"paymentTransport": "Events"` ở **07 Create trip**, **09 Replay create**, **10 Changed payload conflicts**; không cần sửa body thủ công.
4. Giữ nguyên headers và scripts. Request 10 vẫn đổi payment method để kiểm tra xung đột; không sửa nó thành payload giống request 07. Bước **12 Wait for driver offer** chờ reply Matching trước khi lấy token tài xế.
5. Chọn Environment riêng (ví dụ `SwiftRide Local`), kiểm tra `base_url = http://localhost:5080`. Collection lưu token và dữ liệu chuyến vào environment để dùng lại khi Send riêng; xem [hướng dẫn chạy độc lập](api-reference.md#7-kiểm-thử-nhanh-bằng-postman). Dùng environment mới cho một lượt chạy toàn bộ mới.

**Bật broker không tự đổi chuyến sang Events.** Compose hiện không ánh xạ `TRIP_LIFECYCLE__PAYMENT_TRANSPORT` trong `.env` vào Trip; cách rõ ràng nhất cho demo là gửi trường `paymentTransport` như trên. Chuyến đã tạo lưu snapshot transport, nên hãy tạo chuyến mới bằng bản Events, không tiếp tục chuyến REST cũ.

### Bước 13: Ghi nhận trước và sau khi chạy

1. Mở terminal theo dõi queue ở bước 6.
2. Trong collection quản trị, Send **Inspect Queue** lần lượt cho `matching-quote-requested`, `trip-quote-ready`, `matching-driver-match-requested`, `trip-driver-matched`, `payment-ride-completed`, `trip-payment-settled`; ghi nhận các counter trước khi chạy.
3. Trong **SwiftRide Gateway - Matching Events + Payment Events**, chọn **Run collection**, chọn đủ 20 request theo thứ tự, **1 iteration**, delay **1000 ms**, timeout **15000 ms**.
4. Đợi đến **19 Wait for Paid** thành công. Ba bước polling quote/driver/payment đều có giới hạn 30 response đang chờ, không coi timeout là thành công. Retry broker có thể dài hơn thời gian demo này; kiểm tra lỗi và tiến độ nếu hết lượt.
5. Đợi vài giây cho thống kê broker cập nhật. Send lại **Inspect Queue** cho sáu queue; so sánh các counter trước/sau nếu chúng có sẵn. Đổi `queue_name` về đúng queue trước mỗi lần Send.
6. Trong terminal kiểm tra các queue `_error`; nếu có message mới, xem [hướng dẫn xử lý lỗi](messaging-operations.md).

Luồng mong đợi:

```text
POST /trips qua Gateway → Trip Outbox: QuoteRequestedV1
  → matching-quote-requested → Matching → QuoteReadyV1 → trip-quote-ready → Quoted
POST /trips/{id}/confirm-quote → Trip Outbox: DriverMatchRequestedV1
  → matching-driver-match-requested → Matching → DriverMatchedV1 → trip-driver-matched
  → DriverOffered có offeredDriverId → accept → en-route → pickup
POST /trips/{id}/dropoff qua Gateway
  → Trip ghi Outbox
  → exchange SwiftRide.Contracts.V1:RideCompletedV1
  → queue payment-ride-completed
  → Payment consumer xử lý và ghi Outbox kết quả
  → exchange SwiftRide.Contracts.V1:PaymentSettledV1
  → queue trip-payment-settled
  → Trip consumer cập nhật Paid
```

Queue về `0` sau xử lý thành công là bình thường. Counter tăng chỉ là bằng chứng hoạt động tổng hợp, không chứng minh riêng một trip nếu có người khác đang chạy cùng lúc; cần đối chiếu thêm trip ID, event/inbox/outbox hoặc trace khi muốn chứng minh chính xác từng message. Không coi một ACK là bằng chứng đầy đủ về ledger cân bằng/không thu tiền hai lần.

## 5. Nếu muốn thấy message nằm chờ rõ ràng

Phần tùy chọn, chỉ trong môi trường demo riêng. Không làm khi người khác đang dùng Payment. Phương án này tạm dừng consumer Payment để message còn trong queue; không cần lấy message ra bằng Management API.

1. Hoàn thành bước khởi động và xác nhận broker/Trip hoạt động.
2. Trong terminal chạy:

```powershell
docker compose --profile messaging stop payment-service
```

3. Trong Postman Runner của **SwiftRide Gateway - Matching Events + Payment Events**, chọn request **01 đến 18**, bỏ chọn 19 và 20, chạy 1 iteration. Runner chuẩn bị driver, tạo chuyến và thực hiện dropoff; không chờ Paid lúc Payment đã dừng. Matching vẫn phải hoạt động.
4. Đọc **Inspect Queue** với `queue_name = payment-ride-completed`, hoặc chạy lệnh bước 4. Khi Outbox publish xong, mong đợi `messages_ready` tăng, `consumers = 0` nếu không có Payment instance khác. Chờ vài giây rồi đọc lại nếu publisher chưa gửi xong. Có thể có nhiều message nếu các chuyến khác cũng đang chờ.
5. Khởi động lại Payment ngay sau khi quan sát, kể cả nếu demo gặp lỗi:

```powershell
docker compose --profile messaging start payment-service
```

6. Chờ consumer kết nối, Send **Inspect Queue** lại: consumer xuất hiện, ready giảm khi message được xử lý. `unacknowledged` có thể tăng rất ngắn nên không chắc thấy được bằng polling.
7. Giữ đúng environment của lượt demo, Send **19 Wait for Paid** để gọi `GET /trips/{id}` qua Gateway và kiểm tra `Paid`. Token/ID đã được lưu trong environment; nếu Runner có tùy chọn **Keep variable values**, bật trước khi chạy. Khi Send riêng, request không tự polling; gửi lại sau khoảng nghỉ nếu còn `PaymentPending`. Không chia sẻ/export environment hoặc ảnh chứa token.
8. Nếu cần, gửi PUT offline driver demo bằng đúng token driver, hoặc chờ vị trí hết hạn. Không xóa dữ liệu để dọn demo.

Không cần dùng `POST /api/queues/.../get` để xem metrics: thao tác đó thực sự lấy message và thay đổi trạng thái queue, kể cả khi chọn requeue. Hướng dẫn này chỉ dùng các API quản trị GET để quan sát.

## 6. Lỗi thường gặp và cách đọc kết quả

| Triệu chứng | Kiểm tra / xử lý |
|---|---|
| Docker engine không kết nối được | Mở Docker Desktop, đợi engine sẵn sàng, chạy `docker info` |
| Postman không kết nối cổng 15672 | Broker đã chạy chưa, profile messaging đã bật chưa; Postman web cần Desktop Agent |
| `401` ở Management API | Basic Auth phải dùng credential RabbitMQ hiện có, không dùng JWT; volume cũ không tự cập nhật mật khẩu |
| `403` ở Management API | User thiếu quyền/tag quản trị phù hợp; dùng tài khoản operator được cấp quyền trên vhost `/` |
| `404` ở Management API | Kiểm tra queue name, `%2F`, topology bootstrap; queue `_error` có thể chưa tồn tại |
| `400` khi tạo trip Events | Kiểm tra Trip đã bật messaging bằng RabbitMQ và container đã recreate sau khi đổi env |
| Không có `/dev/token` | Kiểm tra môi trường Development, chế độ DevelopmentJwt; endpoint không có trong OIDC/Production |
| Queue luôn `0` | Consumer có thể xử lý nhanh; đọc counter trước/sau hoặc dùng demo dừng Payment ở mục 5 |
| `message_stats` thiếu/null | Chưa có hoạt động, metrics chưa cập nhật hoặc thống kê bị tắt; không kết luận message thất lạc |
| Consumer bằng `0` dù service chạy | Kiểm tra bus connection, quyền vhost, logs Trip/Payment và cấu hình messaging |
| Queue `_error` có message | Xem error headers theo tài liệu vận hành; không purge hoặc replay tùy tiện |
| Trip giữ `PaymentPending` | Kiểm tra broker, consumer và outbox; không tạo payment mới/key mới để ép trạng thái Paid |
| Không có dòng Console tùy chỉnh | Script nằm ở đúng Post-response chưa; request có `200` không; Console có đang lọc log không |

## 7. Kết quả cần ghi lại khi báo cáo

- Tên exchange, kiểu `fanout` và binding tới queue.
- Queue name, vhost, consumer count, ready và unacknowledged trước/sau xử lý.
- HTTP request dropoff và GET trip cuối cùng có `status = Paid` trong demo thành công.
- Log/trace hoặc event ID nếu cần chứng minh message thuộc đúng chuyến; không trình bày queue metrics tổng hợp như lịch sử của từng message.

Ảnh chụp chỉ chứa thông tin kỹ thuật cần thiết; che Authorization, passwords và dữ liệu cá nhân. Không in toàn bộ HTTP headers hay token bằng script Console.

## 8. Nguồn và giới hạn kiểm chứng

- [Cấu hình RabbitMQ của SwiftRide](rabbitmq-setup.md), [vận hành messaging](messaging-operations.md), [hợp đồng event](messaging-contracts-v1.md), [HTTP API và collection](api-reference.md).
- [RabbitMQ HTTP API](https://www.rabbitmq.com/docs/http-api-reference): queue metrics, bindings và đặc điểm thao tác lấy message.
- [RabbitMQ monitoring](https://www.rabbitmq.com/docs/monitoring): ý nghĩa thống kê queue.
- [Postman Console](https://www.postman.com/postman/postman-team-collections/collection/c8w949c/how-to-use-the-postman-console): mở Console và xem log script.

Các lệnh và script trong hướng dẫn được đối chiếu với cấu hình repo và tài liệu chính thức; ví dụ output chỉ minh họa. Chưa chạy kịch bản này trên broker thật trong lượt viết tài liệu. Không cần sửa source, đổi sang Kafka hoặc tạo API nghiệp vụ mới để thực hiện các bước trên.
