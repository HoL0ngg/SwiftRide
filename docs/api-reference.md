# Tài liệu HTTP API SwiftRide

Đối chiếu mã nguồn ngày 12/11/2026. Mã nguồn endpoint và DTO là nguồn mô tả hành vi hiện tại; khi thay đổi chúng cần cập nhật tài liệu này và collection tương ứng.

## 1. Quy ước và kết nối

- Base URL cho client: `http://localhost:5080` (YARP Gateway).
- JSON dùng `camelCase`, gửi `Content-Type: application/json` khi có body. GET và các lệnh driver không có body.
- ID chuyến, quote và payment là UUID. ID người dùng/tài xế là chuỗi từ claim `sub`.
- API công khai cần `Authorization: Bearer <access_token>`, trừ `/health` và `/dev/token`.
- `X-Correlation-Id` tùy chọn để truy vết; không dùng làm khóa chống trùng.
- Tiền là VND nguyên đơn vị. **Trip trả `fareAmount` dạng chuỗi** để giữ độ chính xác; Payment và Quote trả `amount` dạng JSON number. Không chuyển số tiền lớn sang JavaScript `Number` khi xử lý nghiệp vụ.
- Client chỉ gọi Gateway. `/match`, `/quote`, `/payments` là API nội bộ, không có route Gateway. Trong Compose service chỉ có địa chỉ mạng nội bộ, không publish cổng HTTP ra host.

### Xác thực và tiện ích Gateway

| Method | Path | Đầu vào | Kết quả |
|---|---|---|---|
| GET | `/health` | Không body, không JWT | `200`, không body; chỉ xác nhận process Gateway hoạt động, không kiểm tra toàn bộ DB/service |
| POST | `/dev/token?userId=demo-rider&role=rider` | Query `userId`: 1–128 ký tự, không chỉ khoảng trắng; `role`: chính xác `rider` hoặc `driver`; không body | `200` với `{"access_token":"<jwt>"}`; `400` nếu không hợp lệ |

`/dev/token` chỉ được đăng ký khi môi trường **Development** và chế độ **DevelopmentJwt**; token hết hạn sau 30 phút. Ngoài điều kiện này route không tồn tại. Với OIDC, lấy access token từ nhà cung cấp theo [hướng dẫn xác thực](authentication.md). Một số use case giới hạn ID actor ở 100 ký tự, nên dùng ID không quá 100 ký tự xuyên suốt hệ thống.

Compose mặc định môi trường `Production`; để demo token local cần đặt `ASPNETCORE_ENVIRONMENT=Development` trong `.env`. Signing key, issuer, audience và các kết nối phải được cấu hình theo `.env.example`, không đưa secrets vào collection. Không ghi đè `.env` đang có. Xem [chạy end-to-end](end-to-end-booking.md).

Mỗi business service cũng có `GET /health` nội bộ, không JWT/internal key, trả `200` rỗng. Đây là tiện ích vận hành trên địa chỉ của từng service, không phải health tổng hợp và không được Gateway proxy thành route riêng.

## 2. API chuyến đi qua Gateway

### Danh mục

`id` trong bảng là UUID của chuyến. Rider chỉ thao tác chuyến của mình; driver chỉ đọc chuyến được đề nghị hoặc đã giao cho mình, và chỉ cập nhật theo vai trò của mình trên chuyến đó.

