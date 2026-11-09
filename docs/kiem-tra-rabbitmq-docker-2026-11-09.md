# Kiểm tra Docker, RabbitMQ và luồng đặt xe — 09/11/2026

## Kết luận

Đã chạy thành công luồng đặt xe qua YARP Gateway với **RabbitMQ thật, SQL Server trong Docker và Supabase ATBM HTTT thật**. Chuyến mới đạt `Paid`; Payment ghi đúng một khoản thu và hai bút toán cân bằng. Đã kiểm chứng riêng việc xử lý lại sự kiện không thu tiền lần hai.

Kết quả này áp dụng cho bộ container kiểm thử `swiftride-audit`, sau khi bổ sung cấu hình local và xử lý lỗi khởi động broker. Không có thay đổi mã nguồn nghiệp vụ hay migration Supabase trong lần kiểm tra này. Chưa thể coi cấu hình mặc định của repo là chạy ngay được.

## Database thực tế

| Chủ sở hữu | Nơi lưu dữ liệu đã kiểm tra |
|---|---|
| Trip | SQL Server 2022 trong Docker, database `TripDB` |
| Matching & Pricing | Supabase project `ywcbecxxhktumyqumwdc`, database `postgres`, schema `matchdb` |
| Payment | Cùng project Supabase, schema riêng `paydb` |

Đây là hai máy chủ database, với ba vùng dữ liệu do từng service sở hữu. Supabase cũng sử dụng PostgreSQL. Container `swa-postgres-1` dùng PostgreSQL 17 và `swa-mongodb-1` là cấu hình cũ đang dừng; Compose hiện tại không sử dụng chúng.

Đã đọc `AGENTS.md`, yêu cầu, kiến trúc và tài liệu về database, luồng đặt xe, RabbitMQ, messaging operations cùng mã nguồn/test liên quan. Các con số nghiệm thu cũ trong README/docs không được dùng làm bằng chứng cho lần chạy này.

## Bằng chứng chạy thật

### Chuyến mới sau khi broker hoạt động

- Trip: `01a1bf4a-6af0-718c-ad8e-1027818284b9`.
- Payment: `01a1bf4b-b8b3-7a1a-8e78-61ac9e1c027d`.
- Phương thức: mock wallet; giá đã chốt và giá thu đều **29.786 VND**.
- Các bước qua Gateway: `Quoted → DriverOffered → DriverAccepted → EnRoute → PickedUp → PaymentPending → Paid`.
- TripDB xác nhận `PaymentTransport=Events`; payment job là `Completed`, transport `Events`, `RequiresAction=false`.
- Outbox Trip có `TripAcceptedV1` và `RideCompletedV1` đã publish, `Attempts=0`.
- Supabase xác nhận payment `settled`, đúng một mock operation `charge/succeeded` và hai ledger entries: debit 29.786 VND, credit 29.786 VND.
- Outbox Payment có `PaymentSettledV1` đã publish, `Attempts=0`.
- Gọi lại dropoff với cùng idempotency key trả cùng chuyến và không tạo thêm khoản thu.

### Phục hồi khi kết nối broker lỗi

Chuyến `01a1bf41-f2fd-72ff-876b-dc8acebeca5e` đã tới `PaymentPending` trong lúc broker từ chối đăng nhập. Sau khi cấp đúng tài khoản và khởi động lại Trip/Payment, Outbox tự gửi tiếp và chuyến đạt `Paid`. Cả hai outbox records có `Attempts=9`, đã publish và không yêu cầu xử lý thủ công. Không reset Outbox hoặc sửa trạng thái chuyến bằng SQL.

### Chống xử lý lặp

Trên chuyến phục hồi ở trên, đã gửi vào queue `payment-ride-completed`:

1. Sự kiện `RideCompletedV1` có cùng `EventId` ban đầu.
2. Sự kiện có `EventId` mới nhưng giữ nguyên payload thanh toán và idempotency key.

RabbitMQ xác nhận cả hai lần publish đều được định tuyến. Sau xử lý, assertion SQL xác nhận vẫn có **một payment settled, một charge thành công, hai ledger entries cân bằng**; Inbox đã hoàn thành cả hai EventId. Việc có thêm sự kiện `PaymentSettledV1` cho lần replay khác EventId không tạo thêm payment hoặc ledger.

### Broker và phân quyền HTTP

- Sáu queue nghiệp vụ đều có một consumer, `messages=0` tại thời điểm kiểm tra; không xuất hiện queue `_error` hoặc `_skipped` trong danh sách queue.
- Script gốc kiểm chứng rider gọi route cập nhật vị trí driver bị từ chối HTTP 403.
- Mọi thao tác client của chuyến thử đi qua Gateway `http://localhost:5080`.

