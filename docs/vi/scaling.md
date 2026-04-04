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

Các endpoint đăng ký được bảo vệ bởi bộ giới hạn tốc độ phân tán tích hợp sẵn (5 lượt đăng ký mỗi IP mỗi giờ). Khi chạy nhiều instance, số lượt giới hạn tốc độ được tự động chia sẻ giữa tất cả các instance thông qua giao thức gossip — không cần phối hợp bên ngoài.

### Cách hoạt động

Mỗi instance duy trì bộ đếm riêng trong bộ nhớ bằng CRDT G-Counter. Các instance phát hiện lẫn nhau qua UDP multicast và trao đổi trạng thái qua HTTP mỗi vài giây. Tổng số hợp nhất trên tất cả các instance được sử dụng để đưa ra quyết định giới hạn tốc độ.

Điều này có nghĩa là giới hạn tốc độ được thực thi trên toàn cục: nếu một client truy cập 3 instance khác nhau, cả 3 đều biết tổng số là 3, không phải mỗi instance là 1.

### Cấu hình cluster

Clustering được **bật mặc định** mà không cần cấu hình. Các instance trên cùng mạng tự động phát hiện lẫn nhau qua UDP multicast (`239.42.42.42:19847`).

Đối với các môi trường không hỗ trợ multicast (một số cloud VPC), hãy cấu hình một URL nội bộ có cân bằng tải làm phương án dự phòng:

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

Để tắt hoàn toàn clustering (giới hạn tốc độ chỉ trên local):

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

Xem trang [Cấu hình](configuration) để biết tất cả các thiết lập cluster.

### Suy giảm mềm

- **Không tìm thấy peer** — hoạt động như bộ giới hạn tốc độ chỉ trên local (mỗi instance thực thi giới hạn riêng)
- **Peer không thể truy cập** — trạng thái được biết cuối cùng của peer đó vẫn được sử dụng; các peer cũ được loại bỏ sau 30 giây
- **Multicast không khả dụng** — phát hiện thất bại trong im lặng; gossip chuyển sang sử dụng `InternalUrl` nếu đã được cấu hình

## Khuyến nghị mở rộng quy mô

**Mở rộng theo chiều dọc** — tăng CPU và bộ nhớ trên một instance đơn. Hữu ích để xử lý nhiều yêu cầu đồng thời hơn trên mỗi instance.

**Mở rộng theo chiều ngang** — chạy nhiều instance phía sau bộ cân bằng tải. Không cần sticky session hoặc cache chia sẻ. Mỗi instance hoạt động hoàn toàn độc lập.

**Thu nhỏ về không** — Authagonal hỗ trợ triển khai thu nhỏ về không (ví dụ: Azure Container Apps với `minReplicas: 0`). Yêu cầu đầu tiên sau thời gian nhàn rỗi sẽ có thời gian khởi động nguội vài giây trong khi runtime .NET khởi tạo và các khóa ký được tải từ bộ lưu trữ.
