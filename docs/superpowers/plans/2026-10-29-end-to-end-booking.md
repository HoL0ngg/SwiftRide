# Kế hoạch triển khai luồng đặt xe đến thanh toán

> **Cho agent thực thi:** Đọc cả spec và plan. Dùng skill `executing-plans` để thực hiện lần lượt; nếu người dùng chọn triển khai bằng subagent thì dùng `subagent-driven-development`. Các bước dùng checkbox để theo dõi.
> **Trạng thái: chỉ là plan để xem trước, chưa triển khai.** Không đánh dấu checkbox từ kết quả test baseline.

**Mục tiêu:** Nối UI hiện có qua Gateway đến Trip/Matching/Payment, lưu dữ liệu và kết thúc `Paid` với một logical charge và một cặp ledger khi retry/restart.

**Kiến trúc:** REST đồng bộ cho MVP, worker phục hồi nằm trong Trip. Giữ State Pattern và Matching/Pricing hiện có; bổ sung Trip Application/persistence/API, hardening Payment và thay nguồn dữ liệu frontend. Không đổi sang kiến trúc event trong đợt này.

**Stack:** .NET 10, EF Core 10.0.12 theo repo, SQL Server, Npgsql/Supabase `matchdb`/`paydb`, YARP, xUnit/Moq; React/TypeScript/Vite/Vitest hiện có. Không nâng version hàng loạt.

**Spec:** [Thiết kế nối luồng](../specs/2026-10-29-end-to-end-booking-design.md). **Nguồn:** [AGENTS.md](../../../AGENTS.md), [requirements.md](../../requirements.md), code tại `f43074d`.

## Ràng buộc chung

- Tối đa ba business service, client chỉ qua Gateway; business rule ở Domain/Application.
- Trip SQL Server; Matching/Payment có schema, credential riêng; không query chéo schema.
- Không mở lại `TripState`/concrete states; không expose persistence entity; `SwiftRide.Contracts` chỉ chứa integration events.
- VND dùng decimal nguyên đồng, `0 <= amount < 1e18`; public Trip JSON dùng chuỗi tiền, frontend dùng BigInt.
- Public JWT `sub`/`role`; actor canonical tối đa 100 ký tự. Internal key riêng cho hai đường Trip -> Matching/Payment.
- Một rider có một chuyến non-terminal, gồm PaymentPending. Claim rider/driver bằng unique constraint + transaction, không chỉ check trong RAM.
- Stable command key cho UI; deterministic payment key `trip-{tripId:D}-payment`; không raw token/JWT trong DB receipt/log/browser storage.
- Mỗi HTTP call có timeout. Matching/worker retry transient với exponential backoff + jitter và hard limit; không tự retry POST tạo quote.
- Payment chỉ tăng gateway-attempt khi claim thắng và chuẩn bị gọi gateway, tối đa 3. GET/202 không tính là charge attempt.
- Trip đối soát payment tối đa 20 lượt và 10 phút; mỗi lượt tối đa 1 GET + 1 POST, thêm một POST ban đầu. Hết cửa sổ vẫn PaymentPending và requiresAction.
- Mock token alias chỉ thuộc server config và dữ liệu mô phỏng; không biến cách lưu alias này thành cách lưu token nhà cung cấp thật.
- Không gọi Supabase để tạo/xóa schema trong automated test; fixture phải là localhost disposable có guard tên DB.
- Tài liệu/phản hồi tiếng Việt; code, comment và commit tiếng Anh. Không commit `.env` hoặc secret.

## Trọng tâm kiểm tra

1. Payment `201 Failed`, `202 Pending`, settled khác amount/actor hoặc đã refund: Trip không Paid — Task 01/08.
2. Crash sau gateway success/trước finalize, hoặc sau Payment settled/trước Trip Paid: khôi phục mà không tạo charge/ledger mới — Task 05/08/12.
3. Hai tab/race create, driver accept so với cancel, hai chuyến mời một driver: một kết quả hợp lệ, không mất update — Task 03/07/12.
4. Ba response 202 và result settled đến trễ không bị hiểu thành ba lần charge thất bại — Task 05/08.
5. JWT đổi role, response cũ về muộn, tiền `999999999999999999`, reload mất response tạo chuyến: UI giữ đúng actor, dữ liệu và key — Task 10/11/12.

## Thứ tự và cổng bàn giao

| Đợt | Task | Kết quả review được |
|---|---|---|
| A | 01, 02 | Contract/client và Domain restore đúng; không thay State Pattern cũ |
| B | 03; 04 -> 05 | TripDB thật; Payment zero fare/idempotency/claim/restart trên PostgreSQL thật |
| C | 06 -> 07 -> 08 -> 09 | Backend đi được qua HTTP, quyền đúng, có recovery |
| D | 10 -> 11 | Hai tab rider/driver dùng backend, map/search giữ hoạt động |
| E | 12 | Compose + DB thật + fault/restart + browser; cập nhật tài liệu theo kết quả |
| Sau core | 13 | Refund phục hồi an toàn; không chặn kiểm tra happy path Paid |

Thứ tự mặc định là tuần tự. Nếu có nhiều người: Payment 04/05 độc lập Trip 02/03 sau khi thống nhất 01; frontend 10 có thể dùng contract fixture từ 01. Không giao hai người sửa cùng composition root/config trong cùng thời điểm. Không ước lượng “đã xong” bằng số task hoặc số unit test.

## Bản đồ file và interface xuyên task

Đường dẫn dưới đây là quyết định cho **file mới dự kiến**, không khẳng định chúng đã tồn tại. Mỗi task có danh sách phần sửa/test riêng.

