# Báo cáo scaffold SwiftRide

## Kết quả

Đã tạo skeleton gồm 17 project: gateway, Contracts, 12 project thuộc đúng ba service và ba test project. Không thêm logic nghiệp vụ, entity, DTO, migration, state machine, pricing, payment, MassTransit, Outbox, Saga hoặc OpenTelemetry.

Domain, Application và Contracts không có file C# nghiệp vụ. Infrastructure có DI database và hai DbContext trống. Mỗi API có health endpoint và file endpoint riêng trả 501. Gateway có JWT, YARP, fixed-window rate limiter và dev-token endpoint chỉ trong Development.

Đã tạo các file cấu hình repo, bốn Dockerfile multi-stage .NET 10, Compose, .env.example, README tiếng Việt và script kiểm tra docs/check-skeleton.ps1. Cây thư mục đầy đủ ở cuối báo cáo.

## File có sẵn và xung đột

Giữ nguyên AGENTS.md, RideLite-KienTrucPhanMem.pdf và docs/requirements.md. Không tạo placeholder requirements vì tài liệu đã tồn tại. Không có solution, src/, tests/ hoặc Compose trước khi scaffold; không có xung đột file.

Dùng thư mục SWA hiện tại làm root của solution; không tạo thêm thư mục SwiftRide lồng bên trong. Repo hiện chưa có .git; không chạy git commit.

docs/requirements.md có sơ đồ gateway tới Payment và phần mô tả messaging. Skeleton áp dụng phân tách public/internal và hoãn messaging theo yêu cầu hiện tại cùng AGENTS.md; tài liệu gốc không bị chỉnh sửa.

## Phiên bản

| Thành phần | Phiên bản |
|---|---|
| .NET SDK | 10.0.401 tại D:\dotnet-10.0.401 |
| Target framework | net10.0 |
| Microsoft.EntityFrameworkCore.SqlServer | 10.0.12 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 |
| MongoDB.Driver | 3.12.0 |
| Yarp.ReverseProxy | 2.3.0 |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.12 |
| xunit | 2.9.3 |
| Moq | 4.21.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 |
| xunit.runner.visualstudio | 3.1.4, người dùng đã cho phép giữ |
| Docker CLI / Compose | 29.4.0 / v5.1.2 |

Các package reference đã được pin phiên bản cụ thể. Không thêm coverlet từ template. Matching Infrastructure dùng FrameworkReference Microsoft.AspNetCore.App cho DI/configuration, không thêm package DI.

Dockerfile dùng mcr.microsoft.com/dotnet/sdk:10.0 và aspnet:10.0. Database image được cấu hình: SQL Server 2022-latest, mongo:8.0, postgres:17; broker tùy chọn rabbitmq:4.1-management. Chưa tải hoặc chạy các image nên chưa xác minh digest/runtime thực tế.

## Lệnh và xác thực

Trong phiên Codex, PATH vẫn trỏ tới runtime .NET 7; machine PATH đã có D:\dotnet-10.0.401. Các lệnh .NET bên dưới được chạy bằng đường dẫn tuyệt đối tới SDK này.

| Lệnh/kiểm tra | Kết quả |
|---|---|
| dotnet --list-sdks bằng SDK đã tìm thấy | 10.0.401 |
| dotnet new sln --help | Có hỗ trợ --format sln |
| dotnet new sln --format sln -n SwiftRide | Thành công, classic .sln |
| dotnet new web/classlib/xunit --framework net10.0 --no-restore | Tạo đủ 17 project |
| dotnet sln SwiftRide.sln add; dotnet add reference/package | Thành công |
| dotnet restore SwiftRide.sln | Ban đầu lỗi quyền NuGet config và TLS Windows; retry thành công với cache/config tạm |
| dotnet restore SwiftRide.sln --configfile .local/NuGet.Config | Thành công |
| dotnet build SwiftRide.sln --no-restore | Thành công, 0 warning, 0 error |
| dotnet test SwiftRide.sln --no-restore | 3/3 placeholder test pass, không skip |
| docker --config .local/docker compose --env-file .env.example config --quiet | Pass |
| Cấu hình Compose với --profile messaging | Pass |
| docker --config .local/docker compose --env-file .env.example up --build -d | Fail: Docker daemon không chạy, pipe docker_engine không tồn tại |
| docker --config .local/docker compose --env-file .env.example down | Cũng không kết nối được daemon; không có container nào được tạo bởi lần up này |
| Kiểm tra XML references của 17 project và target path | Pass |
| Smoke check local Development/Production | Pass, chi tiết dưới đây |

Để khắc phục hạn chế phiên chạy, DOTNET_CLI_HOME, APPDATA, NUGET_PACKAGES và NUGET_HTTP_CACHE_PATH được đặt trong .local/. Một transport NuGet tạm trên loopback dùng Node HTTPS với kiểm tra chứng chỉ vẫn bật; không có nguồn HTTP public hoặc tắt xác minh TLS. Cache và helper tạm được git-ignore, không tham gia Docker build. Không có cấu hình này trong source project. Các helper server/API kiểm tra đã được tắt.

Smoke check được chạy bằng powershell.exe -NoProfile -ExecutionPolicy Bypass -File docs/check-skeleton.ps1 với gateway local, sau đó thêm -Production cho instance Production. API được chạy từ DLL đã build bằng SDK .NET 10, với cấu hình tạm và signing key ngẫu nhiên trong environment.

