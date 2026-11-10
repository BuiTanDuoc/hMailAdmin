*** HMailBridge ***
Chạy HMailBridge trên chính máy Windows đang cài hMailServer, vì nó gọi COM cục bộ. Các bước như sau:
# 1. Chuẩn bị
- Cài .NET 8 SDK (để chạy dotnet run) trên máy hMailServer.
- Giải nén HMailBridge.zip, mở PowerShell trong thư mục HMailBridge.

# 2. Đặt khoá JWT rồi chạy
```powershell
$b = New-Object byte[] 48
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
$env:Jwt__Key = [Convert]::ToBase64String($b)
dotnet run
```
API sẽ lắng nghe ở http://127.0.0.1:5080. Khoá này chỉ có hiệu lực trong cửa sổ PowerShell đó, nên cứ chạy tiếp ở cùng cửa sổ.

# 3. Kiểm tra nhanh (mở cửa sổ PowerShell khác)
```powershell
$r = Invoke-RestMethod http://127.0.0.1:5080/api/auth/login -Method Post -ContentType 'application/json' `
     -Body '{"username":"Administrator","password":"MẬT_KHẨU_HMAILSERVER"}'
$h = @{ Authorization = "Bearer $($r.token)" }
Invoke-RestMethod http://127.0.0.1:5080/api/domains  -Headers $h
Invoke-RestMethod http://127.0.0.1:5080/api/queue/raw -Headers $h
```
Đăng nhập bằng tài khoản Administrator của hMailServer. Tài khoản email thường sẽ bị từ chối. Với /api/queue/raw, nếu hàng đợi đang có thư, hãy xem thứ tự các cột rồi chỉnh ColId, ColFrom... trong Program.cs cho khớp.

#4. Chạy giao diện (trong thư mục hmailadmin-react)
```powershell
"VITE_MOCK=false" | Out-File .env.local -Encoding ascii
npm install
npm run dev
```
Mở http://localhost:5173 và đăng nhập. Vite tự chuyển /api sang localhost:5080.

# 5. Triển khai thật
Chạy npm run build, chép nội dung dist/* vào thư mục HMailBridge/wwwroot. Khi đó chỉ cần chạy một tiến trình tại http://127.0.0.1:5080.
Chạy dotnet publish -c Release, rồi cài thành Windows Service (ví dụ bằng NSSM) và đặt biến môi trường Jwt__Key cho service đó.
Đặt reverse proxy HTTPS (IIS hoặc nginx) phía trước. Không mở cổng 5080 ra Internet.

# Lỗi hay gặp
“hMailServer COM is not registered”: bạn đang chạy trên máy khác với máy cài hMailServer.
Access denied / 0x80070005 khi gọi COM: tài khoản chạy bridge chưa có quyền DCOM. Làm theo hướng dẫn DCOM permissions trong tài liệu hMailServer (cùng mục mà bản PHP cũ yêu cầu khi chạy trên IIS).
Login trả 401: sai mật khẩu, hoặc tài khoản không phải admin cấp server. Đăng nhập sai quá 5 lần/phút sẽ bị chặn tạm thời (429).