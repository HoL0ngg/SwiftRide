# Demo SwiftRide bằng Postman và RabbitMQ Management

## Chuẩn bị

Import ba collection trong `postman/`:

- `SwiftRide.Gateway.Demo.postman_collection.json`: các kịch bản nghiệp vụ, mặc định thanh toán Events.
- `SwiftRide.RabbitMQ.Observe.postman_collection.json`: thao tác quản trị để giữ bản sao event và xem payload.
- `SwiftRide.Gateway.Smoke.postman_collection.json`: smoke cũ đã sửa chờ tài xế khi Matching bất đồng bộ; vẫn chọn thanh toán REST để giữ cách chạy cũ.

Đặt cấu hình local trong `.env`, giữ nguyên các secret/database credential hợp lệ đang có:

```dotenv
ASPNETCORE_ENVIRONMENT=Development
MESSAGING_TRANSPORT=RabbitMQ
RABBITMQ_HOST=rabbitmq
TRIP_LIFECYCLE_MATCHING_TRANSPORT=Events
TRIP_LIFECYCLE_MAX_DRIVER_REJECTIONS=3
```

```powershell
docker compose --profile messaging config --quiet
docker compose --profile messaging up --build -d
```

`Development` cần áp dụng cho Gateway (lấy token demo) và Trip (cho phép mô phỏng thanh toán). Không dùng trên môi trường thật. Client vẫn chỉ gọi Gateway `http://localhost:5080`; collection Observe gọi Management API với vai trò operator, không gọi API nội bộ của service.

Import `postman/SwiftRide.Local.postman_environment.json`, chọn environment đó rồi điền `rabbitmq_user` và `rabbitmq_password` bằng tài khoản broker local. Không export/chia sẻ environment sau khi điền credential. Các token JWT được giữ bằng `pm.variables` trong lần chạy, không được collection lưu vào environment.

## Quan sát message mà không dừng service

1. Trong collection **SwiftRide RabbitMQ Observe**, chạy **chỉ folder `01 Setup - run before demo`**. Folder tạo queue `demo-observe-postman` và bind vào cả 13 exchange nghiệp vụ. Chọn tên `demo-observe-<ten-rieng>` nếu nhiều người cùng demo.
2. Mở <http://localhost:15672/>, đăng nhập, chọn **Queues and Streams → demo-observe-postman**.
3. Chạy một folder nghiệp vụ trong **SwiftRide Gateway Demo** bằng Collection Runner.
4. Quay lại queue quan sát, mở **Get messages**, chọn chế độ **requeue** (`Ack message requeue true` nếu giao diện dùng nhãn này), rồi **Get Message(s)**. Có thể chạy request `15 Read captured messages (requeue)` trong Postman thay cho UI.
5. Xem `exchange`, `messageType` và payload trong `message`. Dùng `tripId` để đối chiếu với response của chuyến vừa tạo. Trong Postman mở **Console** để xem tóm tắt event.
6. Sau khi xem xong, chạy **chỉ folder `03 Cleanup - run after inspection`** để xóa queue quan sát. Không xóa queue nghiệp vụ hoặc volume.

**Exchange không lưu message.** Tab Exchanges cho thấy exchange, binding và tốc độ publish. Muốn đọc payload đã phát phải có queue nhận từ trước. Queue quan sát nhận bản sao, không lấy message khỏi queue nghiệp vụ. Tạo queue sau khi event đã phát sẽ không lấy lại lịch sử.

Queue quan sát không có consumer nên message nằm lại để xem, tối đa một giờ (`x-message-ttl`); queue tự hết hạn sau một giờ không được sử dụng (`x-expires`). Không thêm vào definitions hay kiến trúc triển khai chính. Khi chạy cả collection Observe liền mạch, Cleanup sẽ xóa queue ngay; vì vậy chạy từng folder như trên.

Mỗi lần đọc Postman lấy tối đa 100 message và requeue. Nếu queue nhiều hơn 100 message, xem tiếp qua UI hoặc tạo queue riêng cho lần demo mới; đọc lại request không phải cơ chế phân trang lịch sử. Payload là mock; không đưa token thật/card/CVV vào message.