| Vùng | File chính và trách nhiệm |
|---|---|
| Trip transport | `Application/Abstractions/IMatchingClient.cs`, `IPaymentClient.cs`; Infrastructure `Http/MatchingClient.cs`, `PaymentClient.cs`: DTO HTTP riêng, parse và timeout |
| Trip business | Domain `TripSnapshot.cs`; Application `Trips/TripContext.cs`, `TripActor.cs`, `TripCommands.cs`, từng use case: gọi Domain và điều phối |
| Trip storage | Infrastructure `Persistence/TripRecord.cs`, `TripConfiguration.cs`, `TripRepository.cs`, `TripUnitOfWork.cs`, `CommandReceiptStore.cs`, `TripWorkStore.cs`, `ActiveAssignmentStore.cs`: SQL Server, rowversion/unique/lease |
| Trip work | Application `Trips/TripWorkCoordinator.cs`, `Payments/TripPaymentCoordinator.cs`; Infrastructure `Workers/PendingTripWorker.cs`, `PendingPaymentWorker.cs`: request và worker dùng chung logic |
| Trip API | `Api/TripContracts.cs`, `TripResponseMapper.cs`, `TripEndpoints.cs`, `TripApplication.cs`: projection, binding/auth và composition root thật |
| Payment recovery | Application `Abstractions/IPaymentProcessingStore.cs`, `IMockOperationStore.cs`; Infrastructure `Persistence/PaymentProcessingStore.cs`, `MockOperationStore.cs`; SQL additive riêng |
| Frontend | `src/WebApp/src/auth.ts`, `api.ts`, `tripTypes.ts`, `tripApi.ts`, `tripStore.ts`, `useTrip.ts`, `money.ts`: session, transport, server state và tiền |
| Fixture | `docker-compose.test.yml`, `docs/run-integration-tests.ps1`; các fixture nằm trong test project hiện có, không tạo Common project |

Các đường dẫn Trip/Payment trong bảng tương đối với `src/TripService`/`src/PaymentService`. Interface Application không tham chiếu EF, ASP.NET hoặc Domain service khác. Dùng `CancellationToken ct` ở mọi async I/O.

Các chữ ký cố định để tránh task sau gọi nhầm tên:

```csharp
// Task 01, types con nằm cùng Application/Abstractions/ExternalContracts.cs.
Task<MatchOutcome> IMatchingClient.MatchAsync(MatchInput input, CancellationToken ct);
Task<QuotedFare> IMatchingClient.QuoteAsync(QuoteInput input, CancellationToken ct);
Task<PaymentObservation?> IPaymentClient.GetByTripAsync(Guid tripId, CancellationToken ct);
Task<PaymentObservation> IPaymentClient.ChargeAsync(ChargeRequest input, CancellationToken ct);
// MatchInput: pickup + excluded IDs. QuoteInput: pickup/destination + mode.
// QuotedFare: FareSnapshot, DistanceKm, EstimatedMinutes, CreatedAt.
// ChargeRequest: trip/rider/driver/amount/currency/method/mock alias/key.
// PaymentObservation: id/trip/rider/driver/amount/currency/status,
// HasSettledRefund, Processing, RetryAfter. Remote status: Pending/Settled/Failed.
// Processing: AttemptsStarted, LeaseUntilUtc, CanRetry, RequiresAction.

// Task 02: public snapshot không phải state handler hoặc HTTP contract.
TripSnapshot Trip.ToSnapshot();
static Trip Trip.Restore(TripSnapshot snapshot);
void Trip.ConfirmQuote(string riderId, Guid quoteId);

// Task 03: TripContext bọc aggregate + route/method/alias/quote metrics,
// timestamps và expected Version byte[], không đưa ra API.
Task<TripContext?> ITripReader.LoadAsync(Guid id, CancellationToken ct);
void ITripWriter.Add(TripContext trip);
void ITripWriter.Update(TripContext trip);
Task ITripUnitOfWork.SaveAsync(CancellationToken ct);
Task<CommandReceipt?> ICommandReceiptStore.FindAsync(CommandScope scope, string key, CancellationToken ct);
void ICommandReceiptStore.Add(CommandReceipt receipt);
void ICommandReceiptStore.Update(CommandReceipt receipt);
void IActiveAssignmentStore.ClaimRider(string riderId, Guid tripId);
void IActiveAssignmentStore.ReleaseRider(Guid tripId);
void IActiveAssignmentStore.ClaimDriver(string driverId, Guid tripId);
void IActiveAssignmentStore.ReleaseDriver(Guid tripId);
Task<TripWorkLease?> ITripWorkStore.TryClaimAsync(TripWorkKind kind, Guid owner, DateTimeOffset now, CancellationToken ct);
void ITripWorkStore.StageCompletion(TripWorkLease lease);
void ITripWorkStore.StageRetry(TripWorkLease lease, DateTimeOffset nextAttempt, string reasonCode);
void ITripWorkStore.StageRequiresAction(TripWorkLease lease, string reasonCode);
void ITripWorkStore.AddPaymentJob(Guid tripId, string paymentKey, DateTimeOffset now);

// Task 06/07/08. Outcome chỉ chứa TripId, Completed/Pending và reason code;
// API reload projection, không trả aggregate/persistence record.
Task<TripCommandOutcome> CreateTripUseCase.ExecuteAsync(TripActor actor, CreateTripCommand command, string key, CancellationToken ct);
Task<TripCommandOutcome> TripCommandUseCase.ExecuteAsync(TripActor actor, Guid tripId, TripCommand command, string key, CancellationToken ct);
Task TripWorkCoordinator.ResumeAsync(TripWorkLease lease, CancellationToken ct);
Task TripPaymentCoordinator.ResumeAsync(TripWorkLease lease, CancellationToken ct);
```

Các exception transport phân biệt transient (network/408/429/5xx), protocol/payload conflict và auth/config. Không dùng một `catch Exception` rồi báo NoDriverAvailable hoặc Paid.

## Task 01 — Khóa contract tích hợp và HTTP client Trip

**File tạo:** các port/DTO/client ở bản đồ; `Infrastructure/Http/ServiceClientOptions.cs`, `InternalKeyHandler.cs`; test `tests/SwiftRide.TripService.Tests/Http/MatchingClientTests.cs`, `PaymentClientTests.cs`. **File sửa:** Trip Infrastructure csproj/DI; Trip test csproj thêm reference Infrastructure. Dùng ASP.NET shared framework reference cho HTTP client/options nếu cần, không thêm resilience package chỉ để có retry.

