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
  20260329-180000-incr/     (sao lưu gia tăng)
    Users.jsonl
    _manifest.json
```

Mỗi tệp `.jsonl` chứa một đối tượng JSON trên mỗi dòng (một cho mỗi thực thể bảng). `_manifest.json` ghi lại dấu thời gian sao lưu, chế độ và số lượng thực thể.

### Sao lưu gia tăng

Sử dụng `--incremental` để chỉ sao lưu các thực thể đã được sửa đổi kể từ lần sao lưu thành công cuối cùng. Công cụ sử dụng thuộc tính `Timestamp` tích hợp của Azure Table Storage để lọc và theo dõi mốc nước cao trong tệp `.lastbackup` trong thư mục đầu ra.

Nếu không có tệp `.lastbackup`, lần chạy gia tăng đầu tiên sẽ thực hiện sao lưu đầy đủ.

### Bảng mặc định

Công cụ sao lưu bao gồm tất cả các bảng Authagonal theo mặc định:

`Users`, `UserEmails`, `UserLogins`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`

Các bảng tạm thời (`SamlReplayCache`, `OidcStateStore`) bị loại trừ theo mặc định — thêm chúng rõ ràng với `--tables` nếu cần.

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
| `--tables <t1,t2,...>` | Danh sách bảng để khôi phục (mặc định: tất cả tệp `.jsonl` trong bản sao lưu) |
| `--dry-run` | Hiển thị những gì sẽ được khôi phục mà không ghi |

### Chế độ khôi phục

| Chế độ | Hành vi |
|---|---|
| `upsert` | Chèn hoặc thay thế mỗi thực thể. Dữ liệu hiện có bị ghi đè. |
| `merge` | Chèn hoặc hợp nhất. Các thuộc tính hiện có không có trong bản sao lưu được giữ lại. |
| `clean` | Xóa tất cả dữ liệu hiện có trong mỗi bảng trước khi khôi phục. |

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
# Sao lưu đầy đủ hàng ngày
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups

# Gia tăng hàng giờ
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental
```