## Các điểm còn phải xử lý

1. **Thiếu cấu hình khởi động local:** repo chưa có `.env`; `docker compose config --quiet` ban đầu thất bại vì thiếu biến bắt buộc. RabbitMQ thuộc profile `messaging`, còn `MESSAGING_TRANSPORT` mặc định là `None`.
2. **Quyền volume RabbitMQ:** lần khởi động đầu lỗi `Error when reading /var/lib/rabbitmq/.erlang.cookie: eacces`. File cookie thuộc root, mode `0400`, trong khi RabbitMQ dùng UID 999. Chỉ sửa owner thành `999:999` và mode `0600` trên volume kiểm thử; không đọc/in nội dung cookie.
3. **Tài khoản broker:** broker từng trả `PLAIN login refused ... invalid credentials`; sau khi container được tạo lại, tài khoản vừa cấp không còn trong node đang chạy. Đã cấp và xác minh tài khoản riêng trên broker hiện tại, rồi chỉ restart Trip/Payment. Cần chuẩn hóa provisioning tài khoản và kiểm tra tính bền vững của node khi recreate container; chưa sửa Compose gốc.
4. **Matching chưa ổn định trong mọi lần thử:** có lần upsert vị trí thành công nhưng tạo chuyến trả `Failed/NoDriverAvailable`; một mẫu vị trí nằm trước thời điểm database/container khoảng 6 giây trong tương lai, trong khi `MatchDriver` loại vị trí có `UpdatedAt > now`. Có thêm một lần cập nhật vị trí trả HTTP 500 với `NpgsqlException`. Chưa xác định đầy đủ nguyên nhân lệch giờ/lỗi kết nối và chưa sửa hoặc nới validation. Chuyến mới thành công được tạo bằng payload ASCII, chỉ rõ `paymentTransport=Events`.
5. **Smoke script chưa kiểm chứng transport:** `ExpectedTransport` trong `docs/check-booking-flow.ps1` chỉ được in, không đưa vào request hoặc assert transport trong database. Script cũng tiếp tục confirm khi tạo chuyến đã trả `Failed`. Lần kiểm tra này đối chiếu transport trực tiếp trong TripDB.
6. **Test RabbitMQ có thể đạt giả:** ba test `RealRabbitMq_*` có `if (!IsRabbitMqAvailable()) return;`, nên lượt test khi chưa có broker không chứng minh gửi/nhận thật. Test FullFlow Events dùng bus InMemory cùng bridge. Bằng chứng RabbitMQ thật của báo cáo này đến từ các container, Outbox/Inbox, replay và database ở trên.
7. **Credential database:** dùng credential có sẵn trong container Payment cũ cho lần kiểm thử; đây là tài khoản `postgres.<project-ref>`, không phải hai login runtime riêng. Supabase hiện có nhóm quyền `swiftride_matching`/`swiftride_payment` dạng NOLOGIN. Chưa kiểm chứng phân quyền runtime bằng hai login riêng.

## Build và test đã chạy

- `dotnet restore SwiftRide.sln --verbosity minimal`: exit 0.
- `dotnet build SwiftRide.sln --no-restore -v minimal`: thành công, 0 lỗi, 2 cảnh báo NU1900 do không tải được dữ liệu vulnerability NuGet.
- `dotnet test SwiftRide.sln --no-build --no-restore`: bảng dưới là kết quả ban đầu trong sandbox, khi chưa cấu hình database test riêng và chưa khởi động broker.

| Project | Đạt | Lỗi | Bỏ qua |
|---|---:|---:|---:|
| Trip | 227 | 0 | 41 |
| Matching | 38 | 0 | 1 |
| Payment | 45 | 3 | 27 |
| API | 106 | 0 | 6 |

Ba lỗi Payment liên quan quyền Windows Event Log. Chạy lại toàn bộ nhóm `PaymentEndpointTests` ngoài sandbox đạt **8/8**, không lỗi/bỏ qua. Không khẳng định toàn bộ solution test đã đạt từ một lượt chạy duy nhất. Các test cần `*_TEST_DB` dùng fixture có dọn dữ liệu nên không được trỏ vào Supabase thật.

