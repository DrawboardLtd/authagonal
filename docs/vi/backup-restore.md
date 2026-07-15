---
layout: default
title: Backup & Restore
---

# Sao lưu & Khôi phục

Authagonal cung cấp hai công cụ CLI để sao lưu và khôi phục dữ liệu Azure Table Storage. Cả hai đều là ứng dụng console .NET trong thư mục `tools/`, và cả hai đều là lớp bọc mỏng trên gói NuGet `Authagonal.Backup`. Các host cần sao lưu theo lịch, đa tenant, hoặc không dựa trên hệ thống tệp có thể sử dụng thư viện trực tiếp (xem [Sử dụng thư viện](#sử-dụng-thư-viện)).

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
| `--prefix <prefix>` | Tiền tố tên bảng (cho lưu trữ đa tenant) |
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
    _tombstones.jsonl.gz
    _manifest.json
```

Mỗi tệp `.jsonl` chứa một đối tượng JSON trên mỗi dòng (một cho mỗi thực thể bảng). Với `--gzip`, các tệp được nén thành `.jsonl.gz`. `_manifest.json` ghi lại id sao lưu, dấu thời gian, chế độ (`full` hoặc `incremental`), nén, mốc nước gia tăng, số lượng thực thể trên mỗi bảng, số lượng tombstone, những bảng nào (nếu có) được đọc qua nhật ký thay đổi (`ChangeLogTables`, null nghĩa là quét đầy đủ toàn bộ), và các hash tệp SHA-256 để xác minh tính toàn vẹn.

Các bản sao lưu gia tăng cũng ghi một tệp `_tombstones.jsonl(.gz)` ghi lại các lần xóa kể từ mốc nước: mỗi dòng cho một hàng đã xóa với `Table`, `PartitionKey`, `RowKey`, và `DeletedAt`. Việc khôi phục sẽ phát lại những mục này để các hàng đã xóa không bị dựng lại (xem [Phát lại tombstone](#phát-lại-tombstone)).

Giá trị thực thể được bảo toàn chính xác qua khứ hồi: mỗi hàng được sao lưu mang một dấu định dạng `"@v"` và một chú thích `"{column}@odata.type"` tường minh (`Edm.Guid`, `Edm.DateTime`, `Edm.Binary`, `Edm.Int64`, `Edm.Double`) cho mọi cột mà JSON không thể biểu diễn một cách rõ ràng, nên việc khôi phục ghi lại đúng các kiểu gốc thay vì các giá trị đã bị chuyển thành chuỗi hoặc suy luận lại.

### Xác minh tính toàn vẹn

Mỗi manifest sao lưu bao gồm một từ điển `FileHashes` ánh xạ tên tệp tới các hash SHA-256 của chúng. Trong quá trình khôi phục, tính toàn vẹn của mỗi tệp được xác minh dựa trên các hash này trước khi bất kỳ dữ liệu nào của nó được ghi; một tệp không vượt qua kiểm tra, hoặc một tệp dữ liệu vắng mặt trong manifest, sẽ hủy bỏ việc khôi phục với một lỗi. Các bản sao lưu được ghi trước khi có băm toàn vẹn (không có `FileHashes` trong manifest) không thể xác minh và được khôi phục kèm một cảnh báo lớn thay vào đó. Việc xác minh có thể tắt bằng cách lập trình qua `RestoreOptions.VerifyIntegrity` (mặc định `true`).

### Sao lưu gia tăng

Sử dụng `--incremental` để chỉ sao lưu các thực thể đã được sửa đổi kể từ lần sao lưu thành công cuối cùng. Công cụ sử dụng thuộc tính `Timestamp` tích hợp của Azure Table Storage để lọc và theo dõi mốc nước cao trong tệp `.lastbackup` trong thư mục đầu ra.

Nếu không có tệp `.lastbackup`, lần chạy gia tăng đầu tiên sẽ thực hiện sao lưu đầy đủ.

Mỗi bộ lọc `Timestamp` gia tăng trừ đi một biên độ an toàn nhỏ (`BackupDefaults.WatermarkSkewMargin`, 5 phút) trước khi lọc. Mốc nước đến từ đồng hồ của bên gọi trong khi dấu thời gian của hàng được đóng bởi dịch vụ lưu trữ, nên một thay đổi được commit trong khoảng lệch đồng hồ nếu không sẽ bị bỏ sót bởi lần chạy này và mọi lần chạy sau đó. Việc đọc lại biên độ tốn một vài hàng trùng lặp mỗi lần chạy, mà ngữ nghĩa upsert của khôi phục sẽ khử trùng lặp.

### Bảng mặc định

Công cụ sao lưu bao gồm tất cả các bảng Authagonal theo mặc định (`BackupDefaults.Tables`):

`Users`, `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `ScimGroupRoleMappings`, `Roles`, `Scopes`, `ProvisioningApps`

Các bảng tạm thời (`SamlReplayCache`, `OidcStateStore`, `RevokedTokens`) bị loại trừ theo mặc định vì các mục của chúng bị giới hạn bởi thời gian sống của token; thêm chúng rõ ràng với `--tables` nếu cần. Bảng nhật ký thay đổi `Tombstones` được engine sao lưu xử lý riêng và không nên được liệt kê.

### Khóa ký bị loại trừ theo mặc định

Bảng `SigningKeys` nằm trong danh sách bảng mặc định nhưng **bị lọc ra khỏi các bản sao lưu theo mặc định** (`BackupOptions.IncludeSigningKeys`, mặc định `false`; CLI không bao giờ bật nó). Đối với các host sử dụng nguồn khóa cục bộ (lưu trong bảng), bảng này chứa **khóa riêng** ký JWT, và ghi nó vào một tệp sao lưu văn bản thuần sẽ cho phép bất kỳ ai đọc được bản sao lưu giả mạo token. (Các host ký qua HashiCorp Vault Transit không giữ khóa riêng nào trong bảng, nên mối lo này không áp dụng cho chúng.)

> ⚠️ Chỉ tùy chọn bật qua `BackupOptions.IncludeSigningKeys` khi chính đích sao lưu được mã hóa khi lưu trữ và được kiểm soát truy cập. Điều tương tự áp dụng cho phần còn lại của bản sao lưu: với nhà cung cấp bí mật **văn bản thuần** mặc định, các bản sao lưu cũng chứa bí mật của client OIDC thượng nguồn và seed TOTP / MFA dưới dạng văn bản rõ. Xem [Cấu hình → Nhà cung cấp bí mật](configuration#nhà-cung-cấp-bí-mật).

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
| `--prefix <prefix>` | Tiền tố tên bảng (cho lưu trữ đa tenant) |
| `--dry-run` | Hiển thị những gì sẽ được khôi phục mà không ghi |

### Chế độ khôi phục

| Chế độ | Hành vi |
|---|---|
| `upsert` | Chèn hoặc thay thế mỗi thực thể. Dữ liệu hiện có bị ghi đè. |
| `merge` | Chèn hoặc hợp nhất. Các thuộc tính hiện có không có trong bản sao lưu được giữ lại. |
| `clean` | Xóa tất cả dữ liệu hiện có trong mỗi bảng trước khi khôi phục. |

Các tệp sao lưu nén gzip (`.jsonl.gz`) được phát hiện và giải nén tự động; không cần cờ bổ sung.

### Phát lại tombstone

Sau các tệp dữ liệu, việc khôi phục áp dụng tệp `_tombstones` của bản sao lưu: mỗi khóa được ghi lại sẽ bị xóa khỏi các bảng đã khôi phục (`RestoreOptions.ApplyTombstones`, mặc định `true`). Các lần xóa của một bản gia tăng là một phần trạng thái của nó không kém gì các lần upsert; bỏ qua chúng sẽ dựng lại các hàng đã xóa, kể cả những hàng đã bị xóa theo GDPR, khi khôi phục một chuỗi bản đầy đủ cộng các bản gia tăng. Các bản sao lưu đầy đủ không mang tệp tombstone. Khi khôi phục một bản đầy đủ theo sau bởi các bản gia tăng, hãy áp dụng chúng theo thứ tự cũ nhất trước để một lần tạo lại sau này rơi vào sau một lần xóa trước đó. Hash của tệp tombstone được xác minh dựa trên manifest giống như các tệp dữ liệu.

### Bảo toàn kiểu dữ liệu chính xác

Các hàng được ghi với dấu định dạng `"@v"` mang các chú thích kiểu EDM tường minh, nên việc khôi phục tái tạo đúng các kiểu cột gốc (`Int64`, `Guid`, `Binary`, `DateTime`, `Double`); một chuỗi không có chú thích được khôi phục dưới dạng chuỗi. Các tệp sao lưu cũ không có dấu này sẽ dùng suy luận dựa trên hình dạng, chỉ được giữ lại để các bản sao lưu cũ vẫn khôi phục được (suy luận có thể gán sai kiểu cho các cột chuỗi có hình dạng GUID hoặc ngày tháng).

### Mã thoát

| Mã | Ý nghĩa |
|---|---|
| `0` | Thành công |
| `1` | Lỗi (thiếu tham số, đầu vào không hợp lệ) |
| `2` | Thành công một phần (một số thực thể có lỗi) |

## Sử dụng thư viện

Gói NuGet `Authagonal.Backup` cung cấp các thao tác tương tự theo cách lập trình, cho các dịch vụ nền hoặc điều phối tùy chỉnh:

| Kiểu | Mục đích |
|---|---|
| `BackupService` | Chạy một bản sao lưu đầy đủ hoặc gia tăng đối với một `TableServiceClient`, ghi vào một `IBackupTarget` |
| `RestoreService` | Xác minh các hash và ghi một bản sao lưu trở lại vào Table Storage |
| `MergeService` | Truyền luồng một bản sao lưu đầy đủ cộng các bản gia tăng (và các tombstone của chúng) thành một khung nhìn trạng thái hiện tại |
| `RollupService` | Gộp các bản gia tăng vào một bản sao lưu đầy đủ mới, tùy chọn xóa các đầu vào |
| `BackupOptions` / `RestoreOptions` | Cấu hình theo từng lần chạy |
| `BackupDefaults` | Danh sách bảng mặc định và các preset nhật ký thay đổi |
| `IBackupSource` / `IBackupTarget` | Các lớp trừu tượng lưu trữ; `FileSystemBackupSource` / `FileSystemBackupTarget` là các hiện thực tích hợp sẵn. Hiện thực `IBackupTarget` để ghi vào blob storage hoặc nơi khác. |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### Sao lưu gia tăng dựa trên nhật ký thay đổi

Azure Table Storage chỉ lập chỉ mục `PartitionKey` và `RowKey`, nên một bản sao lưu gia tăng lọc trên `Timestamp` vẫn là một lần quét đầy đủ mỗi bảng. Để tránh điều đó, các store của Authagonal ghi lại mọi thay đổi trong một nhật ký thay đổi qua seam `IChangeWriter` (`Authagonal.Core`), được hiện thực cho Azure bởi `TableChangeWriter` (`Authagonal.AzureProvider`). Đó là một bảng vật lý duy nhất, vẫn được đặt tên là `Tombstones`: PK = tên bảng logic, RK = `"{pk}|{rk}"`, một cột `Op` là `"U"` (upsert) hoặc `"D"` (xóa), và các cột `OrigPK`/`OrigRK` có thẩm quyền (một ký tự `|` bên trong PartitionKey gốc làm cho việc tách RowKey ghép trở nên mơ hồ, nên bộ đọc sao lưu tin vào các cột và chỉ quay lại việc tách cho các hàng cũ). Mỗi khóa giữ một hàng (upsert-replace), nên thao tác cuối cùng trong một cửa sổ sao lưu sẽ thắng.

Với đường dẫn nhật ký thay đổi được bật, một bản sao lưu gia tăng liệt kê các mục nhật ký thay đổi `Op = "U"` của một bảng kể từ mốc nước và point-read từng hàng trực tiếp thay vì quét bảng. Tính năng này **là tùy chọn và tắt theo mặc định**: `BackupOptions.ChangeLoggedTables` null hoặc rỗng nghĩa là mọi bảng ở lại đường dẫn quét, nên cơ chế được xuất xưởng ở trạng thái trơ cho đến khi có một lần chuyển đổi có chủ đích (một lần triển khai không thể âm thầm bỏ sót các hàng bị thay đổi bởi mã tiền-thu-thập). Hai preset:

| Preset | Nội dung |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | Các bảng có các lượt ghi được nhật ký thay đổi thu thập đầy đủ |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | Cùng tập hợp cộng thêm `Users`. Các lượt ghi trạng thái đăng nhập của Users cố tình không được thu thập (đường dẫn nóng, giá trị thấp), nên preset này **chỉ an toàn khi bạn cũng chạy phương án dự phòng quét toàn bộ bên dưới** |

Thuộc tính `ChangeLogTables` của manifest liệt kê những bảng nào một lần chạy đã đọc qua nhật ký thay đổi; null hoặc rỗng nghĩa là lần chạy có phạm vi quét đầy đủ (một bản đầy đủ, một bản gia tăng quét thuần, hoặc một lần quét dự phòng).

### Phương án dự phòng quét toàn bộ

Vì việc thu thập nhật ký thay đổi có thể bỏ sót các lượt ghi (các trường trạng thái đăng nhập, các bộ ghi không phải store, các pod chạy mã tiền-thu-thập trong một lần triển khai), hãy ghép các bản sao lưu gia tăng theo nhật ký thay đổi với một lần quét lại đầy đủ định kỳ. Đặt `BackupOptions.WatermarkOverride` thành dấu thời gian của lần quét phủ đầy đủ cuối cùng và để `ChangeLoggedTables` không đặt cho lần chạy đó: bản gia tăng khi đó lọc trên `Timestamp` trên toàn bộ cửa sổ kể từ lần quét đó, nhặt lên bất cứ thứ gì nhật ký thay đổi không bao giờ thu thập. Một phương án dự phòng hàng ngày bên cạnh các bản gia tăng theo nhật ký thay đổi hàng giờ là một nhịp độ hợp lý. Các lần xóa là lớp thay đổi duy nhất không có tự chữa lành (một lần quét hàng-trực-tiếp không thể thấy một hàng đã biến mất), đó là lý do các store ghi tombstone xóa **trước khi** xóa hàng dữ liệu.

Tất cả các bộ lọc gia tăng, kể cả phương án dự phòng, đều trừ đi `BackupDefaults.WatermarkSkewMargin` (5 phút) khỏi mốc nước; các bên gọi thanh lọc nhật ký thay đổi sau một bản sao lưu phải giới hạn việc thanh lọc bằng cùng biên độ đó nếu không họ xóa các hàng mà lần chạy tiếp theo vẫn cần.

### Rollup

`RollupService.RollupAsync` hợp nhất một bản sao lưu đầy đủ và các bản gia tăng của nó thành một bản sao lưu đầy đủ mới; `RollupAndCleanAsync` bổ sung việc xóa các đầu vào sau đó. Tham số tùy chọn `newBackupId` đặt tên cho kết quả (null suy ra một id dấu thời gian); một ảnh chụp được giữ lại đặc biệt (ví dụ một rollup hàng tuần) phải truyền id của nó ở đây, vì việc lưu giữ dựa trên id liệt kê các id sao lưu vật lý, không phải các manifest.

Trong một lần hợp nhất, các tombstone áp dụng theo thứ tự dấu thời gian: một lần xóa loại bỏ một hàng đã thu thập chỉ khi `Timestamp` của hàng không muộn hơn `DeletedAt` của tombstone. Một khóa bị xóa sớm trong cửa sổ và được tạo lại sau đó có cả một tombstone và một lần thu thập trực tiếp, và hàng được tạo lại sẽ sống sót qua rollup. Các tombstone cũ không có `DeletedAt` loại bỏ vô điều kiện.

## Docker

Công cụ sao lưu đi kèm một Dockerfile (`tools/Authagonal.Backup/Dockerfile`) để chạy trong CI hoặc mà không cần cài đặt .NET SDK:

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

Công cụ khôi phục không có image; hãy chạy nó với .NET SDK (`dotnet run --project tools/Authagonal.Restore`).

## Lên lịch sao lưu

Cho môi trường production, chạy công cụ sao lưu theo lịch (ví dụ: đầy đủ hàng ngày + gia tăng hàng giờ):

```bash
# Sao lưu đầy đủ hàng ngày (nén)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Gia tăng hàng giờ (nén)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

Các host nhúng thư viện thường chạy các bản gia tăng hàng giờ với đường dẫn nhật ký thay đổi được bật, một phương án dự phòng quét toàn bộ hàng ngày, và các rollup định kỳ để giới hạn chuỗi gia tăng.
