# Trip lifecycle State Pattern - Kế hoạch triển khai

> **Dành cho agent thực thi:** Dùng skill `executing-plans` nếu triển khai trực tiếp; chỉ dùng `subagent-driven-development` khi người dùng chọn cách thực thi bằng subagent. Theo dõi tiến độ bằng các checkbox. Đọc spec cùng plan, không coi ví dụ State rút gọn trong đề là toàn bộ lifecycle.

**Goal:** Xây Trip Domain dùng State Pattern thật, bảo vệ đầy đủ vòng đời 11 trạng thái, giá đã chốt, driver rejection và kết quả thanh toán, với test hành vi cho mọi transition.

**Architecture:** `Trip` là context, giữ dữ liệu và ủy quyền command cho `TripState` hiện tại; mỗi concrete state chỉ cho phép những hành vi hợp lệ của nó. Domain không dùng ASP.NET, EF Core, HTTP hoặc broker. Phần này xây luật và domain event, chưa thay endpoint `501`, chưa lưu DB hoặc thực hiện thanh toán.

**Tech Stack:** C# / `net10.0`, xUnit `2.9.3`; giữ Moq đã có nhưng không mock aggregate/state. Không thêm package, project, state-machine framework hoặc service.

**Spec:** [architecture.md](../../architecture.md), mục 5-7, 9.1, 10, 16-18; [AGENTS.md](../../../AGENTS.md), mục 5.1, 7 và 11. Gateway có plan riêng: [api-gateway-skeleton](2026-10-19-api-gateway-skeleton.md).

## Global Constraints

- Domain chỉ dùng thư viện nền .NET; `SwiftRide.TripService.Domain.csproj` tiếp tục không có package/project reference.
- Đủ 11 trạng thái: `Requested`, `Quoted`, `DriverOffered`, `DriverAccepted`, `EnRoute`, `PickedUp`, `DroppedOff`, `PaymentPending`, `Paid`, `Cancelled`, `Failed`.
- Chỉ `Paid`, `Cancelled`, `Failed` là terminal. `PaymentPending` không terminal, kể cả hết retry.
- Chỉ hủy trước `DriverAccepted`. Bất kỳ transition không hợp lệ đều ném domain exception.
- `k` mặc định `3`, có thể truyền giá trị cấu hình từ Application; so sánh count **sau khi tăng**, lần từ chối thứ 4 mới vượt mặc định.
- Lưu quote snapshot `QuoteId`, amount và currency; không tính giá lại khi rematch hoặc payment.
- Giá dùng `decimal`, `VND`, số nguyên không âm; không có payment token, card number/CVV trong aggregate lifecycle của phạm vi này.
- Domain tạo `RideCompleted` khi dropoff, không gọi service khác hoặc publish integration event trực tiếp.
- Domain chuyển `Paid` chỉ từ kết quả settled khớp chuyến, fare và deterministic key; lỗi thanh toán giữ pending.
- Code/test/commit message bằng tiếng Anh; tài liệu và trao đổi bằng tiếng Việt.
- Mỗi dòng bảng transition có test; ít nhất một transition sai ở mỗi trạng thái. Không giữ test placeholder thay cho test nghiệp vụ.

## Review Focus

1. Nhầm `>= k` với `> k` làm chuyến thất bại sớm; test riêng `k=0`, `k=1`, mặc định `k=3`: **TRIP-02**.
2. Request từ driver cũ sau rematch tăng count/đi tiếp sai; test offered driver bị xóa và driver đã loại không được mời lại: **TRIP-02/TRIP-03**.
3. Command thất bại vẫn sửa fare/count/driver hoặc thêm event; assert dữ liệu trước/sau mọi invalid command quan trọng: **TRIP-02..05**.
4. Payment settled mang nhầm trip, amount, currency hoặc key vẫn làm `Paid`; test từng mismatch, giữ nguyên pending: **TRIP-04**.
5. Dropoff lặp phát hai domain event hoặc terminal tự chấp nhận command mới; test event count và invalid action cho đủ 11 state: **TRIP-03/TRIP-05**.

## 1. Phạm vi đã chốt để lập plan

Ngày 19/10/2026, `src/TripService/Domain` chưa có source nghiệp vụ; `tests/SwiftRide.TripService.Tests/UnitTest1.cs` chỉ có `Skeleton_Compiles`. Application cũng chưa có use case; các Trip endpoint trả `501`.

Plan này tương ứng đầu việc **TRIP-01** trong tài liệu kiến trúc, không bao gồm TRIP-02/FLOW-01 của roadmap tổng thể. Các mã `TRIP-01..05` **bên trong plan này** là năm task con triển khai Domain.

**Trong phạm vi:** aggregate tối thiểu cho lifecycle, quote snapshot, phân biệt candidate/offered/assigned driver, giới hạn từ chối, domain event, xác nhận kết quả payment và test.

**Ngoài phạm vi:** pickup/destination/location model đầy đủ, repository/migration/EF mapping, HTTP DTO/use case, JWT/resource authorization thật, driver reservation trong DB, command receipt/inbox, network retry/worker, charge/ledger/refund, RabbitMQ/Outbox/Saga, frontend. Không thêm in-memory repository để làm endpoint trông như đã hoạt động.

