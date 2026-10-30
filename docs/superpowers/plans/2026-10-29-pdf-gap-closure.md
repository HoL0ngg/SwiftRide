# Kế hoạch hoàn thiện SwiftRide theo đề PDF

> **Dành cho người thực thi:** đọc thiết kế đi kèm trước; dùng skill `executing-plans` để làm lần lượt từng task. Mỗi bước dùng checkbox để theo dõi. Không tự commit hoặc push. Tài liệu này là kế hoạch, chưa phải bằng chứng đã triển khai.

**Mục tiêu:** xử lý cả ba nhóm của bản rà soát: phần chưa đủ, kiến trúc mở rộng và phần vượt yêu cầu/dư, với các mốc nghiệm thu riêng.

**Kiến trúc:** hoàn thiện baseline REST trước, sau đó thêm RabbitMQ cùng outbox/inbox và choreography trong chính Trip/Payment. Snapshot transport theo chuyến để REST và Events không cùng thu tiền. Giữ ba service, DB riêng và client qua YARP.

**Công nghệ:** .NET 10, EF Core 10, SQL Server, PostgreSQL/Supabase, YARP, RabbitMQ/MassTransit, OpenTelemetry, React/TypeScript, xUnit/Moq/Vitest.

**Thiết kế:** [2026-10-29-pdf-gap-closure-design.md](../specs/2026-10-29-pdf-gap-closure-design.md). Nguồn gốc: PDF trong root, `AGENTS.md`, working tree tại ngày 29/10/2026.

Khởi tạo ngày 29/10/2026, rà soát cuối ngày 30/10/2026. Tên file giữ ngày khởi tạo để liên kết ổn định.

## Ràng buộc toàn cục

- Tối đa ba business service: Trip, Matching & Pricing, Payment. Background work nằm trong owner.
- SQL Server TripDB; Supabase PostgreSQL `matchdb`/`paydb`, credentials riêng, không query chéo.
- Không sửa PDF; không đổi 11 trạng thái/cancellation rule; không đưa business rule xuống controller/Gateway/Infrastructure.
- Fare snapshot bất biến; payment/refund/consumer idempotent; ledger balanced và append-only.
- Mọi network call có timeout; retry transient hữu hạn, exponential backoff+jitter; không im lặng bỏ event lỗi.
- Không token thật trong event/outbox/log; không secrets hard-code; không Identity Service/Redis/Kafka/Kubernetes.
- Giữ .NET 10/EF Core 10 và layout hiện tại; docs tiếng Việt, code/comments mới English.
- Không reset DB thật/xóa volume; không ghi đè work đang có; không tự commit/push.
- Bước sửa code dùng test hành vi thất bại trước, sau đó sửa tối thiểu và chạy lại. Việc dọn docs/config dùng parser/config validation và smoke phù hợp, không tạo unit test sao chép implementation.

## Trọng tâm review xuyên suốt

1. Client timeout rồi gửi lại dropoff/payment: chỉ một khoản thu, một cặp ledger; task 1/6/10/18.
2. Process chết giữa gateway success, DB commit, publish và ack: recovery cùng key, không mất event; task 9–11/18.
3. Cấu hình đổi khi còn chuyến dở dang: transport/budget đã snapshot không đổi; task 2/11/18.
4. Event sai identity, fare hoặc thứ tự: không giả Paid/hoàn nhầm payment; task 10–13.
5. Production sai cấu hình OIDC, token hết hạn hoặc thiếu role: fail closed, không fallback dev token; task 14–15/18.

## Thứ tự và bản đồ phạm vi

| Mốc | Task | Điều kiện qua mốc |
|---|---|---|
| A: MVP đã kiểm chứng | 1–7 | REST FullFlow với ba persistence; test bắt buộc không skip; retry không thu trùng; cấu hình sạch. |
| B: kiến trúc mở rộng | 8–16 | Messaging/outbox/inbox/compensation, OIDC và telemetry có kiểm thử riêng. |
| C: bàn giao hợp lý | 17–18 | UI/tài liệu nhất quán; cả hai transport và failure scenarios được nghiệm thu. |

Task 1–5 chủ yếu Trip/reliability; 6 vừa tinh gọn hạ tầng vừa làm fixture thật; 8–13 liên quan Contracts/Trip/Payment/RabbitMQ; 14–15 auth/client; 16 telemetry; 17–18 cleanup và nghiệm thu. Không giao triển khai song song các task có cùng transaction/contract.

## Task 1: Sửa script nghiệm thu và đường chạy integration

**File sửa:** `docs/check-booking-flow.ps1`, `docs/run-integration-tests.ps1`, `docs/end-to-end-booking.md`.
**File test sửa:** fixtures Trip/Payment; `tests/SwiftRide.Api.Tests/FullFlow/FullFlowHost.cs`.
**Interface:** runner `-Suite Trip|Matching|Payment|FullFlow|All`; smoke `-GatewayUrl`, `-RiderAccessToken`, `-DriverAccessToken`, `-ExpectedTransport Rest|Events`. Token không truyền signing key thật.

