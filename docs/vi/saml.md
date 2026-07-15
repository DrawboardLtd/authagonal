---
layout: default
title: SAML
locale: vi
---

# SAML 2.0 SP

Authagonal bao gồm triển khai SAML 2.0 Service Provider tự phát triển. Không có thư viện SAML bên thứ ba: được xây dựng trên `System.Security.Cryptography.Xml.SignedXml` (một phần của .NET).

## Phạm vi

- **SSO khởi tạo từ SP** (người dùng bắt đầu tại Authagonal, được chuyển hướng đến IdP)
- **HTTP-Redirect binding** cho AuthnRequest (có thể ký, xem bên dưới)
- **HTTP-POST binding** cho Response (ACS)
- **Assertion mã hóa** (`EncryptedAssertion`) được giải mã bằng cặp khóa SP riêng cho từng kết nối
- **Single Logout** (khởi tạo từ SP và từ IdP, binding Redirect và POST)
- Azure AD / Entra ID là mục tiêu chính, nhưng bất kỳ IdP tương thích nào đều hoạt động (các tên thuộc tính của Okta, OneLogin, Ping, Google Workspace, ADFS, Shibboleth đều được xử lý)

### Không hỗ trợ

- Artifact binding
- Mã hóa assertion bằng AES-GCM (hạn chế của `EncryptedXml` trong .NET; hãy cấu hình AES-CBC tại IdP, xem bên dưới)

SSO khởi tạo từ IdP được hỗ trợ. Endpoint ACS xử lý các phản hồi không có `InResponseTo` (bỏ qua kiểm tra request-ID cho các phản hồi không được yêu cầu, nhưng vẫn thực thi assertion-ID chỉ dùng một lần, xem phần Bảo mật).

## Thiết lập Azure AD

### 1. Tạo nhà cung cấp SAML

**Tùy chọn A: Cấu hình (khuyến nghị cho thiết lập tĩnh)**

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

Các nhà cung cấp được khởi tạo khi khởi động. Các ánh xạ tên miền SSO được đăng ký tự động từ `AllowedDomains`. Các nhà cung cấp được khởi tạo từ cấu hình yêu cầu một URL `MetadataLocation` và không nhận cặp khóa SP (nên không có AuthnRequest có ký, assertion mã hóa, hoặc thông điệp đăng xuất có ký); hãy dùng API Quản trị cho những tính năng đó.

`EntityId` là **entity ID của SP của bạn** (định danh mà bạn đăng ký tại IdP), không phải entity ID của IdP.

**Tùy chọn B: API Quản trị (cho quản lý tại thời điểm chạy)**

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

API tạo ra `connectionId` (một GUID) và trả về nó trong header `Location` cùng phần thân phản hồi. Các trường tùy chọn bổ sung: `metadataXml` (metadata dán vào, xem bên dưới), `nameIdFormat` (xem bên dưới), `signAuthnRequests` (buộc ký AuthnRequest), `iconUrl` (biểu tượng nút đăng nhập), `disableJitProvisioning` (từ chối người dùng lạ thay vì tự động tạo). Các kết nối tạo qua API cũng nhận một cặp khóa SP được tạo tự động (xem phần Cặp khóa SP bên dưới).

Các kết nối được quản lý qua `POST` / `GET` / `PUT` / `DELETE` trên `/api/v1/saml/connections[/{connectionId}]`. `PUT` là cập nhật một phần: chỉ các trường được gửi trên đường truyền mới bị thay đổi.

### 2. Cấu hình Azure AD

