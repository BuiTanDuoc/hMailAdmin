# hMailAdmin React

Giao diện quản trị **hMailServer** viết lại bằng **React + Vite + TypeScript + Tailwind CSS v4**, thay cho bản PHP [hmailserver-webadmin](https://github.com/coax/hmailserver-webadmin). Dự án gồm hai phần:

| Phần | Công nghệ | Vai trò |
|---|---|---|
| `hmailadmin-react/` | React 18, Vite 6, TypeScript, Tailwind v4 | Giao diện (SPA), tiếng Việt |
| `HMailBridge/` | ASP.NET Core 8 (minimal API) | Cầu nối: gọi COM của hMailServer, xác thực JWT, phục vụ luôn giao diện đã build |

## 1. Kiến trúc

hMailServer chỉ có **API COM** (`hMailServer.Application`), trình duyệt không gọi trực tiếp được. Bản PHP cũ gọi COM qua `php_com_dotnet`; bản này dùng một cầu nối ASP.NET Core chạy **trên cùng máy Windows** với hMailServer.

```
Trình duyệt ──HTTPS──▶ Reverse proxy (IIS / nginx)
                          │ http://127.0.0.1:5080
                          ▼
                    HMailBridge (ASP.NET Core)
                     ├─ /api/...  ──COM──▶ hMailServer
                     ├─ đọc file log, file chứng chỉ, file thư trong hàng đợi
                     └─ /  (wwwroot)  ──▶ giao diện React đã build
```

Điểm cần nhớ:

- Mỗi đối tượng COM mới chưa được đăng nhập, nên bridge **giữ thông tin đăng nhập của lần đăng nhập thành công gần nhất trong bộ nhớ** (không ghi ra đĩa) và xác thực lại cho mỗi lần gọi COM.
- Khởi động lại bridge là mất thông tin đó: giao diện sẽ tự đưa bạn về trang đăng nhập.
- Bridge có **toàn quyền quản trị** hMailServer. Xem mục [Bảo mật](#8-bảo-mật).

## 2. Tính năng

Thanh bên chia nhóm, bấm tiêu đề để thu gọn hoặc mở (trạng thái được nhớ trong trình duyệt).

| Nhóm | Trang |
|---|---|
| **Chính** | Tổng quan (số liệu, biểu đồ phiên, tự làm mới 15 giây) · Hàng đợi gửi (xem nội dung thư, gửi ngay, xoá từng thư) · Nhật ký (phân tích log) · Live logging |
| **Tên miền & tài khoản** | Tên miền · Tài khoản (lọc theo tên miền) · Alias · Danh sách phân phối |
| **Luồng thư** | Rules · Routes · Incoming relays |
| **Chống thư rác & tấn công** | Chống tấn công (auto-ban, greylisting) · IP ranges · DNS blacklists · SURBL · Greylisting whitelist |
| **Kết nối & bảo mật** | TCP/IP ports · Chứng chỉ SSL |
| **Hệ thống** | Cài đặt (chung, SMTP, giao thức, POP3/IMAP, chống thư rác, ClamAV, ghi log) · Tiện ích (dừng/khởi động, sao lưu, kiểm tra blacklist) · Chẩn đoán tên miền |

Tất cả bảng đều có phân trang (10/20/50/100 dòng).

## 3. Yêu cầu

**Máy chủ chạy hMailServer (Windows):**

- hMailServer đã cài và đang chạy; ProgID COM `hMailServer.Application` phải được đăng ký (có sẵn khi cài hMailServer).
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) để build, hoặc chỉ cần ASP.NET Core Runtime 8 (Hosting Bundle) để chạy bản đã publish.
- Truy cập mạng tới nuget.org lần đầu để khôi phục gói (`DnsClient`, `Microsoft.AspNetCore.Authentication.JwtBearer`).
- Tài khoản chạy bridge phải có **quyền DCOM** gọi hMailServer, và quyền **đọc** thư mục Logs, thư mục Data (xem thư trong hàng đợi) và các file chứng chỉ.

**Máy build giao diện** (có thể là máy khác): Node.js 18 trở lên (khuyến nghị 20 LTS).

## 4. Cấu trúc thư mục

```
hMailAdmin/
├─ README.md
├─ HMailBridge/
│  ├─ HMailBridge.csproj
│  ├─ appsettings.json
│  └─ Program.cs                # toàn bộ API, ánh xạ COM, đọc log, chẩn đoán
└─ hmailadmin-react/
   ├─ package.json, vite.config.ts, tsconfig.json, index.html
   └─ src/
      ├─ main.tsx, index.css
      ├─ api.ts                 # kiểu dữ liệu, client, dữ liệu giả (mock)
      └─ App.tsx                # tất cả trang và thanh bên
```

## 5. Chạy thử nhanh (không cần hMailServer)

Giao diện có sẵn **chế độ dữ liệu giả**, mặc định bật:

```powershell
cd hmailadmin-react
npm install
npm run dev
```

Mở `http://localhost:5173`, đăng nhập bằng tên và mật khẩu bất kỳ. Mọi thao tác thêm/sửa/xoá chỉ lưu trong bộ nhớ trình duyệt.

## 6. Triển khai thật

Giả sử thư mục dự án là `D:\HmailServerAdmin\`, thực hiện **trên máy chủ hMailServer**.

### 6.1 Build giao diện

Biến `VITE_MOCK` được gắn cứng vào bản build, nên phải đặt `false` **trước** khi build:

```powershell
cd D:\HmailServerAdmin\hmailadmin-react
"VITE_MOCK=false" | Out-File .env.local -Encoding ascii
npm install
npm run build
```

Chép kết quả vào `wwwroot` của bridge (tạo thư mục nếu chưa có):

```powershell
mkdir D:\HmailServerAdmin\HMailBridge\wwwroot -Force
Copy-Item D:\HmailServerAdmin\hmailadmin-react\dist\* D:\HmailServerAdmin\HMailBridge\wwwroot -Recurse -Force
```

### 6.2 Cấu hình bridge

Sửa `HMailBridge\appsettings.json` nếu cần:

```json
{
  "Urls": "http://127.0.0.1:5080",
  "Jwt": { "Key": "" },
  "Logs": { "Directory": "C:\\Program Files (x86)\\hMailServer\\Logs" }
}
```

- `Urls`: giữ `127.0.0.1` để chỉ máy này truy cập được; reverse proxy sẽ đứng phía trước.
- `Logs:Directory`: thư mục log của hMailServer (đổi nếu bạn cài ở chỗ khác).
- `Jwt:Key`: **không ghi vào file**, truyền bằng biến môi trường `Jwt__Key` (từ 32 ký tự). Bridge không khởi động nếu thiếu.

Tạo khoá ngẫu nhiên:

```powershell
$b = New-Object byte[] 48
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
[Convert]::ToBase64String($b)
```

### 6.3 Chạy thử bằng `dotnet run`

```powershell
cd D:\HmailServerAdmin\HMailBridge
$env:Jwt__Key = "<chuỗi vừa tạo>"
dotnet run
```

Mở `http://127.0.0.1:5080` ngay trên máy chủ và đăng nhập bằng tài khoản **`Administrator`** cùng mật khẩu quản trị hMailServer. Cảnh báo `The WebRootPath was not found` nghĩa là bạn chưa chép giao diện vào `wwwroot` (bước 6.1).

### 6.4 Publish và chạy như dịch vụ Windows

```powershell
cd D:\HmailServerAdmin\HMailBridge
dotnet publish -c Release -o D:\HmailServerAdmin\publish
```

`wwwroot` được đưa vào bản publish, nên nhớ chép giao diện (6.1) **trước** khi publish. Dùng [NSSM](https://nssm.cc/) để chạy nền (bridge không tự đăng ký làm dịch vụ Windows):

```powershell
nssm install HMailBridge "D:\HmailServerAdmin\publish\HMailBridge.exe"
nssm set HMailBridge AppDirectory "D:\HmailServerAdmin\publish"
nssm set HMailBridge AppEnvironmentExtra "Jwt__Key=<chuỗi khoá>"
nssm set HMailBridge ObjectName ".\<tài khoản có quyền DCOM>" "<mật khẩu>"
nssm set HMailBridge Start SERVICE_AUTO_START
nssm start HMailBridge
```

- `AppDirectory` bắt buộc, vì bridge tìm `wwwroot` theo thư mục làm việc.
- Nếu gặp lỗi COM `Access denied (0x80070005)`, tài khoản chạy dịch vụ chưa có quyền DCOM: cấp quyền Launch/Access cho tài khoản đó trên `hMailServer` (Component Services → DCOM Config), hoặc chạy bằng tài khoản quản trị.
- Sau mỗi lần dịch vụ khởi động lại, hãy **đăng nhập lại** trên giao diện.

### 6.5 Đặt HTTPS phía trước

Không mở cổng 5080 ra Internet. Cho một reverse proxy nhận HTTPS ở cổng 443 rồi chuyển về `http://127.0.0.1:5080`. Hướng dẫn đầy đủ để công khai ra Internet với tên miền riêng và chứng chỉ SSL (Let's Encrypt) nằm ở [6.8](#68-công-khai-ra-internet-với-tên-miền-riêng-và-https).

**nginx:**

```nginx
server {
    listen 443 ssl;
    server_name admin.example.com;
    ssl_certificate     C:/ssl/fullchain.pem;
    ssl_certificate_key C:/ssl/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        client_max_body_size 1m;
    }
}
```

**IIS:** cài *URL Rewrite* và *Application Request Routing*, bật proxy, tạo rule rewrite `(.*)` → `http://127.0.0.1:5080/{R:1}`, gắn chứng chỉ vào binding HTTPS của site.

Khuyến nghị thêm: chỉ cho phép dải IP của người quản trị (hoặc chỉ truy cập qua VPN), và tường lửa Windows chỉ mở cổng 443.

### 6.6 Kiểm tra sau triển khai

1. Mở trang bằng HTTPS, đăng nhập bằng `Administrator`.
2. **Tổng quan** hiện số thư đã xử lý; biểu đồ phiên cần khoảng 1 phút sau khi đăng nhập mới có đủ điểm.
3. **Tên miền** và **Tài khoản** hiện đúng dữ liệu.
4. **Nhật ký** liệt kê được file log; **Live logging** có dòng mới khi bấm Bắt đầu (cần bật ghi log ở Cài đặt → Nhật ký).
5. Thử từng trang mới một trên môi trường ít rủi ro (xem [mục 10](#10-mức-độ-kiểm-chứng-và-hạn-chế)).

### 6.7 Cập nhật phiên bản

```powershell
nssm stop HMailBridge
# sao lưu thư mục publish cũ, rồi thay HMailBridge.exe/DLL và wwwroot bằng bản mới
nssm start HMailBridge
```

Sau đó đăng nhập lại. Giao diện mới chỉ cần chép lại `dist\*` vào `wwwroot`; bridge không cần khởi động lại nếu `Program.cs` không đổi.

### 6.8 Công khai ra Internet với tên miền riêng và HTTPS

Mục tiêu: mở `https://admin.tenmien.vn` từ bên ngoài, chứng chỉ SSL cấp **riêng cho tên miền đó**, còn bridge vẫn chỉ nghe ở `127.0.0.1:5080`. Thứ tự: DNS → mạng/tường lửa → reverse proxy kèm chứng chỉ → kiểm tra → siết bảo mật.

> **Trước khi công khai:** bridge có toàn quyền quản trị hMailServer. Hãy dùng bản bridge có giới hạn đăng nhập **theo từng IP**, mật khẩu `Administrator` dài và duy nhất, và ưu tiên hạn chế IP truy cập hoặc chỉ cho vào qua VPN (bước 6).

**Bước 1. Chọn tên miền phụ và trỏ DNS**

Dùng một tên riêng, ví dụ `admin.tenmien.vn` (không dùng chung `mail.tenmien.vn` của mail server). Tại nhà cung cấp DNS tạo bản ghi:

| Loại | Tên | Giá trị |
|---|---|---|
| A | `admin` | IP công khai của máy chủ |
| AAAA | `admin` | IPv6 công khai (chỉ khi máy chủ thật sự có và đã mở IPv6) |

Kiểm tra: `nslookup admin.tenmien.vn` phải trả về đúng IP. Nếu IP công khai của bạn thay đổi, dùng DDNS hoặc IP tĩnh.

**Bước 2. Mở cổng 80 và 443, không mở 5080**

- Trên router/NAT hoặc firewall của nhà cung cấp máy chủ: chuyển tiếp TCP **80** và **443** về máy chủ. Cổng 80 cần để cấp chứng chỉ Let's Encrypt (HTTP-01) và chuyển hướng sang HTTPS.
- Trên Windows Firewall:

```powershell
New-NetFirewallRule -DisplayName "Web 80 (ACME + redirect)" -Direction Inbound -Protocol TCP -LocalPort 80 -Action Allow
New-NetFirewallRule -DisplayName "Web 443 (hMailAdmin)" -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow
```

- Kiểm tra cổng 80/443 chưa bị chiếm: `netstat -ano | findstr ":80 :443"`. IIS Default Web Site hoặc dịch vụ khác đang giữ cổng thì phải dừng hoặc chuyển.
- Một số nhà mạng gia đình chặn 80/443. Khi đó dùng cổng khác kèm xác thực DNS-01, hoặc Cloudflare Tunnel (không cần mở cổng vào).

**Bước 3. Cài reverse proxy có chứng chỉ SSL theo tên miền**

Chọn **một** trong hai cách. Cách A ít bước nhất.

*Cách A: Caddy (tự xin và tự gia hạn chứng chỉ Let's Encrypt)*

1. Tải `caddy_windows_amd64.exe` từ caddyserver.com, đặt vào `C:\caddy\caddy.exe`.
2. Tạo `C:\caddy\Caddyfile`:

```
admin.tenmien.vn {
    encode gzip
    reverse_proxy 127.0.0.1:5080
    header {
        -Server
    }
}
```

3. Chạy thử: `C:\caddy\caddy.exe run --config C:\caddy\Caddyfile`. Lần đầu Caddy tự xin chứng chỉ cho đúng tên `admin.tenmien.vn`, tự chuyển HTTP sang HTTPS và gia hạn trước khi hết hạn.
4. Chạy nền bằng NSSM:

```powershell
nssm install Caddy "C:\caddy\caddy.exe" run --config C:\caddy\Caddyfile
nssm set Caddy AppDirectory C:\caddy
nssm start Caddy
```

Caddy lưu chứng chỉ trong thư mục dữ liệu của tài khoản chạy dịch vụ; nên sao lưu thư mục đó.

*Cách B: IIS + win-acme (nếu máy đã dùng IIS)*

1. Cài IIS, *URL Rewrite* và *Application Request Routing*; trong ARR chọn **Enable proxy**.
2. Tạo site, binding HTTP cổng 80 với host name `admin.tenmien.vn`, thư mục bất kỳ.
3. Trong `web.config` của site:

```xml
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="HTTPS redirect" stopProcessing="true">
          <match url="(.*)" />
          <conditions><add input="{HTTPS}" pattern="off" /></conditions>
          <action type="Redirect" url="https://{HTTP_HOST}/{R:1}" redirectType="Permanent" />
        </rule>
        <rule name="Proxy to bridge" stopProcessing="true">
          <match url="(.*)" />
          <action type="Rewrite" url="http://127.0.0.1:5080/{R:1}" />
        </rule>
      </rules>
    </rewrite>
  </system.webServer>
</configuration>
```

4. Chạy [win-acme](https://www.win-acme.com/) (`wacs.exe`), chọn tạo chứng chỉ cho binding IIS của `admin.tenmien.vn`. Công cụ tự tạo binding HTTPS và đặt tác vụ gia hạn tự động.

*Dùng chứng chỉ có sẵn (wildcard hoặc thương mại)*: chứng chỉ phải có tên `admin.tenmien.vn` hoặc `*.tenmien.vn` trong SAN. Chứng chỉ của mail server (ví dụ chỉ có `mail.tenmien.vn`) **không dùng được**, trình duyệt sẽ báo sai tên. Trang **Chứng chỉ SSL** trong ứng dụng chỉ quản lý chứng chỉ của hMailServer, không phải chứng chỉ của web này.

**Bước 4. Giữ bridge chỉ nghe nội bộ**

Giữ `"Urls": "http://127.0.0.1:5080"`. Reverse proxy nằm cùng máy nên bridge tin tiêu đề `X-Forwarded-For` từ chính proxy và dùng IP thật của người dùng để giới hạn đăng nhập (5 lần/phút cho mỗi IP). Chỉ bridge bản mới mới có hành vi này.

**Bước 5. Kiểm tra từ bên ngoài**

Dùng điện thoại với 4G, hoặc máy ở mạng khác:

1. Mở `https://admin.tenmien.vn`: khoá hợp lệ, đúng tên miền, đăng nhập được.
2. Mở `http://admin.tenmien.vn`: tự chuyển sang HTTPS.
3. `Test-NetConnection <IP-công-khai> -Port 5080` phải **không** kết nối được.
4. Thử sai mật khẩu 6 lần liên tiếp: lần thứ 6 phải bị chặn (429) và chỉ chặn IP đó.
5. Kiểm tra cấu hình TLS, ví dụ bằng SSL Labs; xác nhận lịch gia hạn chứng chỉ (Caddy tự lo; win-acme tạo tác vụ trong Task Scheduler).

**Bước 6. Siết bảo mật (nên làm)**

- **Hạn chế IP:** chỉ cho dải IP của người quản trị vào cổng 443.

```powershell
Set-NetFirewallRule -DisplayName "Web 443 (hMailAdmin)" -RemoteAddress 203.0.113.10,198.51.100.0/24
```

  Giữ cổng 80 mở cho mọi nơi để cấp/gia hạn chứng chỉ (nội dung duy nhất cổng 80 phục vụ là xác thực và chuyển hướng). Với Caddy, có thể chặn ở tầng ứng dụng bằng `@blocked not remote_ip ...` rồi `respond @blocked 403`.
- **VPN hoặc lớp xác thực bên ngoài:** đặt VPN (WireGuard, OpenVPN) hoặc Cloudflare Access trước giao diện nếu có thể. Đây là biện pháp mạnh hơn mọi cách chặn bằng mật khẩu.
- **HSTS:** sau khi chắc chắn HTTPS chạy ổn, thêm header `Strict-Transport-Security: max-age=31536000` ở proxy (Caddy: trong khối `header`).
- Bridge đã tự thêm các header `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy` và `Content-Security-Policy`. Nếu bạn thêm tài nguyên bên ngoài vào giao diện, cần sửa CSP trong `Program.cs`.
- Theo dõi log của proxy và dòng `warn: Login '...' refused` của bridge để phát hiện dò mật khẩu; dùng trang **Nhật ký** để chặn IP nguồn nếu cần.

## 7. Cấu hình

| Tên | Nơi đặt | Mặc định | Ý nghĩa |
|---|---|---|---|
| `Jwt__Key` | Biến môi trường | (bắt buộc) | Khoá ký JWT, từ 32 ký tự |
| `Urls` | `appsettings.json` / `Urls` | `http://127.0.0.1:5080` | Địa chỉ bridge lắng nghe |
| `Logs:Directory` | `appsettings.json` / `Logs__Directory` | `C:\Program Files (x86)\hMailServer\Logs` | Thư mục log hMailServer |
| `VITE_MOCK` | `.env.local` (lúc build) | `true` | `false` để dùng bridge thật |
| `VITE_API_URL` | `.env.local` (lúc build) | `/api` | Địa chỉ gốc của API |

Khi chạy `npm run dev`, Vite chuyển `/api` tới `http://localhost:5080` (xem `vite.config.ts`).

## 8. Bảo mật

- Bridge dùng thông tin đăng nhập admin giữ trong **bộ nhớ**; không có tệp nào chứa mật khẩu. Khoá JWT chỉ nằm trong biến môi trường.
- Chỉ tài khoản **Administrator** hoặc tài khoản email có `AdminLevel = 2` mới đăng nhập được. Đăng nhập giới hạn **5 lần/phút cho mỗi IP**, JWT hết hạn sau **8 giờ**.
- Bridge dùng thông tin đăng nhập thành công **gần nhất** cho mọi yêu cầu, nên chỉ dùng trong nhóm quản trị tin cậy.
- Giữ `Urls` ở `127.0.0.1`, đặt sau HTTPS, hạn chế IP truy cập. Bridge tự thêm các header bảo mật (CSP, `X-Frame-Options`, `nosniff`, `Referrer-Policy`).
- Bridge đọc được nội dung thư trong hàng đợi, file log và file chứng chỉ trên máy chủ. Chỉ đọc các thư do chính hMailServer liệt kê trong hàng đợi, và chỉ đọc file log đúng mẫu `hmailserver_YYYY-MM-DD.log` / `ERROR_hmailserver_...log`.
- Các tính năng **Chẩn đoán** và **kiểm tra blacklist** gửi truy vấn DNS và kết nối cổng 25 ra ngoài từ máy chủ. Chẩn đoán chỉ chạy với tên miền đã có trong hMailServer.
- Lỗi 500 trả về nội dung lỗi thật để dễ gỡ lỗi; API chỉ dành cho quản trị viên đã đăng nhập.

## 9. API (tóm tắt)

Mọi đường dẫn bắt đầu bằng `/api` và (trừ đăng nhập) cần header `Authorization: Bearer <token>`. Đường dẫn `/api/...` không tồn tại trả về 404 dạng JSON.

| Nhóm | Phương thức và đường dẫn |
|---|---|
| Đăng nhập | `POST /auth/login` |
| Tổng quan | `GET /stats` |
| Tên miền | `GET/POST /domains`, `PUT/DELETE /domains/{id}` |
| Tài khoản | `GET/POST /accounts`, `PUT/DELETE /accounts/{id}` |
| Alias | `GET/POST /aliases`, `PUT/DELETE /aliases/{id}` |
| Danh sách phân phối | `GET/POST /lists`, `PUT/DELETE /lists/{id}` |
| IP ranges | `GET/POST /ranges`, `PUT/DELETE /ranges/{id}` |
| Auto-ban, greylisting | `GET/PUT /settings/security` |
| Cài đặt | `GET/PUT /settings` |
| Rules | `GET/POST /rules`, `PUT/DELETE /rules/{id}`, `POST /rules/{id}/move?dir=up\|down` |
| Hàng đợi | `GET /queue`, `GET /queue/raw`, `POST /queue/retry`, `POST /queue/{id}/retry`, `GET /queue/{id}/source`, `DELETE /queue/{id}` |
| Log | `GET /logs`, `GET /logs/{file}/summary`, `GET /logs/{file}/search`, `GET /logs/live` |
| DNS blacklists | `GET/POST /dnsbl`, `PUT/DELETE /dnsbl/{id}` |
| TCP/IP ports | `GET/POST /ports`, `PUT/DELETE /ports/{id}` |
| Chứng chỉ SSL | `GET/POST /ssl`, `PUT/DELETE /ssl/{id}` |
| Routes, relays, SURBL, whitelist | `GET/POST /r/{routes\|relays\|surbl\|greywhite}`, `PUT/DELETE /r/{tên}/{id}` |
| Chẩn đoán | `POST /diagnostics` |
| Tiện ích | `GET /utilities/server`, `POST /utilities/server/{start\|stop}`, `POST /utilities/backup`, `POST /utilities/blacklist` |

## 10. Mức độ kiểm chứng và hạn chế

Bridge gọi COM bằng late binding. Tên thuộc tính được lấy từ tài liệu COM hoặc mã nguồn webadmin của coax khi tìm được; phần còn lại viết theo mô hình đối tượng hMailServer từ trí nhớ. Tài liệu tham khảo chủ yếu thuộc nhánh 6.x, nên **bản hMailServer cũ hơn có thể khác**. Một thuộc tính sai chỉ gây lỗi ở đúng trang đó (thường kèm thông báo rõ ràng), không làm hỏng cả ứng dụng.

**Đã đối chiếu với tài liệu hoặc mã nguồn:** giá trị enum của Rules (trường, kiểu so khớp, loại hành động); mã giao thức (SMTP=1, POP3=3, IMAP=5) và kiểu bảo mật của cổng (0 không mã hoá, 1 SSL/TLS, 2 STARTTLS tuỳ chọn, 3 STARTTLS bắt buộc); `SessionCount(1|3|5)`; `RemovedSpamMessages`, `RemovedViruses`, `ProcessedMessages`; thứ tự cột hàng đợi; thuộc tính của Route; `Start`, `Stop`, `ServerState`, `BackupManager.StartBackup`; `HostName`, `MaxMessageSize`, `RuleLoopLimit`, `ServiceSMTP`, `SMTPRelayer`, `SMTPRelayerPort`, `SMTPDeliveryBindToIP`.

**Viết theo trí nhớ, hãy thử trước khi tin cậy:**

| Trang | Điểm cần thử |
|---|---|
| Alias | `Aliases`, `Name`, `Value` |
| Danh sách phân phối | `DistributionLists`, `Recipients`, `RecipientAddress` |
| IP ranges | `LowerIP`, `UpperIP`, `Priority`, `Allow*Connections`, `Expires` |
| Chống tấn công | tên thuộc tính auto-ban và `AntiSpam.GreyListing*` |
| DNS blacklists, SURBL | `DNSHost`, `Score`, `ExpectedResult`, `RejectMessage` |
| Incoming relays, Greylisting whitelist | `Name`/`LowerIP`/`UpperIP`, `IPAddress`/`Description` |
| Chứng chỉ SSL, TCP/IP ports | `CertificateFile`, `PrivateKeyFile`, `SSLCertificateID` |
| Cài đặt | các khoá ngoài danh sách đã đối chiếu (lời chào, ClamAV, ghi log, chống thư rác) |
| Hàng đợi | `DeliveryQueue.Remove`, `Settings.Directories.DataDirectory` |
| Rules | `HeaderField`, `To`, `IMAPFolder` |

**Hạn chế và chưa làm:**

- Báo cáo DMARC/TLS: chưa có (cần hộp thư nhận báo cáo và phân tích tệp đính kèm).
- Chưa có: địa chỉ riêng của route, thư mục IMAP/rule/tài khoản bên ngoài của từng tài khoản, domain alias, DKIM, public folders, groups, chặn tệp đính kèm, server messages, cài đặt anti-virus ngoài ClamAV, xác thực hai yếu tố.
- Rule dùng hành động chưa hỗ trợ (trả lời, chạy script, route, bind IP) chỉ cho xoá hoặc đổi thứ tự, để tránh mất cấu hình khi lưu.
- Chỉ có giao diện tiếng Việt, không có chế độ tối, không có kiểm thử tự động.
- Phân trang chạy ở trình duyệt; với hàng chục nghìn tài khoản nên chuyển sang phân trang ở bridge.
- Các thay đổi cổng, chứng chỉ, cài đặt giao thức có thể cần khởi động lại dịch vụ hMailServer mới có hiệu lực.

## 11. Xử lý sự cố

| Triệu chứng | Nguyên nhân và cách xử lý |
|---|---|
| `Unexpected token '<' ... is not valid JSON` hoặc "API này chưa có trong bridge" | Giao diện mới nhưng bridge đang chạy bản cũ chưa có API đó. Thay bản bridge mới và khởi động lại |
| `You do not have access to this property / method. Ensure that ...Authenticate()` | Bridge bản cũ, hoặc bridge vừa khởi động lại. Cập nhật bridge, đăng nhập lại |
| Đăng nhập luôn 401 | Tên đăng nhập phải là `Administrator` (hoặc tài khoản email có quyền admin cấp server). Xem dòng `warn: Login '...' refused` trong log bridge |
| Đăng nhập báo 429 | IP của bạn đã thử quá 5 lần/phút, chờ một phút. Nếu mọi người dùng đều bị 429 khi đã công khai, kiểm tra proxy có gửi `X-Forwarded-For` không |
| `hMailServer COM is not registered on this machine` | Bridge không chạy trên máy cài hMailServer |
| `Access denied` / `0x80070005` khi gọi COM | Tài khoản chạy bridge chưa có quyền DCOM, xem 6.4 |
| Cảnh báo `The WebRootPath was not found` | Chưa chép giao diện vào `wwwroot` |
| Giao diện vẫn hiện dữ liệu giả sau khi build | Quên đặt `VITE_MOCK=false` trước `npm run build` |
| Tổng quan: biểu đồ "Đang thu thập dữ liệu…" | Chờ khoảng 1 phút sau đăng nhập; nếu vẫn vậy xem dòng `Session sampling failed` trong log bridge |
| Tổng quan: số thư rác/virus bằng 0 | Xem dòng `Status.<tên> unavailable` trong log bridge |
| Nhật ký: "Không tìm thấy thư mục log" | Đặt đúng `Logs:Directory` |
| Live logging trống | Bật ghi log ở Cài đặt → Nhật ký; chỉ thấy các loại log đã bật |
| Hàng đợi: "Không tìm thấy file thư" | Sai đường dẫn Data hoặc tài khoản chạy bridge thiếu quyền đọc |
| TCP/IP ports: "Không kết nối được" | Cổng mới chưa áp dụng (khởi động lại dịch vụ hMailServer) hoặc không bind được; xem log `ERROR` |
| Một trang báo lỗi COM về thuộc tính | Tên thuộc tính khác theo phiên bản hMailServer; xem bảng ở mục 10 |

Các dòng `warn` và `fail` ở cửa sổ chạy bridge (hoặc log của NSSM) ghi chi tiết nguyên nhân.

## 12. Giấy phép và ghi công

- Dự án tham khảo danh sách tính năng của [hmailserver-webadmin](https://github.com/coax/hmailserver-webadmin) (coax), phát hành theo CC BY-SA 4.0. Mã nguồn ở đây được viết lại từ đầu; nếu bạn sao chép CSS, HTML hoặc văn bản từ bản gốc thì phải giữ ghi công và cùng giấy phép.
- hMailServer và các gói NuGet/npm được sử dụng có giấy phép riêng của từng dự án.