**Consumes:** raw JSON trong Matching `Application/MatchingContracts.cs` và Payment `Application/Payments/Dtos/PaymentDto.cs`. **Produces:** hai client/record theo interface trên; chưa mở endpoint Trip.

- [x] Viết test JSON literal độc lập với serializer của provider: Matched/NoDriverAvailable, quote 201 có decimal time; Payment 201 Settled/Failed, 200 replay, 202 Pending + Retry-After, GET 404/null, 409 và 503. Assert `201 + Failed => RemotePaymentStatus.Failed`, không suy từ success status.

  Assertion chính của `Payment201Failed_IsNotSettled`: `Assert.Equal(RemotePaymentStatus.Failed, result.Status);`.
- [x] Chạy `dotnet test tests/SwiftRide.TripService.Tests --filter FullyQualifiedName~Http`; xác nhận fail do client/contract chưa có.
- [x] Implement client và mapping decimal, enum casing, null driver của Pending/Failed, refund settled. Key/tokens chỉ nằm header/body gửi đúng origin cấu hình; không log body. BaseAddress phải absolute HTTP(S), không nhận URL do browser truyền.
- [x] Áp `/match` timeout 3s + tối đa một retry transient, `/quote` 4s không retry; Payment 12s không retry POST. Mỗi matching interactive work còn outer budget 12s ở Task 06/07. Test cancellation và lỗi parse không bị retry; `POST /quote` handler chỉ nhận đúng một request khi timeout.
- [x] Chạy lại test trên; all pass. Commit phần task: `feat: add trip service HTTP integration clients`.

## Task 02 — Khôi phục aggregate và confirm chưa gắn invitation

**File tạo:** `src/TripService/Domain/TripSnapshot.cs`, `States/TripStateFactory.cs`; `tests/SwiftRide.TripService.Tests/Domain/TripRestorationTests.cs`. **File sửa:** `Domain/Trip.cs`, `States/TripState.cs`, `QuotedState.cs`, `ValueObjects/FareSnapshot.cs`; thêm test confirm vào bộ Domain hiện có.

**Consumes:** Domain hiện tại, không EF/HTTP. **Produces:** `ToSnapshot/Restore/ConfirmQuote` hai tham số đã định nghĩa; overload ba tham số tiếp tục hoạt động.

- [x] Viết round-trip test cho cả 11 status và rematch pending; assert status/fare lock/driver/rejection set/payment key/id/failure giống trước, `restored.DomainEvents` rỗng. Snapshot chứa đúng mọi property business của Trip; không chứa DomainEvents hoặc type concrete state.
- [x] Viết test snapshot mâu thuẫn (Paid thiếu PaymentId; locked fare thiếu quote; driver accepted thiếu assigned; rejection count lệch set) bị từ chối trước khi trả aggregate. Snapshot lưu collection copy, không mutable alias.
- [x] Chạy `dotnet test tests/SwiftRide.TripService.Tests --filter FullyQualifiedName~Domain`; test mới phải fail trước code.
- [x] Implement restore với state factory internal và validation theo bất biến từng state. Overload confirm hai tham số: guard actor/quote trước mutation, khóa fare, DriverOffered/offered null, rejection count giữ nguyên. Không tái phát event bằng replay commands.
- [x] Giới hạn FareSnapshot <1e18 để đồng nhất Payment/SQL decimal(18,0); thêm test biên 999999999999999999 hợp lệ, 1000000000000000000 bị từ chối. Test `Restore_DoesNotReplayEvents` assert `Assert.Empty(restored.DomainEvents);`.
- [x] Assert confirm sai actor/quote không đổi snapshot; hai overload không mở transition sai state. Chạy lại toàn bộ Trip tests gồm 102 test cũ và encapsulation; all pass. Commit `feat: add validated trip snapshot restoration`.

## Task 03 — TripDB, concurrency và công việc bền vững

**File tạo:** Application port theo bản đồ; `Trips/TripContext.cs`, `CommandReceipt.cs`, `TripWorkLease.cs`; các persistence file trong bảng, `TripDbContextFactory.cs`, `Infrastructure/Migrations/*`; `tests/SwiftRide.TripService.Tests/Infrastructure/TripPersistenceTests.cs`, `TripDatabaseFixture.cs`; `docker-compose.test.yml`, `docs/run-integration-tests.ps1`. **File sửa:** `Infrastructure/TripDbContext.cs`, DI/csproj, Trip test csproj; local dotnet tool manifest nếu cần EF CLI.

**Consumes:** Task 02 snapshot. **Produces:** port storage ở trên; `ITripQueryReader` với `GetCurrentAsync(TripActor, ct)`, `GetDriverFeedAsync(TripActor, ct)`, `GetHistoryAsync(TripActor, string? cursor, int limit, ct)` trả read models Application; mọi command mutation+receipt/assignment/job dùng chung scoped context và một `SaveAsync` transaction.

- [x] Tạo fixture SQL Server/PostgreSQL chỉ cho integration test, ports bind localhost, credential qua env; guard tên `swiftride_*_test`/`matching_test` trước DDL. Script có `-Suite Trip|Payment|Matching|All`; chọn suite mà thiếu DB phải fail, không in connection string. Không chạy script này trong giai đoạn lập plan.

  Env test mới: `TRIP_TEST_DB`/`TRIP_TEST_RUNTIME_DB`, `PAYMENT_TEST_DB`/`PAYMENT_TEST_RUNTIME_DB` lần lượt là owner/runtime connection; Matching dùng `MATCHING_TEST_DB` theo fixture hiện có. Tên DB tương ứng `swiftride_trip_test`, `swiftride_payment_test`, `matching_test`. Dùng image DB theo repo; script provision login test và schema bằng owner, assertion runtime dùng credential hạn quyền.