Các kiểm tra rider/driver ID ở Domain là kiểm tra tính nhất quán của aggregate. Khi nối API, Application vẫn phải lấy actor từ JWT; truyền một chuỗi ID vào Domain **không thay xác thực**. Tương tự, nhận `PaymentSettlement` nội bộ không tự chứng minh đã thu tiền; adapter/Application phải xác minh nguồn kết quả.

**Quyết định triển khai của plan:**

- Dùng `TripState` abstract class và 11 state nội bộ; không viết lifecycle thành switch lớn trong controller/aggregate.
- Các state không giữ dữ liệu riêng của chuyến; aggregate giữ data, state chỉ giữ hành vi. Có thể dùng instance stateless dùng chung, nhưng không lưu `Trip` trong singleton state.
- `ConfirmQuote` nhận driver sẽ được mời từ Application, có thể khác candidate gốc nếu candidate đã bận; Domain không tự geo-query hoặc giữ chỗ.
- Driver rejection xóa lời mời hiện tại, đánh dấu cần rematch; `OfferReplacementDriver` hoàn tất rematch mà không đổi giá/count. Lỗi mạng không được ánh xạ thành không có driver.
- Duplicate HTTP/event sẽ được xử lý bằng receipts/inbox ở giai đoạn Application/Infrastructure. Không làm Domain chấp nhận mọi command lặp để giả idempotency.

## 2. Bản đồ file và trách nhiệm

Đường dẫn tương đối với `C:\Users\Wuanz\Desktop\SwiftRide`.

| Thao tác | File | Trách nhiệm |
|---|---|---|
| Tạo | `src/TripService/Domain/Trip.cs` | Aggregate/context, dữ liệu read-only ra ngoài, public domain commands |
| Tạo | `src/TripService/Domain/TripStatus.cs` | Enum đủ 11 trạng thái; chưa chốt numeric persistence mapping |
| Tạo | `src/TripService/Domain/TripDomainException.cs` | Domain exception có `Code` ổn định, không lộ data nhạy cảm |
| Tạo | `src/TripService/Domain/ValueObjects/FareSnapshot.cs` | Quote ID + tiền VND bất biến, có validation |
| Tạo | `src/TripService/Domain/ValueObjects/PaymentSettlement.cs` | Kết quả settled nội bộ dùng để đối chiếu với Trip |
| Tạo | `src/TripService/Domain/PaymentFailureKind.cs` | `Transient`, `UnknownResult`, `Permanent`, `RetryLimitReached` |
| Tạo | `src/TripService/Domain/States/TripState.cs` | Default command không hợp lệ -> domain exception |
| Tạo | `src/TripService/Domain/States/RequestedState.cs` | Apply quote / không có driver / hủy |
| Tạo | `src/TripService/Domain/States/QuotedState.cs` | Confirm/reject quote / hủy |
| Tạo | `src/TripService/Domain/States/DriverOfferedState.cs` | Nhận/từ chối, mời thay thế, hết ứng viên, hủy |
| Tạo | `src/TripService/Domain/States/DriverAcceptedState.cs` | Bắt đầu đến đón |
| Tạo | `src/TripService/Domain/States/EnRouteState.cs` | Đón khách |
| Tạo | `src/TripService/Domain/States/PickedUpState.cs` | Trả khách + domain event |
| Tạo | `src/TripService/Domain/States/DroppedOffState.cs` | Chuyển chờ thanh toán |
| Tạo | `src/TripService/Domain/States/PaymentPendingState.cs` | Ghi kết quả settled/lỗi, không terminal |
| Tạo | `src/TripService/Domain/States/PaidState.cs` | Terminal, không có transition mới |
| Tạo | `src/TripService/Domain/States/CancelledState.cs` | Terminal, không có transition mới |
| Tạo | `src/TripService/Domain/States/FailedState.cs` | Terminal, không có transition mới |
| Tạo | `src/TripService/Domain/Events/IDomainEvent.cs` | Marker nhỏ cho domain events, không phụ thuộc transport |
| Tạo | `src/TripService/Domain/Events/RideCompleted.cs` | Snapshot thông tin chuyến đã hoàn thành phần vận chuyển |
| Tạo | `tests/SwiftRide.TripService.Tests/Domain/TripScenario.cs` | Fixture tạo trạng thái bằng public commands thật |
| Tạo | `tests/SwiftRide.TripService.Tests/Domain/TripCreationTests.cs` | Invariant khi tạo Trip |
| Tạo | `tests/SwiftRide.TripService.Tests/Domain/FareSnapshotTests.cs` | Tiền/quote ID hợp lệ |
| Tạo | `tests/SwiftRide.TripService.Tests/Domain/TripQuoteTests.cs` | Báo giá, khóa giá, cancel/reject |
| Tạo | `tests/SwiftRide.TripService.Tests/Domain/DriverOfferTests.cs` | Nhận/từ chối/rematch và biên `k` |
| Tạo | `tests/SwiftRide.TripService.Tests/Domain/TripProgressTests.cs` | Đến đón, đón, trả và event |
| Tạo | `tests/SwiftRide.TripService.Tests/Domain/TripPaymentTests.cs` | Pending, key và đối chiếu settlement |
| Tạo | `tests/SwiftRide.TripService.Tests/Domain/InvalidTransitionTests.cs` | Invalid action đủ 11 state, không mutation |
| Xóa sau khi có test thật | `tests/SwiftRide.TripService.Tests/UnitTest1.cs` | Bỏ placeholder |
| Cập nhật sau triển khai | `README.md`, `docs/architecture.md` | Hiện trạng Domain đã làm; API/persistence còn thiếu |

