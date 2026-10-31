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

---

## 3. RabbitMQ Topology: Exchanges, Queues và Bindings

Cấu hình bootstrap định sẵn tại `infra/rabbitmq/definitions.json` và `infra/rabbitmq/rabbitmq.conf`.

| Exchange Name (Topic/Fanout Durable) | Queue Name (Durable) | Mục đích / Consumer |
|:---|:---|:---|
| `swiftride.v1.ride-completed` | `payment-ride-completed` | PaymentService nhận lệnh thu tiền tự động |
| `swiftride.v1.payment-settled` | `trip-payment-settled` | TripService cập nhật chuyến đi sang `Paid` |
| `swiftride.v1.payment-failed` | `trip-payment-failed` | TripService ghi nhận lỗi thanh toán |
| `swiftride.v1.payment-compensation-requested` | `payment-compensation` | PaymentService thực hiện hoàn tiền (`Refund`) |
| `swiftride.v1.payment-refunded` | `trip-payment-refunded` | TripService xác nhận hoàn tiền thành công |
| `swiftride.v1.payment-compensation-failed` | `trip-payment-compensation-failed` | TripService cảnh báo bồi hoàn thất bại |
| `swiftride.v1.trip-accepted` | *(Tùy chọn consumer)* | Broadcast sự kiện tài xế nhận chuyến |

---

## 4. Kiểm thử Tự động

File kiểm thử `tests/SwiftRide.Api.Tests/Messaging/ContractSerializationTests.cs` (6 tests) đã kiểm chứng:
1. Serialization & Deserialization round-trip đầy đủ 7 events.
2. Giữ trọn độ chính xác của `Guid`, `DateTimeOffset` UTC, `decimal` tiền tệ VND.
3. Không làm rò rỉ các domain types hay persistence entity models vào contracts.
4. Tên exchange và queue khớp chính xác 100% với file topology.