| Method | Path | Vai trò | Query/body | Thành công |
|---|---|---|---|---|
| POST | `/trips` | rider | Body tạo chuyến bên dưới; bắt buộc header `Idempotency-Key` | `201` mới, `200` replay; `202` nếu còn xử lý matching; trả TripResponse |
| GET | `/trips/current` | rider / driver | Không body | `200` TripResponse hoặc `204` khi không có chuyến hiện tại |
| GET | `/trips/driver-feed` | driver | Không body | `200` với `{"offeredTrip":null,"activeTrip":null}`; mỗi trường khác null là TripResponse |
| GET | `/trips/feed` | driver | Alias của `/trips/driver-feed` | Như trên |
| GET | `/trips/history?limit=20&cursor=...` | rider / driver | `limit` tùy chọn, mặc định 20, clamp vào 1–100; `cursor` tùy chọn lấy từ `nextCursor`, URL-encode khi gửi lại | `200` với `{"items":[],"nextCursor":null}`; items là các TripResponse |
| GET | `/trips/{id}` | rider / driver | Không body | `200` TripResponse; `404` nếu không tồn tại |
| POST | `/trips/{id}/confirm-quote` | rider | `{"quoteId":"<uuid>"}` | `200` TripResponse; thường `DriverOffered`, có thể `Failed` khi không còn tài xế |
| POST | `/trips/{id}/cancel` | rider | Body tùy chọn `{"reason":"Changed plans"}` hoặc `{}` | `200` TripResponse `Cancelled`, chỉ trước `DriverAccepted` |
| POST | `/trips/{id}/driver-accept` | driver được đề nghị | Không body | `200` TripResponse `DriverAccepted` |
| POST | `/trips/{id}/driver-reject` | driver được đề nghị | Không body | `200` TripResponse; rematch `DriverOffered` hoặc `Failed` |
| POST | `/trips/{id}/en-route` | driver được giao | Không body | `200` TripResponse `EnRoute` |
| POST | `/trips/{id}/pickup` | driver được giao | Không body | `200` TripResponse `PickedUp` |
| POST | `/trips/{id}/dropoff` | driver được giao | Không body | `200` TripResponse, có thể `PaymentPending` hoặc đã `Paid`; tự khởi động thanh toán |

### POST /trips

```http
POST /trips
Authorization: Bearer <rider_token>
Content-Type: application/json
Idempotency-Key: <unique-create-key>
```

```json
{
  "pickup": { "latitude": 10.7725, "longitude": 106.6980 },
  "pickupName": "Chợ Bến Thành",
  "pickupAddress": "Quận 1",
  "destination": { "latitude": 10.7951, "longitude": 106.7219 },
  "destinationName": "Landmark 81",
  "destinationAddress": "Bình Thạnh",
  "paymentMethod": "wallet",
  "mode": "standard"
}
```

| Trường | Kiểu | Bắt buộc / quy tắc |
|---|---|---|
| `pickup`, `destination` | object | Bắt buộc, chứa latitude/longitude dạng number; latitude −90..90, longitude −180..180; hai điểm không được trùng nhau |
| `pickupName`, `pickupAddress`, `destinationName`, `destinationAddress` | string / null | Tùy chọn; thiếu/null được lưu thành chuỗi rỗng |
| `paymentMethod` | string | `card` hoặc `wallet`; thiếu/null được endpoint mặc định `card`; nên gửi rõ giá trị |
| `mode` | string | Tùy chọn, mặc định `standard`; phải nằm trong các mode server hỗ trợ |
| `paymentTransport` | string / null | Tùy chọn kỹ thuật cho demo: `Rest` hoặc `Events`; thường bỏ để dùng cấu hình server. Gửi `Events` khi messaging tắt trả `400` |

Rider lấy từ JWT, không gửi `riderId`, giá, payment token hay thông tin thẻ. Trip tự chọn mock token tương ứng phương thức. Một rider chỉ có một chuyến đang hoạt động, nếu tạo thêm trả `409`.

Giới hạn validation hiện tại: DTO tọa độ của Trip dùng `double` không nullable; nếu bỏ một thành phần tọa độ, bộ bind có thể cho giá trị mặc định `0`. Luôn gửi đủ latitude/longitude. Matching dùng trường nullable và kiểm tra thiếu tọa độ rõ ràng. Thay đổi nhỏ đề xuất cho lần sửa validation: kiểm tra sự hiện diện từng thành phần ở biên HTTP của Trip và thêm test; tài liệu này không sửa hành vi API.

Tạo chuyến có thể trả `202` và trạng thái `Requested`: gọi `GET /trips/{id}` với khoảng nghỉ và giới hạn thời gian để chờ `Quoted` hoặc `Failed`. Không tạo khóa mới để retry cùng một yêu cầu khi timeout.