- [ ] Dùng PowerShell parser chứng minh lỗi dòng `$i:` hiện tại. Xác minh `All` hiện thiếu FullFlow/race; ghi lại test bắt buộc theo tên thay vì chỉ tổng số test.
- [ ] Sửa `${i}:`; smoke lấy dev token từ Gateway chỉ ở Development, hoặc nhận token ngoài. Replay cùng create/dropoff key; assert cùng TripId/PaymentId, đúng fare/currency; actor sai bị từ chối; mọi HTTP call có timeout.
- [ ] Runner kiểm tra `$LASTEXITCODE` ngay sau docker/dotnet và exit nonzero khi có lỗi. `All` chạy đủ bốn test project trong cùng môi trường; không dùng filter làm rơi race/FullFlow. Parse TRX để bắt test bắt buộc bị skip, không chỉ tin exit code.
- [ ] Fixtures kiểm tra localhost + tên DB allowlist trước DDL; guard khi thiếu env phải skip mà không khởi tạo kết nối/DDL. Đưa `-e`/credentials vào process theo cách không in secrets, cleanup env test khi kết thúc.
- [ ] Parse tất cả `docs/*.ps1`; chạy smoke và integration runner khi task 6 có môi trường. Kết quả mong đợi: parser 0 lỗi; runner thất bại thật nếu child command thất bại; không chứng nhận ledger chỉ bằng HTTP Paid.

## Task 2: Nối cấu hình và sửa biên retry budget

**File sửa:** `src/TripService/Application/DependencyInjection.cs`, `Application/Trips/CreateTripUseCase.cs`, `Application/Payments/PaymentWorkflowOptions.cs`, `Infrastructure/Persistence/TripWorkStore.cs`, `Infrastructure/Workers/PendingTripWorker.cs`, API appsettings và `.env.example`.
**File mới:** `src/TripService/Application/Trips/TripLifecycleOptions.cs`, `TripRecoveryOptions.cs`.
**Test:** mở rộng `Application/CreateTripTests.cs`, `TripPaymentRecoveryTests.cs`, `Infrastructure/TripPersistenceTests.cs`.
**Interface:** `TripLifecycleOptions.MaxDriverRejections=3`, `SupportedPricingModes=["standard"]`; recovery Matching 5; `PaymentWorkflowOptions` mặc định 20 attempt/10 phút/initial 2s/max 60s. `TripContext.PaymentTransport`/`TripRecord.PaymentTransport` snapshot `Rest|Events` khi tạo, không nhận giá trị từ client; cột migration default Rest cho dữ liệu cũ. `AddPaymentJob(Guid tripId, string paymentKey, DateTimeOffset now, int maxAttempts, DateTimeOffset deadlineUtc, string transport)` thay signature hiện có và cập nhật tất cả caller/mock. Job kế thừa mode từ Trip, không đọc lại lựa chọn runtime.

- [ ] Viết test: config `MaxDriverRejections=1` làm lần từ chối thứ hai Failed; options invalid fail startup; max attempt cuối vẫn chạy; sau deadline không gọi mạng; restart không reset counter; job cũ không nhận budget mới.
- [ ] Chạy `dotnet test tests/SwiftRide.TripService.Tests --filter "FullyQualifiedName~CreateTripTests|FullyQualifiedName~TripPaymentRecoveryTests"`; xác nhận test mới đỏ đúng nguyên nhân.
- [ ] Bind/validate options on startup, inject vào creator/coordinator/worker; bỏ hard-code business budget. Snapshot max/deadline và transport cho Trip/job. Database migration tăng dần cho field mới, migration không sửa ledger hoặc trạng thái cũ.
- [ ] Chạy lại test trên và persistence sau task 6. Defaults giữ hành vi 3/5/20 như thiết kế; giá trị môi trường khác phải có tác dụng thật.

## Task 3: Timeout, retry và phân loại lỗi nhất quán

**File sửa:** Trip `Infrastructure/Http/MatchingClient.cs`, `PaymentClient.cs`, `ServiceClientOptions.cs`, `DependencyInjection.cs`; `Application/Trips/TripWorkCoordinator.cs`, `TripCommandUseCase.cs`; `Application/Payments/TripPaymentCoordinator.cs`; workers; Payment `GatewayCallPolicy.cs` khi cần.
**File mới:** `src/TripService/Application/Reliability/RetrySchedule.cs`.
**Test:** Trip `Http/MatchingClientTests.cs`, `PaymentClientTests.cs`, `Application/TripWorkRecoveryTests.cs`, `TripPaymentRecoveryTests.cs`; mới `Application/RetryScheduleTests.cs`.
**Interface:** `RetrySchedule.NextDelay(int attempt, TimeSpan initial, TimeSpan max, double jitterFraction)` với jitter input 0..1, time formula từ thiết kế. Không tạo project Common.

- [ ] Test 503/network/timeout retry có giới hạn; 400/401/403/409/invalid payload không retry; cancellation của caller không biến thành transient; worker không lên lịch lại auth/conflict; Retry-After không vượt deadline.
- [ ] Test công thức với attempt 1/2/cap, jitter=0/1, deadline boundary; không đo sleep vài millisecond để chứng minh jitter.
- [ ] Chạy test HTTP/Application mới để xác nhận đỏ; sửa retry tại owner, bỏ delay 50ms, giữ timeout cụ thể 3s/4s/12s/10s; expose giá trị qua options validated. Đảm bảo cancellation token timeout bao phủ cả đọc response; durable intent đã commit vẫn phục hồi sau request bị hủy.
- [ ] Chạy `dotnet test tests/SwiftRide.TripService.Tests` và test Payment timeout; số gateway attempt độc lập với số query/reconciliation, không nhân retry không kiểm soát.

## Task 4: Đưa quyết định Failed về Domain

