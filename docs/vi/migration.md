---
layout: default
title: Di chuyển
locale: vi
---

# Di chuyển từ Duende IdentityServer

Authagonal bao gồm công cụ di chuyển để chuyển từ Duende IdentityServer + SQL Server sang Azure Table Storage.

## Chạy di chuyển

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

(Không có dấu phân tách `--` sau tên image: mọi thứ đứng sau nó được truyền thẳng cho công cụ, và một dấu `--` trơ sẽ phá vỡ việc phân tích tùy chọn.)

Hoặc từ mã nguồn:

```bash
dotnet run --project tools/Authagonal.Migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] [--MigrateRefreshTokens true]
```

## Những gì được di chuyển

| Nguồn (SQL Server) | Đích (Table Storage) | Ghi chú |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Users + UserEmails + chỉ mục tên | Truy vấn JOIN đơn. Claim: given_name, family_name, company, org_id (kiểu có thể ghi đè, xem bên dưới). Hash mật khẩu giữ nguyên; hash ASP.NET Identity V3 và BCrypt xác minh không đổi và nâng cấp lên định dạng PBKDF2 gốc của Authagonal ở lần đăng nhập thành công tiếp theo. |
| `AspNetUserLogins` | UserLogins (chỉ mục xuôi + ngược) | `409 Conflict` = bỏ qua (idempotent) |
| Duende `SamlProviderConfigurations` | SamlProviders + SsoDomains | CSV `AllowedDomains` được tách thành các bản ghi tên miền SSO riêng lẻ |
| Duende `OidcProviderConfigurations` | OidcProviders + SsoDomains | Tách tên miền tương tự |
| Duende `Clients` + bảng con | Clients | ClientSecrets, GrantTypes, RedirectUris, PostLogoutRedirectUris, Scopes, CorsOrigins đều được gộp vào một thực thể duy nhất |
| Duende `PersistedGrants` (refresh tokens) | Grants + GrantsBySubject + GrantsByExpiry | Tùy chọn qua `--MigrateRefreshTokens true`. Chỉ token chưa hết hạn. Nếu bỏ qua, người dùng chỉ cần đăng nhập lại. |

## Tùy chọn

| Tùy chọn | Mặc định | Mô tả |
|---|---|---|
| `--DryRun` | `false` | Ghi nhật ký những gì sẽ được di chuyển mà không ghi vào lưu trữ |
| `--MigrateRefreshTokens` | `false` | Bao gồm refresh token đang hoạt động. Nếu không, người dùng xác thực lại sau khi chuyển đổi. |
| `--Source:ClaimMap:{claim}` | chính tên claim OIDC | Ghi đè ClaimType của `AspNetUserClaims` được đọc cho một claim đã ánh xạ, ví dụ `--Source:ClaimMap:given_name=FirstName`. Dùng cho `given_name`, `family_name`, `company`, `org_id`. |

## Tính idempotent

Quá trình di chuyển là idempotent và an toàn để chạy nhiều lần. Các bản ghi hiện có được cập nhật hoặc bỏ qua, không bao giờ bị trùng lặp. Điều này cho phép bạn:

1. Chạy di chuyển trước ngày chuyển đổi nhiều ngày
2. Chạy di chuyển delta cuối cùng gần thời điểm chuyển đổi
3. Chạy lại nếu có vấn đề

## Những gì KHÔNG được di chuyển

Các tính năng Authagonal sau không có tương đương trong Duende và sẽ bắt đầu trống sau khi di chuyển:

- **Vai trò**: Vai trò RBAC và gán vai trò cho người dùng
- **Thông tin xác thực MFA**: Đăng ký TOTP, WebAuthn và mã khôi phục
- **Token và nhóm SCIM**: Cấu hình cung cấp SCIM
- **Cung cấp người dùng**: Trạng thái cung cấp ứng dụng hạ nguồn TCC

Người dùng sẽ cần đăng ký lại MFA nếu `MfaPolicy` của client là `Enabled` hoặc `Required`.

## Di chuyển khóa ký

Chưa được tự động hóa. Để giữ token hiện có hợp lệ trong quá trình chuyển đổi:

1. Xuất khóa ký RSA từ Duende (thường trong appsettings dưới dạng Base64 PKCS8)
2. Nhập vào bảng `SigningKeys`
3. Thực hiện gần thời điểm chuyển đổi

## Chiến lược chuyển đổi

1. Chạy di chuyển người dùng + nhà cung cấp + client (có thể làm trước nhiều ngày)
2. Khởi tạo cấu hình client trong Authagonal
3. Nhập khóa ký (gần thời điểm chuyển đổi)
4. Tùy chọn: di chuyển refresh token đang hoạt động
5. Triển khai Authagonal lên staging, kiểm thử
6. Chế độ bảo trì trên IdentityServer hiện tại
7. Di chuyển delta cuối cùng
8. Chuyển đổi DNS (đặt TTL thành 60 giây trước đó)
9. Giám sát 30 phút
10. Nếu có vấn đề: chuyển DNS ngược lại (khóa ký chung nghĩa là token hoạt động trên cả hai hệ thống)