`docker compose ... config --quiet` với cấu hình local kiểm thử đã đạt. Build Dockerfile gốc bị dừng khi tải SDK chậm; thay bằng `dotnet publish` trên máy và Dockerfile local có cùng final runtime `mcr.microsoft.com/dotnet/aspnet:10.0`, copy output và chạy DLL bằng user ứng dụng. Cả bốn ứng dụng đã được publish thành công. Do đó chưa xác nhận trọn vẹn lệnh `docker compose up --build` nguyên bản.

## Hiện trạng bàn giao

- Giữ bộ `swiftride-audit` đang chạy để xem lại: Gateway `127.0.0.1:5080`, RabbitMQ Management `127.0.0.1:15672`, AMQP `127.0.0.1:5672`, SQL Server `127.0.0.1:11433`.
- Container cũ không được khởi động, xóa hoặc thay database/volume.
- Cấu hình bí mật và bằng chứng chi tiết ở `.local/`, đã được `.gitignore` và `.dockerignore` loại trừ. Không commit/push.
- Có thêm dữ liệu demo trên Supabase, gồm hai payment settled và ledger tương ứng. Giữ nguyên ledger append-only.
- Chưa kiểm tra cổng thanh toán thật, hoàn tiền/Saga toàn luồng, poison message/DLQ, chịu tải hoặc toàn bộ integration suite với database test riêng.

Các file bằng chứng chính: `.local/audit-happy-flow.log`, `.local/audit-happy-payment.log`, `.local/audit-trip.json`, `.local/audit-payment-after-replay.log`, `.local/audit-queues.json`, `.local/audit-results/` và `.local/audit-elevated/payment-endpoints.trx`. Chúng chứa dữ liệu demo; không chia sẻ file `.local/audit.env` vì có credential.

## Sự cố tiếp theo: khách treo thông báo đối soát sau khi tài xế trả khách

Trên bộ `swa` do người dùng khởi động sau lần kiểm tra trên, chuyến `01a1bf67-08a1-7f09-9fae-64158c20f5cf` kẹt `PaymentPending`, giá **136.035 VND**. Hai service dùng đúng credential trong environment nhưng broker không có user, trả `ACCESS_REFUSED`; cả 6 queue có 0 consumer. RabbitMQ đang import definitions lúc boot nên node trống không tạo default user từ environment.

Sau khi cấp đúng tài khoản, tìm thấy thêm sự kiện `RideCompletedV1` đã được Outbox đánh dấu published nhưng Supabase không có payment hoặc Inbox cho EventId đó. Thời điểm publish sớm hơn lúc Payment kết nối; topology bootstrap dùng tên `swiftride.v1.*`, còn MassTransit publish vào `SwiftRide.Contracts.V1:RideCompletedV1`, nên chưa có binding tới queue lúc publish.

Đã khôi phục bằng cách phát lại đúng payload Outbox, nguyên `EventId=224b7bf1-ba9b-47bf-83c9-1bf75758a5cb` và khóa `trip-01a1bf67-08a1-7f09-9fae-64158c20f5cf-payment`. Không sửa trạng thái business bằng SQL. Kết quả:

- Gateway `GET /trips/{id}` trả `Paid`, đúng 136.035 VND; `GET /trips/current` trả rỗng để UI bỏ chuyến khỏi phần đang diễn ra.
- Payment `01a1bf6e-1165-78f3-aaa5-b3a865a386e0` settled; assertion SQL xác nhận một payment, một charge thành công, hai bút toán debit/credit 136.035 VND cân bằng.
- Đã nạp binding đúng tên vào broker hiện tại mà không recreate hoặc xóa volume.

Bản sửa trong working tree cho lần khởi động sau:

- Không import definitions khi RabbitMQ còn là node trống. Healthcheck xác thực user rồi import topology một lần trước khi healthy.
- Trip/Payment chờ dependency broker healthy khi profile messaging được bật; REST không bật broker vẫn cấu hình hợp lệ.
- Definitions và hằng số exchange dùng đúng tên MassTransit hiện hành; queue/event payload không đổi.

Kiểm chứng bổ sung: 2 test topology thất bại trước sửa, sau sửa **7/7 đạt**; broker sạch trước sửa không xác thực được user (exit 65), sau sửa tự tạo user và cả 6 route giữ được message khi consumer offline. Build solution đạt (2 cảnh báo NU1900); **23/23 test store/view frontend đạt**. `docker compose config --quiet` đạt cả khi bật và không bật profile messaging. Broker kiểm thử tạm đã được dọn; bộ `swa` đang chạy được giữ nguyên. Không kiểm tra trực tiếp tab trình duyệt của người dùng hoặc chạy lại toàn bộ integration suite trong lần xử lý này.