- [x] Viết SQL Server test migrate từ DB rỗng -> save/load đủ snapshot/route; stale rowversion -> conflict; hai create key khác nhau cùng rider -> một active slot; hai claim cùng driver -> một thắng; failed unit-of-work không để receipt/assignment rời aggregate.
- [x] Chạy `powershell -NoProfile -File docs/run-integration-tests.ps1 -Suite Trip`; xác nhận test đỏ thật, không bị skip.
- [x] Map Trips/rejections/ActiveRiderTrips/ActiveDriverAssignments/CommandReceipts/TripPaymentJobs. Receipt scope gồm actor ID + role + operation + trip ID (create dùng scope cố định); canonical hash, response ref/progress, lease/attempt/next time; unique scope + key. Tiền SQL decimal(18,0), rowversion do SQL Server sinh.
- [x] Implement CAS claim và fence owner khi hoàn tất; lease Trip work 30s, validate lớn hơn HTTP-work + finalize budget. Matching work lưu trong receipt; payment work có deadline/count/lease riêng. Không giữ lock qua network, không dùng SQL retry để lặp side effect. Chỉ bắt đúng named unique/rowversion conflict; discard tracked mutation và reload winner khi thua race.
- [x] Query driver feed: offeredTrip khi đang giữ invitation, activeTrip chỉ DriverAccepted/EnRoute/PickedUp. Driver được rảnh ở dropoff dù payment cũ pending; lịch sử vẫn theo AssignedDriverId. Rider slot chỉ release ở Paid/Cancelled/Failed. Cursor theo CreatedAt+Id ổn định, limit 1..100, actor filter nằm trong query.
- [x] Chạy lại suite Trip DB và Trip unit tests; assert worker restart vẫn thấy due work, hai owner không cùng finalize. Commit `feat: persist trips and durable workflow progress`.

## Task 04 — Payment nhận zero fare và fingerprint không nhập nhằng

**File sửa:** `src/PaymentService/Domain/ValueObjects/Money.cs`, `Domain/Services/PaymentIdempotency.cs`, `Domain/Entities/Payment.cs`, `Application/Payments/UseCases/ProcessPaymentUseCase.cs`; các test `PaymentTests.cs`, `ProcessPaymentUseCaseTests.cs`, `PaymentEndpointTests.cs` trong Payment test project. **File tạo:** `tests/SwiftRide.PaymentService.Tests/PaymentFingerprintTests.cs`.

**Consumes:** contract Payment hiện có. **Produces:** Money nhận 0 VND; fingerprint v2 canonical có token digest; zero amount settled không gọi gateway. Giữ unique key và journal cũ, không rewrite fingerprint trong DB.

- [x] Viết test zero fare -> Settled + hai entry 0 cân bằng + gateway call count 0; âm/lẻ/1e18/currency khác VND bị từ chối. Sửa test cũ vốn assert 0 invalid để khớp yêu cầu.
- [x] Viết test cùng key/payload/token -> replay; đổi từng rider/driver/amount/currency/method/token -> 409 trước gateway; input chứa dấu `|` không gây trùng canonical payload. Token không xuất hiện trong fingerprint plaintext/log/result.
- [x] Chạy `dotnet test tests/SwiftRide.PaymentService.Tests`; test mới phải đỏ trước thay đổi.
- [x] Đổi `PaymentIdempotency.Fingerprint` sang canonical serialization có version cố định và SHA256 token digest; serialize decimal invariant. Cập nhật caller/test fixture theo chữ ký mới, không giữ nhánh replay bỏ kiểm tra token. Token vẫn gửi qua HTTP để mock xử lý nhưng không lưu raw token.
- [x] Implement zero-settlement và giữ transaction ledger như payment thường. Replay legacy không xác minh bằng metadata v2 ở Task 05 phải conflict/manual review; GET legacy vẫn đọc được. Chạy suite Payment pass; commit `fix: support zero fares and canonical payment fingerprints`.

## Task 05 — Payment claim nguyên tử và mock receipt qua restart

**File tạo:** `database/supabase/paydb-processing.sql`, `verify-paydb-processing.sql`; Application `Abstractions/IPaymentProcessingStore.cs`, `IMockOperationStore.cs`, `Payments/Dtos/PaymentProcessingDto.cs`; Infrastructure `Persistence/PaymentProcessingStore.cs`, `MockOperationStore.cs` và record/config tương ứng; test `Infrastructure/PaymentDatabaseFixture.cs`, `PaymentRecoveryTests.cs`, `PaymentDatabaseTests.cs` trong Payment test project.

**File sửa:** Payment `ProcessPaymentUseCase.cs`, `GetPaymentUseCase.cs`, `GetPaymentByTripUseCase.cs`, `PaymentDto.cs`, `PaymentMapper.cs`, `GatewayCallPolicy.cs`; `PaymentDbContext.cs`, `PaymentUnitOfWork.cs`, DI, gateway factory/mock adapters; Payment test csproj và script fixture Task 03.

**Consumes:** Task 04 và DDL paydb hiện có. **Produces:** `TryClaimAsync(Guid paymentId, Guid owner, DateTimeOffset now, CancellationToken ct)` trả processing claim hoặc trạng thái đang xử lý/hết budget; `Task CompleteAsync(Guid paymentId, Guid owner, CancellationToken ct)` fence cùng owner trong transaction finalize. Bổ sung `Task IUnitOfWork.ExecuteInTransactionAsync(Func<CancellationToken, Task> finalize, CancellationToken ct)` để CAS completion và SaveAsync financial changes cùng commit/rollback, không mở transaction này trước gateway. `IMockOperationStore.ExecuteOnceAsync(MockOperation input, CancellationToken ct)` trả receipt bền vững theo operation+key và canonical hash.

