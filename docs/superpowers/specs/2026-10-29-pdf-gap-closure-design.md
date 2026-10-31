# Thiết kế hoàn thiện SwiftRide theo đề PDF

Khởi tạo: 29/10/2026. Rà soát cuối: 30/10/2026. Trạng thái: thiết kế để lập kế hoạch, chưa triển khai.

## 1. Mục tiêu và nguồn yêu cầu

Người dùng yêu cầu lên kế hoạch cho cả ba nhóm trong lần rà soát: sửa phần chưa đủ; triển khai kiến trúc mở rộng của PDF; điều chỉnh phần vượt yêu cầu và phần dư. Đây là yêu cầu lập kế hoạch, không phải lệnh sửa sản phẩm hoặc triển khai lên Supabase.

Nguồn: `RideLite-KienTrucPhanMem.pdf`, `AGENTS.md`, code và test tại working tree hiện tại. `docs/requirements.md` là bản diễn giải đã điều chỉnh DB, không thay thế PDF gốc. Không sửa PDF.

Bằng chứng của lần rà soát trước: backend build với `--no-restore -m:1` thành công, 8 cảnh báo NuGet; backend 331 pass/38 skip; frontend 96 pass, typecheck/build thành công. Đây là baseline lịch sử, không phải kết quả thực thi kế hoạch này. 38 test bị skip không chứng minh CSDL, race, recovery hoặc FullFlow hoạt động.

Quyết định dưới đây là đề xuất kỹ thuật của người lập kế hoạch, chưa coi là lựa chọn đã được người dùng xác nhận.

## 2. Ràng buộc

- Giữ tối đa ba business service: Trip, Matching & Pricing, Payment. Worker nằm trong service sở hữu.
- Giữ SQL Server TripDB, Supabase PostgreSQL `matchdb` và `paydb`; không truy vấn chéo DB/schema.
- Client chỉ qua YARP. Endpoint payment/matching nội bộ không public.
- Không đổi 11 trạng thái, không cho rider hủy sau `DriverAccepted`, không thêm trạng thái tài chính vào Trip tùy tiện. `PaymentPending` không terminal.
- Domain quyết định transition; Application điều phối; Infrastructure lưu trữ và transport.
- Payment/consumer idempotent, ledger append-only và balanced trong một transaction.
- Giữ fare snapshot; dùng mock token alias, không đưa payment token thực vào event/outbox/log.
- Giữ .NET 10/EF Core 10, xUnit/Moq và cấu trúc hiện có. Không thêm Identity Service, Redis, Kafka, Kubernetes, event sourcing hoặc CQRS framework.
- Code, identifiers và comments mới bằng English; tài liệu và thông báo cho người dùng bằng tiếng Việt.
- Không tự commit/push; không xóa volume hoặc reset DB thật. Không ghi đè các thay đổi đang có của người dùng.

## 3. Các mốc và phương án

Chọn mở rộng tăng dần từ luồng REST đang có. Viết lại đồng loạt sang messaging sẽ làm mất baseline chạy được; giữ hai luồng charge đồng thời sẽ khó chứng minh an toàn.

1. **Mốc A — MVP có bằng chứng:** sửa script, reliability/config/domain, dependency; tinh gọn Compose; chạy integration/FullFlow bằng DB disposable và kiểm tra quyền runtime.
2. **Mốc B — kiến trúc đầy đủ:** contracts, outbox/inbox, RabbitMQ, choreography thanh toán/bồi thường, OIDC và telemetry. Chỉ triển khai sau mốc A.
3. **Mốc C — bản bàn giao:** kiểm tra cả hai transport, sự cố, bảo mật, UI và tài liệu; công bố rõ các phụ thuộc môi trường.

Tinh gọn hạ tầng thực hiện sớm trước nghiệm thu A; dọn UI/tài liệu cuối cùng ở C. Không đợi xong messaging mới sửa PostgreSQL dư.

## 4. Sửa MVP và cấu hình

