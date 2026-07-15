---
layout: default
title: Tùy chỉnh giao diện
locale: vi
---

# Tùy chỉnh giao diện đăng nhập

SPA đăng nhập có thể cấu hình tại thời điểm chạy qua tệp `branding.json` được phục vụ từ thư mục gốc web. Không cần build lại, chỉ cần mount cấu hình và tài nguyên của bạn.

## Cách hoạt động

Khi khởi động, SPA tải `/branding.json`. Nếu tệp không tồn tại hoặc không thể truy cập, các giá trị mặc định sẽ được sử dụng. (Một máy chủ host cũng có thể nhúng sẵn cấu hình dưới dạng payload khởi tạo `<script type="application/json" id="authagonal-boot">`; khi payload này có mặt, SPA đọc nó thay vì tải về.) Cấu hình điều khiển:

- Tên ứng dụng (hiển thị trong tiêu đề và tiêu đề trang)
- Hình ảnh logo, với một "chip" nền tùy chọn cho từng chế độ
- Màu chính (nút, liên kết, vòng focus), với một biến thể tùy chọn cho chế độ tối
- Màu nền trang và nền thẻ, theo từng chế độ
- Hiển thị liên kết quên mật khẩu và đăng ký
- Chế độ tối mặc định (sáng / theo hệ điều hành / tối)
- Tùy chọn bộ chọn ngôn ngữ
- Chân trang "Powered by Authagonal"
- CSS tùy chỉnh cho phong cách sâu hơn

## Cấu hình

Đặt tệp `branding.json` trong thư mục `wwwroot/` (hoặc mount vào container Docker):

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
  "darkPrimaryColor": "#3b82f6",
  "darkMode": "auto",
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
| `showForgotPassword` | `boolean` | `true` | Hiển thị/ẩn liên kết "Forgot password?" trên trang đăng nhập |
| `showRegistration` | `boolean` | `false` | Hiển thị/ẩn liên kết đăng ký tự phục vụ |
| `customCssUrl` | `string \| null` | `null` | URL đến tệp CSS tùy chỉnh được tải sau các style mặc định |
| `welcomeTitle` | `LocalizedString` | `null` | Ghi đè tiêu đề trang đăng nhập (chuỗi thuần hoặc `{ "en": "...", "de": "..." }`) |
| `welcomeSubtitle` | `LocalizedString` | `null` | Ghi đè phụ đề trang đăng nhập |
| `languages` | `array \| null` | `null` | Tùy chọn bộ chọn ngôn ngữ (`[{ "code": "en", "label": "English" }, ...]`). `null` hiển thị tất cả ngôn ngữ được đóng gói ngoại trừ các locale mang tính vui đùa (xem [Bản địa hóa](localization)). |
| `poweredBy` | `boolean` | `true` | Hiển thị/ẩn chân trang "Powered by Authagonal" trên các trang xác thực |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | Chủ đề mặc định khi khách truy cập chưa chọn: `"off"` (chỉ sáng), `"auto"` (theo tùy chọn của hệ điều hành), `"force"` (luôn tối). Nút chuyển chủ đề của khách truy cập vẫn thắng. |
| `lightBg` | `string \| null` | `null` | Màu nền trang ở chế độ sáng |
| `lightCardBg` | `string \| null` | `null` | Màu nền thẻ/biểu mẫu ở chế độ sáng |
| `darkBg` | `string \| null` | `null` | Màu nền trang ở chế độ tối |
| `darkCardBg` | `string \| null` | `null` | Màu nền thẻ/biểu mẫu ở chế độ tối |
| `darkPrimaryColor` | `string \| null` | `null` | Ghi đè `primaryColor` ở chế độ tối |
| `lightLogoBg` | `string \| null` | `null` | Nền chip logo ở chế độ sáng (xem bên dưới) |
| `darkLogoBg` | `string \| null` | `null` | Nền chip logo ở chế độ tối (xem bên dưới) |

