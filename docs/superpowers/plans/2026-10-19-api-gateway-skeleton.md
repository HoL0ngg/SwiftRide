# API Gateway skeleton - Kế hoạch triển khai

> **Dành cho agent thực thi:** Dùng skill `executing-plans` nếu triển khai trực tiếp; chỉ dùng `subagent-driven-development` khi người dùng chọn cách thực thi bằng subagent. Theo dõi tiến độ bằng các checkbox. Đây là kế hoạch, chưa phải yêu cầu bắt đầu sửa code.

**Goal:** Hoàn thiện và kiểm chứng Gateway skeleton có sẵn: YARP proxy đúng public routes, xác thực JWT, dev-token chỉ Development, rate limit và timeout, không chứa nghiệp vụ.

**Architecture:** Giữ project `src/ApiGateway` và toàn bộ cấu hình hiện hành. Tách composition root thành một hàm dựng ứng dụng được cả `Program.cs` và HTTP test sử dụng, rồi kiểm tra Gateway thật trên Kestrel với upstream giả lập trong test. Không triển khai Trip/Matching/Payment use case trong plan này.

**Tech Stack:** `net10.0`, ASP.NET Core, YARP `2.3.0`, JwtBearer `10.0.12`, xUnit `2.9.3`, Docker Compose; dùng package đã có, không thêm test host package.

**Spec:** [architecture.md](../../architecture.md), mục 2, 4-5, 10, 12, 14 và 18; [AGENTS.md](../../../AGENTS.md), mục 3, 5.4, 8 và 11-13. Plan State Pattern độc lập: [trip-lifecycle-state-pattern](2026-10-19-trip-lifecycle-state-pattern.md).

## Global Constraints

- Chỉ có ba business service: Trip, Matching & Pricing, Payment. Gateway là edge component.
- Client gọi backend qua Gateway; `/match`, `/quote`, `/payments/**` là nội bộ và không có route public.
- Gateway không truy cập DB, tính giá, chuyển trạng thái Trip, gọi charge hoặc kiểm tra chủ chuyến.
- JWT/signing key và địa chỉ upstream lấy từ configuration/environment; không commit secret.
- Giữ `.NET 10`, phiên bản package và cấu trúc solution; không tạo Gateway thứ hai.
- Giữ `GlobalExceptionHandler`, Problem Details và các test HTTP hiện có.
- Mọi lời gọi test có timeout; proxy có `ActivityTimeout`; không thêm automatic retry cho `POST` ở Gateway.
- Code, test và commit message bằng tiếng Anh; tài liệu/trao đổi bằng tiếng Việt.
- Chỉ sửa file của task. Repo đang có thay đổi chưa commit ở `.gitignore`, `README.md`, `docs/architecture.md`, `skills-lock.json` và `txt`; kiểm tra lại trước lúc thực thi, không reset/stage toàn bộ workspace.

## Review Focus

1. JWT trông hợp lệ nhưng sai signature/issuer/audience hoặc đã hết hạn phải trả `401` và không tới upstream: **GW-02**.
2. `/payments/**`, `/match`, `/quote`, kể cả có bearer token hợp lệ, phải trả `404` và không tới upstream: **GW-03**.
3. Host Production phải không đăng ký dev-token, dù ký JWT bằng cùng cấu hình test: **GW-02/GW-04**.
4. Upstream nhận request nhưng không phản hồi phải bị timeout hữu hạn; không tự gửi lại mutation: **GW-03**.
5. Test rate-limit không được nhiễu bởi token issuance hoặc test chạy song song; dùng host riêng và token tạo trước: **GW-03**.

## 1. Phạm vi và hiện trạng đã kiểm tra

Đối chiếu source ngày 19/10/2026; chưa chạy build/test ứng dụng trong lần lập plan này.