Không đổi csproj/reference, `SwiftRide.Contracts` hoặc service khác. Tạo file theo task thực tế, không scaffold toàn bộ chữ ký throw sẵn cho các phần chưa làm.

## 3. Hợp đồng Domain mục tiêu

Các chữ ký là quyết định cần giữ nhất quán giữa task và test; không phải nguyên file C# để copy. Namespace gốc `SwiftRide.TripService.Domain`, namespace con theo `States`, `ValueObjects`, `Events`.

### 3.1. Aggregate API

```csharp
public static Trip Request(Guid id, string riderId, int maxDriverRejections = 3);
public void ApplyQuote(FareSnapshot fare, string candidateDriverId);
public void FailNoDriverAvailable();
public void ConfirmQuote(string riderId, Guid quoteId, string offeredDriverId);
public void RejectQuote(string riderId);
public void Cancel(string riderId);
public void AcceptDriver(string driverId);
public void RejectDriver(string driverId);
public void OfferReplacementDriver(string driverId);
public void StartEnRoute(string driverId);
public void Pickup(string driverId);
public void Dropoff(string driverId, DateTimeOffset occurredAtUtc);
public void RequestPayment();
public void SettlePayment(PaymentSettlement settlement);
public void RecordPaymentFailure(PaymentFailureKind failureKind);
```

Public properties chỉ đọc: `Id`, `RiderId`, `Status`, `IsTerminal`, `Fare`, `IsFareLocked`, `CandidateDriverId`, `OfferedDriverId`, `AssignedDriverId`, `RejectionCount`, `MaxDriverRejections`, `RejectedDriverIds`, `IsRematchPending`, `PaymentIdempotencyKey`, `PaymentId`, `LastPaymentFailure`, `DomainEvents`. Property chưa có nghĩa trước một giai đoạn dùng nullable đúng kiểu, ví dụ `FareSnapshot?`, `string?`, `Guid?`, `PaymentFailureKind?`.

Không public setter của `Status`/fare/driver/count, không public constructor cho phép chọn state tùy ý. Collection trả read-only wrapper/snapshot, không trả raw list/hashset có thể cast để sửa aggregate. Chưa cần rehydration constructor/serializer riêng cho EF.

### 3.2. Value object và lỗi

```csharp
public FareSnapshot(Guid quoteId, decimal amount, string currency);
// Get-only: QuoteId, Amount, Currency.

public PaymentSettlement(Guid paymentId, Guid tripId, decimal amount,
    string currency, string idempotencyKey);
// Get-only: PaymentId, TripId, Amount, Currency, IdempotencyKey.

public TripDomainException(string code, string message);
// Get-only: Code. Message tiếng Anh, không chứa token hoặc dữ liệu riêng tư.

public interface IDomainEvent { }
public sealed record RideCompleted(
    Guid TripId, string RiderId, string DriverId, Guid QuoteId,
    decimal FareAmount, string Currency,
    DateTimeOffset OccurredAtUtc) : IDomainEvent;
```

`FareSnapshot` và `PaymentSettlement` dùng get-only properties, không có `init` cho phép `with` bỏ qua validation. Quote/payment/trip ID không rỗng; actor/key không trắng; amount không âm, không phần lẻ. `FareSnapshot` chỉ nhận `VND`; `PaymentSettlement` cho phép currency khác nhưng không trắng để Trip đối chiếu và từ chối kết quả sai tiền tệ bằng `PaymentMismatch`. Domain không làm tròn lại giá đã nhận.

Các `Code` dùng trong test: `InvalidTransition`, `InvalidTripId`, `InvalidRider`, `InvalidDriver`, `InvalidQuote`, `InvalidFare`, `InvalidRejectionLimit`, `RematchPending`, `DriverAlreadyRejected`, `PaymentMismatch`, `InvalidPaymentResult`. Không khóa test vào nguyên câu exception message.

### 3.3. State Pattern

`Trip` giữ private `TripState _state`; `Status` đọc từ state. Public command delegate sang virtual handler tương ứng trên state, thêm `Trip` làm tham số đầu, ví dụ `TripState.AcceptDriver(Trip trip, string driverId)` và `TripState.Dropoff(Trip trip, string driverId, DateTimeOffset occurredAtUtc)`.

Base handler mặc định ném `TripDomainException` với `Code = "InvalidTransition"`. Concrete state override đúng hành vi hợp lệ; validate toàn bộ input/actor trước mutation. Aggregate có hàm chuyển state nội bộ, chỉ code Domain gọi; không expose setter cho controller.

Các helper mutation nội bộ được phép giữ nhỏ và có mục đích cụ thể. Không đưa cả bảng luật trở lại một `switch(Status)` rồi giữ các state class rỗng cho đủ tên pattern. Không dùng singleton state có field mutable chứa dữ liệu chuyến.

## 4. Bảng transition và truy vết test