- Sửa parser PowerShell `${i}:`; script lấy access token qua Gateway Development hoặc nhận token được cấp bên ngoài. Không hướng dẫn truyền signing key thật trên command line.
- Smoke qua Gateway kiểm tra trạng thái, replay command, actor sai. Việc kiểm chứng ledger thực hiện trong integration test/verification script dùng credentials test, không mở API ledger public.
- Integration runner kiểm tra exit code ngay sau lệnh native; có suite `FullFlow`, và `All` bao gồm Trip race, Payment recovery, Matching persistence, FullFlow. Fixtures chỉ kết nối localhost và DB test có tên cho phép; guard trước mọi DDL/destructive setup.
- FullFlow phải có bài dùng persistence Matching thật; bài dùng `FullFlowMatchingStore` giữ làm HTTP/component test. Mốc A chạy hai lần liên tiếp, không skip các bài bắt buộc.
- Matching network call tối đa hai attempt, mỗi attempt timeout 3 giây; quote timeout 4 giây; Payment HTTP timeout 12 giây; mock gateway timeout 10 giây. Giữ timeout có sẵn nếu tương đương, đưa giá trị cần chỉnh vào options validated.
- Retry transient: lỗi kết nối/timeout, HTTP 408/429/500/502/503/504. HTTP 400/401/403/409, payload không hợp lệ và caller cancellation không tự retry. Hủy request không xóa durable intent; worker xử lý lại theo lease bằng token của worker.
- Backoff worker: `min(maxDelay, initialDelay * 2^(attempt-1))`, cộng jitter trong khoảng 0..25%, rồi chặn ở maxDelay. Tôn trọng `Retry-After` hợp lệ trong deadline. Clock và random có thể kiểm soát trong test; không dùng sleep thực để test công thức.
- `TripLifecycle:MaxDriverRejections=3`; matching recovery 5 attempt; payment recovery 20 attempt/10 phút, initial 2 giây/max 60 giây. Payment gateway charge tối đa 3 attempt riêng biệt, không bị reset bằng message mới. Claim cuối cùng vẫn được xử lý; không lỗi lệch một lần tại biên.
- Bind và validate options on startup. Snapshot budget/deadline vào job; thay cấu hình không nới job cũ. Không reset attempt khi restart.
- Chuyển mọi quyết định `Requested -> Failed` khỏi `TripWorkStore` sang `TripWorkCoordinator` và Domain. Store chỉ thay đổi trạng thái kỹ thuật receipt/job.
- Truy nguồn `System.Security.Cryptography.Pkcs`, nâng dependency nhỏ nhất có bản vá và tương thích .NET 10; kiểm tra advisory tại thời điểm thực thi. Không thêm override package nếu nâng dependency cha đã đủ.
- Pricing chỉ có ride mode `standard` ở MVP; surge/toll/promotion/tax vẫn là các bước áp dụng đúng công thức, không tạo mode giả chỉ để đủ tên pattern. `TripLifecycle:SupportedPricingModes=["standard"]` là allowlist input cấu hình thay cho nhánh hard-code; Matching vẫn sở hữu thuật toán và đăng ký strategy. Một mode mới cần implementation+DI Matching và cập nhật allowlist cấu hình Trip, không sửa strategy/validation cũ. Snapshot mode để recovery dùng lại; unsupported mode bị từ chối trước tạo durable intent. Một test strategy bổ sung chứng minh cơ chế, không thêm mode kinh doanh vào UI.

## 5. Messaging, dữ liệu và chuyển đổi

### 5.1 Contracts

Plain records trong `src/SwiftRide.Contracts/V1/`, không Domain/EF/framework:

- `TripAcceptedV1`: event ID, TripId, RiderId, DriverId, thời điểm, correlation/causation.
- `RideCompletedV1`: các ID trên, QuoteId, amount/currency snapshot, method, mock token alias, payment idempotency key và thời điểm.
- `PaymentSettledV1`: event ID, payment/trip/rider/driver IDs, amount/currency, payment key, thời điểm và correlation/causation.
- `PaymentFailedV1`: trip/payment IDs, key, failure code an toàn, loại lỗi permanent/exhausted, metadata. Lỗi transient còn retry không phát failure nghiệp vụ cuối cùng.
- `PaymentCompensationRequestedV1`, `PaymentRefundedV1`, `PaymentCompensationFailedV1`: payment/trip IDs, key khoản thu, reason code, metadata và refund ID khi có.

Event ID được tạo và lưu một lần trong outbox. Publish/redelivery giữ ID, key và payload; không tạo ID mới để né dedup. Broker `MessageId` bằng event ID. Consumer validate payload và identity trước side effect.

### 5.2 Outbox/inbox

Chọn outbox/inbox nhỏ, tường minh theo từng owner; MassTransit chỉ làm transport/consumer. Không tạo `Common` hay worker service. Không đưa `MassTransit.EntityFrameworkCore` EF9 vào dự án EF10 chỉ để lấy outbox.

