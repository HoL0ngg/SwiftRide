# Matching & Pricing: chạy và kiểm thử

Service có bốn lớp: Domain (tọa độ, Strategy, pipeline giá), Application (use case,
interface persistence/geocoding), Infrastructure (EF Core/Npgsql và Nominatim), Api
(DTO, JWT, endpoint và composition root). Domain không phụ thuộc framework.

## API hiện có

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
