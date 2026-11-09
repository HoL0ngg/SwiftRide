# Kế hoạch sửa khởi động RabbitMQ cho thanh toán

Mục tiêu: chuyến đi không kẹt PaymentPending do broker thiếu tài khoản hoặc chưa có binding khi publisher hoạt động.

Phạm vi: cấu hình Compose/RabbitMQ, tên exchange bootstrap, hằng số topology, test contract và tài liệu vận hành. Không thay đổi Domain, API, database hoặc giao diện. Không commit/push.

- [x] Xác minh lỗi trên bộ swa: broker không có user, 6 queue không có consumer; RideCompleted đã publish nhưng Supabase không có Inbox/payment.
- [x] Khôi phục tài khoản, replay nguyên sự kiện; xác minh Paid và một khoản thu/sổ cái cân bằng.
- [x] Bỏ import lúc boot để RabbitMQ tạo user từ environment; healthcheck xác thực rồi import topology, xác nhận đủ 6 binding trước khi đánh dấu healthy.
- [x] Cho Trip/Payment chờ broker healthy khi profile messaging được bật; giữ REST mặc định không bắt buộc broker.
- [x] Đổi exchange bootstrap/hằng số sang đúng tên kiểu message MassTransit đang sử dụng; kiểm tra contract và fresh broker khi consumer offline.
- [x] Chạy kiểm tra Compose, 7 test topology, 23 test frontend/store và build solution; cập nhật tài liệu, review độc lập. Giữ dữ liệu/runtime đang hoạt động.
