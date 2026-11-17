# Matching & Pricing: chạy và kiểm thử

Service có bốn lớp: Domain (tọa độ, Strategy, Decorator tính giá), Application (use case,
interface persistence/geocoding), Infrastructure (EF Core/Npgsql và Nominatim), Api
(DTO, JWT, endpoint và composition root). Domain không phụ thuộc framework.

## Strategy và Decorator tính giá

`IPricingStrategy` chọn cách tính giá cơ bản (`standard` hoặc `night`).
`BaseFare` chuyển kết quả của strategy sang hợp đồng `IFareComponent` và từ chối
giá cơ bản âm. Bốn decorator cùng triển khai `IFareComponent`, mỗi lớp nhận một
component bên trong, gọi nó rồi áp dụng một phép tính:

| Component | Phép tính |
|---|---|
| `BaseFare` | `strategy.CalculateBase(context)` |
| `SurgeDecorator` | Giá bên trong × surge |
| `TollDecorator` | Giá bên trong + toll |
| `PromotionDecorator` | Giá bên trong × promotionFactor |
| `TaxDecorator` | Giá bên trong × (1 + tax) |

Các lớp nằm trong `src/MatchingService/Domain/FareComponents.cs`.
`FareCalculator` ghép chuỗi bằng cách bọc lần lượt:

```csharp
IFareComponent fare = new BaseFare(strategy);
fare = new SurgeDecorator(fare);
fare = new TollDecorator(fare);
fare = new PromotionDecorator(fare);
fare = new TaxDecorator(fare);
```

Tax nằm ngoài cùng nhưng gọi component bên trong trước, nên thứ tự tính vẫn là
**base → surge → toll → promotion → tax**. Decorator không làm tròn.
`FareCalculator.Calculate` là điểm vào: kiểm tra context và rounding policy,
chạy chuỗi, làm tròn một lần thành VND và kiểm tra giới hạn số tiền; overflow
được chuyển thành `MatchingDomainException`.

Ví dụ base 60.000, surge 1,2, toll 5.000, promotion 0,9 và tax 0,08 cho kết quả
74.844 VND. Đổi thứ tự toll và surge sẽ đổi giá nên không cấu hình thứ tự tùy ý.
Thiết kế này thay pipeline `IFareStep` cũ, giữ nguyên API, quote snapshot,
`PrepareContext` và schema database; không cần migration hay thư viện mới.

## API hiện có

Loại xe `motorbike`/`car` độc lập với `mode`. Đơn giá, lọc tài xế và cập nhật
schema được mô tả trong [hướng dẫn loại xe](vehicle-types.md).

### Giả lập giá ban đêm

`mode` hỗ trợ `standard` và `night`. Giao diện Rider có nút **Giả lập ban đêm** trước
khi tạo chuyến; nút đổi giao diện tối và gửi `mode: "night"` qua Gateway. Đây là lựa
chọn kịch bản demo, không tự xác định giờ thực tế. Sau khi có chuyến, giao diện dùng
`pricingMode` do Trip trả về, kể cả khi tải lại trang; muốn đổi mode phải chọn lại
hành trình và lấy báo giá mới.

`NightPricingStrategy` triển khai `IPricingStrategy`. `CreateQuote` chọn strategy,
gọi `PrepareContext` một lần để lấy đơn giá hiệu lực rồi gọi `FareCalculator`.
Strategy thường giữ nguyên context; strategy ban đêm nhân đơn giá km và phút với
`Matching:NightMultiplier` (mặc định `1.2`, phải ≥ 1). Sau đó dùng lại phép tính base
hiện tại, không sửa `StandardPricingStrategy`. Snapshot lưu đơn giá đã điều chỉnh,
nên công thức từ snapshot vẫn tái tạo đúng giá. Không cần migration.

Thứ tự: đơn giá ban đêm → base → surge → toll → promotion → tax → làm tròn một lần.
Ví dụ base thường 50.000đ, ban đêm ×1,2 thành 60.000đ khi các bước còn lại trung tính.
Hệ số này là ví dụ môn học, không phải biểu giá thực tế. Đổi cấu hình bằng
`Matching__NightMultiplier`; thay đổi chỉ tác động báo giá mới. Trip đã chốt giá và
Payment tiếp tục sử dụng fare snapshot.

Trời mưa chưa có nút giả lập hay dữ liệu thời tiết trong bản này. Khi bổ sung, có
thể dùng hệ số mưa ở bước surge, độc lập với mode ban đêm: ×1,2 (đêm) ×1,3 (mưa)
= ×1,56 trên phần base. `Matching:Surge` hiện là cấu hình chung, không phải cờ mưa
theo từng chuyến. Nếu cần bật mưa cho từng yêu cầu, phải truyền điều kiện qua cả
REST/event và lưu để phục hồi; không tạo các mode tổ hợp như `night-rain`.


| Endpoint | Quyền | Đầu vào | Kết quả |
|---|---|---|---|
| `PUT /drivers/{id}/location` | Gateway + JWT driver, `sub == id` | `latitude`, `longitude`, `isOnline` | `204`, upsert và timestamp server |
| `GET /locations/search?query=...` | Gateway + JWT rider/driver | 3–200 ký tự, nhấn Tìm | Danh sách `id`, `displayName`, `latitude`, `longitude` |
| `POST /match` | Nội bộ, không public qua Gateway | `latitude`, `longitude`, `excludedDriverIds` tùy chọn | `status: Matched/NoDriverAvailable`, `candidate` hoặc null |
| `POST /quote` | Nội bộ, không public qua Gateway | `pickup`, `destination` có `latitude`/`longitude`; `mode` mặc định `standard` | `201`: quote ID, giá, VND, khoảng cách/thời gian, breakdown, timestamp |

