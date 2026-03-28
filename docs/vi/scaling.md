---
layout: default
title: Mở rộng quy mô
locale: vi
---

# Mở rộng quy mô

Authagonal được thiết kế để mở rộng cả theo chiều dọc và chiều ngang mà không cần cấu hình đặc biệt.

## Không trạng thái theo thiết kế

Tất cả trạng thái bền vững được lưu trữ trong Azure Table Storage. Không có trạng thái trong tiến trình nào yêu cầu sticky session hoặc phối hợp giữa các instance:

- **Khóa ký** — được tải từ Table Storage, làm mới mỗi giờ
- **Mã ủy quyền và refresh token** — được lưu trong Table Storage với cơ chế sử dụng một lần
- **Chống phát lại SAML** — ID yêu cầu được theo dõi trong Table Storage với xóa nguyên tử
- **OIDC state và PKCE verifier** — được lưu trong Table Storage
- **Cấu hình client và provider** — được lấy theo từng yêu cầu từ Table Storage

## Mã hóa cookie (Data Protection)

Các khóa Data Protection của ASP.NET Core được tự động lưu trữ bền vững vào Azure Blob Storage khi sử dụng chuỗi kết nối Azure Storage thực. Điều này có nghĩa là cookie được ký bởi một instance có thể được giải mã bởi bất kỳ instance nào khác — không cần sticky session.

Đối với phát triển local với Azurite, các khóa Data Protection sẽ sử dụng phương thức lưu trữ dựa trên tệp mặc định.

Bạn cũng có thể chỉ định một blob URI cụ thể thông qua cấu hình:

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

## Bộ nhớ đệm theo instance

Một số lượng nhỏ các giá trị được đọc nhiều, thay đổi chậm được lưu trong bộ nhớ đệm trên mỗi instance để giảm số lượt truy cập Table Storage:

| Dữ liệu | Thời gian cache | Ảnh hưởng khi dữ liệu cũ |
|---|---|---|
| Tài liệu khám phá OIDC | 60 phút | Chậm nhận biết việc xoay khóa IdP |
| Metadata SAML IdP | 60 phút | Tương tự |
| Các origin CORS được phép | 60 phút | Origin mới mất tối đa một giờ để lan truyền |

Các bộ nhớ đệm này phù hợp cho môi trường production. Nếu bạn cần lan truyền ngay lập tức, hãy khởi động lại các instance bị ảnh hưởng.

## Giới hạn tốc độ

Authagonal không bao gồm giới hạn tốc độ tích hợp sẵn. Giới hạn tốc độ nên được áp dụng ở tầng hạ tầng (bộ cân bằng tải, API gateway hoặc reverse proxy) nơi có cái nhìn thống nhất về toàn bộ lưu lượng truy cập qua các instance.

## Khuyến nghị mở rộng quy mô

**Mở rộng theo chiều dọc** — tăng CPU và bộ nhớ trên một instance đơn. Hữu ích để xử lý nhiều yêu cầu đồng thời hơn trên mỗi instance.

**Mở rộng theo chiều ngang** — chạy nhiều instance phía sau bộ cân bằng tải. Không cần sticky session hoặc cache chia sẻ. Mỗi instance hoạt động hoàn toàn độc lập.

**Thu nhỏ về không** — Authagonal hỗ trợ triển khai thu nhỏ về không (ví dụ: Azure Container Apps với `minReplicas: 0`). Yêu cầu đầu tiên sau thời gian nhàn rỗi sẽ có thời gian khởi động nguội vài giây trong khi runtime .NET khởi tạo và các khóa ký được tải từ bộ lưu trữ.