| Đã có | File |
|---|---|
| JWT validation, YARP, fixed-window limiter, `/health`, dev-token 30 phút | `src/ApiGateway/Program.cs` |
| `/trips/{**catch-all}`, `PUT /drivers/{id}/location`, cluster timeout 30 giây | `src/ApiGateway/appsettings.json` |
| Dockerfile, Gateway publish `5080:8080`, hai địa chỉ cluster từ env | `src/ApiGateway/Dockerfile`, `docker-compose.yml` |
| Exception handler an toàn và test cho bốn ứng dụng | `src/ApiGateway/GlobalExceptionHandler.cs`, `tests/SwiftRide.Api.Tests/GlobalExceptionHandlerTests.cs` |
| Script smoke qua Gateway, còn kỳ vọng nghiệp vụ `501` | `docs/check-skeleton.ps1` |

**Kết quả cần có:** khung hiện tại được kiểm chứng bằng automated HTTP tests và smoke Compose. Route nghiệp vụ vẫn trả `501` từ service stub; đây là kết quả đúng của skeleton, chưa phải luồng đặt xe hoàn chỉnh.

**Ngoài phạm vi:** resource authorization trong use case của service, Identity Service, CORS cho WebApp, BFF, per-user quota, readiness DB, internal service authentication, retry liên service và business endpoints. Gateway chỉ yêu cầu token hợp lệ như hiện tại; không gọi đây là đã hoàn tất phân quyền rider/driver của hệ thống.

**Quyết định khi lập plan:** không viết lại cấu hình đang đáp ứng yêu cầu. Chỉ bổ sung seam dựng host để test đường chạy thật và sửa các khoảng trống được test chứng minh, ví dụ signing key toàn khoảng trắng.

## 2. Bản đồ file

Tất cả đường dẫn dưới đây tương đối với repository root `C:\Users\Wuanz\Desktop\SwiftRide`.

| Thao tác | File | Trách nhiệm |
|---|---|---|
| Tạo | `src/ApiGateway/GatewayApplication.cs` | Dựng Gateway từ `WebApplicationBuilder` đã được cấp; chứa đăng ký/middleware/endpoint hiện nằm trong Program |
| Sửa | `src/ApiGateway/Program.cs` | Composition entry point: tạo builder, gọi `GatewayApplication.Build`, chạy host |
| Tạo | `tests/SwiftRide.Api.Tests/Gateway/GatewayFixture.cs` | Khởi động/dispose Gateway thật và hai upstream; cấp config test riêng từng host |
| Tạo | `tests/SwiftRide.Api.Tests/Gateway/UpstreamStub.cs` | Kestrel upstream có marker, capture request và chế độ chờ cancellation |
| Tạo | `tests/SwiftRide.Api.Tests/Gateway/GatewayStartupTests.cs` | Health, startup validation, dựng host thật |
| Tạo | `tests/SwiftRide.Api.Tests/Gateway/GatewaySecurityTests.cs` | JWT và dev-token Development/Production |
| Tạo | `tests/SwiftRide.Api.Tests/Gateway/GatewayProxyTests.cs` | Public/internal routing, method/path/query và Authorization forwarding |
| Tạo | `tests/SwiftRide.Api.Tests/Gateway/GatewayResilienceTests.cs` | `429`, upstream timeout và không replay mutation |
| Sửa phần hướng dẫn cần thiết | `README.md` | Cách chạy/kiểm tra Gateway, phân biệt port local `5082` và Compose `5080` |

`appsettings.json`, `.env.example`, `docker-compose.yml`, Dockerfile và `check-skeleton.ps1` là file **đối chiếu**, chỉ sửa nếu kiểm tra phát hiện sai so với hợp đồng đã chốt. Không bắt buộc tạo diff cho đủ danh sách. Không sửa csproj khi không cần package/reference mới.

## 3. Quy ước TDD và checkpoint

- Hành vi mới/sửa lỗi: viết test trước, chạy thấy lỗi hành vi đúng lý do, triển khai tối thiểu, chạy lại. Lỗi syntax/thiếu reference không tính là bằng chứng RED.
- Test đặc tính **đã tồn tại** có thể xanh ngay; đó là characterization test. Không xóa logic đang chạy để ép RED hoặc tự nhận đã làm red-green cho code cũ.
- Test đi qua `GatewayApplication.Build` và cấu hình route thật, không copy pipeline YARP/JWT sang test rồi kiểm tra bản sao.
- Mỗi task kết thúc bằng kiểm tra liên quan, `git diff --check` và checkpoint. Commit message gợi ý nằm cuối task; chỉ commit khi đang thực thi plan và chỉ stage thay đổi thuộc task, dùng hunk selection cho file đã có thay đổi trước đó.

