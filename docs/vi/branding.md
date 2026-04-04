---
layout: default
title: Tùy chỉnh giao diện
locale: vi
---

# Tùy chỉnh giao diện đăng nhập

SPA đăng nhập có thể cấu hình tại thời điểm chạy qua tệp `branding.json` được phục vụ từ thư mục gốc web. Không cần build lại — chỉ cần mount cấu hình và tài nguyên của bạn.

## Cách hoạt động

Khi khởi động, SPA tải `/branding.json`. Nếu tệp không tồn tại hoặc không thể truy cập, các giá trị mặc định sẽ được sử dụng. Cấu hình điều khiển:

- Tên ứng dụng (hiển thị trong tiêu đề và tiêu đề trang)
- Hình ảnh logo
- Màu chính (nút, liên kết, vòng focus)
- Hiển thị liên kết quên mật khẩu
- CSS tùy chỉnh cho phong cách sâu hơn

## Cấu hình

Đặt tệp `branding.json` trong thư mục `wwwroot/` (hoặc mount vào container Docker):

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
  "supportEmail": "help@acme.com",
  "showForgotPassword": true,
  "customCssUrl": "/branding/custom.css"
}
```

### Tùy chọn

| Thuộc tính | Kiểu | Mặc định | Mô tả |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Hiển thị trong tiêu đề và thanh tiêu đề trình duyệt |
| `logoUrl` | `string \| null` | `null` | URL đến hình ảnh logo. Khi đặt, thay thế tiêu đề văn bản. |
| `primaryColor` | `string` | `"#2563eb"` | Màu hex cho nút, liên kết và chỉ báo focus |
| `supportEmail` | `string \| null` | `null` | Email liên hệ hỗ trợ (dành cho sử dụng trong tương lai) |
| `showForgotPassword` | `boolean` | `true` | Hiển thị/ẩn liên kết "Quên mật khẩu?" trên trang đăng nhập |
| `customCssUrl` | `string \| null` | `null` | URL đến tệp CSS tùy chỉnh được tải sau các style mặc định |

## Ví dụ Docker

Mount các tệp tùy chỉnh giao diện vào container:

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

Hoặc với docker-compose:

```yaml
services:
  authagonal:
    build: .
    ports:
      - "8080:8080"
    volumes:
      - ./my-branding/branding.json:/app/wwwroot/branding.json
      - ./my-branding/assets:/app/wwwroot/branding
    environment:
      - Storage__ConnectionString=...
      - Issuer=https://auth.example.com
```

## CSS tùy chỉnh

Tùy chọn `customCssUrl` tải một stylesheet bổ sung sau các style mặc định, nên các quy tắc của bạn được ưu tiên. Hữu ích để thay đổi phông chữ, điều chỉnh khoảng cách, hoặc thay đổi style các phần tử cụ thể.

### Các lớp CSS có sẵn

| Lớp | Phần tử |
|---|---|
| `.auth-container` | Wrapper toàn trang (flex center) |
| `.auth-card` | Thẻ đăng nhập (hộp trắng có bóng) |
| `.auth-logo` | Khu vực logo/tiêu đề |
| `.auth-logo h1` | Tiêu đề văn bản (khi không có hình logo) |
| `.auth-logo-img` | Hình ảnh logo (khi `logoUrl` được đặt) |
| `.auth-title` | Tiêu đề trang ("Đăng nhập", "Đặt lại mật khẩu") |
| `.auth-subtitle` | Văn bản phụ dưới tiêu đề |
| `.form-group` | Wrapper trường biểu mẫu |
| `.form-group label` | Nhãn trường |
| `input` | Ô nhập liệu |
| `.btn-primary` | Nút hành động chính |
| `.btn-secondary` | Nút phụ (ví dụ: "Tiếp tục với SSO") |
| `.alert-error` | Thông báo lỗi |
| `.alert-success` | Thông báo thành công |
| `.link` | Liên kết văn bản |
| `.sso-notice` | Thông báo phát hiện SSO |
| `.password-requirements` | Danh sách yêu cầu độ mạnh mật khẩu |

### Thuộc tính CSS tùy chỉnh

Màu chính được hiển thị dưới dạng thuộc tính CSS tùy chỉnh. Bạn có thể ghi đè nó trong CSS tùy chỉnh thay vì sử dụng `branding.json`:

```css
:root {
  --color-primary: #059669;
}
```

### Ví dụ: Nền và phông chữ tùy chỉnh

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}

.auth-card {
  border-radius: 16px;
  box-shadow: 0 4px 24px rgba(0, 0, 0, 0.2);
}

.auth-logo h1 {
  font-family: 'Inter', sans-serif;
  font-weight: 800;
}
```

## Các cấp độ tùy chỉnh

| Cấp độ | Bạn cần làm | Đường dẫn cập nhật |
|---|---|---|
| **Chỉ cấu hình** | Mount `branding.json` + logo | Mượt mà — cập nhật Docker image, giữ nguyên mount |
| **Cấu hình + CSS** | Thêm `customCssUrl` với các ghi đè style | Tương tự — các lớp CSS ổn định |
| **Gói npm** | `npm install @drawboard/authagonal-login`, tùy chỉnh `branding.json`, build vào `wwwroot/` | Có thể cập nhật — `npm update` tải phiên bản mới |
| **Fork SPA** | Clone `login-app/`, chỉnh sửa mã nguồn, build riêng | Bạn sở hữu giao diện — cập nhật máy chủ độc lập |
| **Viết giao diện riêng** | Xây dựng frontend hoàn toàn tùy chỉnh dựa trên API xác thực | Toàn quyền kiểm soát — xem [API Xác thực](auth-api) cho hợp đồng |

Xem `demos/custom-server/` để thấy ví dụ hoạt động với tùy chỉnh giao diện (chủ đề xanh lá, "Acme Corp").
