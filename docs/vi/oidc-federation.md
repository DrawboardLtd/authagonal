---
layout: default
title: Liên kết OIDC
locale: vi
---

# Liên kết OIDC

Authagonal có thể liên kết xác thực với các nhà cung cấp danh tính OIDC bên ngoài (Google, Apple, Azure AD, v.v.). Điều này cho phép các luồng kiểu "Đăng nhập bằng Google" trong khi Authagonal vẫn là máy chủ xác thực trung tâm.

## Cách hoạt động

Có hai đường vào liên kết:

**Dựa trên tên miền (đăng nhập tương tác):**

1. Người dùng nhập email trên trang đăng nhập
2. SPA gọi `/api/auth/sso-check`: nếu tên miền email được liên kết với nhà cung cấp OIDC, SSO là bắt buộc
3. Người dùng nhấp "Tiếp tục với SSO" và được chuyển hướng đến IdP bên ngoài
4. Sau khi xác thực, IdP chuyển hướng lại `/oidc/callback`
5. Authagonal xác thực id_token, tạo/liên kết người dùng, và đặt cookie phiên

**Theo gợi ý từ RP (`idp_hint`):**

Relying party phía sau có thể định tuyến thẳng đến một IdP thượng nguồn cụ thể mà không cần đi qua bước email/tên miền SSO. Thêm `idp_hint={connectionId}` vào `/connect/authorize`:

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

Khi yêu cầu chưa được xác thực, Authagonal chuyển hướng đến `/oidc/{connectionId}/login` với URL `/authorize` gốc được giữ lại làm `returnUrl`. Sau khi liên kết hoàn tất, người dùng quay lại `/authorize` với cookie phiên và luồng tiếp tục bình thường.

## Thiết lập

### 1. Tạo nhà cung cấp OIDC

**Tùy chọn A: Cấu hình (khuyến nghị cho thiết lập tĩnh):**

Thêm vào `appsettings.json`:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

Các nhà cung cấp được khởi tạo khi khởi động. Các trường có thể khởi tạo chính xác là những trường được hiển thị, trừ `RedirectUrl`: `ConnectionId`, `ConnectionName`, `MetadataLocation`, `ClientId`, `ClientSecret`, `AllowedDomains`. `RedirectUrl` được chấp nhận để tương thích và bị bỏ qua — redirect URI được suy ra theo từng yêu cầu là `{Issuer}/oidc/callback`, vì nó phải nằm trên origin mà trình duyệt đang truy cập, và đó là URI cần đăng ký với IdP. `ClientSecret` được bảo vệ qua `ISecretProvider` (Key Vault khi được cấu hình, văn bản thuần trong trường hợp khác). Các ánh xạ tên miền SSO được đăng ký tự động từ `AllowedDomains`.

Mô hình kết nối còn mang thêm hành vi tùy chọn: `PassthroughParams` (đặt được qua API tạo của quản trị), cùng `SessionExpClaim` và `DisableJitProvisioning` (các trường cấp store, đặt qua `IOidcProviderStore` từ mã hosting), xem [Luồng scope và claim](#scope-and-claim-flow-through) và [Giới hạn thời gian phiên](#session-lifetime-cap) bên dưới.

**Tùy chọn B: API Quản trị (cho quản lý tại thời điểm chạy):**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"]
  }'
```

### 2. Định tuyến tên miền SSO

Khi `AllowedDomains` được chỉ định (trong cấu hình hoặc qua API tạo), các ánh xạ tên miền SSO được đăng ký tự động. Nếu không có định tuyến tên miền, người dùng vẫn có thể được chuyển đến trang đăng nhập OIDC qua `/oidc/{connectionId}/login`.

## Endpoint

| Endpoint | Mô tả |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...` | Khởi tạo đăng nhập OIDC. Tạo PKCE + state + nonce, chuyển hướng đến endpoint ủy quyền của IdP. |
| `GET /oidc/callback` | Xử lý callback từ IdP. Đổi mã lấy token, xác thực id_token, tạo/đăng nhập người dùng. |

## Luồng scope và claim {#scope-and-claim-flow-through}

Tập scope mà RP phía sau yêu cầu tại `/connect/authorize` được chuyển tiếp đến IdP thượng nguồn, **được lọc về tập OIDC tiêu chuẩn** (`openid`, `profile`, `email`, `address`, `phone`), trong đó `openid` luôn được bao gồm. Bất cứ thứ gì khác mà RP yêu cầu (scope API tùy chỉnh, `offline_access`, ...) đều bị loại bỏ trước lời gọi thượng nguồn: một IdP nghiêm ngặt như Google trả về `invalid_scope` với các giá trị không xác định, và thượng nguồn chỉ cần định danh người dùng; các scope riêng của RP được tôn trọng trên token do Authagonal phát hành, không phải token thượng nguồn. Bất kỳ claim nào mà IdP thượng nguồn gắn lên id_token theo scope đều quay về Authagonal, được cất trên ticket cookie dưới dạng các claim `federated:<name>`, và đi tiếp vào `OidcSubject.FederationClaims` ở lần duyệt `/connect/authorize` kế tiếp. Từ đó `ProtocolTokenService` phát lại chúng trên các token do Authagonal phát hành, được kiểm soát bởi chính danh sách trắng `Scope.UserClaims` vốn kiểm soát `CustomAttributes`. Giá trị liên kết thắng khi trùng khóa.

