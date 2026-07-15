---
layout: default
title: Mở rộng quy mô
locale: vi
---

# Mở rộng quy mô

Authagonal được thiết kế để mở rộng cả theo chiều dọc và chiều ngang mà không cần cấu hình đặc biệt.

## Không trạng thái theo thiết kế

Tất cả trạng thái bền vững được lưu trữ trong table store nền tảng: Azure Table Storage, hoặc DynamoDB trên backend AWS. Không có trạng thái trong tiến trình nào yêu cầu sticky session hoặc phối hợp giữa các instance:

- **Khóa ký**: được tải từ Table Storage, làm mới mỗi giờ
- **Mã ủy quyền và refresh token**: được lưu trong Table Storage với cơ chế sử dụng một lần
- **Chống phát lại SAML**: ID yêu cầu được theo dõi trong Table Storage với xóa nguyên tử
- **OIDC state và PKCE verifier**: được lưu trong Table Storage
- **Cấu hình client và provider**: được lấy theo từng yêu cầu từ Table Storage

## Mã hóa cookie (Data Protection)

Các khóa Data Protection của ASP.NET Core được tự động lưu trữ bền vững vào Azure Blob Storage khi sử dụng chuỗi kết nối Azure Storage thực. Điều này có nghĩa là cookie được ký bởi một instance có thể được giải mã bởi bất kỳ instance nào khác: không cần sticky session.

Đối với phát triển local với Azurite, các khóa Data Protection sẽ sử dụng phương thức lưu trữ dựa trên tệp mặc định.

Bạn cũng có thể chỉ định một blob URI cụ thể thông qua cấu hình (đường dẫn managed-identity, được ưu tiên trong production):

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