| ID spec | State / command | Kết quả | Task |
|---|---|---|---|
| TR-01 | Requested + `ApplyQuote` | Quoted, lưu quote/candidate | TRIP-02 |
| TR-02 | Requested + `FailNoDriverAvailable` | Failed | TRIP-02 |
| TR-03 | Requested + `Cancel` | Cancelled | TRIP-02 |
| TR-04 | Quoted + `ConfirmQuote` | DriverOffered, khóa fare | TRIP-02 |
| TR-05 | Quoted + `RejectQuote` hoặc `Cancel` | Cancelled | TRIP-02 |
| TR-06 | DriverOffered + `AcceptDriver` | DriverAccepted | TRIP-03 |
| TR-07 | DriverOffered + `RejectDriver`, count sau tăng `<= k` | Giữ DriverOffered; xóa lời mời cũ; `OfferReplacementDriver` không đổi fare | TRIP-02 |
| TR-08 | DriverOffered + `RejectDriver`, count sau tăng `> k` | Failed | TRIP-02 |
| TR-09 | DriverOffered đang rematch + `FailNoDriverAvailable` | Failed | TRIP-02 |
| TR-10 | DriverOffered + `Cancel` | Cancelled, xóa lời mời | TRIP-02 |
| TR-11 | DriverAccepted + `StartEnRoute` | EnRoute | TRIP-03 |
| TR-12 | EnRoute + `Pickup` | PickedUp | TRIP-03 |
| TR-13 | PickedUp + `Dropoff` | DroppedOff + một RideCompleted | TRIP-03 |
| TR-14 | DroppedOff + `RequestPayment` | PaymentPending + deterministic key | TRIP-04 |
| TR-15 | PaymentPending + `SettlePayment` khớp dữ liệu | Paid + PaymentId | TRIP-04 |
| TR-16 | PaymentPending + transient/unknown failure | PaymentPending, key/fare không đổi | TRIP-04 |
| TR-17 | PaymentPending + permanent/exhausted failure | PaymentPending, không terminal | TRIP-04 |

`OfferReplacementDriver` là cập nhật lời mời trong cùng state, cần test riêng. `RejectQuote` và `Cancel` cùng state đích nhưng là hai public command nên mỗi command phải được kiểm tra.

## 5. Quy ước thực thi và fixture

Mỗi task: viết test hành vi -> chạy thấy thất bại đúng nguyên nhân -> triển khai tối thiểu -> chạy lại -> kiểm tra diff/checkpoint. Không tính lỗi typo/reference là RED. Với type mới, khai báo hợp đồng tối thiểu cần để test biên dịch; không điền trước logic nhiệm vụ sau.

Fixture test-only `TripScenario` dùng dữ liệu cố định:

```text
TripId     = 11111111-1111-1111-1111-111111111111
QuoteId    = 22222222-2222-2222-2222-222222222222
PaymentId  = 33333333-3333-3333-3333-333333333333
RiderId    = rider-1
DriverId   = driver-1
Fare       = 74844m VND
DropoffUtc = 2026-10-19T00:00:00+00:00
PaymentKey = trip-11111111-1111-1111-1111-111111111111-payment
```

Tạo các factory test khi cần: `Requested(int maxDriverRejections = 3)`, `Quoted(int maxDriverRejections = 3)`, `Offered(int maxDriverRejections = 3)`, `PickedUp()`, `PendingPayment()`, cuối cùng `At(TripStatus status)`, đều trả `Trip`. Chúng đi qua command thật, không reflection/private setter/mock state. Expected value dùng literal/fixture đã tính tay, không suy ra bằng helper production.

## 6. Các task triển khai

### TRIP-01: Aggregate ban đầu, value object giá và default state behavior

**Files:** Tạo `Trip.cs`, `TripStatus.cs`, `TripDomainException.cs`, `ValueObjects/FareSnapshot.cs`, `States/TripState.cs`, `States/RequestedState.cs`; test `TripCreationTests.cs`, `FareSnapshotTests.cs`, `TripScenario.cs`. Đường dẫn đầy đủ theo bảng file mục 2.

**Interfaces:** Tạo `Trip.Request`, các property ban đầu và `FareSnapshot` theo mục 3. Command/state handler mới được bổ sung theo từng task sau; chưa khai báo type payment/event chưa tồn tại.

- [ ] **Bước 1 - Viết test constructor/factory:**

```csharp
[Fact]
public void Request_StartsRequestedWithoutFareOrDriver()
{
    var trip = Trip.Request(TripScenario.TripId, "rider-1");

    Assert.Equal(TripStatus.Requested, trip.Status);
    Assert.False(trip.IsTerminal);
    Assert.Null(trip.Fare);
    Assert.Null(trip.AssignedDriverId);
    Assert.Equal(0, trip.RejectionCount);
}
```

Thêm theory `Request_RejectsInvalidIdentityOrLimit`: empty Trip ID, rider null/trắng, `maxDriverRejections=-1` ném domain exception đúng `Code`; `k=0` được chấp nhận. Thêm `FareSnapshot_RejectsInvalidQuoteOrMoney`: empty quote ID, amount `-1m`, `1.5m`, currency `USD`/null/trắng bị từ chối; `0m` và `74_844m VND` được giữ nguyên, không làm tròn.

- [ ] **Bước 2 - Chạy RED:**

```powershell
dotnet test tests/SwiftRide.TripService.Tests/SwiftRide.TripService.Tests.csproj --filter "FullyQualifiedName~TripCreationTests|FullyQualifiedName~FareSnapshotTests"
```

Sau khi hợp đồng mới biên dịch, kỳ vọng test thất bại vì validation/initial state chưa đúng; ghi lỗi thực, không nhận lỗi build là đã kiểm thử hành vi.

