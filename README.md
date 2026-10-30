# SwiftRide

> **Trạng thái hệ thống 30/10/2026:** Toàn bộ 18 Task theo kế hoạch Gap Closure đã được hoàn thiện và nghiệm thu toàn diện theo [bản thiết kế kiến trúc](docs/superpowers/specs/2026-10-29-pdf-gap-closure-design.md) và [kế hoạch triển khai chi tiết](docs/superpowers/plans/2026-10-29-pdf-gap-closure.md):
> - **3 Microservices cốt lõi**: Trip Service (SQL Server `TripDB`), Matching & Pricing Service (Supabase PostgreSQL `matchdb`), Payment Service (Supabase PostgreSQL `paydb`). Tuyệt đối không thêm dịch vụ thứ 4 theo quy tắc `AGENTS.md`.
> - **API Gateway**: YARP reverse proxy port 5080, xác thực JWT Bearer (hỗ trợ Dual-mode: DevelopmentJwt và OIDC qua PKCE), fixed-window rate limiter, lan truyền W3C `traceparent` và `X-Correlation-Id`.
> - **Transactional Outbox & Inbox Pattern**: Staging sự kiện nguyên tử trong cùng giao dịch cơ sở dữ liệu cục bộ; Background publishers tự động quét và gửi sang RabbitMQ qua MassTransit 8.5.8.
> - **Saga Choreography Compensation**: Hỗ trợ bồi hoàn tự động (refund) với sổ cái kế toán kép cân bằng (balanced double-entry ledger).
> - **OpenTelemetry & Observability**: Giám sát phân tán với OpenTelemetry 1.20.1, core metrics (`trips.created`, `trips.completed`, `matching.duration`, `payments.settled`), tích hợp sẵn Jaeger UI tại cổng 16686.
> - **Frontend WebApp**: React 19 + TypeScript + Tailwind CSS, tính toán tiền tệ chính xác bằng BigInt whole VND, bản đồ tương tác Leaflet với cơ chế dự phòng tìm kiếm địa chỉ offline (Nominatim fallback).
> - **Tổng số bài test đạt chuẩn**: **560/560 bài test pass 100%** (bao gồm 90 bài test integration trên 3 database container thật và 104 bài test vitest frontend).

Backend đặt xe cho môn Kiến trúc Phần mềm, dùng .NET 10 và ba service độc lập: Trip (SQL Server), Matching & Pricing (Supabase PostgreSQL, schema `matchdb`), Payment (Supabase PostgreSQL, schema `paydb`). Toàn bộ client traffic đi qua YARP API Gateway.

## Cấu trúc

```text
src/
  ApiGateway/                  # YARP, JWT Bearer / OIDC, fixed-window rate limiter, Correlation ID
  SwiftRide.Contracts/         # Integration events contracts V1 (TripAccepted, RideCompleted, PaymentSettled, etc.)
  TripService/                 # Domain (State Machine), Application, Infrastructure (SQL Server, Outbox), Api
  MatchingService/             # Domain (Strategy Pricing), Application, Infrastructure (Supabase matchdb), Api
  PaymentService/              # Domain (Double-Entry Ledger), Application, Infrastructure (Supabase paydb, Inbox), Api
  WebApp/                      # Frontend Vite + React + TypeScript + Tailwind CSS + oidc-client-ts
tests/
  SwiftRide.Api.Tests/         # Gateway routing, security, E2E FullFlow (REST & Events), OIDC, Observability
  SwiftRide.TripService.Tests/ # Domain lifecycle, UseCase, Persistence (SQL Server), Outbox & Consumer tests
  SwiftRide.MatchingService.Tests/ # Pricing strategies, geocoding fallback & Postgres repository tests
  SwiftRide.PaymentService.Tests/  # Domain, Gateway, Ledger, Idempotency, Outbox & Compensation tests
docs/
docker-compose.yml
SwiftRide.sln
```

Quy tắc kiến trúc: [AGENTS.md](AGENTS.md). Đề bài: [docs/requirements.md](docs/requirements.md). Tài liệu chi tiết: [docs/](docs/).

Tài liệu hệ thống:
- [Báo cáo Trạng thái Triển khai Toàn diện](docs/implementation-status.md)
- [Hướng dẫn kiểm thử & Vận hành luồng đặt xe End-to-End](docs/end-to-end-booking.md)
- [Hợp đồng Integration Event Contracts V1](docs/messaging-contracts-v1.md)
- [Vận hành RabbitMQ, Error Queue & Replay Poison Message](docs/messaging-operations.md)
- [Kiến trúc Bảo mật & Cấu hình OIDC Dual-Mode](docs/authentication.md)
- [Kiến trúc Giám sát OpenTelemetry, Metrics & Tracing](docs/observability.md)
- [Tài liệu Matching & Pricing Service](docs/matching-pricing.md)
- [Tài liệu Cơ sở dữ liệu Supabase](docs/database-supabase.md)
- [Tài liệu Giao diện Frontend](docs/frontend-demo.md)

## Build và test

Cần .NET 10 SDK trong PATH và Node.js 20+ cho WebApp:

```powershell
# Kiểm tra phiên bản SDK:
dotnet --list-sdks

# Khôi phục và build toàn bộ solution:
dotnet restore SwiftRide.sln
dotnet build SwiftRide.sln

# Chạy toàn bộ Unit & Functional Tests Backend:
dotnet test SwiftRide.sln

# Chạy kiểm thử tự động toàn diện trên 3 Database thật (SQL Server, matchdb, paydb):
powershell -File docs/run-integration-tests.ps1 -Suite All

# Chạy kiểm thử Frontend WebApp:
cd src/WebApp
npm test
npm run typecheck
npm run build
cd ../..
```

## Chạy bằng Docker Compose

Cần Docker daemon đang chạy. Tạo file cấu hình `.env` từ `.env.example`:

```powershell
Copy-Item .env.example .env
# Chỉnh sửa mật khẩu và connection strings Supabase trong .env

# Kiểm tra cấu hình compose hợp lệ:
docker compose --env-file .env config --quiet

# Khởi động toàn bộ stack hệ thống:
docker compose up --build -d
```

Sau khi các container khởi động, chạy kịch bản kiểm thử tự động toàn luồng:
```powershell
powershell -ExecutionPolicy Bypass -File docs/check-booking-flow.ps1 -GatewayUrl "http://localhost:5080"
```

Tắt hệ thống:
```powershell
docker compose down
```