Trên backend AWS, hãy truyền một S3 client + bucket cho `AddAuthagonalAwsStorage` để lưu bền vững vòng khóa vào S3; nếu không có nó, vòng khóa nằm trong bộ nhớ và cookie sẽ hỏng khi khởi động lại và giữa các node. Xem [Cài đặt → Backend AWS](installation#aws-backend).

## Bộ nhớ đệm theo instance

Một số lượng nhỏ các giá trị được đọc nhiều, thay đổi chậm được lưu trong bộ nhớ đệm trên mỗi instance để giảm số lượt truy cập Table Storage:

| Dữ liệu | Thời gian cache | Ảnh hưởng khi dữ liệu cũ |
|---|---|---|
| Tài liệu khám phá OIDC | 60 phút | Chậm nhận biết việc xoay khóa IdP |
| Metadata SAML IdP | 60 phút | Tương tự |
| Các origin CORS được phép | 60 phút | Origin mới mất tối đa một giờ để lan truyền |

Các bộ nhớ đệm này phù hợp cho môi trường production. Tất cả thời lượng đều có thể cấu hình qua phần cấu hình `Cache`, xem [Cấu hình](configuration). Nếu bạn cần lan truyền ngay lập tức, hãy khởi động lại các instance bị ảnh hưởng.

## Giới hạn tốc độ

Các endpoint dễ bị lạm dụng (đăng ký theo IP, đặt lại mật khẩu theo email đích, SCIM theo client, đăng ký client động theo IP, xem [Cấu hình → Giới hạn tốc độ](configuration#giới-hạn-tốc-độ)) được bảo vệ bởi một bộ giới hạn tốc độ tích hợp sẵn.

Các giới hạn được thực thi **trong tiến trình trên mỗi node** phía sau seam `IRateLimiter`, nên với N instance thì trần hiệu dụng là N× giá trị được cấu hình. Điều đó là có chủ đích: bộ giới hạn là một phương án dự phòng chống lại việc lạm dụng mất kiểm soát một node đơn lẻ, còn giới hạn toàn cục có thẩm quyền thuộc về biên (WAF / ingress / CDN), nơi thấy toàn bộ lưu lượng trước khi nó được cân bằng tải.

## Clustering

Nhiều instance phối hợp thông qua một **cuộc bầu chọn leader** và một **event bus xuyên node**, cả hai đều nằm phía sau các backend có thể thay thế:

- **Bầu chọn leader**: một cuộc bầu chọn dựa trên lease (`Cluster:LeaseTtlSeconds`, mặc định 30s, được gia hạn ở khoảng nửa khoảng thời gian đó). Đúng một node giữ lease; quyền leader được chuyển giao tự động khi leader ngừng hoạt động. Công việc do-leader-đảm-nhận (hiện tại là xoay vòng khóa ký, khi được bật) chỉ chạy trên leader để tránh việc sinh khóa đồng thời.
- **Event bus**: các thông báo xuyên node (ví dụ vô hiệu hóa cache trong các host đa tenant), được thăm dò mỗi `Cluster:PollIntervalSeconds` (mặc định 3s).

Mỗi instance tạo một ID node ngẫu nhiên 12 ký tự hex khi khởi động để tự định danh; nó không được lưu trữ bền vững.

### Backends

**Mặc định là trong tiến trình**: một node đơn luôn là leader của chính nó, và các sự kiện chỉ mang tính cục bộ, đúng cho một instance với không cấu hình. Các triển khai đa node thay bằng một backend thực qua callback `configureClustering` trên `AddAuthagonal`:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS: leadership + event bus via DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` chỉ đăng ký event bus, giữ lại lease trong tiến trình (luôn-là-leader); hãy dùng chúng trên các node phải nhận các sự kiện cụm nhưng không bao giờ được tranh giành quyền leader.

> **Lưu ý:** với mặc định trong tiến trình trên nhiều node, *mọi* node đều tin rằng mình là leader. Điều đó vô hại với hầu hết khối lượng công việc, nhưng hãy bật một backend lease thực trước khi bật `Auth:KeyRotationEnabled` trên nhiều instance.

Xem trang [Cấu hình](configuration#cluster) để biết tất cả các thiết lập cluster.

### Triển khai đa tenant

Trong chế độ đa tenant (`AddAuthagonalCore()`), không có dịch vụ nền nào được đăng ký: `TokenCleanupService`, `GrantReconciliationService`, `SigningKeyRotationService`, và các dịch vụ seed cấu hình đều là một phần của thành phần đơn tenant `AddAuthagonal()`. Host quản lý chúng theo từng tenant.

## Phân vùng nóng của chỉ mục tên

Tìm kiếm theo tiền tố tên trong trang quản trị được hỗ trợ bởi các bảng chỉ mục `UserFirstNames` / `UserLastNames`, vốn sử dụng một **phân vùng nóng duy nhất**. Ở quy mô lớn, điều này giới hạn thông lượng ghi chỉ mục ở khoảng 2.000 thao tác/giây, có thể trở thành nút thắt cổ chai khi tạo/cập nhật người dùng dưới tải nặng. Nếu bạn không cung cấp tìm kiếm theo tên trong trang quản trị, hãy đặt `Storage:NameIndexesEnabled = false` để bỏ qua hoàn toàn các lượt ghi này. Xem [Cấu hình](configuration).

## Proxy tin cậy và các endpoint nội bộ

Khi chạy nhiều instance phía sau một bộ cân bằng tải:

- **Forwarded headers**: giới hạn tốc độ và khóa tài khoản lập khóa dựa trên IP của client, được phân giải từ `X-Forwarded-For`. Hãy đặt `ForwardedHeaders:KnownNetworks` thành CIDR của ingress / pod của bạn để IP của client không thể bị giả mạo giữa các instance. `ForwardedHeaders:ForwardLimit` mặc định là `1`. Xem [Cấu hình](configuration#forwarded-headers-proxy-tin-cậy).
- **Các endpoint nội bộ**: `/_internal/backchannel-logout` được bảo vệ bằng IP nguồn (chỉ loopback / riêng tư) trừ khi `Cluster:Secret` được đặt, trong trường hợp đó người gọi phải xuất trình bí mật trong header `X-Cluster-Secret` (so sánh trong thời gian hằng số). Hãy đặt bí mật mỗi khi lưu lượng nội bộ được định tuyến qua bất cứ thứ gì ghi đè IP nguồn.

## Khuyến nghị mở rộng quy mô

**Mở rộng theo chiều dọc**: tăng CPU và bộ nhớ trên một instance đơn. Hữu ích để xử lý nhiều yêu cầu đồng thời hơn trên mỗi instance.

**Mở rộng theo chiều ngang**: chạy nhiều instance phía sau bộ cân bằng tải. Không cần sticky session hoặc cache chia sẻ. Mỗi instance hoạt động hoàn toàn độc lập.

**Thu nhỏ về không**: Authagonal hỗ trợ triển khai thu nhỏ về không (ví dụ: Azure Container Apps với `minReplicas: 0`). Yêu cầu đầu tiên sau thời gian nhàn rỗi sẽ có thời gian khởi động nguội vài giây trong khi runtime .NET khởi tạo và các khóa ký được tải từ bộ lưu trữ.