### TripResponse

Ví dụ minh họa hình dạng dữ liệu, UUID/giá/thời gian không phải kết quả cố định:

```json
{
  "id": "11111111-1111-4111-8111-111111111111",
  "riderId": "demo-rider",
  "status": "Quoted",
  "candidateDriverId": "demo-driver",
  "offeredDriverId": null,
  "assignedDriverId": null,
  "rejectionCount": 0,
  "rematchPending": false,
  "quoteId": "22222222-2222-4222-8222-222222222222",
  "fareAmount": "50000",
  "currency": "VND",
  "distanceKm": 3.6,
  "estimatedMinutes": 10.8,
  "pickupName": "Chợ Bến Thành",
  "pickupAddress": "Quận 1",
  "pickup": { "latitude": 10.7725, "longitude": 106.6980 },
  "destinationName": "Landmark 81",
  "destinationAddress": "Bình Thạnh",
  "destination": { "latitude": 10.7951, "longitude": 106.7219 },
  "paymentMethod": "wallet",
  "createdAt": "2026-11-12T01:00:00Z",
  "updatedAt": "2026-11-12T01:00:00Z"
}
```

`quoteId`, `fareAmount`, `currency`, `distanceKm`, `estimatedMinutes` có thể null trước khi báo giá. Các ID driver có thể null tùy giai đoạn. Timestamps là ISO 8601 có offset. `status` là chuỗi tên trạng thái.

Luồng chính: `Requested → Quoted → DriverOffered → DriverAccepted → EnRoute → PickedUp → DroppedOff → PaymentPending → Paid`. Endpoint dropoff thực hiện cả bước yêu cầu thanh toán, nên không nhất thiết quan sát được `DroppedOff` qua HTTP. `Paid`, `Cancelled`, `Failed` là terminal; `PaymentPending` chưa kết thúc. Không coi `200` của dropoff là thanh toán thành công: đọc `status` và polling nếu cần. Khi thanh toán hết retry vẫn lỗi, chuyến giữ `PaymentPending`.

### Idempotency cho Trip

- `POST /trips` bắt buộc `Idempotency-Key`. Cùng actor, cùng key và nội dung nghiệp vụ tương đương trả lại cùng chuyến; thay đổi nội dung được fingerprint kiểm tra trả `409`.
- Fingerprint tạo chuyến hiện dùng actor, tọa độ làm tròn 6 số thập phân, payment method và mode; **không gồm tên/địa chỉ và paymentTransport**. Không dùng replay để cập nhật các trường này. Response replay phản ánh trạng thái hiện tại, không bảo đảm JSON giống hệt lần đầu.
- Các lệnh còn lại nhận header này tùy chọn; nếu bỏ, server sinh key cố định theo action và trip. Gửi key riêng cho mỗi thao tác mới, giữ nguyên khi retry. Với driver-reject, tài xế mới phải dùng danh tính của mình sau rematch.
- Confirm phải dùng đúng `quoteId` đang gắn với chuyến. Giá được giữ nguyên khi rider xác nhận và khi rematch. Vượt giới hạn từ chối `k` (mặc định 3), hoặc không còn tài xế phù hợp, chuyến có thể `Failed`.

## 3. Vị trí tài xế và tìm địa chỉ qua Gateway

### PUT /drivers/{id}/location

JWT role `driver`, `sub` phải đúng `id` trên path (chuỗi ID tài xế, không bắt buộc UUID).

```json
{
  "latitude": 10.7725,
  "longitude": 106.6980,
  "isOnline": true
}
```

Cả ba trường bắt buộc; tọa độ theo giới hạn nêu trên. Thành công `204`, không body; server tự gắn timestamp. Gửi `isOnline: false` khi tài xế offline. Vị trí hết hạn theo cấu hình Matching (mặc định 5 phút); trong demo nên cập nhật định kỳ. Sai role hoặc ID trả `403`; dữ liệu không hợp lệ trả `400`.