## 4. Các task triển khai

### GW-01: Dùng chung composition root và dựng HTTP fixture

**Files:** Tạo `GatewayApplication.cs`, `GatewayFixture.cs`, `UpstreamStub.cs`, `GatewayStartupTests.cs`; sửa `src/ApiGateway/Program.cs`.

**Interfaces:**

```csharp
// Production: namespace SwiftRide.ApiGateway
public static WebApplication GatewayApplication.Build(WebApplicationBuilder builder);

// Test utilities: namespace SwiftRide.Api.Tests.Gateway
public static Task<GatewayFixture> GatewayFixture.StartAsync(
    string environment = "Development",
    bool stallTripResponse = false,
    IReadOnlyDictionary<string, string?>? overrides = null,
    CancellationToken cancellationToken = default);
public HttpClient GatewayFixture.Client { get; }
public UpstreamStub GatewayFixture.Trip { get; }
public UpstreamStub GatewayFixture.Matching { get; }
public string GatewayFixture.Issuer { get; }
public string GatewayFixture.Audience { get; }
public byte[] GatewayFixture.SigningKey { get; }
public string GatewayFixture.CreateToken(string subject, string role);
public ValueTask GatewayFixture.DisposeAsync();

public static Task<UpstreamStub> UpstreamStub.StartAsync(
    string name, bool stallResponse = false,
    CancellationToken cancellationToken = default);
public Uri UpstreamStub.Address { get; }
public IReadOnlyList<UpstreamRequest> UpstreamStub.Requests { get; }
public Task UpstreamStub.CancellationObserved { get; }
public ValueTask UpstreamStub.DisposeAsync();
public sealed record UpstreamRequest(
    string Method, string Path, string QueryString, bool HasAuthorization);
```

Đây là bảng chữ ký, không phải đoạn C# nguyên khối để dán nguyên vào file. `GatewayFixture` và `UpstreamStub` triển khai `IAsyncDisposable`; fixture tạo key ngẫu nhiên đủ 32 bytes, chỉ giữ trong test process, không ghi ra log. Các property issuer/audience/key dành cho test tự ký token với đúng một thành phần sai. `CancellationObserved` hoàn tất khi request stalled nhận `RequestAborted`, để test chờ hữu hạn thay vì sleep.

Cụ thể với key test: sinh chuỗi Base64 từ 32 random bytes rồi đưa **chuỗi đó** vào `Jwt:SigningKey`. `Fixture.SigningKey` phải là `Encoding.UTF8.GetBytes` của chuỗi cấu hình, đúng cách production đang đọc key; không lấy raw random bytes hoặc decode Base64 để ký token test. Issuer/audience property phản ánh configuration đã merge của host tương ứng.

- [ ] **Bước 1 - Viết characterization test:** `GatewayStartupTests.Health_UsesRealGatewayPipeline_Returns200`. Khởi động Gateway qua seam, `GET /health`, assert `200`, cả hai upstream không nhận request. Thêm `ValidTripRequest_ReachesConfiguredTripUpstream`: token rider hợp lệ, `POST /trips` -> `501` và header marker `X-Upstream: trip`.
- [ ] **Bước 2 - Dựng test infrastructure:** Hai upstream và Gateway bind `http://127.0.0.1:0`, lấy port thực sau `StartAsync`. Fixture tìm repo root qua `SwiftRide.sln`, dùng content root `src/ApiGateway` để load appsettings thật; config test override issuer/audience/key, destination và limiter bằng `AddInMemoryCollection` trên builder riêng. Không sửa env toàn process hoặc dùng port cố định.
- [ ] **Bước 3 - Tạo seam và chạy test:** Di chuyển đăng ký dịch vụ/pipeline/endpoint hiện tại sang `GatewayApplication.Build(WebApplicationBuilder builder)`; `Program.cs` gọi đúng hàm này. Giữ middleware order và error handler. Test-only cleanup/token/stub nằm trong test project, không đưa vào production helper.
- [ ] **Bước 4 - Xác nhận đặc tính sau di chuyển:**