**File sửa:** Trip `Infrastructure/Persistence/TripWorkStore.cs`, `Application/Trips/TripWorkCoordinator.cs`, `Infrastructure/Workers/PendingTripWorker.cs`; giữ API `FailMatchingExhaustedAsync(TripWorkLease lease, CancellationToken ct)` hiện có làm entry point.
**Test:** `Application/TripWorkRecoveryTests.cs`, `Infrastructure/TripPersistenceTests.cs`, Domain transition tests.

- [ ] Test exhausted Requested -> Failed và release rider trong cùng save; nếu trip đã Quoted/Cancelled thì không đổi trạng thái; transaction lỗi không để release slot một mình.
- [ ] Chạy test liên quan trước sửa để thấy fail; thay mọi nhánh store gán `trip.Status = "Failed"` bằng Application load → Domain `FailNoDriverAvailable()` → writer.Update → stage job/receipt → một unit-of-work.
- [ ] Store chỉ cập nhật metadata kỹ thuật, không tự release business assignment hoặc đổi Trip status. Không tạo đường transition mới vượt bảng trạng thái.
- [ ] Chạy Domain/Application và persistence test; tìm lại mọi direct assignment status trong code persistence để phân biệt mapping snapshot với quyết định nghiệp vụ.

## Task 5: Dependency an toàn và điểm mở rộng pricing thật

**File sửa:** Trip API/Infrastructure csproj theo dependency graph; Matching `Application/CreateQuote.cs`/DI; Trip `Application/Trips/CreateTripUseCase.cs`, `TripWorkCoordinator.cs`, `TripContext.cs`, persistence/EF migration khi snapshot Mode.
**Test:** Matching `PricingTests.cs`, `MatchingUseCaseTests.cs`; Trip `CreateTripTests.cs`, `TripWorkRecoveryTests.cs`.
**Interface:** snapshot `TripContext.PricingMode` và `TripRecord.PricingMode` default `standard`; coordinator truyền giá trị đó qua `QuoteInput`, Matching chọn `IPricingStrategy.Mode`.

- [ ] Chạy `dotnet list SwiftRide.sln package --include-transitive` và `dotnet list SwiftRide.sln package --vulnerable --include-transitive`; xác định dependency cha kéo Pkcs. Đọc advisory/source chính thức tại thời điểm sửa, chọn bản vá tương thích nhỏ nhất; ghi package/version/license và lý do.
- [ ] Nâng dependency cha; chỉ pin trực tiếp Pkcs nếu cần và có giải thích. Restore/build lại, audit lại; không tăng framework major.
- [ ] Test thêm strategy chỉ trong test trả giá hợp lệ qua CreateQuote; cấu hình allowlist Trip cho mode test mới; standard/surge/promo giữ công thức; unknown mode trả lỗi trước tạo intent; recovery dùng đúng mode đã snapshot. Thay nhánh hard-code bằng `TripLifecycleOptions.SupportedPricingModes`, Matching vẫn chọn algorithm từ DI; không có danh sách `standard` cố định trong code cản mở rộng.
- [ ] Run Matching/Trip tests trước và sau thay đổi. Không thêm mode UI/giá kinh doanh giả; kết quả mong đợi là mở rộng được bằng implementation+DI mới và không sửa strategy cũ.

## Task 6: Tinh gọn Compose và tạo FullFlow với ba persistence

**File sửa:** `docker-compose.yml`, `docker-compose.override.yml`, `docker-compose.test.yml`, `.env.example`; API FullFlow host/tests; Payment/Trip database fixtures.
**File mới:** `docker-compose.debug.yml`, `tests/SwiftRide.Api.Tests/FullFlow/PersistentBookingFlowTests.cs`, `FullFlowDatabaseFixture.cs`.
**Interface:** `FullFlowHost.StartPersistentAsync(CancellationToken ct)` dùng ba DB test (Trip, matching_test, swiftride_payment_test), từng owner dùng runtime login riêng; setup dùng admin test tách biệt. Store giả giữ nguyên cho component tests.

- [ ] Bỏ PostgreSQL local/volume declaration/POSTGRES vars khỏi runtime Supabase; không `down -v`. Giữ PostgreSQL trong test Compose. Chuyển port 5285 sang debug override chỉ nạp tường minh; default chỉ Gateway publish port.
- [ ] FullFlow fixture khởi tạo schemas SQL tăng dần, role test riêng; không nuốt lỗi SQL rồi tiếp tục. Re-run fixture sạch an toàn, không dùng admin credentials cho service runtime; driver vị trí/quotes thật đi vào matching_test.
- [ ] Bài mới: create→quote→confirm→accept→en-route→pickup→dropoff→Paid qua Gateway; query DB bằng verification credentials: 1 payment, 2 ledger cân bằng, đúng amount/currency, replay không thêm gateway receipt. Thêm race hai chuyến giành một driver và restart sau charge.
- [ ] Sửa integration runner task 1 để gọi persistent FullFlow; chạy `docker compose --env-file .env.example config --quiet`, parser và `powershell -File docs/run-integration-tests.ps1 -Suite All`. Expected: test bắt buộc thật sự chạy, role Payment không đọc matchdb và ngược lại; không skip.

## Task 7: Đóng mốc A và ghi trạng thái đúng bằng chứng

**File sửa:** `README.md`, `docs/end-to-end-booking.md`, `docs/database-supabase.md`.
**File mới:** `docs/implementation-status.md`.

