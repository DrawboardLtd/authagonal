---
layout: default
title: SAML
locale: vi
---

# SAML 2.0 SP

Authagonal bao gồm triển khai SAML 2.0 Service Provider tự phát triển. Không có thư viện SAML bên thứ ba — được xây dựng trên `System.Security.Cryptography.Xml.SignedXml` (một phần của .NET).

## Phạm vi

- **SSO khởi tạo từ SP** (người dùng bắt đầu tại Authagonal, được chuyển hướng đến IdP)
- **HTTP-Redirect binding** cho AuthnRequest
- **HTTP-POST binding** cho Response (ACS)
- Azure AD là mục tiêu chính, nhưng bất kỳ IdP tương thích nào đều hoạt động

### Không hỗ trợ

- SSO khởi tạo từ IdP
- Đăng xuất SAML (sử dụng hết thời gian phiên)
- Mã hóa Assertion (không công bố chứng chỉ mã hóa)
- Artifact binding

## Thiết lập Azure AD

### 1. Tạo nhà cung cấp SAML

**Tùy chọn A — Cấu hình (khuyến nghị cho thiết lập tĩnh):**

Thêm vào `appsettings.json`:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "acme-azure",
      "ConnectionName": "Acme Corp Azure AD",
      "EntityId": "https://auth.example.com/saml/acme-azure",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
      "AllowedDomains": ["acme.com"]
    }
  ]
}
```

Các nhà cung cấp được khởi tạo khi khởi động. Các ánh xạ tên miền SSO được đăng ký tự động từ `AllowedDomains`.

**Tùy chọn B — API Quản trị (cho quản lý tại thời điểm chạy):**

```bash
curl -X POST https://auth.example.com/api/v1/saml/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Acme Corp Azure AD",
    "entityId": "https://auth.example.com/saml/acme-azure",
    "metadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
    "allowedDomains": ["acme.com"]
  }'
```

### 2. Cấu hình Azure AD

1. Trong Azure AD, vào Enterprise Applications, chọn New Application, rồi Create your own
2. Thiết lập Single Sign-On, chọn SAML
3. **Identifier (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **Reply URL (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **Sign on URL:** `https://auth.example.com/saml/acme-azure/login`

### 3. Định tuyến tên miền SSO

Khi `AllowedDomains` được chỉ định (trong cấu hình hoặc qua API tạo), các ánh xạ tên miền SSO được đăng ký tự động. Khi người dùng nhập `user@acme.com` trên trang đăng nhập, SPA phát hiện SSO là bắt buộc và hiển thị "Tiếp tục với SSO".

Bạn cũng có thể quản lý tên miền tại thời điểm chạy qua API Quản trị — xem [API Quản trị](admin-api).

## Endpoint

| Endpoint | Mô tả |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...` | Khởi tạo SSO từ SP. Xây dựng AuthnRequest và chuyển hướng đến IdP. |
| `POST /saml/{connectionId}/acs` | Dịch vụ tiếp nhận Assertion. Nhận phản hồi SAML, xác thực, tạo/đăng nhập người dùng. |
| `GET /saml/{connectionId}/metadata` | XML metadata SP để cấu hình IdP. |

## Tương thích Azure AD

| Hành vi Azure AD | Xử lý |
|---|---|
| Chỉ ký assertion (mặc định) | Xác thực chữ ký trên phần tử Assertion |
| Chỉ ký response | Xác thực chữ ký trên phần tử Response |
| Ký cả hai | Xác thực cả hai chữ ký |
| SHA-256 (mặc định) | Hỗ trợ SHA-256 và SHA-1 |
| NameID: emailAddress | Trích xuất email trực tiếp |
| NameID: persistent (mờ) | Dùng email claim từ các thuộc tính dự phòng |
| NameID: transient, unspecified | Dùng email claim từ các thuộc tính dự phòng |

## Ánh xạ Claim

Các claim Azure AD (định dạng URI đầy đủ) được ánh xạ sang tên đơn giản:

| URI Claim Azure AD | Ánh xạ thành |
|---|---|
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` | `email` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname` | `firstName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname` | `lastName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name` | `name` (UPN) |
| `http://schemas.microsoft.com/identity/claims/objectidentifier` | `oid` |
| `http://schemas.microsoft.com/identity/claims/tenantid` | `tenantId` |
| `http://schemas.microsoft.com/identity/claims/displayname` | `displayName` |

## Bảo mật

- **Ngăn chặn phát lại:** InResponseTo được xác thực với ID yêu cầu đã lưu. Mỗi ID chỉ dùng một lần.
- **Độ lệch đồng hồ:** Dung sai 5 phút cho NotBefore/NotOnOrAfter
- **Ngăn chặn tấn công wrapping:** Xác thực chữ ký sử dụng giải quyết tham chiếu đúng
- **Ngăn chặn chuyển hướng mở:** RelayState (returnUrl) phải là đường dẫn tương đối bắt đầu bằng `/`