```powershell
dotnet test tests/SwiftRide.Api.Tests/SwiftRide.Api.Tests.csproj --filter FullyQualifiedName~GatewayStartupTests
dotnet test tests/SwiftRide.Api.Tests/SwiftRide.Api.Tests.csproj --filter FullyQualifiedName~GlobalExceptionHandlerTests
```

Kỳ vọng: tất cả test được chọn pass. Mỗi `HttpClient.Timeout = 5s`; startup/stop chờ tối đa `10s`. Dispose cả ba app/client trong `finally`/`await using`, kể cả startup lỗi một phần. Không cần database hoặc Docker.

- [ ] **Bước 5 - Checkpoint:** review diff chỉ là composition/test seam; commit gợi ý `refactor: make gateway startup reusable for integration tests`.

### GW-02: Khóa hợp đồng JWT, startup validation và dev-token

**Files:** Tạo `GatewaySecurityTests.cs`; bổ sung `GatewayStartupTests.cs`; sửa `GatewayApplication.cs` nếu test chỉ ra thiếu validation.

**Interfaces:** Dùng `GatewayApplication.Build` và `GatewayFixture.StartAsync/CreateToken` từ GW-01. Không tạo token service hay options wrapper mới. Các test token sai issuer/audience/signature tự tạo token bằng các JWT types đã có trong package dependency hiện tại, với fixture credentials trong bộ nhớ.

- [ ] **Bước 1 - Viết theory HTTP với assertion cụ thể:**

| Test | Input | Assertion |
|---|---|---|
| `PublicRoute_RejectsInvalidAuthentication` | Không Authorization, bearer sai format, signature sai, issuer sai, audience sai, token hết hạn | `POST /trips` trả `401`; `Trip.Requests` rỗng |
| `DevToken_IssuesUsableRiderAndDriverTokens` | `role=rider` hoặc `driver`, `userId=rider-1/driver-1` | Endpoint trả `200`; dùng token đó gọi public route -> `501` đúng upstream |
| `DevToken_RejectsInvalidIdentityInput` | Role `admin`, user ID trắng, user ID 129 ký tự | `400`; không có `access_token` |
| `Production_DoesNotExposeDevToken` | Host Production, POST `/dev/token?userId=rider-1&role=rider` | `404`; JWT tạo cục bộ vẫn gọi public route được |
| `Startup_RejectsInvalidConfiguration` | Issuer/audience thiếu/trắng; key dưới 32 UTF-8 bytes hoặc toàn khoảng trắng; rate limit `0`, window `0`, queue `-1`; destination thiếu hoặc `ftp://...` | Fail startup bằng lỗi cấu hình có tên trường, không chứa giá trị key |

Token hết hạn fixture phải có `notBefore` trong quá khứ và `expires` cách hiện tại ít nhất 30 phút, tránh nhầm với clock skew mặc định. Case Development token kiểm tra hiệu lực 30 phút bằng claim `exp` trong khoảng cho phép sai số thời gian, không assert chuỗi JWT cố định.

- [ ] **Bước 2 - Chạy test trước thay đổi:**

```powershell
dotnet test tests/SwiftRide.Api.Tests/SwiftRide.Api.Tests.csproj --filter "FullyQualifiedName~GatewaySecurityTests|FullyQualifiedName~GatewayStartupTests"
```

Kỳ vọng các hành vi có sẵn xanh; case key toàn khoảng trắng có thể RED với source hiện tại vì code mới chỉ kiểm tra số bytes. Ghi đúng kết quả chạy, không coi dự đoán này là lỗi đã tái hiện.