- [ ] Chạy build/test backend, frontend test/typecheck/build, Compose config; chạy runner All hai lần liên tiếp để tìm fixture flaky/state leak, rồi smoke REST trên stack demo được cấu hình.
- [ ] Kiểm tra lần cuối các nhánh no driver, rider reject/cancel, driver rejection biên k, wrong actor, transient/permanent payment, deadline exhausted còn PaymentPending và retry không thu trùng.
- [ ] Ghi ngày, lệnh, pass/fail/skip và môi trường; không đánh dấu pass dựa baseline 331/38. Phân biệt lỗi code với thiếu credentials/Docker; chưa chạy được thì mốc A chưa qua và chưa bắt đầu B.
- [ ] Docs hiện trạng link về implementation-status; loại tuyên bố “hoàn chỉnh” không có bằng chứng. Giữ lịch sử cũ với nhãn historical.

## Task 8: Chốt contracts V1 và transport package

**File mới:** `src/SwiftRide.Contracts/V1/{TripAcceptedV1,RideCompletedV1,PaymentSettledV1,PaymentFailedV1,PaymentCompensationRequestedV1,PaymentRefundedV1,PaymentCompensationFailedV1}.cs`.
**File sửa:** csproj Infrastructure Trip/Payment và test tương ứng; Compose profile messaging.
**File mới topology:** `infra/rabbitmq/definitions.json`, `infra/rabbitmq/rabbitmq.conf` không chứa user/password; bootstrap queue/binding durable trước message đầu tiên, persist RabbitMQ vào volume `rabbitmq-data`.
**Test mới:** `tests/SwiftRide.Api.Tests/Messaging/ContractSerializationTests.cs`.
**Interface:** positional records theo thiết kế §5.1; mỗi event có `Guid EventId`, `Guid TripId`, `DateTimeOffset OccurredAtUtc`, `string CorrelationId`, `Guid? CausationId`; settled/completed thêm fare/key/party IDs; refund thêm PaymentId/RefundId/reason. Không base class chứa logic.

- [ ] Pin MassTransit/RabbitMQ/test harness cùng 8.5.8; lưu nguồn package/license trong docs. Restore/build/audit để quyết định có chấp nhận phiên bản này; nếu có advisory chưa giải quyết thì chưa bật runtime messaging và ghi lý do, không tự nâng major.
- [ ] Serialization test round-trip GUID/UTC/decimal/VND/version và exact payment key; payload không có token thật, EF entity hoặc domain type. Validation của consumer nằm ngoài Contracts.
- [ ] Pin exchange names `swiftride.v1.<event-kebab-case>`; receive queue names `payment-ride-completed`, `trip-payment-settled`, `trip-payment-failed`, `payment-compensation`, `trip-payment-refunded`, `trip-payment-compensation-failed`. Definitions/bootstrap và registration dùng cùng binding. TripAccepted là event thông tin, chưa thêm consumer nghiệp vụ nếu chưa có caller; không tạo Notification/audit subsystem để đủ tên pattern. Durable queues, persistent delivery và publisher confirmation; test producer phát khi consumer chưa start nhưng queue đã bootstrap vẫn nhận được sau đó.
- [ ] Chạy test Contracts, build solution; tất cả payload thuộc namespace V1. Không phát sự kiện từ controller hoặc Domain trực tiếp vào broker.

## Task 9: Trip durable outbox và publish trong owner

**File mới:** Trip `Application/Messaging/ITripEventWriter.cs`; Infrastructure `Messaging/TripEventWriter.cs`, `TripOutboxPublisher.cs`, `MessagingRegistration.cs`; Persistence records/configurations `IntegrationOutboxRecord`, `IntegrationInboxRecord` và EF migration.
**File sửa:** `TripDbContext.cs`, DI, `TripCommandUseCase.cs`, persistence/UnitOfWork nếu cần.
**Test mới:** `tests/SwiftRide.TripService.Tests/Infrastructure/TripOutboxTests.cs`.
**Interface:** `ITripEventWriter.Add(TripAcceptedV1 message)`, `.Add(RideCompletedV1 message)`, `.Add(PaymentCompensationRequestedV1 message)` chỉ stage vào cùng DbContext; publisher là BackgroundService.

- [ ] Test rollback Trip+outbox cùng rollback; commit khi broker chết vẫn có intent; hai publisher chỉ một owner; crash publish-before-mark gửi lại cùng EventId; stale owner không đánh dấu published.
- [ ] Chạy test đỏ; thêm schema/index/migration tăng dần. Outbox claim/fence, timeout/poll/lease/retry/max theo thiết kế §5.2; serialize message allowlist, không deserialize type name tùy ý từ dữ liệu ngoài.
- [ ] Stage TripAccepted trong accept transaction; stage RideCompleted cùng PaymentPending khi transport Events. REST không stage lệnh charge. Publish sau commit, không external call trong local transaction.
- [ ] Run Trip persistence/outbox tests với SQL Server và broker test; kiểm tra không mất event khi restart, RequiresAction giữ payload khi hết retry.

## Task 10: Payment consumer, inbox và outcome outbox nguyên tử

**File mới:** Payment `Application/Messaging/IPaymentEventWriter.cs`, `PaymentExecutionContext.cs`; Infrastructure Messaging `RideCompletedConsumer.cs`, `PaymentEventWriter.cs`, `PaymentOutboxPublisher.cs`, `MessagingRegistration.cs`; Persistence inbox/outbox records/configuration.
**File mới SQL:** `database/supabase/paydb-messaging.sql`, `verify-paydb-messaging.sql`.
**File sửa:** ProcessPaymentUseCase, PaymentDbContext/DI/unit-of-work boundary; tests fixtures nạp script mới.
**Test mới:** Payment `Infrastructure/PaymentMessagingTests.cs`.
**Interface:** giữ `ExecuteAsync(ProcessPaymentInput input, CancellationToken ct)` cho REST; thêm overload `ExecuteAsync(ProcessPaymentInput input, PaymentExecutionContext execution, CancellationToken ct)`. Context gồm transport/message/correlation/causation; persisted processing metadata giữ origin để retry nhất quán. `IPaymentEventWriter.Add(PaymentSettledV1)`/`.Add(PaymentFailedV1)` stage cùng PayDB context.

