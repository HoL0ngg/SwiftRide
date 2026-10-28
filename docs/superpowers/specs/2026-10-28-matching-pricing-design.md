# Matching & Pricing MVP với Clean Architecture

> Điều chỉnh đã được người dùng duyệt: quote nhận tọa độ đón/đến, Matching dùng Haversine và vận tốc cấu hình; thêm tìm địa chỉ Nominatim qua nút Tìm và Gateway. Thiết kế gốc dưới đây giữ để đối chiếu; hợp đồng triển khai tại [hướng dẫn Matching](../../matching-pricing.md).

## Mục tiêu và quyết định đã chốt

Triển khai ba endpoint hiện có của Matching Service trên .NET 10, giữ bốn project
Domain, Application, Infrastructure, Api. Database là PostgreSQL trên Supabase,
chỉ truy cập schema `matchdb` đã tồn tại. Không tạo lại schema hoặc sửa Payment/TripDB.

Người dùng đã chọn MVP: Trip gửi khoảng cách và thời gian dự kiến; Matching tìm
tài xế bằng khoảng cách đường chim bay, không gọi API bản đồ. Đây là thay đổi so với
contract `/quote` nhận tọa độ trong `docs/architecture.md`; cần cập nhật tài liệu đó.
Chưa triển khai luồng Trip gọi Matching trong lần này vì Trip Application còn trống.

## Hợp đồng HTTP

- `PUT /drivers/{id}/location`: body `latitude`, `longitude`, `isOnline`.
  JWT hợp lệ, role `driver`, claim `sub` bằng route `id`. Thành công `204`;
  chưa đăng nhập `401`, sai quyền `403`, dữ liệu sai `400`.
  Timestamp do server đặt. Upsert theo driver ID, không tạo bản ghi trùng.
- `POST /match`: body `latitude`, `longitude`, `excludedDriverIds` (mặc định rỗng).
  Trả `200` với `status` (`Matched` hoặc `NoDriverAvailable`) và `candidate`
  (null hoặc `driverId`, `distanceKm`). Không có tài xế là kết quả nghiệp vụ bình thường.
- `POST /quote`: body `distanceKm`, `estimatedMinutes`, `mode` (mặc định `standard`).
  Trả `201` với `quoteId`, `amount`, `currency`, khoảng cách/thời gian, breakdown
  và thời điểm tạo. Chỉ trả thành công sau khi lưu quote. Mode không hỗ trợ hoặc
  đầu vào sai trả `400`. Client không được cung cấp giá hay hệ số pricing.

Hai POST là nội bộ, không mở route public tại YARP. MVP dùng ranh giới network
nội bộ như kiến trúc hiện tại; không publish port Matching trong Compose.
PUT public tiếp tục đi qua Gateway; Matching tự kiểm tra JWT và chủ sở hữu.
Khoảng cách/thời gian do Trip cung cấp phải là ước lượng phía backend đáng tin cậy,
không chuyển nguyên giá trị do rider tùy ý nhập sang tính tiền.
Lỗi dùng Problem Details, không lộ connection string hoặc chi tiết database.

## Phân lớp

**Domain:** tọa độ hợp lệ, PricingContext, quote snapshot, domain exception,
`IPricingStrategy`, `StandardPricingStrategy`, các bước surge/toll/promotion/tax
và FareCalculator. Domain không tham chiếu EF Core, ASP.NET hoặc configuration.
Giá dùng decimal, áp dụng đúng thứ tự base → surge → toll → promotion → tax,
làm tròn đúng một lần ở cuối theo policy cấu hình, VND nguyên đồng.
Kiểm tra các invariant hiện hành và giới hạn amount của schema (nhỏ hơn 10^18).

**Application:** ba use case UpdateDriverLocation, MatchDriver, CreateQuote;
DTO riêng và các interface đọc/ghi nhỏ cho vị trí và quote. Không dùng entity Trip.
MatchDriver lọc online, vị trí còn mới, không bị loại, trong bán kính; tính khoảng cách
Haversine rồi sắp theo khoảng cách và DriverId để phá hòa ổn định.
Dùng TimeProvider để kiểm thử timestamp và độ cũ của vị trí.

