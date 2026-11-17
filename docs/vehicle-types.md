# Xe 2 bánh và xe 4 bánh

## Thiết kế để trình bày

`vehicleType` có hai giá trị: `motorbike` (xe 2 bánh) và `car` (xe 4 bánh).
Request/dữ liệu cũ mặc định `motorbike`. `mode` vẫn độc lập: `standard` hoặc `night`.

```text
Loại xe → chọn đơn giá km/phút từ cấu hình
        → StandardPricingStrategy hoặc NightPricingStrategy
        → BaseFare → Surge → Toll → Promotion → Tax → làm tròn
```

Không thêm strategy theo từng tổ hợp xe/ban đêm vì công thức giống nhau, chỉ khác
đơn giá. `NightPricingStrategy.PrepareContext` vẫn điều chỉnh đơn giá hiệu lực;
quote lưu snapshot đơn giá này. Các Decorator tiếp tục dùng chung cho cả hai loại.

Trip lưu loại xe và truyền nó khi lấy quote, tìm tài xế, tìm lại và phục hồi công
việc qua REST hoặc RabbitMQ. Matching chỉ chọn tài xế online, vị trí còn mới,
trong bán kính và đúng loại xe. Không có xe phù hợp thì trả `NoDriverAvailable`,
không tự đổi loại. Payment vẫn thu đúng fare snapshot, không tính lại giá.

## API và cấu hình

Thêm `"vehicleType": "car"` vào body tạo chuyến, cùng với tọa độ và phương thức
thanh toán như trước. Chọn ban đêm bằng `"mode": "night"`. Response Trip trả
`vehicleType` để giao diện giữ đúng lựa chọn sau khi tải lại.

Tài xế gửi loại xe trong `PUT /drivers/{id}/location` qua Gateway:

```json
{
  "latitude": 10.772,
  "longitude": 106.698,
  "isOnline": true,
  "vehicleType": "car"
}
```

`POST /quote`, `POST /match` nội bộ và hai request event
`QuoteRequestedV1`/`DriverMatchRequestedV1` cũng mang `vehicleType`.
Đổi loại xe với cùng khóa tạo chuyến là payload khác và phải bị từ chối bằng
conflict. Muốn chọn lại loại xe, tạo yêu cầu mới đúng quy trình hủy/đặt chuyến.

Matching giữ `Matching:CostPerKm`/`CostPerMinute` cho xe 2 bánh; xe 4 bánh dùng
`Matching:CarCostPerKm`/`CarCostPerMinute`. Đơn giá mặc định chỉ phục vụ demo,
không phải biểu giá thực tế. Mọi thay đổi đơn giá chỉ tác động quote mới.

## Cập nhật database và demo

1. Trip có EF migration bổ sung cột `VehicleType`, mặc định `motorbike` cho dữ
   liệu cũ. Cơ chế migrate khi startup vẫn theo cấu hình hiện có.
2. Matching không tự migrate Supabase. Với database đã tồn tại, chạy
   `database/supabase/matchdb-vehicle-types.sql` bằng tài khoản quản trị trước khi
   chạy bản service mới. Script thêm loại xe vào driver/quote, giữ dữ liệu cũ.
   Database mới dùng DDL `database/supabase/matchdb-paydb.sql` đã cập nhật.
3. Không cần thay đổi schema Payment. Không chạy script trên database dùng chung
   nếu chưa kiểm tra đúng môi trường.
4. Demo: chọn loại xe phía tài xế, bật online để cập nhật vị trí; rider chọn cùng
   loại xe rồi đặt chuyến. Lặp lại với mode ban đêm. Có thể chủ động chạy
   `database/supabase/matchdb-demo-drivers.sql` trên database demo để thêm tài xế
   `demo-driver-car` và tài xế xe 2 bánh. Đây là script seed thủ công, không tự
   chạy khi service khởi động; vị trí seed vẫn hết hạn như trước.

Giao diện tài xế demo hiện đăng nhập bằng `demo-driver`. Khi demo hoàn chỉnh bằng
UI, chọn loại xe cho tài khoản này và cập nhật vị trí qua nút online. Nếu dùng
driver từ script seed qua Postman, JWT phải có `sub` đúng ID driver được ghép.

## Kiểm tra

```powershell
dotnet test tests/SwiftRide.MatchingService.Tests/SwiftRide.MatchingService.Tests.csproj
dotnet test tests/SwiftRide.TripService.Tests/SwiftRide.TripService.Tests.csproj
dotnet test SwiftRide.sln
dotnet build SwiftRide.sln
```

Frontend: chạy `npm test` và `npm run build` trong `src/WebApp`.
Test cần bảo vệ giá theo loại xe + mode, lọc đúng tài xế, truyền loại xe qua
REST/event/retry, fingerprint idempotency và lưu/khôi phục lựa chọn giao diện.

Trong phiên triển khai này, môi trường không tìm thấy .NET SDK, Node/npm và
Docker nên chưa chạy được build/test hay nghiệm thu database thật. Đã review
code và thêm test; cần thực thi các lệnh trên ở máy có đủ công cụ.