- [x] Viết PostgreSQL integration test bằng DDL gốc + additive SQL, runtime login thuộc `swiftride_payment`: insert/settle/rollback, unique key, đúng hai ledger entries, không update/delete journal, không đọc matchdb. Test schema bằng runtime credential, không chỉ owner.
- [x] Viết test 20 request đồng thời cùng key, hai stale retries, gateway success rồi finalize rollback/restart: một receipt charge thành công và một cặp ledger. Fake gateway transient có thể được gọi lại có giới hạn; không gọi hai charge thành công cho cùng operation key.

  Test `RestartAfterGatewaySuccess_FinalizesOnce` kiểm tra `Assert.Single(successfulChargeReceipts);` và `Assert.Equal(2, settledPayment.LedgerEntries.Count);` sau khi restart host với cùng DB.
- [x] Viết test attemptsStarted tối đa 3 qua restart; GET/202 không tăng count; owner hết lease không finalize đè owner mới. Chạy `powershell -NoProfile -File docs/run-integration-tests.ps1 -Suite Payment` để thấy fail thật.
- [x] Tạo `payment_processing` chứa payment ID, canonical version/hash, driver ID phục hồi, attempt count, owner/lease và outcome. Insert cùng transaction Pending. CAS claim/tăng attempt trước network, lease 60s lớn hơn gateway timeout 10s + finalize budget; thua claim trả Pending/Retry-After. Không dùng UpdatedAt làm claim.
- [x] Tạo `mock_gateway_operations` chứa operation/key/hash/result/reference, không raw token; commit receipt trên context/connection riêng trước financial finalize. Success/decline replay bền vững; transient không bị đóng băng thành receipt thành công. Factory/adapter lifetime phù hợp scoped DB, không singleton giữ DbContext.
- [x] Finalize status + hai entry + processing completion trong một transaction có owner fence; thua race reload winner sau đối chiếu. Bổ sung `processing` vào DTO nội bộ của cả POST/GET theo spec, không expose owner/hash/token. Pending/Failed có thể bổ sung driver từ metadata nhưng Trip không giả định legacy có trường này.
- [x] Với record không có metadata v2: GET hoạt động; POST mutating replay trả conflict để kiểm tra thủ công, không backfill bằng suy đoán token/driver và không charge. SQL giữ RLS/grants tách schema; additive script không tự chạy lên Supabase.
- [x] Chạy Payment unit/API/DB suites; kiểm tra suite được chọn không skip. Commit `fix: recover payment processing without duplicate charges`.

## Task 06 — CreateTrip, quote và phục hồi Requested

**File tạo:** `src/TripService/Application/Trips/CreateTripUseCase.cs`, `TripWorkCoordinator.cs`, `TripCommands.cs`, `TripActor.cs`, `DependencyInjection.cs`; Infrastructure `Workers/PendingTripWorker.cs`; test `Application/CreateTripTests.cs`, `TripWorkRecoveryTests.cs` trong Trip test project.

**Consumes:** client Task 01, snapshot Task 02, storage Task 03. **Produces:** `CreateTripUseCase.ExecuteAsync` và `TripWorkCoordinator.ResumeAsync` đã định nghĩa; worker gọi coordinator sau khi lease, không tự đổi state.

- [x] Viết test create -> persist Requested/route/receipt/rider slot trước HTTP -> Matched + Quote -> Quoted. Assert fare lấy nguyên từ quote; route distance dùng QuoteResult, không lấy khoảng cách candidate. NoDriverAvailable thật -> Failed và release rider slot.
- [x] Viết test mất response rồi replay cùng key -> cùng TripId; key khác payload -> conflict; hai key cùng rider -> một trip active. Timeout không thành NoDriverAvailable; client disconnect/restart vẫn còn due work. Chạy `dotnet test tests/SwiftRide.TripService.Tests --filter FullyQualifiedName~Application` để thấy đỏ.
- [x] Implement validation tọa độ, mode standard, method card/wallet; actor từ context được xác thực, ID tối đa 100. Chọn mock alias từ server configuration theo method; không nhận giá/token thật từ browser. Lưu CreatedAt/UpdatedAt UTC và max rejection mặc định 3 theo options.

  Alias mặc định `card.success.v1`/`wallet.success.v1`; HTTP adapter resolve sang mock token qua config, DB Trip chỉ lưu alias. Không retarget alias đã dùng trong pending job; fault tests chọn alias declined/timeout qua test config.
- [x] Sau commit intent, xử lý ngay một lần trong budget 12s; claim receipt trước network. `/quote` không transport retry; workflow retry có thể sinh quote mồ côi nhưng chỉ snapshot đã commit được dùng. Khi ghi kết quả phải reload/kiểm tra version, không hồi sinh chuyến đã cancel.
- [x] Persist matching attempts/next due; tối đa 3 workflow attempt, exponential base 1s/cap 60s + jitter. Hết budget transient giữ trạng thái hiện tại và requiresAction, không bịa terminal Failed. Worker dùng TimeProvider/DI scope, cancellation đúng, không hold lock/transaction khi HTTP.
- [x] Chạy Trip Application + DB tests pass; commit `feat: create trips with persisted matching and quotes`.

## Task 07 — Confirm, driver commands và truy vấn có phân quyền

**File tạo:** `src/TripService/Application/Trips/TripCommandUseCase.cs`, `TripQueryService.cs`; test `Application/TripCommandTests.cs`, `TripAuthorizationTests.cs`, `Infrastructure/DriverAssignmentRaceTests.cs`. **File sửa:** `TripWorkCoordinator.cs`, `TripCommands.cs`, query reader/storage Task 03.

**Consumes:** Task 06 và Domain public commands. **Produces:** `TripCommandUseCase.ExecuteAsync`; `TripQueryService` chuyển kết quả reader thành read model theo actor, không trả aggregate.