- [ ] Test duplicate EventId, duplicate business key khác EventId, cùng key khác fare/party conflict; chỉ 1 gateway charge/2 ledger; crash sau gateway success rồi retry; rollback final transaction không mất cơ hội emit outcome.
- [ ] Consumer validate schema/version/key/parties trước use case. Inbox final receipt, payment final status, ledger và outcome outbox commit cùng transaction; external gateway call ngoài transaction dài. Dùng unique claim/idempotency hiện có, không chỉ inbox chống trùng.
- [ ] Payment Pending không đánh dấu message completed rồi mất recovery: transient throw để bounded retry; charge attempt counter lưu bền, cap 3. Consumer commit PaymentFailedV1+RequiresAction+inbox một lần khi kết quả đã xác định declined hoặc exhausted với xác nhận không thu tiền. Timeout/DB unavailable/poison không được suy ra failed; giữ Pending/RequiresAction, đối soát durable receipt trước khi quyết định. Receipt success cho phép finalize sau cap mà không gọi gateway charge thêm.
- [ ] Claim CurrentlyLeased/remote Pending chưa hoàn thành không được ack final receipt: quan sát winner hoàn tất hoặc throw retryable-busy. Test hai consumer, owner đầu chết trước finalize, delivery sau lease phục hồi thay vì mất message.
- [ ] Schema RLS/quyền/index tăng dần, ledger nguyên trạng. Publisher cùng policy task 9. Run Payment messaging+DB tests, verify role không đọc matchdb, same key vẫn replay đúng API REST.

## Task 11: Trip nhận kết quả và bật Events theo từng chuyến

**File mới:** Trip Infrastructure Messaging `PaymentSettledConsumer.cs`, `PaymentFailedConsumer.cs`; Application `Messaging/ApplyPaymentOutcome.cs`.
**File sửa:** TripCommandUseCase, TripPaymentCoordinator, PendingPaymentWorker, options/context/persistence từ task 2; FullFlow host hỗ trợ transport; docs cấu hình.
**Test mới:** Trip `Application/PaymentOutcomeTests.cs`; API `FullFlow/EventBookingFlowTests.cs`.
**Interface:** `ApplyPaymentOutcome.ExecuteAsync(PaymentSettledV1 message, CancellationToken ct)` và overload PaymentFailedV1. Chuyến dùng transport snapshot `Rest|Events`; worker chỉ Charge ở Rest.

- [ ] Test identity/fare/key/payment ID khớp thì Domain settle; duplicate event giữ Paid và không ghi thêm; event sai identity/unknown trip quarantine, không gọi refund hoặc charge; failed giữ PaymentPending/RequiresAction. Event mâu thuẫn ở terminal state chỉ tạo incident; resolution CompensationRequested/Refunded chặn late settlement chuyển Paid.
- [ ] Inbox receipt cùng Trip transition/job/outbox được commit atomically; optimistic concurrency phải retry qua transaction mới, không ghi receipt trước rồi bỏ business update.
- [ ] Test đổi config/restart khi có chuyến Rest/Events: chuyến cũ giữ mode, không hai ChargeAsync path. Khi broker chết dropoff vẫn trả pending với durable outbox; broker sống lại tới Paid.
- [ ] Chạy Event FullFlow qua Gateway, Payment dùng DB thật; run lại REST suite. Đổi mặc định demo sang Events chỉ sau task 13 pass; hướng dẫn rollback cho chuyến mới và drain chuyến cũ, không mutate transport của job cũ.

## Task 12: Compensation choreography dùng refund hiện có

**File mới:** Trip Application `Messaging/RequestPaymentCompensation.cs`; Payment Infrastructure Messaging `PaymentCompensationConsumer.cs`; Trip `PaymentRefundedConsumer.cs`, `PaymentCompensationFailedConsumer.cs`; technical resolution record/config/migration thuộc Trip. Payment `Application/Abstractions/IRefundProcessingStore.cs`, Infrastructure `Persistence/RefundProcessingStore.cs`, record/configuration và `database/supabase/paydb-refund-lease.sql`.
**File sửa:** Payment RefundPaymentUseCase để stage outcome/inbox trong transaction; payment event writer overload refund outcome.
**Test mới:** API `FullFlow/CompensationFlowTests.cs`; mở rộng `PaymentService.Tests/Infrastructure/RefundRecoveryTests.cs`.
**Interface:** `RequestPaymentCompensation.ExecuteAsync(Guid tripId, Guid paymentId, string correlationId, CancellationToken ct)` chỉ dùng nội bộ từ validated settlement handler; không public route. Refund overload nhận execution context như task 10. `IRefundProcessingStore.TryClaimAsync(Guid refundId, Guid owner, DateTimeOffset now, TimeSpan lease, int maxAttempts, CancellationToken ct)` trả `Task<ProcessingClaimResult>` như payment processing (DriverId không dùng với refund); `CompleteAsync(Guid refundId, Guid owner, string outcome, CancellationToken ct)` trả `Task<bool>` fence trong finalize transaction.

