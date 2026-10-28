# Kế hoạch triển khai Matching & Pricing

**Mục tiêu:** Matching Clean Architecture với Supabase, tìm địa chỉ Nominatim và chọn điểm trên giao diện.
**Thiết kế:** ../specs/2026-10-28-matching-pricing-design.md, được điều chỉnh theo xác nhận mới nhất của người dùng: nhận tọa độ, Haversine, thời gian theo vận tốc cấu hình; tìm địa chỉ bằng nút Tìm, không autocomplete.
**Công nghệ:** .NET 10, EF Core/Npgsql, xUnit/Moq, React/Leaflet hiện có.

## Ràng buộc

Giữ ba service. Schema matchdb có sẵn, không migrate/xóa dữ liệu Supabase.
Endpoint /match và /quote nội bộ. GET /locations/search public qua Gateway, JWT rider/driver.
Nominatim chỉ gọi theo thao tác người dùng, tổng tối đa một request/giây, cache có giới hạn,
User-Agent nhận diện, URL cấu hình được; timeout và retry tạm thời có giới hạn.
Chưa nối Trip lifecycle thật: giao diện tìm địa chỉ thật nhưng chuyến/giá thanh toán vẫn demo.

## Công việc

- [x] 1. Domain/Application: GeoPoint, PricingContext, strategy và pipeline, ba use case; test công thức 74844, invariant, tọa độ, stale/offline/excluded/radius/tie, lưu quote.
- [x] 2. Infrastructure/Api: mapping schema rõ ràng, upsert nguyên tử, seed Development opt-in, JWT ownership, validation, DI và Compose. Test HTTP và mapping.
- [x] 3. Geocoding: IAddressSearch và AddressResult trong Application; NominatimAddressSearch ở Infrastructure. GET /locations/search?query=...; response [{id,displayName,latitude,longitude}]. Test giới hạn tốc độ/cache/timeout/lỗi/validation bằng HTTP giả lập.
- [x] 4. WebApp: nhập và nhấn Tìm, chọn kết quả, hai ghim xem trước/chỉnh trên map, lỗi rõ; gọi Gateway có token Development hoặc token người dùng. Test không autocomplete, lỗi và kết quả tọa độ hợp lệ. Giữ demo lifecycle.
- [x] 5. Build/test .NET, frontend tests/build, Compose validation, review thay đổi, cập nhật tài liệu và giới hạn kiểm chứng.

## Trọng tâm kiểm tra

Không lấy driver của người khác; không trả persistence entity; không âm thầm fallback geocoding;
không gọi Nominatim từ mỗi replica độc lập (demo một instance); không ghi secret vào source;
không để kết quả search cũ ghi đè lựa chọn mới; không tạo lại schema đã có.

## Nhật ký

- Người dùng đã duyệt triển khai; thực hiện trên nhánh feat/matching-pricing-supabase, giữ nguyên thay đổi có sẵn.
- Hợp đồng quote cập nhật: pickup/destination gồm latitude/longitude, mode mặc định standard.
- Kiểm thử DB thật chỉ dùng môi trường cô lập, không seed/test ghi vào project Supabase dùng chung.

- Kiểm chứng: build solution đạt; 208 .NET tests đạt (38 Matching gồm PostgreSQL thật, 14 Payment, 102 Trip, 54 API); 63 frontend tests và build đạt; Compose config đạt.
- Một lượt tìm địa danh công khai qua Gateway → Matching → Nominatim thật trả một kết quả có tọa độ hợp lệ. Không ghi dữ liệu vào Supabase.
- Review đã sửa log query YARP và click propagation nút recenter. Chưa kiểm thử UI trực tiếp bằng trình duyệt.
- Cảnh báo còn lại: dependency System.Security.Cryptography.Pkcs 9.0.11 thuộc Trip có NU1902/NU1903; NuGet audit Matching có NU1900 do truy cập nguồn không ổn định.