- [x] Viết test confirm khóa giá -> DriverOffered/rematch pending -> claim driver + invitation; driver accept/en-route/pickup đúng thứ tự; sai actor hoặc transition không mutate. Cancel chỉ trước DriverAccepted; driver reject tăng đúng một lần, >k Failed, <=k rematch giữ nguyên quote/giá.
- [x] Viết race test accept/cancel và hai trip claim cùng driver; replays không tăng rejection count hay lặp command. Driver bận không tính là từ chối; driver đã reject/rider ID không được chọn. Chạy Trip tests để thấy test mới đỏ.
- [x] Implement confirm overload hai tham số rồi commit pending intent; gọi `/match` lại để kiểm tra online/fresh trước invitation, không tính lại giá. Claim+OfferReplacementDriver+receipt hoàn tất trong một transaction. Tối đa 5 candidate mỗi lượt, xung đột unique thì reload và thử ứng viên tiếp, không publish invitation chưa commit.
- [x] Chỉ `/match` NoDriverAvailable thật mới gọi FailNoDriverAvailable; hết batch conflict thì schedule lượt sau trong budget, không giả rằng hết tài xế. Limit excluded IDs <=100, không truyền danh sách vô hạn. Reuse worker Task 06 cho rematch.
- [x] Implement current/feed/history theo Task 03; GET-by-ID chỉ rider chủ chuyến hoặc driver invitation/assignment. Driver đã reject không đọc được chuyến chỉ vì từng thấy offer. Cursor invalid/limit sai -> 400; hai page không trùng/mất item khi thứ tự CreatedAt bằng nhau.
- [x] Release driver slot ở dropoff/cancel/fail, rider slot ở terminal; mọi update cùng transaction. Chạy unit/SQL race tests pass; commit `feat: add authorized trip commands and driver matching`.

## Task 08 — Dropoff, payment job và đối soát đến Paid

**File tạo:** `src/TripService/Application/Payments/TripPaymentCoordinator.cs`, `PaymentWorkflowOptions.cs`; Infrastructure `Workers/PendingPaymentWorker.cs`; test `Application/TripPaymentTests.cs`, `TripPaymentRecoveryTests.cs`. **File sửa:** dropoff branch của `TripCommandUseCase.cs`, Trip work store/DI.

**Consumes:** Task 05 processing metadata, Task 07 commands, IPaymentClient. **Produces:** coordinator theo interface xuyên task, persisted payment job; worker và request dùng chung coordinator.

- [x] Viết test dropoff ghi RideCompleted một lần, RequestPayment và job cùng transaction trước HTTP. Key luôn `trip-{id:D}-payment`; charge đúng locked fare/assigned driver/method, không hỏi Pricing lại.
- [x] Viết test Settled khớp -> Paid/release rider; 201 Failed/202 Pending/503/mismatch/refund-settled không Paid. GET xác nhận settled sau mất response -> chỉ finalize Trip, không POST lại. Chạy Trip payment tests để thấy đỏ.
- [x] Implement bước đầu một POST sau khi lưu intent; những lượt sau GET-by-trip trước. GET lỗi/mơ hồ chỉ schedule GET tiếp; GET 404 hoặc Processing.CanRetry khi lease hết mới POST lại cùng payload/key. 400/401/403/409 hoặc decline -> requiresAction, không transient retry.
- [x] Lưu reconciliation count trước network, deadline từ job ban đầu; tối đa 20 lượt/10 phút, tối đa 1 GET + 1 POST/lượt. Payment mới là bên cưỡng chế tối đa 3 gateway attempts. Khi attempt đã hết vẫn GET trong cửa sổ còn lại; Retry-After được cap 60s, schedule bằng DB, không sleep HTTP request.
- [x] Viết test ba 202 liên tiếp không tiêu hao charge budget; attempt thứ ba còn in-flight rồi settled vẫn Paid; hết reconciliation budget không còn auto network. Restart không reset counters/deadline. Không tự refund khi Trip save Paid bị lỗi: đối soát lại và lưu Paid.
- [x] Chạy Trip tests + recovery DB tests pass; commit `feat: settle trips through durable payment reconciliation`.

## Task 09 — Trip HTTP API, JWT và khóa internal endpoints

**File tạo:** `src/TripService/Api/TripApplication.cs`, `TripContracts.cs`, `TripResponseMapper.cs`, `src/PaymentService/Api/PaymentApplication.cs`; auth policy file tại API của Matching và Payment; `tests/SwiftRide.Api.Tests/Trip/TripEndpointTests.cs`, `InternalServiceSecurityTests.cs`. **File sửa:** Trip `Program.cs`, `TripEndpoints.cs`, `GlobalExceptionHandler.cs`, API DI/config; Matching API endpoints/composition; Payment `Program.cs`, `PaymentEndpoints.cs`, endpoint tests; Gateway tests/config khi cần.

**Consumes:** Task 06-08. **Produces:** các route/JSON/status trong spec §3.2; composition `TripApplication.Build(WebApplicationBuilder builder, Action<IServiceCollection>? configureServices = null)` và `PaymentApplication.Build(...)` cùng seam như Matching để integration test dùng host thật.

- [x] Viết HTTP test rider/driver JWT: missing401, wrong role403, wrong resource403, invalid input400, transition/key conflict409, safe unexpected500. Tạo mới 201+Location; accepted pending202; replay200 hoặc202 theo tiến độ; current rỗng204. Assert fare JSON là string và route literals không bị `{id}` nuốt.
- [x] Viết internal route test: không key/sai key ->401/403; browser JWT không charge; đúng key có quyền đúng scope. Gateway vẫn404 cho `/match`, `/quote`, `/payments/**`; chặn/loại header internal key do browser gửi. Chạy `dotnet test tests/SwiftRide.Api.Tests` để thấy test mới đỏ.
- [x] Map endpoint mỏng vào use case, actor lấy từ sub/role, MapInboundClaims=false. Không nhận actor/status/fare tùy ý từ body. ProblemDetails chỉ mã an toàn/traceId, không exception message/token/body/connection string.
- [x] Bảo vệ Matching match/quote bằng key Trip->Matching và mọi Payment GET/POST/refund bằng key Trip->Payment; health giữ liveness. Không thêm public payment route để frontend nối nhanh. Dùng config keys riêng, không hardcode hoặc tạo Common auth project.
- [x] Thêm composition seam Payment/Trip, thay process-global env mutation trong PaymentEndpointTests bằng per-host test config; bỏ DotNetEnv traversal implicit khỏi bootstrap. Giữ startup behavior khác; chỉnh test exception/Gateway cũ vốn mong 501 theo behavior mới.
- [x] Chạy toàn bộ .NET suite pass; commit `feat: expose authorized trip APIs and secure service calls`.