- [ ] Fault injection test tạo settlement thực cho đúng trip/parties/key nhưng sai locked fare; Trip xác minh bằng GetByTripAsync rồi mới emit compensation, chỉ khi Trip PaymentPending. Terminal trip nhận conflict chỉ incident; event giả/sai party không hoàn nhầm. Test race late-settled với CompensationRequested/Refunded không chuyển Paid.
- [ ] `PaymentCompensationRequestedV1` có durable EventId, PaymentId/TripId/key/reason. Payment đối chiếu DB mình, dùng refund key `payment-{paymentId}-refund`; transient retry cap, permanent/exhausted emit failed một lần.
- [ ] Tạo refund_processing cùng Refund Pending; cả REST/consumer dùng atomic claim lease 60s/cap 3 và fencing. Không cho REST bypass lease. Crash sau gateway success: đối soát receipt trước cap, finalize không charge/refund thêm; unknown giữ Pending/RequiresAction, không giả failure.
- [ ] Refund settlement+2 reverse entries+outbox+inbox+fenced completion chung local transaction; duplicate/restart không thêm refund/ledger. Trip lưu Refunded/RequiresAction trên technical record, giữ PaymentPending; tuyệt đối không mở cancellation sau DriverAccepted.
- [ ] Assert tổng debit/credit gốc+đảo cân bằng, tài khoản trở về số dư ban đầu; refund failed hiển thị cần xử lý, không tự settle Trip. Chạy compensation integration và toàn bộ refund suite.

## Task 13: Retry message, error queue và vận hành replay

**File sửa:** MessagingRegistration hai owner, Compose messaging; workers/publishers retry config.
**File mới:** `docs/messaging-operations.md`, API `Messaging/BrokerFailureTests.cs`; chỉ thêm `Infrastructure/Messaging/BoundedMessageRetryPolicy.cs` trong từng owner nếu retry built-in không đáp ứng bound/jitter đã kiểm tra. Không thêm Common project, delayed-message plugin hoặc scheduler.
**Interface:** receive endpoints đặt tên rõ cho RideCompleted/settlement/compensation; `<endpoint>_error` là error queue MassTransit. Replay qua broker management/local operator script dùng same MessageId, không route public mới.

- [ ] Cấu hình tối đa 3 retry sau lần đầu bằng exponential+jitter min 30s/max 120s, tổng chờ <=300s; concurrent limit 2/prefetch 4. Đọc source/test phiên bản pin để xác nhận jitter/bound; custom policy chỉ nếu cần. Test dùng clock/interval configuration nhỏ để không chờ vài phút cho mỗi test.
- [ ] Test validation/permanent không retry; exception cuối vào error queue có original ID/trace và error an toàn. Outbox exhausted giữ RequiresAction riêng, không giả đã vào broker error queue; poison/transport failure không tự phát PaymentFailedV1 nếu chưa biết kết quả tài chính. Timeout chưa rõ phải giữ lease/đối soát, kết quả transient xác định không thu tiền có thể release lease an toàn.
- [ ] Viết hướng dẫn broker down, timeout, poison message, inspect/fix/replay, drain và rollback transport. Admin credentials chỉ operator, không cấp client quyền queue management.
- [ ] Test replay message sau fix thành công một lần, replay outcome đã xử lý không thêm side effect; kiểm tra tổng gateway attempt không vượt 3 dù nhiều delivery. Document retry giữ consumer slot là trade-off chấp nhận cho demo nhỏ, không tuyên bố đây là cơ chế scheduler tải lớn.
- [ ] Restart/recreate broker với volume giữ nguyên: durable messages vẫn được nhận; test queue có sẵn trước consumer startup, confirmation bật và schema bootstrap không chứa secrets. Không chạy `down -v` để thử restart.
- [ ] Chạy messaging integration với broker thật; cập nhật trạng thái mốc B phần messaging, pin version và license nguồn đã đọc.

## Task 14: OIDC cho Gateway và resource services

**File sửa:** GatewayApplication/appsettings, TripApplication, MatchingApplication, Compose/.env.example.
**File mới:** cấu hình auth trong từng API owner; API tests `Security/OidcSecurityTests.cs`, `Security/OidcDiscoveryFixture.cs`; `docs/authentication.md`.
**Interface config:** `Authentication:Mode=DevelopmentJwt|Oidc`; `Authority`, `Audience`, `RoleClaimType`, `NameClaimType`; Production bắt buộc Oidc với HTTPS metadata và trusted issuer/audience. Discovery/JWKS client timeout 5s.

- [ ] Test token đúng issuer/aud/role, sai issuer/aud/signature/expiry/algorithm, key rotation và thiếu role; owner/resource authorization giữ nguyên; Production không expose dev-token, sai cấu hình fail startup.
- [ ] Dùng JwtBearer discovery/JWKS, không ký token production trong Gateway. Mapping claim dùng allowlist role rider/driver; không tin role header từ client. Dev config chỉ Development, không fallback khi provider lỗi.
- [ ] Fixture JWKS nằm trong test host, không thêm service deploy. Test metadata unavailable fail closed; request không chờ vô hạn.
- [ ] Run Gateway/Trip/Matching security suites. Docs nêu Authority/audience/role mapping và tài khoản phải có từ provider ngoài; chưa có account/config thật thì ghi chưa nghiệm thu live, không gọi fixture là OIDC production hoàn chỉnh.

