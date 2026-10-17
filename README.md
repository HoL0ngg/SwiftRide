# SwiftRide

Backend đặt xe cho môn Kiến trúc Phần mềm, dùng .NET 10 và ba service độc lập: Trip (SQL Server), Matching & Pricing (MongoDB), Payment (PostgreSQL). Client đi qua YARP API Gateway.

Hiện tại chỉ có skeleton: thư viện Domain/Application/Contracts trống, DI database, health endpoint và endpoint nghiệp vụ trả về 501. Chưa có entity, migration hoặc logic nghiệp vụ.

## Cấu trúc

```text
src/
  ApiGateway/                  # YARP, JWT, fixed-window rate limiter
  SwiftRide.Contracts/         # Chưa có contract
  TripService/                 # Domain, Application, Infrastructure, Api
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

## Build và test

Cần .NET 10 SDK trong PATH:

```powershell
dotnet --list-sdks
dotnet restore SwiftRide.sln
dotnet build SwiftRide.sln
dotnet test SwiftRide.sln
```

Mỗi service có một test placeholder `Skeleton_Compiles`; phải xóa khi có test thật. Runner xUnit được giữ theo sự cho phép của người dùng.

`tests/SwiftRide.Api.Tests` kiểm tra exception handler của cả bốn ứng dụng qua HTTP trong Development và Production, gồm mã lỗi, định dạng JSON và việc ẩn dữ liệu nhạy cảm.

## Xử lý exception

Mỗi service và Gateway có `GlobalExceptionHandler` riêng tại tầng API, dùng `IExceptionHandler` có sẵn của ASP.NET Core. `Program.cs` đăng ký `AddProblemDetails`, `AddExceptionHandler` và đặt `UseExceptionHandler` trước các middleware/endpoint còn lại.

- `BadHttpRequestException` có mã 4xx giữ nguyên mã HTTP (ví dụ 400 hoặc 413).
- Exception khác trả 500 với thông báo chung; không gửi exception message hoặc stack trace cho client, kể cả Development.
- Response có content type `application/problem+json`, các trường `type`, `title`, `status`, `detail`, `traceId`. Log chỉ ghi mã lỗi, tên kiểu exception và `traceId`, tránh lộ token/connection string qua exception message.

Handler chỉ xử lý exception trong HTTP pipeline khi response chưa bắt đầu; không xử lý lỗi cấu hình lúc startup hay lỗi background worker. Các response 401/403/404/429/501 hiện có giữ nguyên hành vi. Chưa có domain exception nên chưa tự ánh xạ lỗi nghiệp vụ thành 404/409; thêm ánh xạ theo kiểu exception cụ thể khi triển khai use case. Lỗi kết nối upstream do YARP chuyển thành response 502/504 không phải exception thoát ra pipeline.

Tham khảo: [xử lý lỗi ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-10.0).

## Chạy bằng Docker Compose

Cần Docker daemon đang chạy. Tạo cấu hình local rồi thay tất cả password/signing key mẫu:

```powershell
Copy-Item .env.example .env
docker compose config --quiet
docker compose up --build -d --wait --wait-timeout 180
powershell -NoProfile -ExecutionPolicy Bypass -File docs/check-skeleton.ps1
docker compose down
```

`.env` được git-ignore; không đưa secrets vào Git. Khi đổi thông tin đăng nhập, database volume đã tồn tại không tự cập nhật password. MongoDB credentials trong connection URI cần URL-encode ký tự đặc biệt.

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

Matching cần `ConnectionStrings__MatchDB` và `MongoDB__Database`; Payment cần `ConnectionStrings__PaymentDB`. Gateway cần `Jwt__Issuer`, `Jwt__Audience`, `Jwt__SigningKey`, `RateLimit__PermitLimit`, `RateLimit__WindowSeconds`, `RateLimit__QueueLimit` và hai destination address như trong Compose. Không đưa connection string thật vào tài liệu.

Bước tiếp theo: triển khai MVP bắt đầu từ Trip Domain và test chuyển trạng thái theo AGENTS.md.

## Giao diện rider và driver

`src/WebApp` có hai giao diện Swift Emerald, dùng Vite + React + TypeScript + Tailwind CSS. Bản đồ tải thật từ OpenStreetMap; API, chuyến đi và thanh toán đang mô phỏng trong trình duyệt, chưa kết nối backend. Có thể mở hai tab để trải nghiệm chung một chuyến từ đặt xe đến thanh toán. Xem cách chạy, giới hạn và kịch bản thử tại [docs/frontend-demo.md](docs/frontend-demo.md).
