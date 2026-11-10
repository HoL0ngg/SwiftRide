# Tài liệu Integration Events Contracts V1 & RabbitMQ Topology

> **Cập nhật ngày:** 30/10/2026  
> **Nhiệm vụ:** Task 8 thuộc Mốc B (Kiến trúc mở rộng hướng sự kiện)  
> **Tuân thủ:** [AGENTS.md](../AGENTS.md) mục 6 (Choreography, integration events trong `src/SwiftRide.Contracts/V1/`)

---

## 1. Phiên bản Thư viện & Giấy phép (Package & License)

- **Thư viện Message Bus:** `MassTransit` & `MassTransit.RabbitMQ`
- **Phiên bản được ghim (Pinned Version):** `8.5.8`
- **Nguồn:** [NuGet.org / MassTransit](https://www.nuget.org/packages/MassTransit/)
- **Giấy phép (License):** Apache License 2.0 (phù hợp cho dự án học thuật và thương mại, không có hạn chế copyleft).
- **Kiểm định an toàn (Security Audit):**
  - Chạy lệnh `dotnet list package --vulnerable --include-transitive`.
  - Kết quả: **0 lỗ hổng bảo mật (0 CVE, 0 advisory)** trên toàn bộ 18 projects trong giải pháp SwiftRide.

---

## 2. Danh sách Integration Event Contracts V1

Toàn bộ các contract nằm tại namespace `SwiftRide.Contracts.V1`, là các `record` thuần túy (plain records), không chứa domain types, không phụ thuộc EF Core hay database entities.

### 2.1 `TripAcceptedV1`
Phát ra khi tài xế chấp nhận chuyến xe:
- `Guid EventId`: Mã định danh duy nhất của sự kiện.
- `Guid TripId`: Mã chuyến đi.
- `string RiderId`: Mã hành khách.
- `string DriverId`: Mã tài xế nhận chuyến.
- `DateTimeOffset OccurredAtUtc`: Thời điểm xảy ra sự kiện (UTC).
- `string CorrelationId`: Mã theo dõi phân tán xuyên suốt luồng.
- `Guid? CausationId`: Mã nguyên nhân dẫn tới sự kiện (tùy chọn).

### 2.2 `RideCompletedV1`
Phát ra khi tài xế hoàn thành chuyến xe (`DroppedOff`):
- `Guid EventId`
- `Guid TripId`
- `string RiderId`, `string DriverId`
- `Guid QuoteId`: Snapshot báo giá đã thỏa thuận.
- `decimal FareAmount`: Giá cước thỏa thuận (VND).
- `string FareCurrency`: Đơn vị tiền tệ ("VND").
- `string PaymentMethod`: Phương thức thanh toán (ví dụ: "wallet", "card").
- `string MockTokenAlias`: Token thanh toán mô phỏng.
- `string PaymentIdempotencyKey`: Khóa chống thu trùng (`trip-{tripId}-payment`).
- `DateTimeOffset OccurredAtUtc`
- `string CorrelationId`, `Guid? CausationId`

### 2.3 `PaymentSettledV1`
Phát ra từ PaymentService khi thanh toán đã được quyết toán thành công và ghi sổ cái kế toán kép cân bằng:
- `Guid EventId`
- `Guid PaymentId`: Mã giao dịch thanh toán trong `paydb.payments`.
- `Guid TripId`
- `string RiderId`, `string DriverId`
- `decimal Amount`, `string Currency`
- `string PaymentKey`: Khóa idempotency đã xử lý.
- `DateTimeOffset OccurredAtUtc`
- `string CorrelationId`, `Guid? CausationId`

### 2.4 `PaymentFailedV1`
Phát ra từ PaymentService khi thanh toán thất bại dứt điểm (ví dụ: không đủ tiền hoặc vượt quá retry budget):
- `Guid EventId`
- `Guid? PaymentId`: Mã giao dịch (nếu đã tạo record).
- `Guid TripId`
- `string PaymentKey`
- `string FailureCode`: Mã lỗi an toàn (ví dụ: `insufficient_funds`, `invalid_token`).
- `bool IsPermanent`: Xác định lỗi vĩnh viễn (true) hay đã cạn kiệt số lần thử (false).
- `string SafeErrorMessage`: Thông điệp lỗi an toàn (không lộ bí mật).
- `DateTimeOffset OccurredAtUtc`
- `string CorrelationId`, `Guid? CausationId`

### 2.5 `PaymentCompensationRequestedV1`
Phát ra khi cần hoàn tiền/bù trừ (ví dụ: chuyến đi bị hủy sau khi đã trừ tiền):
- `Guid EventId`
- `Guid TripId`, `Guid PaymentId`
- `string PaymentKey`
- `decimal Amount`, `string Currency`
- `string ReasonCode`: Lý do bồi hoàn (ví dụ: `trip_cancelled_after_charge`).
- `DateTimeOffset OccurredAtUtc`
- `string CorrelationId`, `Guid? CausationId`

### 2.6 `PaymentRefundedV1`
Phát ra khi hoàn tiền thành công:
- `Guid EventId`
- `Guid RefundId`: Mã giao dịch hoàn tiền trong `paydb.refunds`.
- `Guid PaymentId`, `Guid TripId`
- `decimal Amount`, `string Currency`
- `DateTimeOffset OccurredAtUtc`
- `string CorrelationId`, `Guid? CausationId`

### 2.7 `PaymentCompensationFailedV1`
Phát ra khi việc bồi hoàn gặp lỗi không thể tự động xử lý:
- `Guid EventId`
- `Guid? RefundId`
- `Guid PaymentId`, `Guid TripId`
- `string ReasonCode`
- `string SafeErrorMessage`
- `DateTimeOffset OccurredAtUtc`
- `string CorrelationId`, `Guid? CausationId`

### 2.8 Nhóm event Trip ↔ Matching (request/reply bất đồng bộ)

Khi `TripLifecycle:MatchingTransport = "Events"`, Trip không gọi `/match` và `/quote` bằng REST nữa mà trao đổi với Matching bằng 6 event dưới đây (xem [luồng Trip ↔ Matching qua event](messaging-operations.md#35-luồng-trip--matching-qua-event-matchingtransport--events)).
Mọi event đều mang `EventId`, `TripId`, `OccurredAtUtc`, `string CorrelationId`, `Guid? CausationId` (với reply, `CausationId` = `EventId` của request).

| Event | Chiều | Trường nghiệp vụ |
|:---|:---|:---|
| `QuoteRequestedV1` | Trip → Matching | `PickupLatitude/Longitude`, `DestinationLatitude/Longitude`, `Mode` |
| `QuoteReadyV1` | Matching → Trip | `QuoteId`, `Amount`, `Currency`, `DistanceKm`, `EstimatedMinutes`, `CandidateDriverId`, `CandidateDistanceKm` |
| `QuoteFailedV1` | Matching → Trip | `ReasonCode`: `NoDriverAvailable` hoặc `InvalidRequest` |
| `DriverMatchRequestedV1` | Trip → Matching | `PickupLatitude/Longitude`, `ExcludedDriverIds` (tối đa 100) |
| `DriverMatchedV1` | Matching → Trip | `DriverId`, `DistanceKm`, `ExcludedDriverIds` (Matching trả lại nguyên danh sách của request) |
| `DriverMatchFailedV1` | Matching → Trip | `ReasonCode`: `NoDriverAvailable` hoặc `InvalidRequest` |

Ghi chú thiết kế:
- `QuoteReadyV1.QuoteId` chính là `EventId` của `QuoteRequestedV1`, nên request được giao lại vẫn ra cùng một quote (idempotent).
- `DriverMatchedV1.ExcludedDriverIds` giúp Trip nối dài danh sách loại trừ khi tài xế được chọn đã bận, tránh chọn lại đúng người đó.
- Contract không chứa entity của Trip hay Matching, chỉ chứa dữ liệu thuần.

---

## 3. RabbitMQ Topology: Exchanges, Queues và Bindings

Topology định sẵn tại `infra/rabbitmq/definitions.json`; healthcheck Compose import sau khi broker tạo user và trước khi đánh dấu healthy. Tên exchange khớp topology mặc định của MassTransit để giữ message khi consumer chưa chạy.

| Exchange Name (Topic/Fanout Durable) | Queue Name (Durable) | Mục đích / Consumer |
|:---|:---|:---|
| `SwiftRide.Contracts.V1:RideCompletedV1` | `payment-ride-completed` | PaymentService nhận lệnh thu tiền tự động |
| `SwiftRide.Contracts.V1:PaymentSettledV1` | `trip-payment-settled` | TripService cập nhật chuyến đi sang `Paid` |
| `SwiftRide.Contracts.V1:PaymentFailedV1` | `trip-payment-failed` | TripService ghi nhận lỗi thanh toán |
| `SwiftRide.Contracts.V1:PaymentCompensationRequestedV1` | `payment-compensation` | PaymentService thực hiện hoàn tiền (`Refund`) |
| `SwiftRide.Contracts.V1:PaymentRefundedV1` | `trip-payment-refunded` | TripService xác nhận hoàn tiền thành công |
| `SwiftRide.Contracts.V1:PaymentCompensationFailedV1` | `trip-payment-compensation-failed` | TripService cảnh báo bồi hoàn thất bại |
| `SwiftRide.Contracts.V1:QuoteRequestedV1` | `matching-quote-requested` | MatchingService tìm tài xế + tạo báo giá cho chuyến mới |
| `SwiftRide.Contracts.V1:DriverMatchRequestedV1` | `matching-driver-match-requested` | MatchingService tìm tài xế kế tiếp (sau confirm hoặc khi tài xế từ chối) |
| `SwiftRide.Contracts.V1:QuoteReadyV1` | `trip-quote-ready` | TripService chuyển `Requested` → `Quoted` |
| `SwiftRide.Contracts.V1:QuoteFailedV1` | `trip-quote-failed` | TripService chuyển `Requested` → `Failed` |
| `SwiftRide.Contracts.V1:DriverMatchedV1` | `trip-driver-matched` | TripService giữ chỗ tài xế và mở lời mời (`DriverOffered`) |
| `SwiftRide.Contracts.V1:DriverMatchFailedV1` | `trip-driver-match-failed` | TripService chuyển chuyến đang chờ rematch sang `Failed` |
| `SwiftRide.Contracts.V1:TripAcceptedV1` | *(Tùy chọn consumer)* | Broadcast sự kiện tài xế nhận chuyến |

---

## 4. Kiểm thử Tự động

File kiểm thử `tests/SwiftRide.Api.Tests/Messaging/ContractSerializationTests.cs` đã kiểm chứng (round-trip của 7 event ban đầu; riêng 6 event Matching mới chỉ được thêm vào kiểm tra tên exchange/queue/binding, chưa có test round-trip riêng):
1. Serialization & Deserialization round-trip 7 events thanh toán/chuyến đi.
2. Giữ trọn độ chính xác của `Guid`, `DateTimeOffset` UTC, `decimal` tiền tệ VND.
3. Không làm rò rỉ các domain types hay persistence entity models vào contracts.
4. Tên exchange và queue khớp chính xác 100% với file topology.
