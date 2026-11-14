# Kế hoạch bổ sung demo Postman và RabbitMQ

**Mục tiêu:** Demo qua Gateway các luồng thành công, không có tài xế, hủy chuyến, từ chối tài xế, thanh toán declined/timeout và quan sát message trên Management UI.

**Phạm vi:** Giữ ba service và topology hiện tại. Bổ sung `demoPaymentOutcome` chỉ ở Development, không nhận token từ client. Giá và idempotency vẫn do service sở hữu. Không commit/push.

- [ ] Viết test cho lựa chọn mock, fingerprint và chặn môi trường ngoài Development; chạy để thấy lỗi trước khi triển khai.
- [ ] Bổ sung lựa chọn mock vào API/Application, không đổi Domain, DB hoặc event contract.
- [ ] Sửa smoke collection chờ driver khi matching bất đồng bộ; thêm demo collection theo folder và collection quan sát RabbitMQ riêng với credential nhập lúc chạy.
- [ ] Kiểm tra script collection bằng Node, chạy build và test .NET liên quan. Chạy HTTP thực tế khi môi trường cho phép; phân biệt rõ với kiểm thử script.
- [ ] Viết hướng dẫn import/Runner, cấu hình Events, cách xem payload bằng observer queue và cách tạm dừng consumer. Ghi rõ PaymentPending không tự chứng minh declined, và các chuyến lỗi cần can thiệp vận hành.

**Lựa chọn triển khai:** Mỗi kịch bản dùng rider/driver riêng và tọa độ demo biệt lập để không tắt tài xế có sẵn. Không xóa ledger/dữ liệu chuyến để dọn demo. Các queue quan sát không có business consumer, cần xóa sau demo.