### GET /locations/search?query=Ben%20Thanh

JWT role `rider` hoặc `driver`. Query `query` bắt buộc, độ dài sau trim 3–200 ký tự; không body. `200` trả mảng, có thể rỗng:

```json
[
  { "id": "example-place", "displayName": "Chợ Bến Thành, TP. Hồ Chí Minh", "latitude": 10.7725, "longitude": 106.6980 }
]
```

`id` là chuỗi của kết quả tìm kiếm. `400` nếu query sai, `503` khi geocoding không khả dụng. Tìm khi người dùng chủ động nhấn nút, không gọi autocomplete từng ký tự. Xem [giới hạn Nominatim](matching-pricing.md).

## 4. API nội bộ Matching & Pricing

Chỉ dành cho service gọi service. Base address cấu hình `Services:Matching:BaseAddress` ở Trip; ví dụ mạng Compose `http://matching-service:8080`. Bắt buộc header `X-Internal-Api-Key`, khớp cấu hình Matching. Thiếu/sai key hoặc server chưa cấu hình key trả `401`. Không gọi các route này qua Gateway và không đưa internal key vào client.

### POST /match

```json
{ "latitude": 10.7725, "longitude": 106.6980, "excludedDriverIds": [] }
```

Tọa độ bắt buộc. `excludedDriverIds` tùy chọn/null mặc định mảng rỗng; tối đa 100 ID, mỗi ID không trắng và không quá 128 ký tự. Thành công luôn `200` cho hai kết quả nghiệp vụ:

```json
{ "status": "Matched", "candidate": { "driverId": "demo-driver", "distanceKm": 0.05 } }
```

```json
{ "status": "NoDriverAvailable", "candidate": null }
```

Chỉ xét tài xế online, vị trí còn mới và trong bán kính cấu hình, loại danh sách đã từ chối. Matching chọn ứng viên, không giữ chỗ; Trip giữ chỗ khi offer. Dữ liệu sai trả `400`.

### POST /quote

```json
{
  "pickup": { "latitude": 10.7725, "longitude": 106.6980 },
  "destination": { "latitude": 10.7951, "longitude": 106.7219 },
  "mode": "standard"
}
```

Hai điểm bắt buộc; `mode` tùy chọn/null mặc định `standard`, phải khớp mode strategy. API không nhận cước, surge, tax, toll hoặc promotion từ client. Thành công `201`:

| Trường response | Kiểu / ý nghĩa |
|---|---|
| `quoteId` | UUID báo giá |
| `amount`, `currency` | number nguyên VND, chuỗi `VND` |
| `distanceKm`, `estimatedMinutes` | number, khoảng cách Haversine và thời gian từ vận tốc cấu hình |
| `breakdown` | object chứa các number: `distanceKm`, `estimatedMinutes`, `costPerKm`, `costPerMinute`, `surge`, `toll`, `promotionFactor`, `tax` |
| `createdAt` | Timestamp ISO 8601 |

Giá = `((costPerKm * distanceKm + costPerMinute * estimatedMinutes) * surge + toll) * promotionFactor * (1 + tax)`, làm tròn một lần ở cuối theo cấu hình. Dữ liệu/mode sai trả `400`.

## 5. API nội bộ Payment

Base address cấu hình `Services:Payment:BaseAddress` ở Trip, ví dụ mạng Compose `http://payment-service:8080`. Tất cả `/payments` cần `X-Internal-Api-Key` khớp Payment; không public qua Gateway.

| Method | Path | Đầu vào | Kết quả |
|---|---|---|---|
| POST | `/payments` | Body bên dưới + `Idempotency-Key` | `201` kết quả mới, `200` replay; `202` còn Pending; trả PaymentDto |
| GET | `/payments/{id}` | UUID payment, không body | `200` PaymentDto hoặc `404` |
| GET | `/payments?tripId={tripId}` | Query UUID trip bắt buộc, không body | `200` một PaymentDto hoặc `404`; không phải mảng |
| POST | `/payments/{id}/refund` | `{"reason":"Trip invalidated"}` | `200` PaymentDto chứa `refund`; kiểm tra `refund.status` |