- [ ] **Bước 3 - Triển khai:** Factory có private constructor, state khởi đầu Requested, validate identity/k; value object immutable; state base có status và cơ chế lỗi mặc định. Chỉ thêm các property/handler cần cho task hiện tại. Không nhận `IConfiguration`/`DbContext` vào Domain.
- [ ] **Bước 4 - Chạy lại nhóm test trên và cả Trip test project.** Kỳ vọng tất cả pass. Test boundary của fare là kiểm tra invariant thật, không test getters cho đủ coverage.
- [ ] **Bước 5 - Checkpoint:** `git diff --check`, commit gợi ý `feat: introduce trip aggregate and immutable fare snapshot`; chỉ stage file task, chưa xóa placeholder cho tới khi các test nghiệp vụ mới đã xanh.

### TRIP-02: Báo giá, hủy, lời mời và driver rejection/rematch

**Files:** Bổ sung `Trip.cs`, `TripState.cs`, `RequestedState.cs`; tạo `QuotedState.cs`, `DriverOfferedState.cs`, `CancelledState.cs`, `FailedState.cs`, `TripQuoteTests.cs`, `DriverOfferTests.cs`; mở rộng `TripScenario.cs`.

**Interfaces:** Dùng factory/fare task trước. Thêm các command `ApplyQuote`, `FailNoDriverAvailable`, `ConfirmQuote`, `RejectQuote`, `Cancel`, `RejectDriver`, `OfferReplacementDriver` đúng chữ ký mục 3. `AcceptDriver` và `DriverAcceptedState` để TRIP-03 thực hiện.

- [ ] **Bước 1 - Viết test cho TR-01..05/TR-07..10:**

| Test | Assertion trọng tâm |
|---|---|
| `ApplyQuote_StoresSnapshotAndCandidate` | Requested -> Quoted; quote `222...`, amount `74844m`, currency `VND`; chưa assigned/locked |
| `ApplyQuote_RejectsMissingFareOrCandidate` | Fare null hoặc candidate trắng -> `InvalidFare`/`InvalidDriver`; vẫn Requested, không snapshot/driver mới |
| `NoDriverAvailable_FailsRequestedTrip` | Failed terminal; không driver assignment |
| `ConfirmQuote_LocksFareAndOffersSelectedDriver` | DriverOffered; đúng quote ID và rider; offered có thể khác candidate gốc, fare giữ nguyên |
| `ConfirmQuote_RejectsDifferentQuoteOrRider` | Exception `InvalidQuote`/`InvalidRider`; snapshot/state không đổi |
| `ConfirmQuote_RejectsBlankOfferedDriver` | `InvalidDriver`; vẫn Quoted, fare chưa locked, chưa có invitation |
| `RejectQuote_CancelsQuotedTrip` | Cancelled, không payment |
| `Cancel_IsAllowedOnlyInPreAcceptanceStates` | Requested/Quoted/DriverOffered đều Cancelled; xóa invitation khi có |
| `RejectDriver_AtLimitStillAllowsRematch` | Biên `k=0/1/3` theo ví dụ dưới; so sánh count sau tăng |
| `Rematch_PreservesFareAndRejectsPreviousDrivers` | Mời driver mới không đổi count/quote/amount/currency; driver đã bị loại ném `DriverAlreadyRejected` |
| `RejectDriver_RequiresCurrentOffer` | Sai driver hoặc request của driver cũ sau từ chối bị chặn, count không tăng |
| `NoCandidateAfterRejection_FailsTrip` | DriverOffered với offered null -> Failed |
| `ReplacementOffer_CannotOverwriteCurrentInvitation` | Không đổi offered driver hiện tại khi chưa có reject/rematch |
| `ReplacementOffer_RejectsBlankDriverWithoutMutation` | Khi đang rematch, driver null/trắng -> `InvalidDriver`, count/fare không đổi |

Ví dụ test biên mặc định cần chạy bằng command thật:

```csharp
[Fact]
public void FourthRejection_ExceedsDefaultLimit()
{
    var trip = TripScenario.Offered();
    for (var i = 1; i <= 3; i++)
    {
        trip.RejectDriver($"driver-{i}");
        Assert.Equal(TripStatus.DriverOffered, trip.Status);
        Assert.Equal(i, trip.RejectionCount);
        Assert.True(trip.IsRematchPending);
        trip.OfferReplacementDriver($"driver-{i + 1}");
    }

    trip.RejectDriver("driver-4");
    Assert.Equal(TripStatus.Failed, trip.Status);
    Assert.Equal(4, trip.RejectionCount);
}
```

Thêm case `k=0`: lần reject đầu -> Failed, và `k=1`: lần 1 vẫn rematch, lần 2 Failed. Invalid command phải giữ nguyên status, quote/fare, driver IDs, count và rejected IDs; không chỉ assert có exception.

- [ ] **Bước 2 - Chạy RED:**

```powershell
dotnet test tests/SwiftRide.TripService.Tests/SwiftRide.TripService.Tests.csproj --filter "FullyQualifiedName~TripQuoteTests|FullyQualifiedName~DriverOfferTests"
```

Kỳ vọng thiếu transition/guard thể hiện bằng assertion lỗi sau khi contract biên dịch. Không sửa expected `4` thành `3` để khớp implementation.