## Chạy các kịch bản nghiệp vụ

Chạy **toàn bộ một folder** trong Runner, **1 iteration**, **delay 1000 ms**, **request timeout 20000 ms**. Có thể chạy toàn collection theo thứ tự. Script `setNextRequest` chỉ điều khiển luồng trong Runner/CLI, không tự polling khi bấm Send đơn lẻ. Mỗi polling có tối đa 60 lần; thất bại assertion thì dừng run.

| Folder | Thao tác và kết quả mong đợi | Event nổi bật khi Matching/Payment dùng Events |
|---|---|---|
| `01 Success...` | Tạo → quote → confirm → driver accept → en-route → pickup → dropoff → `Paid`; kiểm tra replay, conflict, sai quyền, pickup sai thứ tự và hủy quá muộn | `QuoteRequested`, `QuoteReady`, `DriverMatchRequested`, `DriverMatched`, `TripAccepted`, `RideCompleted`, `PaymentSettled` |
| `02 No driver...` | Điểm đón không có tài xế → `Failed`, trả lại slot rider | `QuoteRequested`, `QuoteFailed` |
| `03 Cancel quoted` | Rider từ chối giá bằng cancel khi `Quoted` → `Cancelled`; replay cancel không tạo tác dụng phụ | `QuoteRequested`, `QuoteReady`; không có event cancel riêng |
| `04 Cancel offered` | Đã mời tài xế, chưa accept → cancel được; trả slot rider/driver | Có thêm cặp driver match; không có event cancel riêng |
| `05 Driver rejects...` | Hai tài xế online; người đầu từ chối → người khác được mời → hoàn thành `Paid` | Thêm cặp `DriverMatchRequested`/`DriverMatched`, giá không đổi |
| `06 Rejection limit...` | Bốn tài xế online; từ chối lần 1–3 rematch, lần 4 → `Failed` khi `k=3` | Các cặp driver match; lần từ chối vượt k do Trip tự xử lý, không cần event fail riêng |
| `07 Payment declined...` | Mock gateway từ chối; Trip giữ `PaymentPending`, replay dropoff không đổi giá/chuyến | `RideCompleted` rồi `PaymentFailed` với `failureCode=payment_method_declined`, `isPermanent=true` |
| `08 Payment timeout...` | Mock gateway trả lỗi tạm thời; Trip giữ `PaymentPending`; consumer retry có giới hạn | `RideCompleted`; xem retry/log/error queue; không yêu cầu có `PaymentFailed` ngay |
| `09 Driver rejects...` | Chỉ một tài xế; từ chối rồi không còn người thay thế → `Failed`, không cần chờ vượt k | `DriverMatchRequested`, `DriverMatchFailed` sau lần từ chối |
| `10 Invalid requests...` | Thiếu idempotency key, tọa độ sai, mock outcome sai → `400`; rider cập nhật driver → `403`; chuyến không tồn tại → `404` | Request bị từ chối trước khi phát event |

Collection dùng rider/driver mới theo UUID cho mỗi folder. Tọa độ demo ở kinh độ −140 và các vĩ độ riêng, cách xa seed TP.HCM để kiểm soát kết quả; folder không có tài xế dùng vĩ độ 20. Không tắt tài xế hiện có. Chạy tuần tự, không chạy hai lần cùng một folder đồng thời. Nếu database có tài xế khác tại chính vùng demo, kết quả có thể khác; dùng database demo riêng để cô lập hoàn toàn.

Vị trí tài xế mặc định hết hạn sau 5 phút. Nếu dừng thuyết trình lâu trước confirm/rematch, chạy lại folder từ đầu hoặc cập nhật lại vị trí. Nếu assertion thất bại giữa chừng, bước offline cuối folder chưa chắc đã chạy; các tài xế demo sẽ tự hết hiệu lực matching khi vị trí cũ.