- [ ] **Bước 3 - Bổ sung validation tối thiểu nếu RED:** Trong `GatewayApplication.Build`, giữ tối thiểu 32 **UTF-8 bytes**, thêm `string.IsNullOrWhiteSpace(signingKey)`; không trim/biến đổi secret hoặc đưa secret vào exception. Giữ dev-token trong `IsDevelopment()` và chỉ nhận `rider/driver` như hiện tại.
- [ ] **Bước 4 - Chạy lại security/startup tests và cả API test project.** Kỳ vọng không regression ở Problem Details. Không ép các lỗi YARP `502/504` thành Problem Details nếu chúng là response của proxy, không phải exception thoát pipeline.
- [ ] **Bước 5 - Checkpoint:** commit gợi ý `test: verify gateway authentication and development token boundaries`; nếu có sửa guard, mô tả rõ trong commit body.

### GW-03: Kiểm chứng routing, rate limit và timeout bằng proxy thật

**Files:** Tạo `GatewayProxyTests.cs`, `GatewayResilienceTests.cs`; dùng fixture/stub GW-01. Đối chiếu `src/ApiGateway/appsettings.json`; chỉ sửa cấu hình nếu test chứng minh lệch hợp đồng.

**Interfaces:** Fixture/stub từ GW-01. Upstream trả `501` và header `X-Upstream` theo tên; chế độ stall chờ `RequestAborted` và không gửi response. Không dùng fake YARP hoặc mock `HttpClient` để thay đường proxy.

- [ ] **Bước 1 - Viết theory routing:**

| Request có JWT hợp lệ | Kỳ vọng |
|---|---|
| `POST /trips` | `501`, marker `trip` |
| `GET /trips/trip-1?view=summary` | `501`, upstream nhận nguyên method/path/query |
| `POST /trips/trip-1/{action}` với 7 action trong `TripEndpoints.cs` | `501`, marker `trip`, Authorization còn được chuyển tiếp |
| `PUT /drivers/driver-1/location` | `501`, marker `matching`; không gửi tới Trip |
| `GET` hoặc `POST /drivers/driver-1/location` | `404`, không upstream request |
| `POST /match`, `/quote`, `/payments`, `/payments/payment-1/refund` | `404`, không upstream request, cả khi đã có JWT |

Thêm case không JWT cho cả hai public route -> `401`. Marker/captured request chứng minh routing đến đúng upstream, tránh nhận nhầm `404/501` do fake host tự trả.

- [ ] **Bước 2 - Viết test rate limit:** `Requests_OverFixedWindowLimit_Return429WithoutProxying`. Fixture riêng, override `PermitLimit=2`, `WindowSeconds=60`, `QueueLimit=0`; tạo JWT bằng `CreateToken`, không gọi `/dev/token` để tránh tiêu tốn quota. Ba request tuần tự `POST /trips`: `501`, `501`, `429`; upstream nhận đúng hai request. Không chờ hết cửa sổ hoặc dùng sleep 60 giây.
- [ ] **Bước 3 - Viết test timeout:** `StalledMutation_TimesOutWithoutReplay`. Fixture riêng với `stallTripResponse=true`, override `ReverseProxy:Clusters:trip-service:HttpRequest:ActivityTimeout=00:00:00.250`; client có timeout 5 giây. `POST /trips` phải kết thúc bằng `504`, upstream chỉ nhận một request; `Trip.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2))` hoàn tất. Không gửi byte keep-alive từ stub; không diễn giải ActivityTimeout là timeout tổng cho mọi response đang stream.
- [ ] **Bước 4 - Chạy các test mới, sửa đúng phần registration/config nếu cần rồi chạy lại:**

```powershell
dotnet test tests/SwiftRide.Api.Tests/SwiftRide.Api.Tests.csproj --filter "FullyQualifiedName~GatewayProxyTests|FullyQualifiedName~GatewayResilienceTests"
dotnet test tests/SwiftRide.Api.Tests/SwiftRide.Api.Tests.csproj
```

Kỳ vọng tất cả pass. Nếu YARP đang pin có response khác ở một đường lỗi, kiểm tra nguyên nhân thực và ghi hợp đồng chính xác; không nới thành nhận mọi `5xx` hoặc bỏ assertion timeout. Test không tạo automatic retry, không phụ thuộc port/time window của test khác.