Kết quả thực tế: không có danh sách trắng claim theo từng kết nối cần bảo toàn. Mọi claim phi giao thức mà thượng nguồn đặt lên id_token đều được thu nhận; những claim nào đến được token phía sau do `UserClaims` của scope phía sau kiểm soát, khai báo claim ở đó và giá trị sẽ chảy qua.

`FederationClaims` sống sót qua các lần xoay vòng refresh tách biệt với `CustomAttributes`, nên ngữ cảnh liên kết theo phiên (ví dụ một token share-link được thu nhận tại authorize gốc) vẫn nguyên vẹn trong khi các thuộc tính theo người dùng vẫn được đọc lại mới từ user store.

## Tham số truy vấn passthrough

`OidcProviderConfig.PassthroughParams` là danh sách trắng theo từng kết nối gồm các khóa truy vấn chảy từ yêu cầu `/authorize` gốc sang URL ủy quyền của IdP thượng nguồn. Tập tiêu chuẩn (`scope`, `state`, `nonce`, PKCE) luôn được chuyển tiếp; danh sách này dành cho các giá trị bổ sung do RP chỉ định, như một thông tin xác thực dùng một lần mà thượng nguồn cần để xác thực (ví dụ `link_token` cho các IdP share-link).

Khi một khóa nằm trong danh sách trắng, Authagonal lấy giá trị của nó từ truy vấn `/authorize` gốc (được mang qua `returnUrl`) và nối vào URL thượng nguồn. Bất cứ thứ gì không nằm trong danh sách trắng đều bị loại bỏ âm thầm.

## Giới hạn thời gian phiên {#session-lifetime-cap}

`OidcProviderConfig.SessionExpClaim` là tên tùy chọn của một claim trên id_token (giây Unix) mà giá trị của nó giới hạn thời gian sống của phiên cục bộ. Khi có mặt, giá trị thượng nguồn đi theo dưới dạng `session_max_exp` trên ticket cookie và vào mã ủy quyền được phát hành; các token access / id / refresh bị kẹp lại sao cho không token nào (kể cả các token sinh ra từ những lần xoay vòng) sống lâu hơn phiên thượng nguồn. Hữu ích khi IdP thượng nguồn áp đặt giới hạn phiên ngắn hơn mức Authagonal mặc định.

## Tính năng bảo mật

- **PKCE**: code_challenge với S256 trên mỗi yêu cầu ủy quyền
- **Xác thực nonce**: nonce được lưu cùng state, phải có mặt trong id_token và khớp
- **Xác thực state**: sử dụng một lần (được tiêu thụ nguyên tử qua `IOidcStateStore`, lưu bền với thời hạn) **và gắn với trình duyệt**: một cookie `SameSite=Lax` giới hạn phạm vi `/oidc` được đặt lúc đăng nhập và phải khớp với `state` trên callback, nên kẻ tấn công không thể hoàn tất một luồng liên kết do chính hắn khởi tạo rồi giao URL callback cho nạn nhân (login CSRF)
- **Xác thực chữ ký id_token**: khóa được lấy từ endpoint JWKS của IdP; issuer, audience và thời hạn được xác thực
- **Dự phòng userinfo**: nếu id_token không chứa email, endpoint userinfo sẽ được thử. `sub` của userinfo phải khớp với `sub` của id_token (OIDC Core 5.3.2), nếu không phản hồi bị bỏ qua
- **Liên kết danh tính ổn định**: người dùng quay lại được phân giải theo nhà cung cấp + `sub`, không bao giờ chỉ theo email. Việc gắn một danh tính liên kết vào một tài khoản cục bộ **có sẵn** theo email đòi hỏi `AllowedDomains` của kết nối phải bao phủ tên miền của email đó, tức sự bảo chứng rõ ràng của quản trị viên rằng IdP sở hữu tên miền ấy. Một `email_verified` do thượng nguồn khẳng định là *không* đủ để chiếm một tài khoản có sẵn
- **Thực thi tên miền**: khi `AllowedDomains` được đặt, kết nối chỉ được khẳng định các danh tính trong những tên miền đó (`access_denied` trong trường hợp khác)
- **Tắt JIT**: `DisableJitProvisioning` từ chối người dùng không xác định thay vì tự động tạo họ
- **Chặn open-redirect**: `returnUrl` phải là đường dẫn tương đối cùng site; các dạng protocol-relative (`//`) và gạch chéo ngược đều bị từ chối
- **MFA cục bộ vẫn áp dụng**: liên kết chỉ chứng minh yếu tố thứ nhất. Người dùng đã đăng ký MFA (hoặc có chính sách client yêu cầu MFA) được định tuyến qua các trang thử thách/thiết lập MFA cục bộ sau callback thay vì được đăng nhập thẳng; chỉ khi đó phiên mới mang dấu MFA

## Đặc thù Azure AD

Azure AD đôi khi trả về email dưới dạng mảng JSON trong claim `emails` (đặc biệt với B2C). Authagonal xử lý điều này bằng cách kiểm tra cả claim `email` và mảng `emails`.

## Nhà cung cấp được hỗ trợ

Bất kỳ nhà cung cấp tương thích OIDC nào hỗ trợ:
- Luồng Authorization Code
- PKCE (S256)
- Tài liệu khám phá (`.well-known/openid-configuration`)

Đã được kiểm thử với:
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C