Đặt environment `payment_transport=Rest` nếu muốn thử cùng các kịch bản bằng thanh toán REST. Matching vẫn do cấu hình server quyết định, không do collection quyết định. Chỉ bật RabbitMQ không chứng minh một chuyến đang dùng Events; bản sao `QuoteRequestedV1` và `RideCompletedV1` mang đúng `tripId` là bằng chứng rõ hơn.

## Mô phỏng thanh toán và giới hạn kiểm chứng

API tạo chuyến nhận thêm trường tùy chọn **chỉ trong Development**:

```json
{ "demoPaymentOutcome": "declined" }
```

Các giá trị hợp lệ: `success`, `declined`, `timeout`; bỏ trường này vẫn thành công như trước. Đây là phần bổ sung vào body tạo chuyến đầy đủ, không phải payload tạo chuyến độc lập. Trip ánh xạ sang alias mock của Card/Wallet, không nhận payment token tùy ý từ client. Gửi trường này ngoài Development hoặc giá trị khác trả `400`. Cùng idempotency key nhưng đổi từ success sang declined/timeout phải conflict; không dùng replay để đổi kết quả một chuyến cũ.

**HTTP `PaymentPending` chỉ chứng minh chuyến chưa được xác nhận Paid.** Folder 07/08 không tự khẳng định Payment đã nhận message, không double-charge hoặc đã hết retry chỉ từ response này:

- Với declined, tìm đúng `PaymentFailedV1` có `tripId` tương ứng trong queue quan sát. Đây là lỗi nghiệp vụ được xử lý thành công, không nhất thiết đi vào `_error`.
- Với timeout, mock trả lỗi transient; `RideCompletedConsumer` có thể ném exception để retry. Chờ chính sách retry hiện hành rồi kiểm tra `payment-ride-completed_error`, hoặc log consumer và trạng thái xử lý. Không tự phát `PaymentSettledV1` để kết thúc demo.
- Chứng minh không double-charge và ledger cân bằng cần kiểm tra payment/mock-operation/ledger trong database sở hữu hoặc integration tests; collection không truy cập DB chéo và không mở `/payments` ra Gateway.

Hai folder lỗi cố ý để lại chuyến `PaymentPending`; đây không phải terminal. Rider tương ứng vẫn có chuyến hoạt động. Các lần chạy sau dùng rider mới. Không dọn bằng cách sửa `Paid`, xóa ledger hoặc tạo chuyến khác với cùng rider để lách trạng thái. Refund/Saga và poison message được hướng dẫn vận hành trong `messaging-operations.md`; collection này không giả lập business event để ép bù trừ.

## Xem message trong queue nghiệp vụ nếu muốn trình bày từng bước

Ưu tiên queue quan sát vì không gián đoạn xử lý. Nếu cần thấy message thực sự đang chờ ở queue nghiệp vụ, trong môi trường demo riêng:

```powershell
# Dừng Payment trước khi gửi bước dropoff trong Postman
docker compose --profile messaging stop payment-service
# Dropoff trả PaymentPending; queue payment-ride-completed sẽ có message Ready
# Xem bằng Get messages với requeue=true rồi khởi động lại:
docker compose --profile messaging start payment-service
```

Tương tự, dừng `matching-service` trước bước Create để thấy `matching-quote-requested`; Trip vẫn trả `202 Requested`. Khởi động lại Matching trước khi hết polling. Với thao tác từng bước, phải giữ token/ID của run; dễ nhất giữ Runner tại thời điểm quan sát hoặc dùng queue bản sao để khỏi phải dừng run. Không dừng Trip khi cần gửi HTTP dropoff qua Gateway. Không purge hoặc ack-delete message nghiệp vụ chỉ để xem.

## Kiểm tra file và script

```powershell
node --test postman/collection.test.mjs postman/demo.test.mjs
postman collection run postman/SwiftRide.Gateway.Demo.postman_collection.json -i "01 Success, idempotency and authorization" --delay-request 1000 --timeout-request 20000 --no-report-events
```

Lệnh Node chỉ kiểm tra script/fixture, không chứng minh backend hay broker hoạt động. CLI chạy collection local với `--no-report-events` để không gửi kết quả demo lên Postman cloud.
