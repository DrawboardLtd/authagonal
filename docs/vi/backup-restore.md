---
layout: default
title: Backup & Restore
---

# Sao lưu & Khôi phục

Authagonal cung cấp hai công cụ CLI để sao lưu và khôi phục dữ liệu Azure Table Storage. Cả hai đều là ứng dụng console .NET trong thư mục `tools/`.

## Sao lưu

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### Tùy chọn

| Tùy chọn | Mô tả |
|---|---|
| `--connection-string <conn>` | Chuỗi kết nối Azure Table Storage (hoặc đặt biến môi trường `STORAGE_CONNECTION_STRING`) |
| `--output <dir>` | Thư mục đầu ra (mặc định: `./backups`) |
| `--incremental` | Chỉ sao lưu các thực thể đã thay đổi kể từ lần sao lưu cuối |
| `--tables <t1,t2,...>` | Danh sách bảng phân cách bằng dấu phẩy (mặc định: tất cả bảng Authagonal) |
| `--gzip` | Nén các tệp sao lưu bằng gzip (`.jsonl.gz`) |
| `--dry-run` | Hiển thị những gì sẽ được sao lưu mà không ghi |

### Định dạng đầu ra

Mỗi bản sao lưu tạo một thư mục có dấu thời gian:

```
backups/
  20260329-120000/          (sao lưu đầy đủ)
    Users.jsonl
    Clients.jsonl
    Grants.jsonl
    ...
    _manifest.json
  20260329-180000-incr/     (gia tăng, nén)
    Users.jsonl.gz
    _manifest.json
```

Mỗi tệp `.jsonl` chứa một đối tượng JSON trên mỗi dòng (một cho mỗi thực thể bảng). Với `--gzip`, các tệp được nén thành `.jsonl.gz`. `_manifest.json` ghi lại dấu thời gian sao lưu, chế độ, nén, số lượng thực thể và các hash tệp SHA-256 để xác minh tính toàn vẹn.

### Xác minh tính toàn vẹn

Mỗi manifest sao lưu bao gồm một từ điển `FileHashes` ánh xạ tên tệp tới các hash SHA-256 của chúng. Trong quá trình khôi phục, tính toàn vẹn của tệp được tự động xác minh dựa trên các hash này trước khi bất kỳ dữ liệu nào được ghi. Nếu phát hiện hash không khớp, việc khôi phục sẽ hủy bỏ với một lỗi.

### Sao lưu gia tăng

Sử dụng `--incremental` để chỉ sao lưu các thực thể đã được sửa đổi kể từ lần sao lưu thành công cuối cùng. Công cụ sử dụng thuộc tính `Timestamp` tích hợp của Azure Table Storage để lọc và theo dõi mốc nước cao trong tệp `.lastbackup` trong thư mục đầu ra.

Nếu không có tệp `.lastbackup`, lần chạy gia tăng đầu tiên sẽ thực hiện sao lưu đầy đủ.

### Bảng mặc định

Công cụ sao lưu bao gồm tất cả các bảng Authagonal theo mặc định:

`Users`, `UserEmails`, `UserLogins`, `UserExternalIds`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `Roles`

Các bảng tạm thời (`SamlReplayCache`, `OidcStateStore`) bị loại trừ theo mặc định — thêm chúng rõ ràng với `--tables` nếu cần.

### Khóa ký bị loại trừ theo mặc định

Bảng `SigningKeys` **bị loại trừ khỏi các bản sao lưu theo mặc định** (`Backup:IncludeSigningKeys` mặc định là `false`). Đối với các host sử dụng nguồn khóa cục bộ (lưu trong bảng), bảng này chứa **khóa riêng** ký JWT — ghi nó vào một tệp sao lưu văn bản thuần sẽ cho phép bất kỳ ai đọc được bản sao lưu giả mạo token. (Các host ký qua HashiCorp Vault Transit không giữ khóa riêng nào trong bảng, nên mối lo này không áp dụng cho chúng.)

> ⚠️ Chỉ tùy chọn bật qua `Backup:IncludeSigningKeys` khi chính đích sao lưu được mã hóa khi lưu trữ và được kiểm soát truy cập. Điều tương tự áp dụng cho phần còn lại của bản sao lưu: với nhà cung cấp bí mật **văn bản thuần** mặc định, các bản sao lưu cũng chứa bí mật của client OIDC thượng nguồn và seed TOTP / MFA dưới dạng văn bản rõ — xem [Cấu hình → Nhà cung cấp bí mật](configuration#nhà-cung-cấp-bí-mật).

Khi khôi phục, tính toàn vẹn của tệp được xác minh dựa trên các hash SHA-256 của manifest trước khi bất kỳ dữ liệu nào được ghi (xem [Xác minh tính toàn vẹn](#xác-minh-tính-toàn-vẹn)); một hash không khớp sẽ hủy bỏ việc khôi phục.

## Khôi phục

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### Tùy chọn

| Tùy chọn | Mô tả |
|---|---|
| `--connection-string <conn>` | Chuỗi kết nối Azure Table Storage (hoặc đặt biến môi trường `STORAGE_CONNECTION_STRING`) |
| `--input <dir>` | Thư mục sao lưu để khôi phục |
| `--mode <mode>` | Chế độ khôi phục: `upsert` (mặc định), `merge`, hoặc `clean` |
| `--tables <t1,t2,...>` | Danh sách bảng để khôi phục (mặc định: tất cả tệp `.jsonl`/`.jsonl.gz` trong bản sao lưu) |
| `--dry-run` | Hiển thị những gì sẽ được khôi phục mà không ghi |

### Chế độ khôi phục

| Chế độ | Hành vi |
|---|---|
| `upsert` | Chèn hoặc thay thế mỗi thực thể. Dữ liệu hiện có bị ghi đè. |
| `merge` | Chèn hoặc hợp nhất. Các thuộc tính hiện có không có trong bản sao lưu được giữ lại. |
| `clean` | Xóa tất cả dữ liệu hiện có trong mỗi bảng trước khi khôi phục. |

Các tệp sao lưu nén gzip (`.jsonl.gz`) được phát hiện và giải nén tự động — không cần cờ bổ sung.

### Mã thoát

| Mã | Ý nghĩa |
|---|---|
| `0` | Thành công |
| `1` | Lỗi (thiếu tham số, đầu vào không hợp lệ) |
| `2` | Thành công một phần (một số thực thể có lỗi) |

## Docker

Cả hai công cụ đều có Docker image để chạy trong CI hoặc mà không cần cài đặt .NET SDK:

```bash
# Sao lưu
docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  drawboardci/authagonal-backup --output /backups

# Khôi phục
docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  drawboardci/authagonal-restore --input /backups/20260329-120000
```

## Lên lịch sao lưu

Cho môi trường production, chạy công cụ sao lưu theo lịch (ví dụ: đầy đủ hàng ngày + gia tăng hàng giờ):

```bash
# Sao lưu đầy đủ hàng ngày (nén)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Gia tăng hàng giờ (nén)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```