Giá trị màu phải là một màu hex (`#rgb`, `#rrggbb`, `#rrggbbaa`) hoặc một biểu thức `rgb()`/`rgba()`/`hsl()`/`hsla()`; mọi thứ khác đều bị bỏ qua. Các màu theo từng chế độ được chèn vào dưới dạng một quy tắc `<style id="branding-theme-vars">` sau các style được đóng gói (giá trị sáng tại `:root`, giá trị tối tại `.dark`), nên một giá trị tối có thể khác với đối tác sáng của nó.

### Chip nền logo

Nếu logo của bạn có phần hình màu trắng hoặc trong suốt, nó có thể biến mất trên thẻ sáng. Đặt `lightLogoBg` và/hoặc `darkLogoBg` để hiển thị logo bên trong một "chip" có đệm, bo tròn với màu nền đó:

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

Chip (một wrapper `data-auth="logo-chip"` được điều khiển bởi biến CSS `--auth-logo-bg`) chỉ nhận phần đệm và nền khi một nền logo được cấu hình, nên các tenant không đặt nó vẫn thấy logo nằm sát trên thẻ đúng như trước. Hai trường này độc lập với nhau: chỉ đặt `lightLogoBg` để tạo chip cho logo ở chế độ sáng và để trần ở chế độ tối.

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

Tùy chọn `customCssUrl` tải một stylesheet bổ sung sau các style mặc định, nên các quy tắc của bạn được ưu tiên. Hữu ích để thay đổi phông chữ, điều chỉnh khoảng cách, hoặc thay đổi style các phần tử cụ thể. URL phải cùng origin (các URL tương đối như `/branding/custom.css` là hợp lệ); các stylesheet khác origin bị bỏ qua âm thầm.

### Thuộc tính CSS tùy chỉnh

Giao diện đăng nhập phơi bày một số thuộc tính CSS tùy chỉnh để kiểm soát chi tiết:

| Thuộc tính | Mặc định | Mô tả |
|---|---|---|
| `--brand-primary` | `#2563eb` | Màu chính cho nút, liên kết, vòng focus |
| `--auth-bg` | `#f3f4f6` | Màu nền trang |
| `--auth-card-bg` | `#ffffff` | Màu nền thẻ/biểu mẫu |
| `--auth-logo-bg` | `transparent` | Nền chip logo (phần đệm của chip chỉ xuất hiện khi một nền logo được cấu hình) |
| `--auth-radius` | `0.5rem` | Bán kính bo góc cho thẻ xác thực |
| `--auth-font` | *(kế thừa; ngăn xếp phông hệ thống)* | Họ phông cho thẻ xác thực |
| `--auth-heading` | `#111827` | Màu chữ tiêu đề |

Các biến màu ở đây ánh xạ trực tiếp tới các trường cấu hình (`primaryColor`, `lightBg`/`darkBg`, `lightCardBg`/`darkCardBg`, `lightLogoBg`/`darkLogoBg`), nên hãy ưu tiên dùng cấu hình cho các thay đổi màu đơn giản và để dành CSS tùy chỉnh cho mọi thứ khác.

Ghi đè chúng trong CSS tùy chỉnh của bạn:

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

Giao diện đăng nhập sử dụng Tailwind CSS. CSS tùy chỉnh có thể nhắm mục tiêu các phần tử HTML tiêu chuẩn và các lớp tiện ích Tailwind. Các component UI được xuất (`Button`, `Input`, `Card`, `Alert`, v.v.) sử dụng Tailwind nội bộ.

## Chế độ tối

SPA đăng nhập được đóng gói với các chủ đề sáng, tối, và **system** (hệ thống). Nút chuyển chủ đề luôn hiển thị trong bố cục. Lựa chọn của người dùng được lưu vào `localStorage` dưới khóa `auth-theme`.

### Cách hoạt động

- **Mặc định:** cho đến khi khách truy cập chọn một chủ đề, tùy chọn tùy chỉnh giao diện `darkMode` đặt giá trị mặc định: `"off"` (sáng), `"auto"` (hệ thống, mặc định), hoặc `"force"` (tối). Một khi khách truy cập dùng nút chuyển, lựa chọn của họ luôn thắng.
- **Phát hiện:** khi chủ đề là "system", SPA quan sát `window.matchMedia('(prefers-color-scheme: dark)')` và tự động áp dụng lại chủ đề khi tùy chọn của hệ điều hành thay đổi.
- **Áp dụng:** SPA bật/tắt một lớp `.dark` trên `<html>`. Biến thể dark của Tailwind (`&:where(.dark, .dark *)`) kích hoạt các style tối được biên dịch vào mọi component.
- **Lưu trữ bền:** các lựa chọn tường minh "light" / "dark" / "system" được lưu trong `localStorage`.