### Tạo thanh toán

```http
POST /payments
Content-Type: application/json
X-Internal-Api-Key: <payment_internal_key>
Idempotency-Key: trip-11111111-1111-4111-8111-111111111111-payment
```

```json
{
  "tripId": "11111111-1111-4111-8111-111111111111",
  "riderId": "demo-rider",
  "driverId": "demo-driver",
  "amount": 50000,
  "currency": "VND",
  "method": "Wallet",
  "paymentToken": "wallet.success.v1"
}
```

`tripId` phải khác UUID rỗng; `riderId` và `driverId` bắt buộc, khác nhau, sau trim dài 1–100 ký tự. `amount` bắt buộc theo nghiệp vụ: nguyên, `0 <= amount < 10^18`, lấy từ fare Trip đã chốt; `currency` thiếu/null mặc định `VND`, chỉ chấp nhận `VND`. `method` dùng `Card` hoặc `Wallet`; `paymentToken` không rỗng (ví dụ trên là alias mock, không phải credential thật). Gửi đầy đủ các trường, không dựa vào giá trị mặc định của bộ bind JSON.

Key phải có dạng xác định theo trip `trip-{tripId}-payment`. Cùng key và fingerprint payload trả cùng payment; khác payload trả `409`. Khi pending/timeout/`503`, giữ nguyên key và payload, không tạo key mới. Trạng thái nghiệp vụ là `Pending`, `Settled`, `Failed`; **HTTP 201 không đồng nghĩa Settled** (cổng từ chối có thể trả payment `Failed`).

### PaymentDto

| Trường | Kiểu / ý nghĩa |
|---|---|
| `id`, `tripId` | UUID payment/chuyến |
| `riderId`, `driverId` | string; `driverId` có thể null tùy bản ghi/trạng thái |
| `amount`, `currency` | number nguyên, `VND` |
| `method`, `status`, `failureCode` | string (`Card`/`Wallet`, `Pending`/`Settled`/`Failed`); failureCode có thể null |
| `createdAt`, `settledAt` | Timestamp ISO 8601; settledAt có thể null |
| `ledgerEntries` | Mảng `{account, direction, amount, currency, createdAt}`; direction `Debit` hoặc `Credit` |
| `refund` | null hoặc `{id, amount, currency, reason, status, createdAt, settledAt, ledgerEntries}` |
| `processing` | null hoặc `{attemptsStarted, leaseUntilUtc, canRetry, requiresAction}`; leaseUntilUtc có thể null |

Payment settled ghi một cặp Debit/Credit cân bằng trong cùng transaction. PaymentDto không trả raw payment token hay idempotency key. Để kiểm chứng chống charge trùng cần kiểm tra cả Payment/ledger và gateway-operation evidence, không chỉ quan sát Trip `Paid`.

### Hoàn tiền

Refund là hoàn **toàn bộ**, chỉ cho payment `Settled`. Body JSON bắt buộc; `reason` tùy chọn, có thể gửi `{}`. Một payment có tối đa một refund; server tự sinh key `payment-{id}-refund`. Gửi lại cùng lý do nhận lại refund; lý do khác trả `409`. Refund cũng có `Pending`/`Settled`/`Failed`, nên `200` chưa chắc hoàn tiền xong. `503` cho lỗi cổng tạm thời, giữ cùng yêu cầu khi thử lại. Không có API public hoàn tiền hoặc API public ledger.

## 6. Mã lỗi và retry