- TripDB thêm `IntegrationOutbox`, `IntegrationInbox`; `TripPaymentJobs` thêm `Transport` (`Rest`/`Events`), kết quả xử lý và thông tin compensation kỹ thuật cần thiết.
- PayDB thêm `integration_outbox`, `integration_inbox`, metadata recovery message cần thiết; migration SQL tăng dần, có RLS/grant đúng `swiftride_payment`. Không chạy lại SQL khởi tạo và không `EnsureCreated` lên Supabase.
- Mỗi outbox có unique message ID, type version, payload, trace metadata, attempt, next-attempt, lease owner/expiry, published-at, last safe error và requires-action.
- Inbox unique `(ConsumerName, MessageId)`. Receipt được commit cùng thay đổi business và event đi ra. Gateway charge/refund nằm ngoài DB transaction dài và luôn dùng idempotency key cũ; inbox không thay thế gateway idempotency.
- Background publisher claim nguyên tử/fence owner, timeout publish 10 giây, poll 2 giây, lease 30 giây, tối đa 10 attempt, backoff+jitter cap 60 giây. Hết budget giữ bản ghi RequiresAction, không xóa event.
- Crash sau publish trước đánh dấu sẽ gửi lại cùng ID. Consumer trùng không thêm charge/ledger/transition.

### 5.3 Chọn transport và khôi phục

`PaymentWorkflow:Transport=Rest|Events`, mặc định `Rest` đến khi mốc B qua nghiệm thu. Snapshot lựa chọn vào Trip khi tạo; dùng lựa chọn đó cho cả vòng đời, kể cả khi cấu hình đổi/restart. Thay cấu hình chỉ ảnh hưởng chuyến mới.

- `Rest`: dùng `IPaymentClient` hiện có và job reconciliation. Không enqueue `RideCompletedV1` làm lệnh charge.
- `Events`: dropoff ghi `PaymentPending` và `RideCompletedV1` vào cùng Trip transaction; không gọi `ChargeAsync` từ HTTP request hoặc worker REST. Payment consumer gọi use case hiện có; chốt kết quả và stage outbox trong cùng PayDB transaction; Trip consumer xác thực rồi áp dụng Domain transition.
- Không vừa bật đường REST charge vừa bật đường event cho cùng chuyến. Đọc đối soát REST được phép trong Events khi kết quả bất thường; nó không thu tiền.
- Khi dừng messaging, drain outbox/in-flight Events hoặc giữ workers Events phục vụ chuyến cũ. Rollback cấu hình cho chuyến mới không tự chuyển chuyến Events cũ sang REST.
- Broker chết: endpoint vẫn commit outbox và trả `PaymentPending`; broker trở lại thì publisher tiếp tục. Endpoint không đợi publish broker.
- RabbitMQ dùng volume `rabbitmq-data`, durable queue/exchange, persistent message và publisher confirmation. Bootstrap definitions không chứa user/password tạo sẵn queue/binding trước producer đầu tiên, tránh publish được xác nhận nhưng chưa có subscriber queue. Names được pin trong cấu hình topology và tests, không phụ thuộc tên class tự sinh. Không xóa volume khi rollback.
- Message consumer dùng một bounded retry policy: tối đa 3 retry sau lần đầu, exponential+jitter với min 30 giây/max 120 giây, tổng chờ không quá 300 giây; attempt gateway vẫn cap 3. Validation/permanent failure không retry. Dùng retry middleware của MassTransit với backoff+jitter được kiểm tra từ source/test phiên bản đã pin; nếu công thức built-in khác bound thì cấu hình custom retry policy nhỏ trong Infrastructure, không thêm scheduler.
- Retry middleware giữ consumer slot trong lúc chờ: chấp nhận cho tải demo nhỏ, giới hạn concurrent 2/prefetch 4 mỗi endpoint; ghi rõ trade-off này trong docs. Không thêm delayed-message plugin hoặc Quartz chỉ để trì hoãn message. Gateway success chưa finalize có thể đối soát receipt sau lease expiry; test cấu hình khoảng thời gian nhỏ hơn cho fault scenarios nhưng không đổi logic.
- Hết retry vào `<endpoint>_error` của MassTransit; gọi rõ đây là error queue, không giả định RabbitMQ DLX hoặc delayed redelivery đã được cấu hình. Có hướng dẫn xem/replay sau khi sửa nguyên nhân, giữ message ID; broker redeliver/restart vẫn không reset counter gateway lưu DB.
- Message đang bị lease bởi một worker khác không được ack như đã hoàn thành nếu owner kia có thể chết. Consumer phải quan sát completed hoặc báo retryable busy; inbox không được commit completed cho một payment/refund Pending chưa có đường recovery. Outbox và gateway budgets độc lập nhưng đều không được reset bởi redelivery.
- Phân biệt hết budget business với lỗi hạ tầng chưa biết kết quả: consumer Payment ghi RequiresAction và PaymentFailedV1 cùng transaction chỉ khi kết quả xác định (declined hoặc các attempt đều được xác nhận transient không thu tiền). Timeout/DB mất kết nối/error queue không chứng minh thất bại thanh toán. Trường hợp chưa rõ giữ Pending/RequiresAction và đối soát receipt bằng key; nếu receipt đã success thì finalize dù budget gọi gateway đã hết. Message transport poison vào error queue không được tự phát PaymentFailedV1.