- [ ] **Bước 3 - Triển khai state:** Requested nhận quote/candidate hoặc fail/hủy. Quoted xác nhận đúng rider/quote rồi khóa fare và tạo invitation. DriverOffered xử lý reject, clear offered, lưu driver đã loại; nếu vượt k thì Failed, còn lại chờ replacement. Cancelled/Failed terminal, mọi command còn lại dùng default invalid handler.
- [ ] **Bước 4 - Ghi rõ rematch pending:** `IsRematchPending` đúng khi status DriverOffered và chưa có offered driver. `OfferReplacementDriver` chỉ nhận driver chưa bị loại, không có invitation đang hoạt động; `FailNoDriverAvailable` trong DriverOffered chỉ dùng lúc đang chờ rematch. Chưa gọi network hoặc claim driver DB.
- [ ] **Bước 5 - Chạy lại nhóm test mới và cả Trip test project.** Kỳ vọng mọi dòng transition thuộc task và fixture đều pass.
- [ ] **Bước 6 - Checkpoint:** commit gợi ý `feat: model trip quoting cancellation and driver rejection states`.

### TRIP-03: Driver nhận chuyến, thực hiện hành trình và domain event

**Files:** Bổ sung `Trip.cs`, `TripState.cs`, `DriverOfferedState.cs`; tạo `DriverAcceptedState.cs`, `EnRouteState.cs`, `PickedUpState.cs`, `DroppedOffState.cs`, `Events/IDomainEvent.cs`, `Events/RideCompleted.cs`, `TripProgressTests.cs`; bổ sung `DriverOfferTests.cs`, `TripScenario.cs`.

**Interfaces:** Dùng `TripScenario.Offered()` và các properties đã có. Thêm `AcceptDriver`, `StartEnRoute`, `Pickup`, `Dropoff` theo mục 3; thêm `DomainEvents` kiểu `IReadOnlyCollection<IDomainEvent>`. `RideCompleted` dùng đúng chữ ký mục 3.2. Chưa thêm integration event hoặc `IEventBus`.

- [ ] **Bước 1 - Viết test TR-06/TR-11..13:**

| Test | Assertion |
|---|---|
| `AcceptDriver_AssignsOnlyCurrentlyOfferedDriver` | DriverOffered -> DriverAccepted; assigned `driver-1`, invitation được xóa |
| `AcceptDriver_RejectsWrongDriverOrPendingRematch` | Sai driver -> `InvalidDriver`; chưa có replacement -> `RematchPending`; state/count/fare không đổi |
| `AssignedDriver_MustAdvanceInOrder` | Accept -> EnRoute -> PickedUp -> DroppedOff bằng ba command tương ứng |
| `Progress_RejectsAnotherDriver` | En-route/pickup/dropoff sai driver bị chặn trước mutation/event |
| `Cancel_AfterDriverAcceptance_IsInvalid` | DriverAccepted, EnRoute, PickedUp, DroppedOff đều không hủy |
| `Dropoff_EmitsRideCompletedWithAgreedFare` | Một event đúng trip/rider/driver/quote/`74844m`/VND/thời điểm UTC; status DroppedOff, chưa Paid |
| `Dropoff_RepeatedCommand_DoesNotAppendAnotherEvent` | Lần hai `InvalidTransition`; vẫn DroppedOff và chỉ một event |
| `Dropoff_NormalizesEventTimeToUtc` | Input `2026-10-19T07:00:00+07:00` -> event `2026-10-19T00:00:00+00:00` |

Ví dụ kiểm tra event độc lập với framework/broker:

```csharp
[Fact]
public void Dropoff_EmitsRideCompletedWithAgreedFare()
{
    var trip = TripScenario.PickedUp();
    trip.Dropoff("driver-1", TripScenario.DropoffUtc);

    Assert.Equal(TripStatus.DroppedOff, trip.Status);
    var completed = Assert.Single(trip.DomainEvents.OfType<RideCompleted>());
    Assert.Equal(TripScenario.TripId, completed.TripId);
    Assert.Equal("driver-1", completed.DriverId);
    Assert.Equal(TripScenario.QuoteId, completed.QuoteId);
    Assert.Equal(74_844m, completed.FareAmount);
    Assert.Equal("VND", completed.Currency);
    Assert.Equal(TripScenario.DropoffUtc, completed.OccurredAtUtc);
}
```

- [ ] **Bước 2 - Chạy RED:**

```powershell
dotnet test tests/SwiftRide.TripService.Tests/SwiftRide.TripService.Tests.csproj --filter "FullyQualifiedName~TripProgressTests|FullyQualifiedName~DriverOfferTests"
```

Kỳ vọng test chỉ ra thiếu accept/progress/event hoặc guard. Fixture `PickedUp()` phải đi từ Requested bằng public commands, không đặt thẳng state.

- [ ] **Bước 3 - Triển khai:** State DriverOffered chuyển ownership lời mời sang assigned driver khi nhận; mỗi state tiếp theo cho phép đúng một mốc tiến. Dropoff ghi một `RideCompleted` snapshot với UTC rồi chuyển DroppedOff; toàn bộ guard xảy ra trước mutation. Không tự RequestPayment trong Domain dropoff và không gọi mạng/publish event.
- [ ] **Bước 4 - Chạy lại nhóm test và cả Trip test project.** Kỳ vọng event chỉ sinh khi dropoff hợp lệ; không sự kiện mới khi transition bị từ chối. Test đúng actor là invariant nội bộ, chưa phải JWT integration test.
- [ ] **Bước 5 - Checkpoint:** commit gợi ý `feat: apply trip progression states and ride completed domain event`.