## Task 15: WebApp đăng nhập và token lifecycle

**File sửa:** WebApp `src/services/auth.ts`, `api.ts`, `App.tsx`, `vite.config.ts`, `package.json`/lockfile; component login/callback mới ở `src/components/`.
**Test:** `__tests__/services/auth.test.ts`, `api.test.ts`, `components/views.test.tsx`; mới `services/oidc.test.ts`.
**Interface:** mở rộng `AuthSession` bằng `signIn(): Promise<void>`, `completeSignIn(): Promise<void>`, `signOut(): Promise<void>`, `getActor()` lấy sub/role đã xác thực trong backend mode. `createSwiftRideClient` dùng cùng session; role switch tự do chỉ offline demo/Development.

- [ ] Chọn thư viện OIDC SPA hỗ trợ Authorization Code+PKCE, kiểm tra docs/license/audit và pin exact version vào package.json/lockfile; không tự viết OAuth protocol hoặc thêm client secret vào bundle.
- [ ] Test login/callback state/nonce, token hết hạn, 401 clear/re-auth, logout xóa token; không retry mù mutation bằng key mới. Dev token cache phải kiểm tra expiry thay vì giữ vô thời hạn.
- [ ] Cấu hình public `VITE_OIDC_AUTHORITY`, client ID, redirect/logout URI/scopes; access token giữ trong session memory theo khả năng thư viện, không in/log; callback không làm lộ token vào URL/history.
- [ ] Chạy npm test/typecheck/build; nghiệm thu live bằng hai account được cấp role. Nếu thiếu provider, fixture và build được báo riêng với live blocked; không cản chốt các task không phụ thuộc.

## Task 16: Trace, correlation và metrics có thể demo

**File sửa:** Gateway và ba API startup/csproj; Http clients, Messaging registrations/publishers; business Application instrumentation của owner.
**File mới:** `docker-compose.observability.yml`, `observability/otel-collector.yaml`, `docs/observability.md`; API tests `Observability/TracePropagationTests.cs`, `SensitiveLoggingTests.cs`.
**Interface:** mỗi owner có ActivitySource/Meter riêng tên `SwiftRide.<Owner>`; `CorrelationId` giới hạn 128 ký tự theo allowlist, thiếu/invalid tạo mới; W3C traceparent/tracestate lưu cùng outbox và restore context khi publish/consume.

- [ ] Test request→Trip→Matching và request→outbox→RabbitMQ→Payment→outcome có liên kết trace; không dùng một span sống lâu bao cả thời gian chờ queue.
- [ ] Test không log Authorization/payment token/địa chỉ cá nhân; metric label không chứa TripId/UserId. Đo trip outcome, matching/API latency, payment failure; queue depth lấy từ broker metrics/exporter.
- [ ] Pin bộ package OpenTelemetry tương thích .NET10 sau restore/audit; exporter OTLP timeout hữu hạn và buffer bounded, collector chết không làm booking fail. Collector/backend chỉ trong profile observability, không business service mới.
- [ ] Run tests và mở một trace/metric thật trong demo stack; ghi đường xem và bước chứng minh. Không coi console log hoặc CorrelationId đơn lẻ là distributed tracing hoàn chỉnh.

## Task 17: Giữ UI hữu ích, bỏ phần dư và hợp nhất tài liệu

**File sửa:** WebApp App/AddressSearch/RideMap/auth theo cần thiết; README; docs frontend/end-to-end/architecture/database/matching/scaffold; `.env.example`; Payment README/HUONG-DAN-SUPABASE.
**File có thể bỏ sau kiểm tra refs:** `docs/check-skeleton.ps1`, override cũ không còn caller. Không xóa test stub Gateway trả 501.

- [ ] Giữ Rider/Driver, bản đồ, history/current/feed/heartbeat, jobs và receipts. Không refactor store/component chỉ vì khác phong cách. Gắn nhãn offline demo rõ; backend production lấy actor từ token.
- [ ] Nominatim optional: khi tắt/lỗi vẫn chọn tọa độ trên bản đồ và đặt xe được; không gọi search khi tắt; giữ cache/pacing đang có. Test fallback vì ảnh hưởng luồng người dùng, không thêm fake routing engine.
- [ ] `rg` toàn repo cho skeleton script/POSTGRES vars/port 5285 và mọi file dự định xóa; sửa caller/docs trước khi bỏ. Chỉ expose Gateway ở default; debug override phải nạp rõ. Không xóa volume người dùng.
- [ ] implementation-status là nguồn hiện trạng; docs khác ghi thiết kế/lịch sử hoặc link tới status. Cập nhật schema scripts/order, license, transport flags, OIDC requirements, monitoring/error queue; gỡ tuyên bố đã pass chưa chạy.
- [ ] Chạy frontend tests/typecheck/build, parser và Compose config cho default/messaging/observability/debug. Kết quả: mỗi phần thêm có mục đích demo/reliability rõ, không hạ tầng orphan.

## Task 18: Nghiệm thu toàn bộ và bàn giao bằng chứng

**File sửa:** `docs/implementation-status.md`, `docs/end-to-end-booking.md`, `docs/messaging-operations.md`, `docs/authentication.md`, `docs/observability.md`.

