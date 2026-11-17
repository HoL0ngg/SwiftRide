# Kế hoạch thêm loại xe

Giữ `StandardPricingStrategy`, `NightPricingStrategy` và chuỗi Decorator. Loại xe
`motorbike`/`car` chọn đơn giá trước khi strategy xử lý. Dữ liệu/request cũ mặc định
`motorbike`; không đổi logic Payment. Không commit/push tự động.

- [x] Matching: cấu hình đơn giá, quote snapshot, vị trí tài xế, lọc đúng loại xe;
  REST/event, SQL cập nhật schema và test nghiệp vụ.
- [x] Trip: nhận/lưu/trả loại xe, fingerprint idempotency, truyền qua quote/match,
  rematch và phục hồi REST/event; EF migration và test.
- [x] WebApp: chọn loại xe phía rider/driver, gửi request và giữ lựa chọn từ server;
  test giao diện/API/heartbeat.
- [x] Điều phối: cập nhật event contracts, docs; review chéo toàn bộ luồng và kiểm tra
  build/test khả dụng. Báo rõ giới hạn môi trường.

Phân công: `matching_vehicle`, `trip_vehicle`, `ui_vehicle` sửa các vùng riêng;
agent gốc cập nhật contracts/docs và tích hợp, reviewer độc lập kiểm tra cuối.

Đã bổ sung test và review tĩnh; reviewer phát hiện khả năng đổi loại xe khi có
lời mời, đã sửa khóa lựa chọn. Build/test chưa thực thi được vì máy không có
.NET SDK, Node/npm hay Docker. Chưa áp dụng SQL vào database thật.