### TRIP-04: PaymentPending, deterministic key và kết quả settled

**Files:** Bổ sung `Trip.cs`, `TripState.cs`, `DroppedOffState.cs`; tạo `PaymentPendingState.cs`, `PaidState.cs`, `ValueObjects/PaymentSettlement.cs`, `PaymentFailureKind.cs`, `TripPaymentTests.cs`; mở rộng `TripScenario.cs`.

**Interfaces:** Dùng fare snapshot và event từ task trước. Thêm `RequestPayment`, `SettlePayment`, `RecordPaymentFailure` cùng nullable payment properties theo mục 3. `PaymentFailureKind` có đúng bốn giá trị được liệt kê ở bản đồ file; không thêm retry counter/worker trong Domain.

- [ ] **Bước 1 - Viết test TR-14..17 và mismatch:**

| Test | Assertion |
|---|---|
| `RequestPayment_UsesStableTripPaymentKey` | DroppedOff -> PaymentPending; key literal ở fixture; fare không đổi; pending không terminal |
| `PaymentFailure_RemainsPendingWithoutChangingKeyOrFare` | Cả 4 failure kind vẫn pending; đúng `LastPaymentFailure`; payment ID null; key/quote/amount/currency không đổi |
| `SettlePayment_MatchingResultMarksPaid` | Result đúng -> Paid terminal; lưu payment ID, xóa last failure hiện tại |
| `SettlePayment_RejectsMismatchedResultWithoutMutation` | Trip ID khác, amount `74845m`, currency `USD`, key khác -> `PaymentMismatch`; vẫn pending, PaymentId null |
| `PaymentSettlement_RejectsMalformedResult` | Empty payment/trip ID, amount âm/phần lẻ, currency/key trắng -> `InvalidPaymentResult` |
| `Settlement_AfterRetryExhaustion_CanStillReconcile` | Đã ghi RetryLimitReached vẫn pending; result settled đúng tới sau -> Paid |
| `Paid_RejectsRepeatedSettlementAtDomainBoundary` | Domain ném InvalidTransition, không đổi payment ID/event count; replay idempotent do Application xử lý sau |

Ví dụ kiểm tra key đúng một literal độc lập với cách implementation format:

```csharp
[Fact]
public void RequestPayment_UsesStableTripPaymentKey()
{
    var trip = TripScenario.PickedUp();
    trip.Dropoff("driver-1", TripScenario.DropoffUtc);
    trip.RequestPayment();

    Assert.Equal(TripStatus.PaymentPending, trip.Status);
    Assert.False(trip.IsTerminal);
    Assert.Equal(
        "trip-11111111-1111-1111-1111-111111111111-payment",
        trip.PaymentIdempotencyKey);
    Assert.Equal(74_844m, trip.Fare!.Amount);
}
```

Các case mismatch tạo `PaymentSettlement` mới với đúng một trường sai; không dùng `with` phá validation. Currency USD là kết quả có cấu trúc nhưng sai với fare VND, nên bị chặn tại `SettlePayment`, không bị che bởi constructor. Amount 0 VND phải đi được tới Paid với kết quả settled amount 0; không có thao tác charge thật ở test này.

- [ ] **Bước 2 - Chạy RED:**

```powershell
dotnet test tests/SwiftRide.TripService.Tests/SwiftRide.TripService.Tests.csproj --filter FullyQualifiedName~TripPaymentTests
```

Kỳ vọng lỗi phản ánh thiếu pending/settlement guards, không phải do thiếu Payment API hay DB.

- [ ] **Bước 3 - Triển khai:** DroppedOff tạo và giữ key theo `trip-{Id:D}-payment`; PaymentPending đối chiếu trip/fare/currency/key trước khi gán PaymentId và sang Paid. Failure chỉ ghi loại lỗi và giữ nguyên key/fare/state. Enum value không định nghĩa cũng bị domain exception `InvalidPaymentResult`.
- [ ] **Bước 4 - Chạy lại payment tests và cả Trip test project.** Kỳ vọng toàn bộ pass, gồm trường hợp lỗi lâu dài không làm Pending thành Failed/Cancelled/Paid. `DomainEvents` vẫn chỉ có RideCompleted trước đó; chưa thêm PaymentSettled integration event từ Trip.
- [ ] **Bước 5 - Checkpoint:** commit gợi ý `feat: guard trip payment pending and settlement transitions`.

### TRIP-05: Khóa ma trận invalid transitions và nghiệm thu Domain

**Files:** Tạo `InvalidTransitionTests.cs`; hoàn thiện `TripScenario.At(TripStatus status)`; xóa `UnitTest1.cs`; cập nhật phần hiện trạng `README.md`, `docs/architecture.md`. Chỉ sửa Domain nếu test bổ sung chỉ ra lỗi còn thiếu.

**Interfaces:** Dùng toàn bộ aggregate API của TRIP-01..04. `TripScenario.At` tạo trạng thái qua public commands, trả `Trip` có dữ liệu mẫu nhất quán; test không kiểm tra tên private state class hoặc số lần gọi virtual method.

- [ ] **Bước 1 - Viết theory ít nhất một invalid action mỗi state:**

