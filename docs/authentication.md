# Hướng Dẫn Xác Thực & Phân Quyền (OIDC & Development JWT) — SwiftRide

Tài liệu này mô tả chi tiết kiến trúc xác thực và phân quyền trong hệ thống microservices SwiftRide (.NET 10, YARP API Gateway, OpenID Connect / JWT).

---

## 1. Tổng Quan Kiến Trúc Xác Thực

SwiftRide áp dụng mô hình phân quyền tập trung tại **YARP API Gateway** kết hợp với xác thực tài nguyên độc lập tại các **Resource Services** (TripService, MatchingService):

```text
[Client / Mobile / WebApp]
         │
         │ (Bearer JWT Token)
         ▼
 ┌───────────────┐
 │  API Gateway  │ ◄─── Validates JWT via JWKS (RS256) or DevKey (HS256)
 └───────┬───────┘
         │
         ├───► /trips/* ────────► [Trip Service] (Authorizes Rider/Driver & Trip Ownership)
         └───► /locations/* ────► [Matching Service] (Authorizes Rider/Driver)
```

Mọi request từ phía người dùng bắt buộc phải đi qua API Gateway mang theo Bearer Token hợp lệ. Gateway xác thực tính toàn vẹn của token (chữ ký, thời hạn, issuer, audience), áp dụng rate limiting, sau đó chuyển tiếp request kèm thông tin người dùng sang các service nghiệp vụ.

---

## 2. Hai Chế Độ Xác Thực (`Authentication:Mode`)

Hệ thống hỗ trợ 2 chế độ cấu hình linh hoạt thông qua thuộc tính `Authentication:Mode`:

| Thuộc Tính | Chế Độ `DevelopmentJwt` | Chế Độ `Oidc` |
|---|---|---|
| **Mục đích** | Phát triển cục bộ (Local Dev), Unit Test, Demo offline | Môi trường Production, Staging, Live Demo |
| **Thuật toán ký** | HMAC-SHA256 (Khóa đối xứng đối ứng) | RSA RS256 (Khóa bất đối xứng qua JWKS) |
| **Cơ chế xác thực** | `Jwt:SigningKey` (tối thiểu 32 bytes) | Discovery Document (`/.well-known/openid-configuration`) & JWKS |
| **Cổng cấp token dev** | Cho phép `POST /dev/token` (chỉ khi `IsDevelopment`) | **Tuyệt đối không mở `POST /dev/token`** |
| **Yêu cầu HTTPS** | Không bắt buộc | Bắt buộc đối với `Authority` trong Production |
| **Ràng buộc Production** | **CẤM** (Startup sẽ ném ngoại lệ nếu cấu hình) | **BẮT BUỘC** |

---

## 3. Cấu Hình Biến Môi Trường

### 3.1. Cấu hình chế độ Development (`DevelopmentJwt`)
```json
{
  "Authentication": {
    "Mode": "DevelopmentJwt"
  },
  "Jwt": {
    "Issuer": "swiftride-development",
    "Audience": "swiftride-clients",
    "SigningKey": "Replace_With_A_Unique_Key_At_Least_32_Bytes!"
  }
}
```
Hoặc qua biến môi trường trong Docker / Shell:
```bash
AUTHENTICATION__MODE=DevelopmentJwt
JWT_ISSUER=swiftride-development
JWT_AUDIENCE=swiftride-clients
JWT_SIGNING_KEY=Replace_With_A_Unique_Key_At_Least_32_Bytes!
```

### 3.2. Cấu hình chế độ Production (`Oidc`)
```json
{
  "Authentication": {
    "Mode": "Oidc",
    "Authority": "https://auth.swiftride.com/realms/swiftride",
    "Audience": "swiftride-api",
    "RoleClaimType": "role",
    "NameClaimType": "sub",
    "RequireHttpsMetadata": true,
    "MetadataTimeoutSeconds": 5
  }
}
```
Hoặc qua biến môi trường:
```bash
AUTHENTICATION__MODE=Oidc
AUTHENTICATION__AUTHORITY=https://auth.swiftride.com/realms/swiftride
AUTHENTICATION__AUDIENCE=swiftride-api
AUTHENTICATION__REQUIREHTTPSMETADATA=true
AUTHENTICATION__METADATATIMEOUTSECONDS=5
```