### Biến CSS

Các giá trị sáng được khai báo tại `:root`; các ghi đè chế độ tối được giới hạn phạm vi trong `.dark`, nên tùy chỉnh giao diện của tenant trong `customCssUrl` luôn được ưu tiên khi được cung cấp.

| Biến | Sáng | Tối |
|---|---|---|
| `--auth-bg` | `#f3f4f6` (hoặc `lightBg`) | `#030712` (hoặc `darkBg`) |
| `--auth-card-bg` | `#ffffff` (hoặc `lightCardBg`) | `#111827` (hoặc `darkCardBg`) |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent` (hoặc `lightLogoBg`) | `transparent` (hoặc `darkLogoBg`) |
| `--brand-primary` | `#2563eb` (hoặc `primaryColor`) | giá trị sáng (hoặc `darkPrimaryColor`) |

### Vô hiệu hóa hoặc ghi đè

Tùy chỉnh giao diện của tenant luôn thắng. Để buộc một chủ đề duy nhất, hãy đặt các giá trị của riêng bạn trong `customCssUrl`:

```css
/* Force dark palette regardless of user choice */
:root {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
.dark {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

Để loại bỏ hoàn toàn nút chuyển chủ đề, hãy dùng đường dẫn gói npm: import `AuthLayout` và render mà không có nút chuyển, hoặc fork SPA.

### Thuộc tính Data

Tất cả các phần tử biểu mẫu đăng nhập đều có các thuộc tính `data-auth` để nhắm mục tiêu bằng CSS và tự động hóa kiểm thử:

| Thuộc tính | Phần tử |
|---|---|
| `data-auth="page"` | Wrapper trang chính |
| `data-auth="header"` | Phần header |
| `data-auth="logo-chip"` | Wrapper quanh hình ảnh logo (chỉ có đệm khi một nền logo được đặt) |
| `data-auth="logo"` | Hình ảnh logo |
| `data-auth="app-name"` | Tiêu đề tên ứng dụng |
| `data-auth="content"` | Vùng nội dung chính |
| `data-auth="languages"` | Bộ chọn ngôn ngữ |
| `data-auth="language-trigger"` | Nút kích hoạt bộ chọn ngôn ngữ |
| `data-auth="theme-toggle"` | Nút chuyển chủ đề sáng/hệ thống/tối |
| `data-auth="powered-by"` | Chân trang "Powered by Authagonal" |

Nhắm mục tiêu chúng trong CSS tùy chỉnh của bạn:

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

### Ví dụ: Nền và phông chữ tùy chỉnh

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## Các cấp độ tùy chỉnh

| Cấp độ | Bạn cần làm | Đường dẫn cập nhật |
|---|---|---|
| **Chỉ cấu hình** | Mount `branding.json` + logo | Mượt mà: cập nhật Docker image, giữ nguyên các mount |
| **Cấu hình + CSS** | Thêm `customCssUrl` với các ghi đè style | Tương tự: các lớp CSS ổn định |
| **Gói npm** | `npm install @authagonal/login`, tùy chỉnh `branding.json`, build vào `wwwroot/` | Có thể cập nhật: `npm update` tải phiên bản mới |
| **Fork SPA** | Clone `login-app/`, chỉnh sửa mã nguồn, build riêng | Bạn sở hữu giao diện: cập nhật máy chủ độc lập |
| **Viết giao diện riêng** | Xây dựng frontend hoàn toàn tùy chỉnh dựa trên API xác thực | Toàn quyền kiểm soát: xem [API Xác thực](auth-api) cho hợp đồng |

Xem `demos/custom-server/` để thấy ví dụ hoạt động với tùy chỉnh giao diện (chủ đề xanh lá, "Acme Corp").