## Task 10 — Frontend session, client, store và tiền chính xác

**File tạo:** `src/WebApp/src/auth.ts`, `api.ts`, `tripTypes.ts`, `tripApi.ts`, `tripStore.ts`, `useTrip.ts`, `money.ts` và test cùng tên. **File sửa:** `locations.ts`, `locations.test.ts`, `vite.config.ts`; tách shared Place type để demo/map/HTTP cùng dùng.

**Consumes:** Trip public contract Task 09. **Produces:** `createTripApi(session)` với create/getCurrent/getById/getDriverFeed/getHistory/command; `createTripStore(api, actor)` quản lý server state/single-flight và subscribe; `formatVnd(amount: string): string`, `sumVnd(amounts: string[]): string`. Hook chỉ subscribe và quản lý lifecycle của store.

- [x] Viết Vitest cho Authorization chung booking/search, AbortSignal timeout 20s, 401 không fallback mock; DEV lấy token đúng role/ID, production không gọi dev token. Token chỉ trong memory, không localStorage/log. Chạy `npm ci` tại `src/WebApp` khi bắt đầu thực thi, rồi `npm test`; ghi nhận đỏ của test mới.
- [x] Viết test exact money `999999999999999999` và tổng hai fare; khoảng thời gian decimal được chấp nhận; Requested có fare null. Không convert tiền qua Number để format/cộng.

  Test `sumVnd preserves whole-unit precision`: `expect(sumVnd(['999999999999999999', '1'])).toBe('1000000000000000000')`.
- [x] Implement typed API và per-action UUID command key; pending action lưu key/payload/trip ID qua reload, không lưu JWT/token. Cùng action retry giữ key; user đổi payload sinh action mới. Khi create response mất, ưu tiên current rồi replay cùng key nếu chưa tìm được.
- [x] Implement store một request poll/role mỗi 3s, không overlapping; abort và version guard khi đổi actor/role/trip/unmount; request timeout không reset UI sang demo. Backoff polling lỗi 429 theo Retry-After, tránh retry loop khi token hết hạn.
- [x] Reuse session cho locations; bỏ dev token request riêng mỗi search. Vite proxy thêm `/trips`, `/drivers`, giữ routes hiện có, không mở internal APIs. Không thêm state library hoặc query framework mới.
- [x] Chạy `npm run typecheck`, `npm test`, `npm run build` pass; commit `feat: add frontend gateway clients and trip state`.

## Task 11 — Nối Rider/Driver UI, heartbeat và history

**File sửa:** `src/WebApp/src/App.tsx`, `RiderView.tsx`, `DriverView.tsx`, `RideMap.tsx`, `demo.ts`, `demo.test.ts`, các component tìm địa chỉ; **file tạo:** `driverLocation.ts`, `driverLocation.test.ts`, test controller booking/driver API.

**Consumes:** Task 10 store/client và existing layout/map. **Produces:** backend mode mặc định khi nghiệm thu; mock mode chỉ khi chọn tường minh, không tự chuyển khi API lỗi.

- [x] Viết test controller: quote/create -> confirm/cancel gọi đúng API/key; driver actions đúng route; UI không tự gán Paid. Role switch bỏ response cũ; read history theo actor và chỉ tải khi mở/sau action liên quan. Chạy `npm test` để thấy test mới đỏ.
- [x] Thay useDemo/demoApi trong backend mode của App/views/map bằng store. Giữ map selection/address search/attribution, giữ layout; tách types khỏi demo, không viết lại giao diện để né integration. Hiển thị Requested/rematch/Pending/requiresAction rõ ràng; remove manual settle ở backend mode.
- [x] Driver online gửi PUT location ngay + heartbeat 30s, offline gửi isOnline=false và dừng timer. Dùng vị trí demo có nhãn, không nhận là GPS thật; driver phụ demo-driver-2 phục vụ rematch. Test fake timers không trùng heartbeat, cleanup/đổi token/offline và error recovery bounded.
- [x] Driver feed chỉ current offer/active ride; sau dropoff slot rảnh, payment cũ đọc qua detail/history. Rider PaymentPending vẫn giữ active trip và không hiện nút đặt mới/hủy trái luật. Tổng thu nhập dùng BigInt và chỉ cộng các trip Paid.
- [x] Chạy frontend typecheck/test/build pass; kiểm tra browser thực hai tab rider/driver, reload ở Quoted/DriverAccepted/PaymentPending; search/map còn dùng được. Commit `feat: connect rider and driver screens to trip APIs`.

## Task 12 — Compose, kiểm thử toàn luồng và tài liệu vận hành

**File sửa:** `docker-compose.yml`, `.env.example`, Trip/Payment/Matching appsettings/DI, `README.md`, `docs/architecture.md`, `database-supabase.md`, `frontend-demo.md`, `matching-pricing.md`, `check-skeleton.ps1`, Payment README/hướng dẫn Supabase; **file tạo:** `docs/check-booking-flow.ps1`, `docs/end-to-end-booking.md`, `tests/SwiftRide.Api.Tests/FullFlow/BookingFlowTests.cs`, `BookingRecoveryTests.cs` và fixture host.

**Consumes:** Task 01-11; Docker/DB runtime credentials do môi trường cung cấp. **Produces:** quy trình bootstrap và bằng chứng Gate A-E của spec. Không đánh dấu đạt khi DB test skip.

