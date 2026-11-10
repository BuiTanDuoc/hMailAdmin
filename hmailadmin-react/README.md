# hMailAdmin React

Giao diện quản trị hMailServer viết lại bằng React + Vite + TypeScript + Tailwind v4.

## Chạy thử (dữ liệu giả)
    npm install && npm run dev      # VITE_MOCK mặc định bật, đăng nhập bằng bất kỳ tài khoản nào

## Kết nối hMailServer thật
hMailServer chỉ có API COM (`hMailServer.Application`), trình duyệt không gọi được trực tiếp
(bản PHP cũ dùng `php_com_dotnet`). Cần một cầu nối ASP.NET Core chạy trên cùng máy Windows,
tham chiếu COM `hMailServer`, và mở các endpoint sau (JWT Bearer):

| Method | Path | Trả về |
|---|---|---|
| POST | /api/auth/login | `{ token }` (gọi `Application.Authenticate`) |
| GET | /api/stats | `{ sessions[], queue, messages{total,spam,virus} }` |
| GET | /api/domains | `Domain[]` |
| GET | /api/accounts | `Account[]` |
| GET | /api/queue | `QueueItem[]` |
| DELETE | /api/queue/{id} | 204 |

Đặt `VITE_MOCK=false` và `VITE_API_URL` (mặc định `/api`, dev proxy tới `localhost:5080`).
Kiểu dữ liệu nằm ở `src/api.ts`.

## Giấy phép
Bản gốc của coax dùng CC BY-SA 4.0. Dự án này viết lại từ đầu, chỉ tham chiếu danh sách tính năng;
nếu bạn sao chép CSS/HTML/văn bản từ bản gốc thì phải giữ ghi công và cùng giấy phép.