- [ ] Chạy `dotnet build SwiftRide.sln -m:1`; `dotnet test SwiftRide.sln --no-build -m:1`; `powershell -File docs/run-integration-tests.ps1 -Suite All` sau khi runner đã bao gồm suites mới. Không bỏ qua test bắt buộc, không áp dụng schema lên Supabase chung chỉ để chạy tests.
- [ ] Trong `src/WebApp`: `npm test`, `npm run typecheck`, `npm run build`. Parse scripts; config Compose từng profile bằng env test phù hợp; không lưu/render secrets trong evidence.
- [ ] REST: chuyến tới Paid với 1 charge/2 ledger; replay, wrong actor, rejection biên k, no driver, payment failure/exhausted. Events: cùng kịch bản với broker thật, duplicate, out-of-order, outage, restart và crash points; compensation đúng 1 refund/2 reverse entries.
- [ ] Kiểm tra đổi mode khi còn chuyến dở; rollback cho chuyến mới và drain chuyến Events cũ; migration additive giữ dữ liệu cũ, quyền schema không rò. Demo chọn map khi Nominatim tắt.
- [ ] OIDC live: provider/tài khoản thật nếu được cung cấp; trace/metrics/error queue nhìn thấy thật. Thiếu bên ngoài phải ghi rõ external-blocked và điều kiện hoàn tất; không hạ tiêu chí hoặc đánh dấu xong toàn bộ.
- [ ] Review ma trận dưới đây, đối chiếu code/command log; chỉ đánh dấu hoàn thành khi có chứng cứ. Bàn giao file thay đổi và vấn đề còn mở, giữ working tree chưa commit.

## Ma trận truy vết ba nhóm của người dùng

| Nhóm / phát hiện | Task | Bằng chứng cần có |
|---|---|---|
| 1: Script parser lỗi | 1 | Parser 0 lỗi, smoke thực chạy. |
| 1: 38 test skip/thiếu FullFlow thật | 1/6/7/18 | Bắt buộc không skip; Matching cũng dùng DB thật, không chỉ store giả. |
| 1: Retry thiếu jitter/lỗi permanent bị retry | 2/3/13 | Test phân loại/status/cancellation, cap/deadline/jitter. |
| 1: Transition ở Infrastructure | 4 | Domain transition, atomic slot release, không direct decision trong store. |
| 1: Config hard-code/unbound | 2/5/11 | Config khác default có tác dụng, trip/job snapshot qua restart. |
| 1: Vulnerable dependency | 5/8/15/16 | Dependency graph/audit/version được ghi sau restore thật. |
| 1: Docs tự tuyên bố hoàn chỉnh | 7/17/18 | Status dated + lệnh/kết quả/giới hạn. |
| 1: OCP pricing xuyên Trip | 5 | Strategy mới qua DI và recovery đúng mode; không thêm mode giả. |
| 2: RabbitMQ/PubSub/contracts | 8–11/13 | Events qua broker tới Paid, IDs/version đúng. |
| 2: Outbox/inbox/consumer idempotency | 9–11 | Crash/rollback/duplicate trên DB/broker thật. |
| 2: Saga/compensation | 12 | Validated mismatch → refund đúng một lần; event giả không hoàn tiền. |
| 2: Error queue/retry/replay | 13 | Poison vào error queue; replay giữ ID không duplicate. |
| 2: OAuth/OIDC | 14–15 | Security fixture và live provider được báo riêng. |
| 2: OpenTelemetry/metrics | 16 | Trace liên service/message + metric + broker queue depth. |
| 3: UI/map/Nominatim/history/heartbeat | 15/17 | Giữ thứ đang dùng, auth đúng và search optional có fallback. |
| 3: PostgreSQL/volume declaration/port debug dư | 6/17 | Default config chỉ expose Gateway; không xóa volume. |
| 3: Skeleton 501/docs cũ | 17 | Không còn caller hướng dẫn nghiệm thu sai; stub tests giữ đúng vai trò. |
| 3: Worker/receipt/strategy phình phạm vi | 2/5/11/17 | Một charge transport theo chuyến; không thêm abstraction/mode không có nhu cầu. |

## Điều kiện môi trường và cách dừng an toàn

- Docker daemon/SDK/package feed phải có; test DB disposable riêng, tên/host được fixture kiểm tra.
- Demo Supabase cần login riêng hai service và migrations tăng dần được quản trị áp dụng; việc lập kế hoạch không tự cấp quyền triển khai DB thật.
- OIDC live cần issuer/client/audience/redirect/role mapping và tài khoản thật; các giá trị cụ thể nhập bằng env/user-secrets lúc thực thi, không commit.
- Sau mỗi task chỉ chạy check liên quan; full suite ở các gate. Không đánh dấu các task về sau xong chỉ vì build đang xanh.
- Có failure chưa giải quyết thì giữ mốc chưa đạt, báo task và bằng chứng; không tự commit/push, không bật event flow trên dữ liệu đang dùng mà thiếu migration/drain plan.

## Tự rà soát kế hoạch

- [x] Bao phủ cả ba nhóm của yêu cầu, phân biệt MVP với mở rộng và phần dư.
- [x] Mỗi thay đổi business có vị trí code, test và tiêu chí; cleanup có parser/config/smoke.
- [x] Giữ service ownership, state machine, idempotency và không public payment API.
- [x] Định nghĩa transport snapshot, atomic outbox/inbox, compensation trigger và rollback.
- [x] Không cần chờ secrets/provider cụ thể để lập kế hoạch; nghiệm thu external vẫn ghi riêng.
- [x] Không có bước tự commit/push hoặc destructive thao tác trên DB thật.

Các dấu `[x]` ở phần tự rà soát chỉ xác nhận chất lượng tài liệu kế hoạch; tất cả task triển khai phía trên còn `[ ]`.