| HTTP | Ý nghĩa thường gặp |
|---|---|
| 400 | Body/query sai, tọa độ sai, thiếu key tạo Trip, phương thức/mode không hợp lệ, vi phạm dữ liệu tiền |
| 401 | JWT thiếu/hết hạn/không hợp lệ, hoặc internal API key sai/thiếu |
| 403 | Sai role hoặc không sở hữu tài nguyên |
| 404 | Không có tài nguyên hoặc route không được đăng ký/expose |
| 409 | Transition sai, rider đã có chuyến, idempotency xung đột, refund sai trạng thái |
| 429 | Gateway vượt hạn mức fixed-window; không retry dồn dập |
| 500 | Lỗi không dự kiến; dùng traceId/correlation ID để điều tra |
| 502 / 504 | Gateway không kết nối được upstream hoặc upstream timeout |
| 503 | Geocoding hoặc cổng thanh toán tạm thời không khả dụng |

Exception handler thường trả `application/problem+json`, ví dụ:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict",
  "status": 409,
  "detail": "The request conflicts with the current trip state or idempotency key.",
  "traceId": "<trace-id>"
}
```

Không phải mọi lỗi có body/traceId: ví dụ authentication challenge, `Forbid`, `NotFound` trực tiếp và một số lỗi proxy có thể rỗng. Client dựa vào HTTP status trước khi parse. Mọi network call cần timeout; chỉ retry lỗi tạm thời với backoff, jitter và số lần giới hạn. Với POST phải giữ nguyên idempotency key. Không tự retry lỗi 400/401/403/409 mà chưa xử lý nguyên nhân.

## 7. Kiểm thử nhanh bằng Postman

Có thể dùng collection có sẵn: [SwiftRide Gateway — Matching Events + Payment Events](../postman/SwiftRide.Gateway.Smoke.postman_collection.json). Giữ tên file cũ để không gãy liên kết; nội dung hiện kiểm thử cả Matching và Payment qua event. Đây là file JSON Collection v2.1 để import; không cần tài khoản workspace hay đưa secrets vào file.

1. Chuẩn bị `.env` với DB, JWT và internal API keys theo `.env.example` (Compose hiện vẫn yêu cầu các keys dù luồng này dùng event). Đặt thêm `ASPNETCORE_ENVIRONMENT=Development`, `MESSAGING_TRANSPORT=RabbitMQ`, `TRIP_LIFECYCLE_MATCHING_TRANSPORT=Events`, `RABBITMQ_HOST=rabbitmq` và credential RabbitMQ riêng. Không commit `.env`. Chạy `docker compose --profile messaging up --build -d`, kiểm tra các service đã nối bus và `http://localhost:5080/health`. Gateway health không chứng minh broker/consumer đã sẵn sàng.
2. Trong Postman chọn **Import**, mở file collection trên. Nếu dùng Postman web, chọn Desktop Agent để truy cập localhost.
3. Tạo và chọn một **Environment** riêng, ví dụ `SwiftRide Local`. Script báo lỗi nếu chưa chọn environment. `base_url` mặc định ở collection là `http://localhost:5080` (không dấu `/` cuối); có thể ghi đè trong environment. Đây là smoke test dành cho DevelopmentJwt, không dùng nguyên bộ trên OIDC/Production.
4. Để chạy tự động, dùng environment mới/chưa có dữ liệu chuyến, chọn **Run collection**, toàn bộ **20 request** theo thứ tự, **1 iteration**, delay **1000 ms**, request timeout **15000 ms**. Nếu Runner có tùy chọn **Keep variable values**, bật để giữ các giá trị sau lượt chạy. Polling tự lặp bằng `pm.execution.setNextRequest` trong Runner; khi bấm **Send** riêng thì mỗi lần chỉ gửi một request.
5. Xem kết quả Tests trong Runner. Dừng ở assertion lỗi đầu tiên; đọc status/body của request lỗi.

Kịch bản: health → token rider/driver → driver online → rider bị chặn route driver và request thiếu JWT bị chặn → tạo chuyến (`202 Requested`) → polling Quoted → replay cùng key/cùng ID/fare → đổi payload cùng key phải `409` → confirm → **12 Wait for driver offer** → lấy token đúng offered driver → accept → en-route → pickup → dropoff → replay dropoff → **19 Wait for Paid** → đưa driver demo ban đầu offline. Script chỉ sinh `rider_id`, `driver_id`, `create_key`, `dropoff_key` khi environment chưa có giá trị; gọi Health hoặc lấy lại token không đổi các ID/key đã lưu.