1. Trong Azure AD → Enterprise Applications → New Application → Create your own
2. Set up Single Sign-On → SAML
3. **Identifier (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **Reply URL (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **Sign on URL:** `https://auth.example.com/saml/acme-azure/login`

### 3. Định tuyến tên miền SSO

Khi `AllowedDomains` được chỉ định (trong cấu hình hoặc qua API tạo), các ánh xạ tên miền SSO được đăng ký tự động. Khi người dùng nhập `user@acme.com` trên trang đăng nhập, SPA phát hiện SSO là bắt buộc và hiển thị "Continue with SSO". Một tên miền chỉ có thể được ánh xạ tới một kết nối; API từ chối một tên miền đã được kết nối khác chiếm giữ.

Bạn cũng có thể quản lý tên miền tại thời điểm chạy qua API Quản trị; xem [API Quản trị](admin-api).

## XML metadata dán vào

Một số IdP không công bố URL metadata (Google Workspace), hoặc endpoint metadata của họ không thể truy cập được từ SP (ADFS trong mạng riêng). Với những trường hợp đó, hãy dán tài liệu metadata vào thay thế: cung cấp `metadataXml` khi tạo/cập nhật. Phải cung cấp đúng một trong hai `metadataLocation` hoặc `metadataXml`; việc cung cấp một cái khi cập nhật sẽ xóa cái kia.

Metadata dán vào được xác thực tại thời điểm lưu và **rút gọn** (`SamlMetadataParser.Condense`) thành một `EntityDescriptor` tối thiểu chuẩn tắc chỉ giữ đúng những gì SP tiêu thụ: entityID, các chứng chỉ ký, endpoint SSO, endpoint SLO nếu có, và cờ `WantAuthnRequestsSigned`. Tài liệu của nhà cung cấp có thể vượt quá 100KB (`FederationMetadata.xml` của ADFS), vượt qua giới hạn 64KB cho thuộc tính của Azure Table, trong khi các phần mà SP dùng chỉ vài KB. Metadata dán vào không phân tích được sẽ bị từ chối với mã 400; tài liệu phải chứa một `IDPSSODescriptor` với một chứng chỉ ký và một `SingleSignOnService`.

## Định dạng NameID

Trường `nameIdFormat` điều khiển Format của `NameIDPolicy` được yêu cầu trong AuthnRequest:

| Giá trị | Hành vi |
|---|---|
| bỏ qua / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress` (mặc định lịch sử) |
| `"none"` | Bỏ hoàn toàn phần tử `NameIDPolicy`. Đây là cài đặt an toàn cho ADFS: ADFS làm hỏng toàn bộ lượt đăng nhập (MSIS7070) khi các quy tắc claim của nó không phát ra định dạng được yêu cầu. |
| bất kỳ giá trị nào khác | Được gửi nguyên văn dưới dạng URN Format (phải bắt đầu bằng `urn:`) |

Khi cập nhật, `""` đặt lại về mặc định emailAddress. Metadata của SP quảng bá định dạng mà kết nối yêu cầu (và bỏ `NameIDFormat` khi đặt là `"none"`).

## Endpoint

| Endpoint | Mô tả |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | Khởi tạo SSO từ SP. Xây dựng một AuthnRequest (có ký khi phù hợp) và chuyển hướng đến IdP. `loginHint` được truyền dưới dạng `login_hint` cho các IdP tôn trọng nó (Entra, Google). |
| `POST /saml/{connectionId}/acs` | Dịch vụ tiếp nhận Assertion. Nhận phản hồi SAML, xác thực, tạo/đăng nhập người dùng. |
| `GET /saml/{connectionId}/metadata` | XML metadata SP để cấu hình IdP. |
| `GET /saml/{connectionId}/logout?returnUrl=...` | Single Logout khởi tạo từ SP. Kết thúc phiên cục bộ, rồi gửi một LogoutRequest đến IdP khi IdP hỗ trợ SLO. |
| `GET/POST /saml/{connectionId}/slo` | Endpoint Single Logout. Nhận các LogoutRequest khởi tạo từ IdP (binding Redirect hoặc POST) và nhánh LogoutResponse của SLO khởi tạo từ SP. |

URL trả về sau đăng nhập được mang ở phía máy chủ trên AuthnRequest đã lưu (khóa theo request ID), không phải trong RelayState: đặc tả SAML giới hạn RelayState ở 80 byte và một số IdP cắt bớt nó. RelayState chỉ được tham chiếu cho các luồng khởi tạo từ IdP.

## Cặp khóa SP và assertion mã hóa

Mọi kết nối tạo qua API đều nhận một cặp khóa SP được tạo tự động: một chứng chỉ RSA 2048-bit tự ký (hiệu lực 10 năm), được lưu dưới dạng PKCS#12 và được bảo vệ khi lưu trữ bởi nhà cung cấp bí mật của host. Nó chỉ tồn tại phía máy chủ và không bao giờ được API trả về. Cặp khóa cho phép:

- **AuthnRequest có ký** (ký truy vấn `SigAlg`/`Signature` của redirect-binding). Việc ký tự động bật khi metadata của IdP khai báo `WantAuthnRequestsSigned`, hoặc luôn bật khi kết nối đặt `signAuthnRequests: true`.
- **Giải mã assertion mã hóa.** Khi metadata của SP quảng bá một chứng chỉ mã hóa, ADFS mặc định bắt đầu mã hóa các assertion; ACS giải mã chúng bằng khóa riêng của SP và đưa assertion đã giải mã qua cùng một quy trình chữ ký/điều kiện như một assertion văn bản rõ. Được hỗ trợ: chuyển khóa RSA-OAEP (SHA-1/SHA-256) và RSA-1.5; mã hóa dữ liệu AES-128/192/256-CBC và 3DES. **AES-GCM không được hỗ trợ** (hạn chế của `EncryptedXml` trong .NET) và tạo ra lỗi rõ ràng; hãy cấu hình IdP dùng AES-CBC.
- **Thông điệp đăng xuất có ký** (LogoutRequest/LogoutResponse trên binding redirect).

Metadata của SP công bố chứng chỉ vừa là một `KeyDescriptor` `signing` vừa là một `KeyDescriptor` `encryption`, và đặt `AuthnRequestsSigned="true"` khi kết nối buộc ký.

## Single Logout

ACS ghi lại phiên SAML trên cookie xác thực (các claim `saml_connection`, `saml_name_id`, `saml_name_id_format`, `saml_session_index`) để việc đăng xuất có thể được liên kết trở lại với phiên IdP.

- **Khởi tạo từ SP:** `GET /saml/{connectionId}/logout` luôn kết thúc phiên cookie cục bộ trước (người dùng đã yêu cầu đăng xuất; SLO của IdP là nỗ lực tốt nhất). Nếu phiên của trình duyệt đến từ kết nối này và metadata của IdP quảng bá một `SingleLogoutService`, một LogoutRequest (NameID + SessionIndex, có ký khi SP có khóa) được gửi qua binding redirect; LogoutResponse của IdP quay lại `/slo`, đưa người dùng đến `returnUrl` đã lưu. Các IdP không có endpoint SLO (Google) chỉ được đăng xuất cục bộ.
- **Khởi tạo từ IdP:** IdP gửi một LogoutRequest đến `/saml/{connectionId}/slo` (binding Redirect GET hoặc POST). Các yêu cầu có ký được xác thực với các chứng chỉ trong metadata của IdP. **Các LogoutRequest không ký chỉ được tôn trọng khi phiên của chính trình duyệt thuộc về kết nối này**, nên một kẻ tấn công chưa xác thực không thể đăng xuất ai khác ngoài chính mình. Một LogoutResponse có ký được trả về khi IdP có endpoint SLO. Chỉ front-channel: thông điệp đến trong trình duyệt của người dùng, nên việc kết thúc phiên cookie đăng xuất chính xác trình duyệt đó.

## Bộ nhớ đệm metadata và luân chuyển chứng chỉ

- Metadata của IdP được lấy từ `MetadataLocation` được lưu đệm trong bộ nhớ 60 phút (có thể cấu hình qua `Cache:SamlMetadataCacheMinutes`), khóa theo URL metadata (không phải theo connection ID, nên không thể xảy ra nhầm lẫn đệm giữa các tenant).
- Metadata dán vào được lưu đệm theo nội dung (băm của XML) và không bao giờ được lấy lại.
- **Lấy lại khi thất bại chữ ký:** một thất bại xác thực chữ ký ngay sau khi IdP luân chuyển chứng chỉ nghĩa là metadata đã lưu đệm bị cũ. Đúng vào thất bại đó, mục đệm bị xóa và metadata được lấy lại một lần, rồi việc xác thực được thử lại, với thời gian chờ 5 phút cho mỗi vị trí metadata để một assertion rác không thể bị dùng để dội bom endpoint metadata của IdP. Nếu không có cơ chế này, một lần luân chuyển chứng chỉ sẽ làm hỏng các lượt đăng nhập cho đến khi TTL đệm hết hạn. (Chỉ với metadata lấy qua URL; metadata dán vào không có gì để lấy lại.)

## Tương thích Azure AD

| Hành vi Azure AD | Xử lý |
|---|---|
| Chỉ ký assertion (mặc định) | Xác thực chữ ký trên phần tử Assertion |
| Chỉ ký response | Xác thực chữ ký trên phần tử Response |
| Ký cả hai | Xác thực cả hai chữ ký |
| SHA-256 (mặc định) | Hỗ trợ SHA-256 và SHA-1 |
| NameID: emailAddress | Trích xuất email trực tiếp |
| NameID: persistent (mờ) | Dùng email claim từ các thuộc tính dự phòng |
| NameID: unspecified | Dùng email claim từ các thuộc tính dự phòng |
| NameID: transient | Xoay vòng mỗi lượt đăng nhập, nên không bao giờ được dùng làm khóa liên kết. Thuộc tính object-id ổn định của IdP được dùng thay thế; nếu không có cái nào được khẳng định, lượt đăng nhập bị từ chối với một lỗi có tính hành động (hãy cấu hình một NameID persistent hoặc emailAddress, hoặc khẳng định một thuộc tính object-id). |

## Ánh xạ thuộc tính

Các thuộc tính được lập chỉ mục không phân biệt hoa thường theo cả `Name` và `FriendlyName` của chúng (Okta và Shibboleth phát ra các Name dạng OID với các FriendlyName cho con người; khớp bất kỳ cái nào là điều làm cho việc ánh xạ theo nhà cung cấp hoạt động). Mỗi trường thử một danh sách bí danh theo thứ tự; bí danh đầu tiên là URI claim của Microsoft, nên hành vi Entra/ADFS không thay đổi, và phần còn lại bao phủ các tên thân thiện và tên OID mà Okta, OneLogin, Ping, Google và Shibboleth phát ra theo mặc định:

| Trường | Các tên thuộc tính được chấp nhận |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

(`.../claims/...` viết tắt cho URI đầy đủ `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` hoặc `http://schemas.microsoft.com/ws/2008/06/identity/claims/...`.)

Thứ tự ưu tiên phân giải email: thuộc tính email tường minh (bất kỳ bí danh nào) → NameID khi định dạng của nó là emailAddress → claim `name` nếu nó chứa `@` → từ chối (bắt buộc phải có email).

**Groups là đa giá trị:** mọi phần tử `AttributeValue` đều được thu thập (mỗi phần tử cho một tư cách thành viên nhóm), không chỉ phần tử đầu tiên.

## Cấp phát JIT

Người dùng lạ được tự động tạo ở lần đăng nhập đầu tiên (email, tên/họ từ assertion, email được đánh dấu đã xác nhận) và được liên kết với kết nối theo định danh liên kết ổn định của họ (`saml:{connectionId}` + NameID, hoặc object-id đối với NameID transient). Đặt `disableJitProvisioning: true` để từ chối người dùng lạ thay thế. Người dùng quay lại được đối chiếu theo liên kết liên kết trước tiên, không bao giờ chỉ theo email; một tài khoản cục bộ hiện có chỉ được gắn theo email khi `AllowedDomains` của kết nối bao phủ tên miền của email đó (lời khẳng định tường minh của quản trị viên rằng IdP này sở hữu tên miền), ngăn chặn việc chiếm đoạt tài khoản qua một IdP giả mạo.

## Bảo mật

- **Ngăn chặn phát lại:** đối với các luồng khởi tạo từ SP, `InResponseTo` được xác thực với một request ID đã lưu (dùng một lần). Độc lập với điều đó, ID của mọi assertion được chấp nhận đều được lưu và thực thi dùng một lần, điều này cũng bao phủ các phản hồi khởi tạo từ IdP và các phản hồi có `InResponseTo` bị lược bỏ (ID assertion nằm bên trong assertion đã ký, nên không thể bị thay đổi mà không phá vỡ chữ ký).
- **Độ lệch đồng hồ:** Dung sai 5 phút cho NotBefore/NotOnOrAfter
- **Ngăn chặn tấn công wrapping:** Reference URI của chữ ký phải khớp với ID của phần tử đã ký
- **Ngăn chặn chuyển hướng mở:** URL trả về sau đăng nhập phải là một đường dẫn gốc-tương đối (bắt đầu bằng `/`, không có `//`, không có dấu gạch chéo ngược, vì trình duyệt coi `\` như `/`)
- **Bảo chứng tên miền:** khi `AllowedDomains` được cấu hình, các assertion cho các email nằm ngoài các tên miền đó bị từ chối, nên một kết nối không thể khẳng định tên miền của kết nối khác hoặc email của một người dùng cục bộ
- **MFA:** liên kết chỉ chứng minh yếu tố thứ nhất. Nếu chính sách hiệu lực của người dùng yêu cầu MFA, lượt đăng nhập được định tuyến qua thử thách/thiết lập MFA cục bộ thay vì cấp một phiên đã xác thực đầy đủ.