> [!IMPORTANT]
> **Quy tắc Fail-Fast khi khởi động:**
> - Nếu `ASPNETCORE_ENVIRONMENT=Production` mà `Authentication:Mode` không phải là `Oidc`, service sẽ lập tức dừng khởi động và ném lỗi `InvalidOperationException`.
> - Nếu `Authentication:Authority` không bắt đầu bằng `https://` trong môi trường Production, service sẽ dừng khởi động.
> - Client timeout cho discovery JWKS được cố định ở `5 giây` để đảm bảo hệ thống fail closed an toàn khi identity provider gặp sự cố, không để request treo vô hạn.

---

## 4. Phân Quyền Vai Trò (RBAC) & Kiểm Soát Tài Nguyên

### 4.1. Vai trò hợp lệ (Role Allowlist)
Hệ thống chỉ chấp nhận 2 vai trò chuẩn:
1. `rider`: Khách đặt xe.
2. `driver`: Tài xế nhận chuyến và cập nhật vị trí.

Các claim vai trò không thuộc allowlist trên (ví dụ `admin`, `guest`, chuỗi rỗng) sẽ bị từ chối quyền truy cập (403 Forbidden). Hệ thống tuyệt đối không tin cậy các role header gửi trực tiếp từ client.

### 4.2. Kiểm soát sở hữu tài nguyên (Resource Ownership Authorization)
- **Luồng Rider:**
  - Khách chỉ được xem danh sách hoặc chi tiết chuyến đi do chính mình đặt (`trip.RiderId == User.GetUserId()`).
  - Khách chỉ được xác nhận báo giá (`confirm-quote`) hoặc hủy chuyến (`cancel`) trên chuyến của mình trước khi tài xế chấp nhận (`DriverAccepted`).
- **Luồng Driver:**
  - Tài xế chỉ được thao tác cập nhật trạng thái (`driver-accept`, `driver-reject`, `en-route`, `pickup`, `dropoff`) trên chuyến đi đã được gán cho chính tài xế đó (`trip.DriverId == User.GetUserId()`).
  - Việc cố tình thao tác lên chuyến của tài xế khác lập tức trả về lỗi `403 Forbidden`.

---

## 5. Kiểm Thử Với `OidcDiscoveryFixture`

Trong bộ kiểm thử tự động, hệ thống sử dụng fixture `OidcDiscoveryFixture` để giả lập identity provider mà không cần phụ thuộc vào internet hay dịch vụ ngoài:
- Tự động sinh cặp khóa RSA 2048-bit động.
- Cung cấp endpoint metadata OIDC chuẩn: `/.well-known/openid-configuration` và `/.well-known/jwks.json`.
- Cung cấp khả năng xoay khóa (Key Rotation): Thêm khóa mới vào JWKS và tạo token ký bằng khóa mới để xác nhận gateway tự động cập nhật khóa mà không cần restart.
- Kiểm thử toàn diện các kịch bản lỗi: Sai Issuer, sai Audience, token hết hạn, chữ ký không tin cậy, thiếu vai trò hợp lệ.

---

## 6. Hướng Dẫn Kết Nối Với Identity Provider Thực Tế (Production Readiness)

Khi triển khai trên môi trường thật với nhà cung cấp OIDC như **Auth0**, **Keycloak**, hoặc **Google Identity**:

1. **Trên Identity Provider:**
   - Tạo một **API Resource** với Identifier (Audience) là `swiftride-api`.
   - Tạo hai Role: `rider` và `driver`.
   - Cấu hình gán role vào Access Token dưới dạng claim `role`.
   - Đăng ký SPA Client cho WebApp SwiftRide (sử dụng Authorization Code Flow with PKCE).
2. **Trên Backend SwiftRide:**
   - Thiết lập `AUTHENTICATION__MODE=Oidc`.
   - Điền `AUTHENTICATION__AUTHORITY` trỏ tới Domain OIDC của provider (ví dụ: `https://dev-swiftride.us.auth0.com/`).
   - Điền `AUTHENTICATION__AUDIENCE=swiftride-api`.
3. **Ghi chú về nghiệm thu:**
   - Trong khuôn khổ đồ án môn học, việc chạy test tự động với `OidcDiscoveryFixture` và demo local bằng `DevelopmentJwt` đảm bảo 100% logic xác thực OIDC và RBAC hoạt động chính xác.
   - Để nghiệm thu live OIDC cần tài khoản thật và domain HTTPS thực tế từ nhà cung cấp bên ngoài.