| State ban đầu | Action phải bị từ chối |
|---|---|
| Requested | `Pickup("driver-1")` |
| Quoted | `AcceptDriver("driver-1")` |
| DriverOffered | `Pickup("driver-1")` |
| DriverAccepted | `Cancel("rider-1")` |
| EnRoute | `Dropoff("driver-1", fixtureUtc)` |
| PickedUp | `Cancel("rider-1")` |
| DroppedOff | `Dropoff("driver-1", fixtureUtc)` lần nữa |
| PaymentPending | `Cancel("rider-1")` |
| Paid | `RequestPayment()` |
| Cancelled | `ConfirmQuote("rider-1", fixtureQuoteId, "driver-1")` |
| Failed | `ApplyQuote(validFare, "driver-1")` |

Test `InvalidAction_PreservesTripData` phải kiểm tra `TripDomainException.Code == "InvalidTransition"`, status bằng state ban đầu, fare/driver IDs/rejection count/payment key/PaymentId/event count không đổi. Với collections, so sánh nội dung public snapshot trước/sau, không chỉ reference.

- [ ] **Bước 2 - Viết test terminal và actor invariant:** `OnlyPaidCancelledAndFailed_AreTerminal` chạy trên đủ 11 state, đặc biệt Pending sau RetryLimitReached vẫn false. `RiderCommands_RejectDifferentRiderWithoutMutation` kiểm tra confirm/reject-quote/cancel từ state hợp lệ; `DriverCommands_RejectBlankOrDifferentDriverWithoutMutation` kiểm tra accept/reject và ba mốc hành trình từ state phù hợp. Dùng trạng thái đúng của command để test actor, tránh default transition guard che lỗi actor.
- [ ] **Bước 3 - Chạy ma trận, sửa lỗ hổng nếu có rồi chạy full Trip suite:**

```powershell
dotnet test tests/SwiftRide.TripService.Tests/SwiftRide.TripService.Tests.csproj --filter FullyQualifiedName~InvalidTransitionTests
dotnet test tests/SwiftRide.TripService.Tests/SwiftRide.TripService.Tests.csproj
```

Test bổ sung có thể xanh ngay nếu implementation trước đã đúng. Không cần sửa code chỉ để test đỏ. Trước khi xóa placeholder phải có các test Domain mới pass.

- [ ] **Bước 4 - Đối chiếu coverage với spec:** Đánh dấu đủ TR-01..TR-17 trong mục 4, riêng RejectQuote/Cancel và OfferReplacementDriver đều có assertion. Kiểm tra bằng code review rằng Trip delegate sang state thật, state stateless, không setter công khai và Domain không có dependency hạ tầng. Không viết test đọc source text để chứng minh pattern.
- [ ] **Bước 5 - Xóa `UnitTest1.cs`, cập nhật hiện trạng docs:** Domain/state machine đã có; endpoint vẫn `501`, Application/DB/payment/idempotency thực tế chưa triển khai. Sửa câu README hiện nói cả Domain trống nhưng giữ thông tin các phần khác còn trống. Bổ sung link hai plan nếu chưa có; không đánh dấu toàn MVP hoàn thành.
- [ ] **Bước 6 - Chạy kiểm tra cuối:**

```powershell
dotnet build SwiftRide.sln
dotnet test SwiftRide.sln
git diff --check
```

Kỳ vọng build và toàn suite exit code `0`; ghi số test thật. Không cần Docker cho thay đổi Domain thuần; nếu Gateway plan đã thực thi, cả API test project mới phải được chạy trong solution suite này.

- [ ] **Bước 7 - Checkpoint:** commit gợi ý `test: enforce complete trip lifecycle transition matrix`; chỉ stage file task và hunk docs thuộc thay đổi này.

## 7. Definition of Done và ranh giới bàn giao

- [ ] Đủ 11 state class có hành vi, aggregate delegate đúng State Pattern.
- [ ] Mọi dòng TR-01..TR-17 và ít nhất một invalid action/state có test.
- [ ] Count biên k đúng; driver đã từ chối không được nhận lại lời mời của cùng trip.
- [ ] Fare snapshot bất biến sau confirm và giữ qua rematch/payment.
- [ ] Dropoff hợp lệ sinh một RideCompleted; command sai không mutation/event mới.
- [ ] Payment key deterministic; result mismatch không Paid; Pending không terminal khi hết retry.
- [ ] Không package/reference mới trong Domain, không network/DB/framework attribute.
- [ ] Build và suite được báo đúng kết quả thực; placeholder Trip test đã thay bằng test nghiệp vụ.
- [ ] Docs mô tả đúng phần đã triển khai, không nhầm Domain complete với API hay payment flow complete.

**Thứ tự:** TRIP-01 -> TRIP-02 -> TRIP-03 -> TRIP-04 -> TRIP-05. Các task cùng sửa context/state base nên thực hiện tuần tự, không chia implementer ghi đồng thời cùng file.

**Việc tiếp theo sau hai plan:** triển khai Trip Application/persistence/command receipts và service resource authorization; sau đó Matching/Pricing và Payment integration theo roadmap tổng thể. Không cần làm các phần đó để nghiệm thu hai yêu cầu hiện tại.

**Lúc lập plan:** chưa tạo các file Domain/test kể trên, chưa chạy build/test nghiệp vụ và chưa thay đổi stub. Các checkbox dành cho lần thực thi kế hoạch sau khi được yêu cầu.