**Chạy request riêng bằng Send:**

- Có thể bắt đầu ngay ở **02 Rider token** hoặc **03 Driver token**, không cần chạy Health. Token trả về được lưu bằng `pm.environment.set()`: `rider_token`, `initial_driver_token` và `driver_token`. Request 03 lưu token driver vào cả hai biến driver; request **13 Offered driver token** cập nhật riêng `driver_token` theo `offered_driver_id`. Khi tài xế được mời khác tài xế demo, dùng request 13 trước khi accept; gọi lại request 03 sẽ chọn lại tài xế demo cho `driver_token`.
- `trip_id`, `quote_id`, `fare_amount` và `offered_driver_id` cũng được lưu trong environment sau các response tương ứng. Có thể gửi lại request cần kiểm tra mà không chạy lại toàn bộ collection. Nếu dùng chuyến có sẵn, điền đúng các giá trị đó cùng token/ID actor liên quan vào environment. Request vẫn phải tuân thủ quyền và state machine; lưu token không cho phép pickup trước accept/en-route.
- Với chuyến mới: Send 07 → Send 08 đến khi `Quoted` để lưu quote/giá → Send 11 → Send 12 đến khi có tài xế → Send 13. Sau đó có thể thao tác từng bước của chuyến bằng dữ liệu đã lưu. Bộ đếm polling là biến tạm, bắt đầu từ 0 cho mỗi Send riêng; chỉ Runner tự lặp và giới hạn 30 response đang chờ.
- Token hết hạn: Send lại request lấy token cho đúng actor. Để chạy một kịch bản mới hoàn toàn, chọn environment mới, hoặc xóa các biến phiên (`rider_id`, `driver_id`, ba token, `create_key`, `dropoff_key`, `trip_id`, `quote_id`, `fare_amount`, `offered_driver_id`) trước khi bắt đầu. Không đổi idempotency key chỉ để retry một yêu cầu đang xử lý.

**Status dễ nhầm khi Send riêng:** request **05** mong đợi `403` (rider không được cập nhật vị trí driver), request **06** mong đợi `401` (không có JWT); đây là test âm thành công nếu assertion xanh. Request **07** tạo mới qua event trả `202 Requested`; Send lại cùng `create_key` trả `202` khi còn Requested hoặc `200` khi chuyến đã tiến triển. Script chấp nhận `200` khi ID khớp `trip_id` đã lưu, không tạo chuyến mới. Nếu ID chưa được lưu, đối chiếu response/GET chuyến hiện tại và điền đúng `trip_id` trước khi replay. Tiếp tục từ bước phù hợp trạng thái hiện tại; không chạy lại accept/pickup cho chuyến đã Paid. `201` ở request 07 vẫn bị từ chối vì là đường tạo mới xử lý matching đồng bộ.

