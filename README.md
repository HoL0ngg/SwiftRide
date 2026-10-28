# SwiftRide

> Cập nhật Matching 28/10/2026: đã triển khai Clean Architecture, Npgsql/schema matchdb, báo giá từ tọa độ và tìm địa chỉ Nominatim. Xem [hướng dẫn hiện hành](docs/matching-pricing.md). Các mô tả scaffold chưa triển khai Matching bên dưới là trạng thái lịch sử.

Backend đặt xe cho môn Kiến trúc Phần mềm, dùng .NET 10 và ba service độc lập: Trip (SQL Server), Matching & Pricing (Supabase PostgreSQL, schema `matchdb`), Payment (Supabase PostgreSQL, schema `paydb`). Client đi qua YARP API Gateway.

Hiện tại hệ thống đã hoàn thành:
1. **API Gateway skeleton**: Composition root chung, routing public/internal, JWT authentication, dev-token boundary, rate limiter và upstream timeout đã kiểm chứng qua automated HTTP tests.
2. **Trip Domain lifecycle**: Đầy đủ 11 trạng thái với State Pattern, bảo vệ ma trận 17 transitions, bất biến khóa giá, giới hạn từ chối tài xế `k`, sự kiện `RideCompleted` và đối chiếu kết quả thanh toán với 102 unit tests.

Các tầng Application, DB persistence, contract dùng chung và các service Matching/Payment vẫn đang ở dạng skeleton, endpoint nghiệp vụ trả về 501. Chưa có entity EF Core, migration hoặc logic nghiệp vụ cho Matching/Payment.

## Cấu trúc

```text
src/
  ApiGateway/                  # YARP, JWT, fixed-window rate limiter
  SwiftRide.Contracts/         # Chưa có contract
  TripService/                 # Domain (State Pattern 11 states), Application, Infrastructure, Api
  MatchingService/             # Domain, Application, Infrastructure, Api
  PaymentService/              # Domain, Application, Infrastructure, Api
tests/
  SwiftRide.Api.Tests/
  SwiftRide.TripService.Tests/
  SwiftRide.MatchingService.Tests/
  SwiftRide.PaymentService.Tests/
docs/
SwiftRide.sln
docker-compose.yml
```

Quy tắc kiến trúc: [AGENTS.md](AGENTS.md). Đề bài: [docs/requirements.md](docs/requirements.md). Tài liệu và script kiểm tra: [docs/](docs/).

Tài liệu nền để triển khai chức năng: [docs/architecture.md](docs/architecture.md), gồm ranh giới service, quy tắc nghiệp vụ, API, dữ liệu và lộ trình MVP.

Kế hoạch triển khai chi tiết: [API Gateway skeleton](docs/superpowers/plans/2026-10-19-api-gateway-skeleton.md) và [Trip lifecycle với State Pattern](docs/superpowers/plans/2026-10-19-trip-lifecycle-state-pattern.md). Cả hai kế hoạch đã được thực thi và kiểm chứng hoàn chỉnh bằng automated test suites.

## Build và test

Cần .NET 10 SDK trong PATH:

```powershell
dotnet --list-sdks
dotnet restore SwiftRide.sln
dotnet build SwiftRide.sln
dotnet test SwiftRide.sln

# Chạy riêng bộ kiểm thử tích hợp Gateway (routing, security, rate limit, timeout):
dotnet test tests/SwiftRide.Api.Tests/SwiftRide.Api.Tests.csproj --filter FullyQualifiedName~Gateway

# Chạy riêng bộ kiểm thử Trip Domain (11 states, 17 transitions, fare snapshot, driver rejection):
dotnet test tests/SwiftRide.TripService.Tests/SwiftRide.TripService.Tests.csproj
```

Placeholder test của `TripService` đã được xóa và thay bằng 102 unit tests nghiệp vụ. MatchingService và PaymentService vẫn giữ test placeholder `Skeleton_Compiles` cho tới khi có test thật. Runner xUnit được giữ theo sự cho phép của người dùng.

`tests/SwiftRide.Api.Tests` kiểm tra:
1. `GlobalExceptionHandler`: Xử lý ngoại lệ của cả bốn ứng dụng qua HTTP trong Development và Production, mã lỗi, định dạng JSON và ẩn dữ liệu nhạy cảm.
2. `Gateway`: Pipeline composition chung, định tuyến public routes (`/trips/**`, `PUT /drivers/{id}/location`), chặn internal routes (`/match`, `/quote`, `/payments/**` -> 404), xác thực JWT, dev-token Development/Production, rate limiter (429) và timeout (504 không replay). Nghiệp vụ backend hiện tại vẫn trả về `501 Not Implemented` từ service stub (chưa có resource authorization và DB persistence).

## Xử lý exception

Mỗi service và Gateway có `GlobalExceptionHandler` riêng tại tầng API, dùng `IExceptionHandler` có sẵn của ASP.NET Core. `Program.cs` đăng ký `AddProblemDetails`, `AddExceptionHandler` và đặt `UseExceptionHandler` trước các middleware/endpoint còn lại.

- `BadHttpRequestException` có mã 4xx giữ nguyên mã HTTP (ví dụ 400 hoặc 413).
- Exception khác trả 500 với thông báo chung; không gửi exception message hoặc stack trace cho client, kể cả Development.
- Response có content type `application/problem+json`, các trường `type`, `title`, `status`, `detail`, `traceId`. Log chỉ ghi mã lỗi, tên kiểu exception và `traceId`, tránh lộ token/connection string qua exception message.