Ví dụ body `/quote`:

```json
{
  "pickup": { "latitude": 10.772, "longitude": 106.698 },
  "destination": { "latitude": 10.783, "longitude": 106.705 },
  "mode": "standard"
}
```

Không nhận giá hoặc hệ số thuế/khuyến mãi do client truyền. Khoảng cách là Haversine
đường chim bay; thời gian = khoảng cách / vận tốc cấu hình × 60. Giá dùng decimal,
theo thứ tự base → surge → toll → promotion → tax, chỉ làm tròn một lần thành VND.
Giá trị trong appsettings là ví dụ phục vụ đồ án, không phải giá/thuế thực tế.

Matching lọc tài xế online, vị trí trong 5 phút, trong bán kính 5 km, không thuộc danh sách
loại; sắp khoảng cách rồi DriverId. Các giới hạn chỉnh trong section `Matching`.
Matching trả ứng viên, chưa giữ chỗ tài xế. DB scan phù hợp quy mô demo; PostGIS là mở rộng.

## Supabase và cấu hình

Schema đã có: `matchdb.driver_locations`, `matchdb.quotes`. Map đúng tên bảng/cột;
service không chạy migration/EnsureCreated. Vị trí upsert bằng `ON CONFLICT`, bản ghi cũ
không ghi đè timestamp mới. Quote lưu toàn bộ đầu vào và kết quả; không retry ghi ngầm.

Đặt `ConnectionStrings__MatchDB` qua environment hoặc user-secrets; ví dụ placeholder
trong `.env.example`. Cần tài khoản login riêng có membership `swiftride_matching`,
không dùng postgres quản trị. Bắt buộc `SSL Mode=VerifyFull`, timeout kết nối 1–30 giây,
command timeout 1–60 giây. Không gửi mật khẩu vào chat hoặc commit secrets.
JWT issuer/audience/signing key phải khớp Gateway (`Jwt__Issuer`, `Jwt__Audience`,
`Jwt__SigningKey`; Compose lấy từ các biến JWT hiện có).

Seed `Matching__SeedDemoDrivers=true` chỉ được bật trong Development và do người chạy
chủ động lựa chọn. Hai ID cố định `demo-driver`, `demo-driver-2`, không nhân đôi khi chạy lại;
seed một lần lúc startup, vị trí vẫn hết hạn theo quy tắc 5 phút. Gửi PUT để cập nhật tiếp.

Compose đã bỏ container Matching database cũ, nhận connection string Supabase từ env.
Không xóa volume dữ liệu cũ trên máy. Chỉ chạy một instance Matching khi dùng Nominatim public.

## Tìm địa chỉ miễn phí

Người dùng đã chọn Nominatim public cho demo. Gọi qua Matching/Gateway, không gọi theo
từng ký tự. Giới hạn một request upstream mỗi giây cho cả process, cache 256 truy vấn
trong 24 giờ, timeout mỗi request 3 giây/tổng 12 giây. Retry tối đa hai lần cho lỗi tạm thời;
429 áp dụng cooldown, không retry dồn dập. Lỗi upstream trả `503`; không fallback địa chỉ giả.
URL và User-Agent nhận diện nằm ở `Geocoding:SearchUrl`, `Geocoding:UserAgent`.
Không ghi truy vấn địa chỉ vào HttpClient/YARP forwarder logs.

Tuân thủ [chính sách Nominatim](https://operations.osmfoundation.org/policies/nominatim/):
không autocomplete/bulk query, chỉ dùng địa danh công khai cho demo, không gửi dữ liệu
riêng tư; có attribution, không bảo đảm uptime, đổi provider trước khi mở rộng nhiều instance.
[Bản đồ OSM](https://operations.osmfoundation.org/policies/tiles/) giữ attribution và cache
trình duyệt, không prefetch/tải offline. Giới hạn tốc độ trong bộ nhớ không dùng cho hệ thống
nhiều replica hoặc nhiều bản triển khai cùng chia sẻ hạn mức.

## Giao diện và giới hạn tích hợp

Frontend nhập địa chỉ, nhấn Tìm, chọn kết quả và có thể chỉnh điểm bằng click bản đồ.
Xem [hướng dẫn frontend](frontend-demo.md). Chỉ tìm địa chỉ đang nối backend thật;
Trip, giá trên giao diện và Payment vẫn được ghi rõ là demo. Backend `/quote` đã triển khai
để Trip gọi khi bổ sung use case Trip; không mở thêm route public báo giá trái kiến trúc.
Hai endpoint nội bộ dựa vào network nội bộ của MVP; không đưa port service lên Internet.

## Kiểm thử

```powershell
dotnet test tests/SwiftRide.MatchingService.Tests/SwiftRide.MatchingService.Tests.csproj
dotnet test tests/SwiftRide.Api.Tests/SwiftRide.Api.Tests.csproj
dotnet build SwiftRide.sln
dotnet test SwiftRide.sln
docker compose config --quiet
```

Test repository thật chỉ bật khi đặt `MATCHING_TEST_DB` trỏ PostgreSQL disposable trên
localhost, database `matching_test`. Test tạo schema từ đoạn DDL gốc rồi rollback;
không trỏ biến này tới Supabase. Không có biến thì ca integration báo skipped rõ ràng.
Test geocoding mặc định dùng HTTP handler giả lập, không gọi Nominatim thật.