- [ ] **Bước 5 - Checkpoint:** commit gợi ý `test: cover gateway proxy boundaries rate limits and timeouts`.

### GW-04: Nghiệm thu khung Gateway và cập nhật cách chạy

**Files:** `README.md`; đối chiếu `docker-compose.yml`, `.env.example`, Dockerfile và `docs/check-skeleton.ps1`. Không đổi status stub hoặc script thành test luồng thanh toán.

**Interfaces:** Dùng route/config đã kiểm chứng ở GW-01..03; không thêm API mới.

- [ ] **Bước 1 - Chạy build và suite hiện có:**

```powershell
dotnet build SwiftRide.sln
dotnet test SwiftRide.sln
git diff --check
```

Kỳ vọng exit code `0`; ghi số test thật và mọi lỗi nếu có. .NET SDK hiện kiểm tra được là `10.0.401` tại `C:\Program Files\dotnet`; không dùng đường dẫn SDK `D:\...` từ báo cáo scaffold cũ. Nếu PATH thay đổi, kiểm tra lại bằng `dotnet --list-sdks`, không nâng SDK/package trong task này.

- [ ] **Bước 2 - Kiểm tra Compose với env local đã chuẩn bị, không in secrets:**

```powershell
docker compose config --quiet
docker compose up --build -d --wait --wait-timeout 180
powershell -NoProfile -ExecutionPolicy Bypass -File docs/check-skeleton.ps1
```

Điều kiện: Docker daemon đang chạy, `.env` có secret local và `ASPNETCORE_ENVIRONMENT=Development`; Gateway health sẵn sàng trước smoke. Kỳ vọng health `200`, thiếu JWT `401`, các stub public có token `501`, internal routes `404`. Nếu daemon/image/network chặn, báo cụ thể phần chưa chạy; config validation không thay container smoke. Không xóa volume hoặc tạo/lưu secret thật trong tài liệu.

- [ ] **Bước 3 - Kiểm tra Production:** Chạy host Production với môi trường được cấu hình riêng, sau đó `docs/check-skeleton.ps1 -Production`. Kỳ vọng `/dev/token` là `404`. HTTP tests ở GW-02 vẫn cần thiết vì script Production không tự tạo bearer token.
- [ ] **Bước 4 - Cập nhật README:** Ghi Gateway skeleton đã được kiểm chứng đến đâu, các lệnh test Gateway, local launch profile `5082` khác Compose `5080`, public/internal route, limiter hiện là một cửa sổ dùng chung. Giữ chính xác giới hạn: nghiệp vụ vẫn `501`, chưa chứng minh quyền chủ chuyến/driver hoặc DB readiness.
- [ ] **Bước 5 - Checkpoint:** commit gợi ý `docs: document verified gateway skeleton and smoke checks`. Chỉ dừng container do lần kiểm thử này tạo nếu cần; không `down -v` và không dừng môi trường đang dùng của người khác.

## 5. Nghiệm thu và bàn giao

- [ ] Gateway dùng một pipeline chung giữa entry point và HTTP test.
- [ ] Public route đi đúng cluster, giữ method/path/query và bearer header.
- [ ] Token sai/thiếu bị chặn; dev-token không tồn tại trong Production.
- [ ] Internal payment/matching endpoints không mở qua Gateway.
- [ ] Rate limit trả `429`, upstream đứng bị timeout; không replay mutation.
- [ ] Existing exception-handler tests còn pass; không thêm package/service/business logic.
- [ ] Build/test được báo theo kết quả thực; Compose smoke được ghi riêng là đã chạy hoặc còn bị chặn.

**Thứ tự khuyến nghị:** hoàn thành GW-01 -> GW-02 -> GW-03 -> GW-04, rồi chuyển sang plan State Pattern. Hai plan không phụ thuộc code của nhau; làm tuần tự giúp tránh trộn thay đổi và dễ review.

**Lúc lập plan:** chỉ đọc source/config/skill và kiểm tra SDK; chưa chạy build, test, container smoke hoặc viết production code. Các lệnh và kết quả kỳ vọng bên trên là hướng dẫn cho bước thực thi sau khi người dùng yêu cầu.