Handler chỉ xử lý exception trong HTTP pipeline khi response chưa bắt đầu; không xử lý lỗi cấu hình lúc startup hay lỗi background worker. Các response 401/403/404/429/501 hiện có giữ nguyên hành vi. Chưa có domain exception nên chưa tự ánh xạ lỗi nghiệp vụ thành 404/409; thêm ánh xạ theo kiểu exception cụ thể khi triển khai use case. Lỗi kết nối upstream do YARP chuyển thành response 502/504 không phải exception thoát ra pipeline.

Tham khảo: [xử lý lỗi ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-10.0).

## Chạy bằng Docker Compose

> Matching và Payment dùng Supabase PostgreSQL với schema riêng. Adapter Matching và Compose chưa hoàn tất chuyển đổi; hướng dẫn scaffold dưới đây chưa bảo đảm chạy được luồng Supabase hoàn chỉnh. Xem [trạng thái database](docs/database-supabase.md).

Cần Docker daemon đang chạy. Tạo cấu hình local rồi thay tất cả password/signing key mẫu:

```powershell
Copy-Item .env.example .env
docker compose config --quiet
docker compose up --build -d --wait --wait-timeout 180
powershell -NoProfile -ExecutionPolicy Bypass -File docs/check-skeleton.ps1
docker compose down
```

`.env` được git-ignore; không đưa secrets vào Git. Khi đổi thông tin đăng nhập, database volume đã tồn tại không tự cập nhật password. Connection string Supabase dùng Npgsql, TLS, timeout và tài khoản riêng theo service.

Gateway ở `http://localhost:5080`; chỉ gateway publish port. Các service nghe cổng 8080 trong network Compose; mỗi service chỉ phụ thuộc database riêng. Health endpoint của gateway và service là liveness, chưa kiểm tra kết nối database.

Bốn Dockerfile dùng build nhiều giai đoạn và chạy ứng dụng bằng user không phải root (`APP_UID`). Compose dùng sẵn DNS `trip-service:8080` và `matching-service:8080`; chỉ cần đặt các biến địa chỉ khi muốn ghi đè. `ASPNETCORE_ENVIRONMENT` được truyền cho cả Gateway và ba service. Dữ liệu được lưu trong ba named volume; `docker compose down` giữ dữ liệu, tránh dùng `down -v` nếu cần giữ database.

`ASPNETCORE_ENVIRONMENT=Development` mới đăng ký `POST /dev/token?userId=example&role=rider` (hoặc `driver`). Response chứa `access_token`; gửi bằng header `Authorization: Bearer <token>`. Token hết hạn sau 30 phút. Production không có endpoint này; kiểm tra bằng `docs/check-skeleton.ps1 -Production`.

JWT issuer/audience/signing key, cluster addresses và giới hạn request lấy từ cấu hình/environment. Rate limiter dùng một fixed window chung. Gateway chỉ proxy `/trips/**` và `PUT /drivers/{id}/location`, yêu cầu JWT. `/match`, `/quote`, `/payments/**` là nội bộ và trả 404 ở gateway.

RabbitMQ chỉ chạy khi bật profile, chưa có code messaging:

```powershell
docker compose --profile messaging up --build -d
docker compose --profile messaging down
```

## Chạy project trực tiếp

Ví dụ Trip API sau khi cấu hình connection string qua environment:

```powershell
$env:ConnectionStrings__TripDB = '<connection string của TripDB>'
dotnet run --project src/TripService/Api/SwiftRide.TripService.Api.csproj
```

Cấu hình đích: Matching dùng `ConnectionStrings__MatchDB` (Npgsql, schema `matchdb`); Payment dùng `ConnectionStrings__PaymentDB` (schema `paydb`). Adapter Matching hiện chưa chuyển sang PostgreSQL. Gateway cần `Jwt__Issuer`, `Jwt__Audience`, `Jwt__SigningKey`, `RateLimit__PermitLimit`, `RateLimit__WindowSeconds`, `RateLimit__QueueLimit` và hai destination address như trong Compose. Không đưa connection string thật vào tài liệu.

Khi chạy trực tiếp qua `dotnet run --project src/ApiGateway/SwiftRide.ApiGateway.csproj` (launch profile), Gateway mặc định lắng nghe tại `http://localhost:5082`, khác với cổng `http://localhost:5080` khi chạy qua Docker Compose. Rate limiter của Gateway hiện dùng một fixed window chung (`gateway`).

Bước tiếp theo: triển khai MVP bắt đầu từ Trip Domain và test chuyển trạng thái theo AGENTS.md.

## Giao diện rider và driver

`src/WebApp` có hai giao diện Swift Emerald, dùng Vite + React + TypeScript + Tailwind CSS. Bản đồ tải thật từ OpenStreetMap; API, chuyến đi và thanh toán đang mô phỏng trong trình duyệt, chưa kết nối backend. Có thể mở hai tab để trải nghiệm chung một chuyến từ đặt xe đến thanh toán. Xem cách chạy, giới hạn và kịch bản thử tại [docs/frontend-demo.md](docs/frontend-demo.md).