- Gateway GET /health trả 200; POST /trips không token trả 401; có dev token trả 501.
- Mọi Trip stub và PUT /drivers/{id}/location qua gateway trả 501.
- POST /payments, /payments/{id}/refund, /match, /quote qua gateway trả 404.
- Role không hợp lệ ở /dev/token trả 400; bearer token sai trả 401.
- Production không đăng ký /dev/token, trả 404.
- Fixed-window rate limiter trả 429 khi vượt hạn mức.
- Ba service local có health 200; Matching/Payment internal stub trả 501 khi kiểm tra trực tiếp.

Kiểm tra references: Domain và Contracts không có reference/package; Application chỉ Domain; Infrastructure chỉ Application và Domain; API chỉ Application và Infrastructure; mỗi test chỉ Domain và Application của service tương ứng. Gateway không có project reference. Không có reference chéo service.

## Chưa chạy và giới hạn

**Chưa chạy được containers, database healthchecks và HTTP qua gateway trong Docker.** Docker CLI có sẵn nhưng daemon không khả dụng. Local smoke check không thay thế xác thực Docker hoặc kết nối database. Chưa xác minh SQL Server initialization, database persistence hay startup ordering thực tế.

Health endpoints hiện là liveness. Không tự tạo schema/migration; Compose chỉ khởi tạo database TripDB, cấu hình MatchDB và PayDB. MongoDB tạo database vật lý khi có ghi dữ liệu; skeleton chưa ghi dữ liệu.

.env.example chỉ có placeholder; chưa tạo .env hoặc lưu secret thật. Khi chạy local, thay placeholder và URL-encode MongoDB credentials nếu chứa ký tự đặc biệt. Chỉ gateway publish 5080; RabbitMQ nằm trong profile messaging.

## Bước tiếp theo

Bật Docker daemon rồi chạy lại hướng dẫn Compose và script smoke check trong README. Sau khi xác thực containers, triển khai MVP bắt đầu từ Trip Domain và các test chuyển trạng thái theo AGENTS.md; xóa test placeholder khi có test thật.

## Cây thư mục cuối cùng

Không hiển thị bin/, obj/ hoặc .local/ (cache tạm được ignore).

```text
SWA/
|-- docs/
|   |-- check-skeleton.ps1
|   |-- requirements.md
|   \-- scaffold-report.md
|-- src/
|   |-- ApiGateway/
|   |   |-- Properties/
|   |   |   \-- launchSettings.json
|   |   |-- appsettings.Development.json
|   |   |-- appsettings.json
|   |   |-- Dockerfile
|   |   |-- Program.cs
|   |   \-- SwiftRide.ApiGateway.csproj
|   |-- MatchingService/
|   |   |-- Api/
|   |   |   |-- Properties/
|   |   |   |   \-- launchSettings.json
|   |   |   |-- appsettings.Development.json
|   |   |   |-- appsettings.json
|   |   |   |-- Dockerfile
|   |   |   |-- MatchingEndpoints.cs
|   |   |   |-- Program.cs
|   |   |   \-- SwiftRide.MatchingService.Api.csproj
|   |   |-- Application/
|   |   |   \-- SwiftRide.MatchingService.Application.csproj
|   |   |-- Domain/
|   |   |   \-- SwiftRide.MatchingService.Domain.csproj
|   |   \-- Infrastructure/
|   |       |-- DependencyInjection.cs
|   |       \-- SwiftRide.MatchingService.Infrastructure.csproj
|   |-- PaymentService/
|   |   |-- Api/
|   |   |   |-- Properties/
|   |   |   |   \-- launchSettings.json
|   |   |   |-- appsettings.Development.json
|   |   |   |-- appsettings.json
|   |   |   |-- Dockerfile
|   |   |   |-- PaymentEndpoints.cs
|   |   |   |-- Program.cs
|   |   |   \-- SwiftRide.PaymentService.Api.csproj
|   |   |-- Application/
|   |   |   \-- SwiftRide.PaymentService.Application.csproj
|   |   |-- Domain/
|   |   |   \-- SwiftRide.PaymentService.Domain.csproj
|   |   \-- Infrastructure/
|   |       |-- DependencyInjection.cs
|   |       |-- PaymentDbContext.cs
|   |       \-- SwiftRide.PaymentService.Infrastructure.csproj
|   |-- SwiftRide.Contracts/
|   |   \-- SwiftRide.Contracts.csproj
|   \-- TripService/
|       |-- Api/
|       |   |-- Properties/
|       |   |   \-- launchSettings.json
|       |   |-- appsettings.Development.json
|       |   |-- appsettings.json
|       |   |-- Dockerfile
|       |   |-- Program.cs
|       |   |-- SwiftRide.TripService.Api.csproj
|       |   \-- TripEndpoints.cs
|       |-- Application/
|       |   \-- SwiftRide.TripService.Application.csproj
|       |-- Domain/
|       |   \-- SwiftRide.TripService.Domain.csproj
|       \-- Infrastructure/
|           |-- DependencyInjection.cs
|           |-- SwiftRide.TripService.Infrastructure.csproj
|           \-- TripDbContext.cs
|-- tests/
|   |-- SwiftRide.MatchingService.Tests/
|   |   |-- SwiftRide.MatchingService.Tests.csproj
|   |   \-- UnitTest1.cs
|   |-- SwiftRide.PaymentService.Tests/
|   |   |-- SwiftRide.PaymentService.Tests.csproj
|   |   \-- UnitTest1.cs
|   \-- SwiftRide.TripService.Tests/
|       |-- SwiftRide.TripService.Tests.csproj
|       \-- UnitTest1.cs
|-- .dockerignore
|-- .editorconfig
|-- .env.example
|-- .gitignore
|-- AGENTS.md
|-- Directory.Build.props
|-- docker-compose.yml
|-- README.md
|-- RideLite-KienTrucPhanMem.pdf
\-- SwiftRide.sln
```
