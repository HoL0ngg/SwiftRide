# MatchDB và PayDB trên Supabase

Ngày triển khai: 26/10/2026. Project: **ATBM HTTT** (`ywcbecxxhktumyqumwdc`).

Theo quyết định của nhóm, Matching và Payment dùng chung PostgreSQL trên Supabase,
nhưng tách schema và quyền theo service. TripDB do thành viên khác phụ trách, không thay đổi trong lần này.
Quyết định này thay thế phần MongoDB cho Matching trong thiết kế mục tiêu cũ.

## Cấu trúc đã triển khai

| Schema | Bảng | Vai trò |
|---|---|---|
| `matchdb` | `driver_locations` | Driver ID dạng chuỗi, latitude/longitude, online và thời điểm cập nhật |
| `matchdb` | `quotes` | Snapshot đầu vào tính giá, giá VND nguyên đồng, thời điểm tạo |
| `paydb` | `payments` | Một khoản thu cho mỗi TripId, idempotency key, SHA-256 fingerprint, phương thức, trạng thái và mã gateway |
| `paydb` | `ledger_entries` | Hai bút toán debit/credit cho mỗi payment, trên hai tài khoản khác nhau |

SQL đã áp dụng được lưu ở [matchdb-paydb.sql](../database/supabase/matchdb-paydb.sql).
Migration trên Supabase có tên `swiftride_matchdb_paydb_mvp`. Đây là SQL khởi tạo một lần,
không chạy lại trên schema đã tồn tại; không phải migration EF Core.

Mỗi payment đóng vai trò một journal trong MVP. Chưa cần bảng journal riêng khi chỉ có thu tiền,
chưa có refund. Ledger không cho UPDATE/DELETE/TRUNCATE. Constraint trigger kiểm tra cuối transaction:
payment settled phải có đúng hai bút toán, cùng tiền/currency với payment, trên hai tài khoản khác nhau.
Pending/failed không được có bút toán. Khoản thu 0 VND vẫn hợp lệ theo invariant giá không âm.
Payload payment bất biến; payment đã settled không được sửa tiếp.

Tiền dùng PostgreSQL `numeric` với CHECK giới hạn 18 chữ số và bắt buộc nguyên đồng,
để từ chối số lẻ thay vì âm thầm làm tròn khi ghi. Timestamp dùng `timestamptz`.
Không có foreign key tới TripDB, không lưu PAN/CVV hoặc token thanh toán trong các bảng này.

## Quyền truy cập

- `swiftride_matching`: role NOLOGIN, chỉ có quyền trên `matchdb`; cập nhật vị trí và thêm/đọc quote.
- `swiftride_payment`: role NOLOGIN, chỉ có quyền trên `paydb`; thêm/đọc payment, cập nhật kết quả và thêm/đọc ledger.
- Bốn bảng đều bật RLS, với policy chỉ dành cho role backend tương ứng.
- Không cấp quyền schema/table/function cho `anon`, `authenticated`, `service_role` hoặc PUBLIC.
- Không cấu hình expose hai schema qua Data API. Client tiếp tục gọi qua YARP Gateway.
- Không thay đổi bảng, quyền hoặc policy của ứng dụng hiện có trong `public`.

Khi tích hợp backend, tạo login riêng cho mỗi service và cấp đúng một role nhóm phía trên.
Đặt mật khẩu bằng kênh quản trị an toàn, lưu connection string trong environment/user-secrets;
không sử dụng tài khoản `postgres` hoặc Supabase service key cho runtime.
NOLOGIN hiện tại là nhóm quyền, chưa phải tài khoản mà .NET có thể đăng nhập.

Backend dùng Npgsql/EF Core và mapping schema tường minh, chẳng hạn
`ToTable("payments", "paydb")`. Không dùng truy vấn xuyên schema của service khác.
HTTP request/message vẫn cần authorization theo rider/driver tại service;
policy backend không thay thế kiểm tra quyền nghiệp vụ.

## Phần backend cần làm tiếp

1. Thay MongoDB adapter của Matching bằng PostgreSQL; thêm mapping, repository và seed demo.
   Lọc driver online và vị trí còn mới, loại driver đã từ chối rồi tính khoảng cách trong Matching.
   Bản nhỏ lưu latitude/longitude, chưa có spatial index; thêm PostGIS khi lượng driver đủ lớn để cần geo-query.
2. Tính giá trong Application/Domain của Matching và lưu snapshot quote, không tính giá bằng trigger.
3. Payment tạo key `trip-{tripId}-payment`, chuẩn hóa payload và tính SHA-256 fingerprint.
   Cùng key/cùng payload trả kết quả cũ; khác payload trả conflict. Unique constraint chỉ ngăn bản ghi trùng,
   chưa tự thực hiện ngữ nghĩa HTTP này hoặc bảo đảm mock gateway không thu lại tiền sau restart.
4. Dùng mock gateway có idempotency; trong một local transaction cập nhật settled và thêm cả hai ledger entries.
   Payment nhận giá từ Trip, không truy vấn quote hoặc tính lại giá. Không đặt external gateway call trong DB transaction kéo dài.
5. Cấu hình connection string có TLS, connection/command timeout và cancellation.
   Cập nhật Compose, cấu hình service và kiểm thử end-to-end sau khi adapter có thể chạy.

Code .NET và `docker-compose.yml` hiện vẫn dùng cấu hình scaffold cũ (Matching/MongoDB, Payment/PostgreSQL local).
Lần này chỉ triển khai cấu trúc dữ liệu trên Supabase, chưa chuyển runtime của hai service.
Chưa thêm refund, Outbox/Inbox, Saga, PostGIS, demo seed hoặc thay đổi TripDB.

## Kiểm chứng

[verify-matchdb-paydb.sql](../database/supabase/verify-matchdb-paydb.sql) đã chạy thành công
trên project thật: upsert/validate tọa độ, insert quote, tách quyền service, chặn payment trùng,
chặn settled thiếu ledger, chặn ledger lệch tiền, cho phép settlement cân bằng,
chặn sửa payment settled và chặn sửa/xóa ledger. Transaction rollback cả fixture và membership role tạm.
Chưa kiểm thử đồng thời, gọi gateway thật hoặc luồng HTTP end-to-end. Không chạy build/test .NET vì chưa sửa code .NET.

Security advisor không báo finding cho hai schema mới. Performance advisor báo index vị trí chưa dùng
vì chưa có traffic; giữ index để lọc driver online theo thời điểm cập nhật.
Các thông báo khác thuộc bảng `public` có sẵn, ngoài phạm vi lần này.

Tài liệu tham khảo: [Postgres roles](https://supabase.com/docs/guides/database/postgres/roles),
[PostGIS](https://supabase.com/docs/guides/database/extensions/postgis),
[unused index advisor](https://supabase.com/docs/guides/database/database-linter?lint=0005_unused_index).