Giữ token ở giá trị local của environment, đánh dấu secure trong Postman và không Share/Export environment đã chứa token. Xem [cách Postman lưu và cập nhật environment variables](https://learning.postman.com/docs/use/send-requests/variables/environment-variables/).

Ba bước polling giới hạn **30 response đang chờ mỗi bước**. Với delay 1 giây, thời gian thực tế còn cộng latency HTTP. Bước 12 chờ `DriverOffered` và `offeredDriverId` có giá trị, đồng thời kiểm tra quote/giá đã chốt. Khi hết lượt hoặc nhận `Failed`, assertion thất bại; không tuyên bố thanh toán thành công nếu vẫn `PaymentPending`. Giới hạn này dành cho lượt demo không lỗi; retry broker có thể lâu hơn, nên khi timeout hãy kiểm tra tiến độ và error queue, không tạo key thanh toán mới. Kịch bản không gọi geocoding nên không phụ thuộc Nominatim.

Collection chọn rõ `paymentTransport: "Events"` ở cả ba payload tạo/replay/conflict. Matching transport được chọn trên **server**, không có trường payload để đổi nó. Request 07 yêu cầu `202 Requested` cho tạo mới, hoặc `200` replay đúng ID đã lưu; assertion HTTP vẫn không phân biệt RabbitMQ với InMemory hoặc mọi tình huống fallback REST. Cần đối chiếu cấu hình runtime và bằng chứng broker.

Để kiểm chứng RabbitMQ thật, theo [hướng dẫn terminal/Postman](rabbitmq-terminal-postman.md), quan sát trước/sau các queue `matching-quote-requested`, `trip-quote-ready`, `matching-driver-match-requested`, `trip-driver-matched`, `payment-ride-completed`, `trip-payment-settled`, cùng các queue `_error` nếu có. Đối chiếu TripId/CorrelationId với outbox, inbox hoặc trace; queue về 0 là bình thường khi consumer xử lý nhanh. Chỉ riêng HTTP `Paid` hoặc counter tổng hợp không chứng minh từng event thuộc chuyến vừa chạy.

Có thể kiểm tra cú pháp, dữ liệu nối request, giới hạn polling và cách xử lý assertion lỗi mà không gọi HTTP:

```powershell
# Cần dependencies WebApp đã được cài (tái sử dụng Chai hiện có, không thêm dependency).
node --test postman/collection.test.mjs
```

Self-check này mô phỏng bề mặt `pm` và các response; không thay cho chạy Collection Runner, kiểm tra schema đầy đủ bằng Postman hoặc kiểm thử backend thật.

Collection tạo dữ liệu demo thật trong DB và dùng mock payment gateway. Nếu dừng giữa chừng có thể còn chuyến đang hoạt động và driver online; không tự xóa dữ liệu. Có thể PUT offline driver demo bằng token của nó hoặc chờ vị trí hết hạn. Tài xế được offer có thể khác tài xế vừa tạo nếu có driver khác gần hơn; collection lấy token Development đúng ID được offer.

Smoke test qua Gateway xác minh luồng và replay ở mức API; **chưa chứng minh một charge duy nhất/cặp ledger duy nhất, refund, retry lỗi tạm thời hoặc duplicate event**. Các kiểm tra này cần test Payment/integration hiện có. Không thêm route Payment public để phục vụ Postman.

Tham khảo chính thức: [Collection v2.1](https://schema.postman.com/json/collection/v2.1.0/docs/index.html), [điều khiển thứ tự Runner](https://learning.postman.com/docs/tests-and-scripts/running-collections/building-workflows), [biến Postman](https://learning.postman.com/docs/use/send-requests/variables/variables).

## 8. Nguồn và khác biệt với tài liệu cũ

- Nguồn HTTP: `src/ApiGateway/GatewayApplication.cs`, `appsettings.json`; `src/TripService/Api/TripEndpoints.cs`, `TripContracts.cs`; `src/MatchingService/Api/MatchingEndpoints.cs`; `src/PaymentService/Api/PaymentEndpoints.cs`, `Requests/`, `Application/Payments/Dtos/` và use case tương ứng.
- [AGENTS.md](../AGENTS.md), [đề bài](requirements.md), [xác thực](authentication.md), [luồng end-to-end](end-to-end-booking.md), [Matching & Pricing](matching-pricing.md) cung cấp phạm vi và quy tắc kiến trúc. Event là hợp đồng khác HTTP, xem [contracts V1](messaging-contracts-v1.md).
- Một số docs cũ nhắc `paymentId`/`fareCurrency` trong response Trip; DTO hiện tại chỉ có `currency`, không trả `paymentId`. Không viết assertion dựa trên trường không tồn tại.
- Phần cuối tài liệu Matching cũ nói Trip/Payment còn demo chưa tích hợp và nội bộ chỉ dựa vào network; code hiện có tích hợp Trip–Matching–Payment và kiểm tra internal key. Tài liệu này mô tả code hiện tại, không thay đổi kiến trúc.
- README từng công bố số test pass của lần triển khai trước; đó không phải bằng chứng kiểm thử cho lần thay đổi tài liệu này.
