# Ngôn ngữ thiết kế giao diện — "Sky Calm"

Tài liệu mô tả phong cách thiết kế của hai ứng dụng web Rider và Driver trong `src/WebApp`.
Cảm hứng: Grab và Be ở cách tổ chức **bản đồ làm trung tâm, thẻ nổi, nút lớn bo tròn**, nhưng dùng bảng màu
**xanh dương nhẹ, sáng, hiện đại** và giọng điệu ngắn gọn, thân thiện.

## 1. Nguyên tắc

- **Map-first:** bản đồ phủ toàn khung nội dung; thông tin và hành động nằm trong một thẻ nổi bên trái (desktop)
  hoặc bottom sheet (dưới 768px).
- **Một hành động chính cho mỗi bước:** nút pill cao tối thiểu 48px, màu primary; hành động phụ dùng nút viền.
- **Dễ đọc:** chữ thân tối thiểu 14px, nhãn phụ tối thiểu 12px, tương phản rõ, focus ring nhìn thấy được,
  tôn trọng `prefers-reduced-motion`.
- **Không dữ liệu giả:** không hiển thị điểm đánh giá, tên hay số liệu mà backend không cung cấp.
- **Giao diện không chứa luật nghiệp vụ:** trạng thái chuyến, giá và thanh toán đến từ API; UI chỉ hiển thị và gửi lệnh.

## 2. Bảng màu (design tokens)

Khai báo trong `src/WebApp/src/styles/tokens.css` bằng `@theme static` của Tailwind v4.
Component dùng token (class Tailwind hoặc `var(--color-*)`), không ghi trực tiếp mã hex.

| Token | Giá trị | Dùng cho |
| --- | --- | --- |
| `brand-500` / `brand-600` | `#3B82F6` / `#2563EB` | Màu chính, hover |
| `brand-50` / `brand-100` | `#F5F9FF` / `#E0EBFA` | Nền, viền, chip |
| `ink-900` / `ink-500` | `#12294A` / `#5B7090` | Chữ chính, chữ phụ |
| `accent` | `#FF9F43` | Điểm đến (dùng tiết kiệm) |
| `success` / `warning` / `danger` | `#16A34A` / `#D97706` / `#DC2626` | Trạng thái |
| `bar` | `#0F2347` | Thanh trên cùng của Driver, marker tài xế |

Hai vai trò có nhận diện khác nhau thông qua thuộc tính `data-app` trên thẻ `<html>`:

- `data-app="rider"` (mặc định): xanh trời sáng, thanh trên cùng trắng.
- `data-app="driver"`: thang `brand` đậm hơn (primary `#1E40AF`) và thanh trên cùng màu navy `bar`.

Marker bản đồ (Leaflet không đọc được CSS variable) dùng các hằng số trong `RideMap.tsx`,
sao chép từ `brand-500`, `accent` và `bar`. Khi đổi token phải đổi cả các hằng số này.

## 3. Hình khối và kiểu chữ

- Font: Be Vietnam Pro (400/500/600/700).
- Bo góc: thẻ 24px (`--radius-card`), control 16px, nút và chip dạng pill.
- Bóng đổ mềm ngả xanh: `--shadow-float` cho thẻ nổi, `--shadow-soft` cho nút phụ.
- Icon: `lucide-react`.

## 4. Bố cục từng ứng dụng

**Rider (`/rider/`)**

- Thanh trên cùng: logo, tab "Đặt xe" / "Lịch sử", trạng thái đăng nhập, avatar.
- Thẻ nổi theo trạng thái chuyến:
  1. Chưa có chuyến: hai ô điểm đón/điểm đến (tìm địa chỉ hoặc chọn trên bản đồ), đổi chiều, chọn Ví/Thẻ, nút "Xem giá chuyến đi".
  2. `Quoted`: thẻ dịch vụ SwiftCar kèm giá, nút "Xác nhận giá & đặt xe", nút "Chọn lại hành trình".
  3. Đang diễn ra: thanh tiến trình 4 bước, thông tin tài xế, nút "Hủy chuyến đi" chỉ khi `Quoted` hoặc `DriverOffered`.
  4. Kết thúc (`Paid`, `Cancelled`, `Failed`): tổng kết và nút "Đặt một chuyến mới".

**Driver (`/driver/`)**

- Thanh trên cùng màu navy: logo kèm nhãn "Tài xế", tab "Chuyến" / "Thu nhập", tổng đã nhận, công tắc Trực tuyến.
- Thẻ nổi: lời mời chuyến (giá lớn, điểm đón/đến, km và phút, "Nhận chuyến" / "Từ chối"),
  nút hành động kế tiếp ("Bắt đầu đến đón" → "Đã đón khách" → "Đã trả khách"), hoặc trạng thái chờ kèm thu nhập.
- Marker tài xế trên bản đồ; bấm bản đồ để đổi vị trí khi Online và chưa nhận chuyến.
- Không có đồng hồ đếm ngược cho lời mời, vì backend không giới hạn thời gian offer.

**Trang chọn vai trò (`/`)**: hai thẻ lớn dẫn tới `/rider/` và `/driver/`.

## 5. Bản đồ

- Tile OpenStreetMap chuẩn (không cần API key), làm dịu màu bằng CSS (`saturate(0.55)`) để giao diện xanh nổi bật.
  Không dùng basemap CARTO vì hiện yêu cầu API key (hiện watermark "API KEY REQUIRED").
- Marker: điểm đón xanh dương, điểm đến cam, tài xế navy.
- Giữ cơ chế phát hiện lỗi tải tile (15 giây), nút "về vị trí chuyến đi" và theo dõi đổi kích thước.

## 6. Cấu trúc mã nguồn

```text
src/WebApp/
├── index.html, rider/index.html, driver/index.html   # ba entry (Vite multi-page)
└── src/
    ├── entries/        # landing.tsx, rider.tsx, driver.tsx (+ shared.ts: font và CSS)
    ├── apps/           # landing/LandingPage, rider/RiderApp, driver/DriverApp
    ├── components/     # RiderView, DriverView, RideMap, AddressSearch, AuthStatus, ui/*
    ├── store/          # tripStore, useTrip (có polling), useTripState (chỉ đọc), useDriverPresence
    ├── services/       # api, auth, tripApi, locations, driverLocation (không đổi)
    ├── utils/          # money, driverTrips
    └── styles/         # tokens.css, base.css, components.css, app.css
```

Quy ước polling: **chỉ shell của app** (`RiderApp`, `DriverApp`) gọi `useTrip` để khởi động polling;
các view dùng `useTripState` (chỉ đọc), nên việc view bị gỡ khi đổi tab không dừng nhầm vòng polling.