**Infrastructure:** thay MongoDB.Driver bằng Npgsql/EF Core phiên bản theo repo;
map rõ tên bảng/cột của `matchdb.driver_locations` và `matchdb.quotes`.
Upsert vị trí nguyên tử theo khóa chính; quote lưu toàn bộ snapshot đầu vào và giá.
Không chạy EnsureCreated/Migrate trên Supabase schema đã có.
Connection string từ environment/user-secrets, TLS và timeout kết nối/lệnh;
truyền CancellationToken xuyên các lớp. Không tự retry ghi quote khi kết quả commit
chưa rõ, tránh tạo nhiều quote do retry ngầm.

**Api:** bind request, xác thực/kiểm tra quyền, gọi use case, map response/lỗi.
DI nằm ở composition root; không tính giá hay tìm tài xế trong endpoint.

## Cấu hình và dữ liệu demo

Các lựa chọn triển khai đề xuất: chỉ một mode `standard` ban đầu; rate, surge, toll,
promotion, tax, rounding, bán kính và tuổi tối đa của vị trí đều lấy từ configuration
và validate khi startup. Thêm mode bằng strategy mới và đăng ký DI.
Giá trị demo được ghi rõ là phục vụ bài tập, không phải giá/thuế thực tế.
Rounding mặc định AwayFromZero; bán kính 5 km và tuổi vị trí tối đa 5 phút là giá trị
demo đề xuất, có thể đổi trong cấu hình.

Seed driver ID cố định, chạy lặp an toàn và chỉ bật rõ ràng trong Development.
Không tự chèn dữ liệu vào Supabase dùng chung khi chạy test hoặc startup Production.
Tài khoản runtime phải là login riêng được cấp role `swiftride_matching` hiện có;
không dùng tài khoản quản trị postgres cho service.
Nếu chưa có credential phù hợp, vẫn hoàn tất code và kiểm thử local; báo rõ phần
kết nối runtime Supabase chưa xác minh, không yêu cầu người dùng gửi mật khẩu vào chat.

## Phạm vi file và kiểm chứng

Sửa MatchingService, test Matching/API liên quan, phần Matching trong Compose,
placeholder cấu hình môi trường và tài liệu API/cách chạy. Giữ các thay đổi người dùng
đang có trong Compose và các file khác. Loại cấu hình/container MongoDB nếu không còn
consumer; không xóa volume dữ liệu thực tế hoặc sửa cấu hình Payment ngoài phạm vi.

Kiểm thử xUnit/Moq: công thức mẫu 74.844 VND; thứ tự các bước; rounding và các invariant;
tọa độ không hữu hạn/ngoài miền; lọc offline, stale, excluded, ngoài bán kính; phá hòa;
no-driver; snapshot quote; cancellation và lỗi persistence.
Kiểm thử HTTP: JWT/role/ownership, validation, kết quả ba endpoint, lỗi không lộ secrets;
giữ các test Gateway chứng minh route nội bộ không public.
Kiểm thử PostgreSQL có giá trị: mapping schema, upsert lặp/đồng thời và lưu snapshot,
chạy trên database kiểm thử cô lập khi có môi trường phù hợp.

Chạy test nhỏ trước, sau đó build/test solution và `docker compose config --quiet`.
Không tuyên bố kiểm thử Supabase hoặc luồng end-to-end đã qua nếu chưa thực sự chạy.

## Ngoài phạm vi

RabbitMQ, Outbox/Saga, PostGIS, giữ chỗ tài xế, API bản đồ, frontend và triển khai luồng
Trip/Payment hoàn chỉnh. Matching chỉ trả ứng viên; không bảo đảm driver chưa được
chuyến khác giữ chỗ. Giới hạn scan vị trí phù hợp dữ liệu demo, thêm PostGIS khi cần.