- [x] Viết FullFlow test chạy Gateway/Trip/Matching/Payment composition thật qua HTTP với SQL Server/PostgreSQL fixture Task 03, không thay repository bằng memory. Script booking lấy dev JWT rồi create/confirm/accept/en-route/pickup/dropoff/poll Paid qua Gateway; assert một Trip, một payment settled, đúng hai entries cân bằng. Assertion DB dùng test-only credential theo từng service, không runtime join chéo schema.
- [x] Viết replay/race/recovery cases: lặp mọi command; cancel vs accept; reject đến >k; không có driver; zero fare; mock declined/timeout; crash sau dropoff, sau gateway receipt/trước finalize, sau Payment settled/trước Trip Paid. Dùng barrier/fault injection chỉ ở test composition, không public debug endpoint.
- [x] Chạy FullFlow tests và xác nhận đỏ trước thiếu config/fixture; sau đó thêm base addresses, JWT Trip, internal keys và validated worker options vào Compose. Bỏ Payment depends_on postgres/option migrate không được sử dụng; bỏ local PostgreSQL khỏi stack runtime mặc định Supabase, giữ riêng trong test compose, không xóa volume cũ.
- [x] Thêm migration TripDB rõ ràng với owner credential, runtime least privilege; Matching/Payment không auto migrate Supabase. Trước cloud rollout: verify drift + kiểm kê Pending legacy + áp SQL additive đã test bằng owner ở đúng môi trường. Không dùng test bootstrap để reset cloud schema. `.env.example` chỉ placeholders; không in `docker compose config` đã expand secret, dùng `--quiet`.
- [x] Chạy `dotnet build SwiftRide.sln`, `dotnet test SwiftRide.sln`, `powershell -NoProfile -File docs/run-integration-tests.ps1 -Suite All`, frontend typecheck/test/build, `docker compose config --quiet`. Sau đó `docker compose up --build` và script `docs/check-booking-flow.ps1` với Supabase demo đã provision. Liveness không thay được readiness/database test.
- [x] Kiểm tra browser hai tab, freshness qua ít nhất một heartbeat, driver reject/rematch, reload và history server. Ghi command/kết quả, môi trường và số ca chạy/skip; không gọi mock financial charge là thanh toán ngân hàng thật.
- [x] Cập nhật docs bỏ các nhận định cũ rằng Matching/Payment còn skeleton hoặc Payment chưa map paydb. Giữ các plan 19/10 như lịch sử. Các cảnh báo Pkcs hiện có ghi riêng; nếu xử lý package thì thành thay đổi bảo trì có kiểm chứng riêng.
- [x] Chỉ khi Gate A-E có chứng cứ đầy đủ mới chốt core; commit `test: verify persisted booking and payment recovery end to end`.

## Task 13 — Hardening refund sau core

**File sửa:** Payment `Application/Payments/UseCases/RefundPaymentUseCase.cs`, `Domain/Entities/Refund.cs`, `Infrastructure/Persistence/PaymentDbContext.cs`, `Configurations/RefundConfiguration.cs`, gateway adapters; **file tạo:** `database/supabase/paydb-refund-processing.sql`, `tests/SwiftRide.PaymentService.Tests/Infrastructure/RefundRecoveryTests.cs`; cập nhật Payment docs.

**Consumes:** Task 05 claim/receipt primitives và refund DDL hiện có. **Produces:** refund pending intent bền vững, cùng row được resume khi pending/failed, operation key `payment-{paymentId:D}-refund`; không public refund UI.

- [x] Viết DB tests concurrent refund, timeout/restart sau refund gateway receipt/trước commit, replay failed/pending trên cùng row. Assert một refund thành công và một cặp reversal ledger; không sửa/debit lại payment gốc. Chạy Payment suite để thấy test đỏ.
- [x] Persist Pending trước gateway, claim bằng operation scope refund, receipt bền vững; permit đúng transitions của refund trong EF guard. `updated_at` do DB trigger sở hữu nên EF không UPDATE cột không có grant. Retry transient có budget, không tạo refund row/key mới để vượt constraint.
- [x] Legacy refund không xác minh payload/receipt thì đọc được nhưng mutating retry yêu cầu kiểm tra thủ công. Không tự refund chỉ vì Trip đang PaymentPending trong khi Payment đã settled.
- [x] Chạy Payment unit/API/PostgreSQL recovery tests pass; cập nhật hướng dẫn và commit `fix: persist and recover refund processing safely`.

## Checklist tự rà soát plan

- [x] Đối chiếu code mới: Payment đã map paydb, Matching/location/search tái sử dụng; Trip 501/persistence trống là phần nối chính.
- [x] State Pattern internal tiếp tục giữ; Domain có restore được kiểm tra, không dùng reflection hoặc phát lại events.
- [x] Chốt một active rider; driver release ở dropoff; query/feed phù hợp cardinality và quyền actor.
- [x] Phân biệt gateway attempts và reconciliation polls; late settlement còn được đối soát, mọi retry có giới hạn qua restart.
- [x] Zero VND, token fingerprint, legacy records, crash/race và refund đều có task/test sở hữu.
- [x] Interface tên nhất quán; không thêm business service, broker/Redis hoặc Common project.
- [x] Kiểm chứng frontend bằng browser thực và backend bằng DB thật, không suy E2E từ unit tests.

## Bằng chứng hiện có và điều kiện bắt đầu

Trong lần đọc/lập plan: build .NET đã chạy thành công; **221 test pass, 1 PostgreSQL test skip**. Chưa kiểm chứng frontend, Supabase runtime hoặc luồng E2E. Các checkbox Task 01-13 đều chưa thực hiện; checklist tự rà soát chỉ đánh giá tài liệu.

Khi bắt đầu thực thi: kiểm tra working tree, dùng skill worktree nếu cần isolation, đọc lại source đã thay đổi sau `f43074d`, rồi làm Task 01. Cần Docker, môi trường DB test disposable, Supabase demo có login runtime riêng và quyền áp additive DDL; secret lấy qua env/user-secrets, không đưa vào chat hoặc commit.

Ưu tiên bàn giao core Task 01-12 trước; Task 13 nghiệm thu riêng trước khi tuyên bố refund chịu được restart. Nếu một gate thiếu môi trường thì ghi đúng phần chưa kiểm chứng, tiếp tục các task độc lập, không đánh dấu toàn bộ luồng đã hoàn thành.