### 5.4 Phiên bản và license

Đề xuất pin `MassTransit`/`MassTransit.RabbitMQ`/test harness cùng **8.5.8**, kiểm tra restore/build/audit trước khi chấp nhận. Package này tồn tại và bản source tag có license Apache-2.0. Không tự nâng major 9. Không gọi đây là bản mới nhất hoặc mặc nhiên an toàn; nếu audit tìm advisory chưa vá, phải giải quyết điều kiện package trước khi đưa messaging vào runtime.

Nguồn đã đọc ngày 29/10/2026: [NuGet RabbitMQ 8.5.8](https://www.nuget.org/packages/MassTransit.RabbitMQ/8.5.8), [MassTransit 8.5.8](https://www.nuget.org/packages/MassTransit/8.5.8), [license tại tag](https://github.com/MassTransit/MassTransit/blob/v8.5.8/LICENSE).

## 6. Saga choreography và compensation có giới hạn

Không thêm central orchestrator hoặc thay luật cancellation. Happy path: Trip phát RideCompleted → Payment xử lý → PaymentSettled → Trip Paid. PaymentFailed permanent/exhausted giữ Trip PaymentPending/RequiresAction.

Tình huống compensation demo: Payment đã settled cho đúng TripId/rider/driver/key nhưng kết quả tài chính không khớp fare snapshot. Trip xác minh lại kết quả thật qua API nội bộ `GetByTripAsync` trước khi yêu cầu refund; event giả/sai identity chỉ bị quarantine/RequiresAction, không được hoàn một payment khác. Sau khi xác minh mismatch thật, Trip stage một `PaymentCompensationRequestedV1` trong local transaction với trạng thái xử lý kỹ thuật.

Chỉ tự kích hoạt compensation khi Trip vẫn PaymentPending. Event mâu thuẫn đến sau Paid/Cancelled/Failed chỉ tạo incident RequiresAction và không tự refund/chuyển trạng thái. Khi technical resolution đã CompensationRequested/Refunded, một PaymentSettled đến trễ không được chuyển Trip thành Paid. Guard và inbox/technical resolution được lưu trong cùng local transaction để tránh race giữa settle và compensation.

Payment xác minh command thuộc đúng payment/trip/key rồi gọi refund toàn phần hiện có bằng `payment-{paymentId}-refund`. Refund settled và hai bút toán đảo cùng `PaymentRefundedV1` nằm trong một local transaction. Refund transient dùng bounded retry; declined/exhausted phát compensation failed và requires-action. Trip giữ `PaymentPending`, lưu kết quả compensation vào technical record và yêu cầu xử lý thủ công, không thêm trạng thái Trip và không giả `Paid`.

Để không có hai consumer đồng thời gọi refund gateway, thêm `paydb.refund_processing` keyed RefundId, cùng transaction tạo Refund Pending: owner/lease 60s, attempt cap 3, outcome và fencing. Cả REST refund lẫn Events dùng cùng store, không giữ fallback không khóa trong production. Gateway idempotency vẫn bắt buộc để chịu được crash sau side effect; trước khi kết luận budget hết, đối soát durable mock receipt. Không có receipt xác định thì giữ Pending/RequiresAction, không giả refund failed hoặc settled. Saga technical record không thay thế ledger hay transaction.

Demo dùng fault injection chỉ trong test để tạo mismatch được xác minh; không mở endpoint bất kỳ cho rider hủy/refund sau chuyến. Refund là một capability business có thật; kịch bản fault injection là bằng chứng compensation, không tuyên bố đã có chính sách vận hành hủy chuyến sau charge.

## 7. OIDC và telemetry

- Tích hợp một nhà cung cấp OIDC bên ngoài qua `Authority`, `Audience`, HTTPS metadata/JWKS, issuer/lifetime/algorithm validation. Không triển khai Identity Service.
- Gateway, Trip và Matching dùng cấu hình issuer/claims nhất quán, giữ authorization tài nguyên. Role lấy từ claim được issuer tin cậy phát; client không tự chọn rider/driver production. Dev-token chỉ Development và không là fallback production khi OIDC cấu hình sai.
- WebApp Authorization Code + PKCE; callback/signout, token expiry/401, không client secret trong SPA. Dùng thư viện OIDC có sẵn được pin sau kiểm tra package/license, không tự viết protocol.
- Thông số phải có khi nghiệm thu nhà cung cấp thật: Authority, API audience, SPA client ID, redirect/logout URI, role claim, và hai tài khoản rider/driver. Kế hoạch không cần chờ các giá trị này; test dùng discovery/JWKS fixture trong test host. Mốc nghiệm thu OIDC thật phải báo blocked nếu chưa được cung cấp, không coi fixture là đăng nhập thật.
- OpenTelemetry cho Gateway và ba service: ASP.NET/HttpClient/activity của business, messaging trace; propagation W3C traceparent/tracestate. CorrelationId validate độ dài/format, tạo nếu thiếu, đi qua HTTP, outbox/inbox và log; không dùng user-supplied header để quyết định auth.
- Metrics: trip outcome counter, matching/API duration histogram, payment failure counter; queue depth do broker exporter/management cung cấp, không suy ra từ số message consumer thấy. Label cardinality thấp, không TripId/UserId/token.
- OTLP collector/backend chạy profile `observability` dành demo. Đây là hạ tầng quan sát, không business service thứ tư. Không log token/Authorization, địa chỉ hoặc dữ liệu cá nhân không cần thiết.

## 8. Giữ, tinh gọn và sửa tài liệu

| Hạng mục | Quyết định |
|---|---|
| React, hai vai trò, bản đồ | Giữ làm client demo; production actor theo token, mode demo offline được gắn nhãn rõ. |
| Nominatim/cache/pacing | Giữ nhưng tách bật/tắt cấu hình; tọa độ/chọn điểm bản đồ vẫn đặt xe được khi search lỗi. |
| Current/history/driver-feed/heartbeat | Giữ vì đang có caller và giúp demo; không thêm push notification service. |
| Job lease, receipt, mock receipts | Giữ vì cần reliability; mode REST/Events quyết định đường xử lý, không để hai hệ thống charge cạnh tranh. |
| PostgreSQL runtime dư | Bỏ service `postgres`, `payment-data`, biến POSTGRES khỏi Compose runtime/Supabase; giữ Postgres trong `docker-compose.test.yml`. Không xóa volume trên máy. |
| Port Payment 5285 trong override tự load | Chuyển thành file override debug phải chỉ định rõ; default chỉ publish Gateway. Debug vẫn cần internal key. |
| RabbitMQ | Profile `messaging`, service tự bật Events chỉ khi cấu hình khớp; không còn là container không có consumer. |
| Script skeleton 501 | Lưu lịch sử hoặc bỏ sau tìm tất cả tham chiếu; hướng dẫn nghiệm thu dùng script hiện hành. Test Gateway dùng upstream stub 501 vẫn hợp lệ, không xóa máy móc. |
| Docs cũ/mâu thuẫn | Một trang trạng thái có ngày và bằng chứng; các tài liệu khác link về đó, ghi rõ historical/design-only. Không xóa lịch sử plan chỉ vì cũ. |
| Strategy mới | Chứng minh cơ chế mở rộng bằng test, không thêm mode/option không có nghiệp vụ. |

## 9. Tiêu chí bàn giao

Mốc A: build/test liên quan pass, các integration bắt buộc không skip, HTTP thật dùng cả ba persistence, retry same key giữ đúng một charge và journal cân bằng; Compose mặc định chỉ expose Gateway.

Mốc B: Events hoàn tất tới Paid; broker outage/restart, duplicate event, outbox crash, wrong event, transient/exhausted và compensation không thêm charge/refund. Tất cả sự cố được assert vào trạng thái bền vững, không chỉ kiểm tra log.

Mốc C: OIDC fixture pass và provider thật được báo riêng; có một trace từ HTTP tới message tới Payment; có metric và error queue demo; tài liệu phân biệt implemented/tested/external-blocked. Build/test/smoke cuối cùng có log và thời điểm thực chạy. Không lấy checklist đánh dấu sẵn làm bằng chứng.

## 10. Kết quả rà soát thiết kế

Một lượt builder check độc lập đã chỉ ra và được bổ sung: guard compensation chỉ khi PaymentPending/không settle sau refund; phân biệt failure xác định với kết quả tài chính chưa rõ do lỗi hạ tầng; claim/fencing bền vững cho refund cả REST và Events. Các lựa chọn này được người lập kế hoạch quyết định trong phạm vi giữ nguyên luật chuyến đi, không cần thay chính sách hủy chuyến.
