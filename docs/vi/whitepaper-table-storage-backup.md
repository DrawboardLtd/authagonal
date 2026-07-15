---
layout: default
title: Sách trắng Sao lưu Table Storage
locale: vi
---

# Sao lưu Azure Table Storage: Một cách tiếp cận thực tiễn

**Cách Authagonal triển khai sao lưu đầy đủ và tăng dần cho một kho NoSQL không lược đồ**

---

## Vấn đề

Azure Table Storage là một kho key-value có chi phí thấp và khả năng mở rộng cực lớn, nhưng nó không cung cấp cơ chế sao lưu gốc nào. Không có snapshot, không có khôi phục theo thời điểm, không có nút xuất dữ liệu. Nếu một lần triển khai tồi làm hỏng dữ liệu, hoặc một người vận hành vô tình xóa một bảng, việc khôi phục hoàn toàn phụ thuộc vào bất cứ thứ gì bạn tự xây dựng.

Đối với một nền tảng danh tính như Authagonal, nơi các bảng lưu giữ người dùng, thông tin xác thực, các cấp quyền OAuth, khóa ký, cấu hình SSO, và trạng thái cung cấp SCIM, thì rủi ro rất lớn. Mất dữ liệu này không chỉ làm hỏng một ứng dụng; nó khóa mọi người ở ngoài.

Bài viết này mô tả chiến lược sao lưu mà Authagonal sử dụng: cách nó xuất dữ liệu, cách các bản sao lưu tăng dần hoạt động bất chấp mô hình truy vấn hạn chế của Table Storage, cách theo dõi các thao tác xóa, và cách các phần ghép lại thành một pipeline sao lưu sẵn sàng cho production.

## Mục tiêu thiết kế

1. **Sao lưu đầy đủ và tăng dần.** Một bản sao lưu đầy đủ hàng ngày là ổn cho các triển khai nhỏ, nhưng ở quy mô lớn, các bản tăng dần theo giờ giữ cho cửa sổ sao lưu ngắn và chi phí lưu trữ thấp.
2. **Vòng lặp trung thực.** Mọi thuộc tính của thực thể (chuỗi, số nguyên, boolean, DateTimeOffset, GUID, nhị phân) đều phải sống sót qua một chu kỳ sao lưu/khôi phục mà không bị ép kiểu hay mất dữ liệu.
3. **Hỗ trợ đa tenant.** Authagonal dùng tiền tố tên bảng để cô lập các tenant (ví dụ `acmecorpUsers`, `acmecorpClients`). Sao lưu và khôi phục phải nhận biết tiền tố để một storage account đơn có thể chứa nhiều tenant với các lịch sao lưu độc lập.
4. **Lưu trữ có thể cắm được.** Các bản sao lưu nên hoạt động với hệ thống tệp cục bộ trong quá trình phát triển và với blob storage (hoặc bất kỳ đích nào khác) trong production, mà không thay đổi logic lõi.
5. **Đầu ra dễ đọc cho con người.** Khi có sự cố, một người vận hành nên có thể mở một tệp sao lưu trong trình soạn thảo văn bản và thấy những gì bên trong nó.

## Kiến trúc

Hệ thống sao lưu được cấu trúc như một thư viện .NET (`Authagonal.Backup`) với các lớp bọc CLI mỏng cho các thao tác sao lưu và khôi phục. Thư viện được tách khỏi máy chủ Authagonal chính để nó có thể được dùng như một công cụ độc lập, trong một container Docker, hoặc nhúng vào một công việc theo lịch.

```
Authagonal.Backup (library)
  BackupService         -- orchestrates full/incremental export
  RestoreService        -- imports backup data into Table Storage
  MergeService          -- consolidates full + incrementals into one snapshot
  RollupService         -- merge + cleanup of old backups
  IBackupTarget         -- write abstraction (filesystem, blob, etc.)
  IBackupSource         -- read abstraction
  FileSystemBackupTarget/Source -- local filesystem implementation

tools/Authagonal.Backup     -- CLI entry point for backup
tools/Authagonal.Restore    -- CLI entry point for restore
```

### Trừu tượng hóa lưu trữ

Các dịch vụ lõi không bao giờ chạm trực tiếp vào hệ thống tệp. Chúng vận hành dựa trên hai interface:

**IBackupTarget** cung cấp bốn thao tác: mở một stream có thể ghi cho một tệp sao lưu, ghi một manifest, lấy watermark cuối cùng (để lập lịch tăng dần), và đặt một watermark mới.

**IBackupSource** cung cấp phía đọc: đọc một manifest, mở một stream có thể đọc, liệt kê các ID sao lưu theo trình tự thời gian, liệt kê các tệp trong một bản sao lưu, và xóa một bản sao lưu.

Các triển khai trên hệ thống tệp rất đơn giản (các thư mục có dấu thời gian với các tệp JSONL bên trong), nhưng sự trừu tượng hóa nghĩa là việc chuyển sang Azure Blob Storage hoặc S3 chỉ đòi hỏi triển khai đúng hai interface này.

## Sao lưu đầy đủ

Một bản sao lưu đầy đủ lặp qua mọi bảng Authagonal, truy vấn tất cả thực thể, và ghi chúng vào các tệp JSONL (một đối tượng JSON mỗi dòng, một tệp mỗi bảng).

Quy trình sao lưu:

1. Tạo một ID sao lưu từ dấu thời gian UTC hiện tại (ví dụ `20260329-120000`).
2. Với mỗi bảng trong số 20 bảng Authagonal mặc định, truy vấn `QueryAsync<TableEntity>` của SDK Azure Table Storage với kích thước trang là 1.000.
3. Tuần tự hóa mỗi thực thể thành một từ điển JSON phẳng, giữ lại tất cả thuộc tính, bao gồm các thuộc tính hệ thống (`PartitionKey`, `RowKey`, `Timestamp`, `ETag`).
4. Ghi mỗi thực thể đã tuần tự hóa thành một dòng đơn vào `{TableName}.jsonl` (hoặc `{TableName}.jsonl.gz` nếu nén được bật).
5. Ghi số lượng thực thể và thời lượng theo từng bảng vào một manifest (`_manifest.json`).
6. Cập nhật tệp watermark `.lastbackup` bằng thời gian bắt đầu sao lưu.

Các bảng không tồn tại trong storage account sẽ bị bỏ qua âm thầm (HTTP 404 được bắt và bỏ qua). Các bảng tạm thời như `SamlReplayCache` và `OidcStateStore` bị loại trừ theo mặc định vì nội dung của chúng chỉ tồn tại thoáng qua.

### Định dạng đầu ra

```
backups/
  20260329-120000/
    Users.jsonl
    Clients.jsonl
    Grants.jsonl
    GrantsBySubject.jsonl
    ...
    _manifest.json
```

Một dòng đơn trong `Users.jsonl` trông như thế này:

```json
{"PartitionKey":"u_abc123","RowKey":"profile","Timestamp":"2026-03-28T09:14:22+00:00","ETag":"W/\"...\"","Email":"alice@example.com","DisplayName":"Alice","CreatedAt":"2025-11-01T00:00:00+00:00"}
```

JSONL được chọn thay vì CSV hay một định dạng nhị phân vì nó bảo toàn bản chất không lược đồ, không đồng nhất của các thực thể Table Storage (các thực thể khác nhau trong cùng một bảng có thể có các thuộc tính khác nhau), có thể stream được (không cần đệm toàn bộ bảng vào bộ nhớ), và có thể kiểm tra trực tiếp bằng các công cụ tiêu chuẩn như `jq` hoặc bất kỳ trình soạn thảo văn bản nào.

### Nén

Khi cờ `--gzip` được đặt, mỗi tệp JSONL được bọc trong một stream GZip ở mức `CompressionLevel.Optimal` trước khi ghi. Phần mở rộng tệp đổi thành `.jsonl.gz`. Công cụ khôi phục tự động phát hiện GZip bằng cách kiểm tra các magic byte (`0x1f 0x8b`) ở đầu mỗi tệp, nên không cần cờ nào trong lúc khôi phục.

## Sao lưu tăng dần

### Mẹo dùng Timestamp

Azure Table Storage tự động duy trì một thuộc tính `Timestamp` trên mọi thực thể, được cập nhật ở mỗi lần chèn hoặc thay thế. Đây là một thuộc tính do máy chủ quản lý, các ứng dụng không thể đặt nó. Hệ thống sao lưu khai thác điều này bằng cách lọc các truy vấn thành `Timestamp gt datetime'{watermark}'`, trong đó watermark là thời gian bắt đầu của bản sao lưu thành công gần nhất.

Điều này nghĩa là một bản sao lưu tăng dần chỉ tải về các thực thể đã được tạo hoặc sửa đổi kể từ lần chạy trước. Với một hệ thống có 500.000 thực thể mà 200 thực thể đã thay đổi trong giờ vừa qua, bản sao lưu tăng dần chuyển 200 hàng thay vì 500.000.

Watermark được lưu trong một tệp `.lastbackup` ở thư mục gốc của bản sao lưu. Nếu tệp không tồn tại (lần chạy đầu tiên, hoặc sau khi dọn dẹp thủ công), bản sao lưu quay về một lần xuất đầy đủ. Các ID sao lưu tăng dần bao gồm một hậu tố `-incr` (ví dụ `20260329-180000-incr`) và manifest ghi lại `"mode": "incremental"` cùng với giá trị watermark đã được dùng để lọc.

### Chi phí của bộ lọc Timestamp

Cần thành thật về một hạn chế: `Timestamp` không được lập chỉ mục. Azure Table Storage chỉ lập chỉ mục `PartitionKey` và `RowKey`. Một bộ lọc trên `Timestamp gt datetime'...'` dẫn đến một lần quét toàn bảng: Azure đọc mọi thực thể ở phía máy chủ và đánh giá điều kiện trước khi trả về các kết quả khớp. Việc lọc giảm lượng dữ liệu truyền tải (chỉ các thực thể đã thay đổi mới đi qua đường truyền), nhưng không giảm chi phí đọc ở phía máy chủ.

Quan trọng hơn, cách tiếp cận hiện tại quét **cả 20 bảng** một cách riêng lẻ, ngay cả khi chỉ một bảng có thay đổi. Đó là 20 lần quét toàn bảng cho mỗi bản sao lưu tăng dần, bất kể thực tế có bao nhiêu thực thể đã thay đổi.

Ở khối lượng dữ liệu danh tính điển hình của Authagonal (hàng chục nghìn thực thể, không phải hàng triệu), điều này hoàn toàn chấp nhận được: các lần quét nhanh, việc đọc rẻ ($0.00036 cho mỗi 10.000 giao dịch), và thao tác chỉ đọc mà không ảnh hưởng đến lưu lượng đang hoạt động. Phần về [mở rộng vượt ra ngoài quét theo timestamp](#scaling-beyond-timestamp-scans) thảo luận cách điều này có thể tiến hóa.

### Vấn đề xóa

Bộ lọc `Timestamp` nắm bắt một cách thanh lịch các thao tác chèn và cập nhật, nhưng nó không thể nắm bắt các thao tác xóa. Một thực thể đã xóa đơn giản là biến mất: không có `Timestamp` để lọc, không có tombstone nào do chính Table Storage để lại.

Authagonal giải quyết điều này bằng việc theo dõi tombstone ở cấp ứng dụng.

## Theo dõi Tombstone

Mọi kho dữ liệu trong Authagonal (người dùng, client, cấp quyền, khóa ký, tên miền SSO, nhà cung cấp SAML/OIDC, thông tin xác thực MFA, tài nguyên SCIM, vai trò) đều chấp nhận một phụ thuộc `ITombstoneWriter` tùy chọn. Khi một kho xóa một thực thể, nó ghi một bản ghi tombstone vào một bảng `Tombstones` chuyên dụng:

| Cột | Giá trị |
|---|---|
| `PartitionKey` | Tên bảng logic (ví dụ `"Users"`) |
| `RowKey` | `"{originalPartitionKey}\|{originalRowKey}"` |
| `DeletedAt` | Dấu thời gian UTC của lần xóa |

Đây là một kênh phụ nhẹ, chủ yếu là nối thêm. Việc ghi tombstone là một upsert đơn giản, được gộp lô đến giới hạn giao dịch 100 thực thể của Azure cho các thao tác hàng loạt.

Trong một bản sao lưu tăng dần, sau khi xuất các thực thể đã sửa đổi từ mỗi bảng, dịch vụ sao lưu truy vấn bảng `Tombstones` để tìm các bản ghi có `Timestamp > watermark`. Chúng được ghi vào một tệp `_tombstones.jsonl` riêng trong thư mục sao lưu, với một định dạng đã chuẩn hóa:

```json
{"Table":"Users","PartitionKey":"u_abc123","RowKey":"profile","DeletedAt":"2026-03-29T14:30:00+00:00"}
```

Điều này nghĩa là một bản sao lưu tăng dần nắm bắt một bức tranh hoàn chỉnh về những gì đã thay đổi: các thực thể được thêm/sửa đổi (từ các tệp JSONL theo từng bảng) và các thực thể bị xóa (từ tệp tombstones).

## Hợp nhất và Rollup

Theo thời gian, một thư mục sao lưu tích lũy một bản sao lưu đầy đủ và nhiều bản tăng dần. Để khôi phục về trạng thái hiện tại, tất cả chúng sẽ cần được áp dụng theo thứ tự. **MergeService** hợp nhất chúng thành một bản sao lưu đầy đủ duy nhất.

Thuật toán hợp nhất:

1. Nạp tập thực thể của bản sao lưu đầy đủ cho mỗi lần một bảng (để giới hạn mức sử dụng bộ nhớ).
2. Xếp từng bản tăng dần lên trên theo thứ tự thời gian: các giá trị mới hơn ghi đè các giá trị cũ hơn, khóa theo `(PartitionKey, RowKey)`.
3. Áp dụng các tombstone: với mỗi bộ `(Table, PartitionKey, RowKey)` trong các tệp tombstone, xóa thực thể khỏi tập đã hợp nhất.
4. Ghi tập thực thể kết quả thành một bản sao lưu đầy đủ mới.

**RollupService** bọc điều này kèm dọn dẹp: sau một lần hợp nhất thành công, nó xóa bản sao lưu đầy đủ cũ và tất cả các bản tăng dần đã được gộp vào. Điều này giữ cho mức sử dụng lưu trữ không tăng vô hạn.

Một lịch production điển hình có thể trông như thế này:

- **Hàng giờ:** Sao lưu tăng dần
- **Hàng ngày (2 giờ sáng):** Sao lưu đầy đủ
- **Hàng tuần:** Rollup (hợp nhất các bản tăng dần hàng ngày + hàng giờ của tuần trước, xóa các bản gốc)

## Khôi phục

Công cụ khôi phục đọc một thư mục sao lưu và ghi các thực thể trở lại vào Azure Table Storage. Nó hỗ trợ ba chế độ:

**Upsert** (mặc định): Mỗi thực thể được chèn hoặc thay thế. Các thực thể hiện có với cùng khóa sẽ bị ghi đè. Đây là chế độ an toàn nhất cho việc khôi phục sau thảm họa.

**Merge**: Mỗi thực thể được chèn hoặc hợp nhất. Các thuộc tính có trong bản sao lưu ghi đè các thuộc tính tương ứng trong thực thể hiện có, nhưng các thuộc tính tồn tại trong bảng đang hoạt động mà không có trong bản sao lưu sẽ được giữ lại. Hữu ích cho các lần khôi phục một phần.

**Clean**: Tất cả các thực thể hiện có trong mỗi bảng đích bị xóa trước khi khôi phục. Điều này tạo ra một bản sao chính xác của trạng thái sao lưu, với cái giá là một lần quét toàn bảng (có thể chậm) để xóa dữ liệu hiện có.

### Độ trung thực kiểu dữ liệu

Một thách thức chính khi đưa dữ liệu Table Storage đi vòng qua JSON là bảo toàn các kiểu thuộc tính. Table Storage hỗ trợ sẵn các chuỗi, số nguyên (Int32/Int64), double, boolean, DateTimeOffset, Guid, và nhị phân. JSON không có biểu diễn gốc cho hầu hết các kiểu này.

Dịch vụ khôi phục dùng các phương pháp phỏng đoán để phục hồi các kiểu từ biểu diễn chuỗi JSON của chúng:

- **DateTimeOffset**: Các chuỗi dài từ 19 đến 35 ký tự, bắt đầu bằng một chữ số, và phân tích được thành ISO 8601 sẽ được khôi phục thành `DateTimeOffset`.
- **Guid**: Các chuỗi có đúng 36 ký tự và phân tích được thành một GUID sẽ được khôi phục thành `Guid`.
- **Số**: Các số JSON được thử lần lượt là `Int32`, rồi `Int64`, rồi `double`, theo thứ tự đó.
- **Boolean và null**: Ánh xạ trực tiếp.

Cách tiếp cận phỏng đoán này bao phủ các mẫu dữ liệu thực tế của Authagonal mà không đòi hỏi một sổ đăng ký lược đồ hay các chú thích kiểu trong định dạng sao lưu.

### Xử lý lỗi

Các thao tác khôi phục có khả năng chịu lỗi ở cấp thực thể. Nếu một thực thể riêng lẻ ghi thất bại (ví dụ do một lỗi Azure thoáng qua), bộ đếm lỗi được tăng nhưng việc khôi phục vẫn tiếp tục. Kết quả cuối cùng báo cáo số lượng thành công và lỗi theo từng bảng, và tiến trình thoát với mã `2` cho thành công một phần, khác với `0` (thành công hoàn toàn) và `1` (lỗi nghiêm trọng).

## Đa tenant

Authagonal hỗ trợ các triển khai đa tenant, nơi các bảng của mỗi tenant được đặt tiền tố (ví dụ `acmecorpUsers`, `contosoclients`). Cả sao lưu và khôi phục đều chấp nhận một cờ `--prefix` được thêm vào trước các tên bảng logic khi giao tiếp với Azure Table Storage.

Điều này nghĩa là:
- Sao lưu với `--prefix acmecorp` đọc từ `acmecorpUsers`, `acmecorpClients`, v.v., nhưng ghi các tệp có tên `Users.jsonl`, `Clients.jsonl` (tên logic).
- Khôi phục với `--prefix contoso` đọc `Users.jsonl` và ghi vào `contosoUsers`.

Điều này giúp dễ dàng nhân bản dữ liệu của một tenant, di chuyển giữa các môi trường, hoặc khôi phục một tenant mà không ảnh hưởng đến các tenant khác.

## Manifest

Mọi bản sao lưu đều bao gồm một tệp `_manifest.json` ghi lại:

- **BackupId**: Mã định danh có dấu thời gian (ví dụ `20260329-120000` hoặc `20260329-180000-incr`)
- **Mode**: `"full"` hoặc `"incremental"`
- **BackupTimestamp**: Thời điểm bản sao lưu bắt đầu (UTC)
- **Watermark**: Với các bản tăng dần, dấu thời gian ngưỡng dùng để lọc
- **Compressed**: Các tệp có được nén GZip hay không
- **Tables**: Một từ điển ánh xạ tên bảng đến số lượng thực thể và thời lượng
- **TombstoneCount**: Số lượng bản ghi tombstone (chỉ với bản tăng dần)
- **TotalEntities**: Tổng số lượng thực thể trên tất cả các bảng
- **DurationSeconds**: Thời gian thực tế cho lần chạy sao lưu
- **FileHashes**: Các băm SHA-256 của mỗi tệp sao lưu để xác minh tính toàn vẹn

Manifest vừa đóng vai trò một bảng điều khiển vận hành (bản sao lưu lớn cỡ nào? mất bao lâu? bảng nào lớn nhất?) vừa là một lưới an toàn (việc xác minh băm trong lúc khôi phục phát hiện các tệp bị hỏng hoặc bị can thiệp).

## Đặc tính vận hành

**Tốc độ sao lưu** bị giới hạn bởi thông lượng truy vấn của Azure Table Storage, thường là 5.000-10.000 thực thể mỗi giây mỗi bảng. Một bản sao lưu đầy đủ gồm 100.000 thực thể trên 20 bảng hoàn tất trong chưa đầy một phút. Các bản sao lưu tăng dần với vài trăm thực thể đã thay đổi hoàn tất trong vài giây.

**Mức sử dụng bộ nhớ** là tối thiểu. Dịch vụ sao lưu stream các thực thể trực tiếp ra đĩa, nó không bao giờ nạp toàn bộ một bảng vào bộ nhớ. Dịch vụ hợp nhất xử lý mỗi lần một bảng, chỉ nạp tập thực thể của bảng đó. Với các bảng rất lớn (hàng triệu thực thể), dấu chân bộ nhớ khi hợp nhất tỷ lệ thuận với bảng đơn lớn nhất.

**Chính sách thử lại** được cấu hình với backoff theo cấp số mũ: 5 lần thử lại, bắt đầu ở 500ms, tối đa 30 giây. Điều này bao phủ việc điều tiết thoáng qua mà Table Storage áp dụng khi tải nặng.

Chế độ **Dry run** (`--dry-run`) liệt kê các thực thể mà không ghi bất kỳ tệp nào, hữu ích để xác thực kết nối và ước tính kích thước sao lưu trước khi cam kết chạy đầy đủ.

## Mở rộng vượt ra ngoài quét theo Timestamp

Cách tiếp cận dựa trên `Timestamp` là thực dụng ở quy mô vừa phải, nhưng chi phí của nó tỷ lệ thuận với tổng kích thước dữ liệu, chứ không phải với số lượng thay đổi. Khi các bảng lớn lên, 20 lần quét toàn bảng cho mỗi bản sao lưu tăng dần ngày càng trở nên lãng phí. Sự tiến hóa tự nhiên là một **bảng nhật ký thay đổi hợp nhất**.

Điểm mấu chốt là cơ chế tombstone đã chứng minh mẫu này cho các thao tác xóa. Bảng `Tombstones` là một chỉ mục xuyên bảng duy nhất, gọn nhẹ: mọi thao tác xóa trên cả 20 bảng dữ liệu đều được ghi lại ở một nơi, có thể truy vấn theo timestamp. Việc mở rộng điều này để bao phủ mọi thao tác thay đổi (chèn, cập nhật, và xóa) sẽ loại bỏ hoàn toàn nhu cầu quét các bảng dữ liệu.

### Thiết kế nhật ký thay đổi

Một bảng nhật ký thay đổi với các khóa phân vùng theo lô thời gian sẽ trông như thế này:

| PartitionKey | RowKey | Thuộc tính |
|---|---|---|
| `2026-03-29T18` | `Users\|u_abc123\|profile` | `Op = "upsert"` |
| `2026-03-29T18` | `Clients\|c_456\|config` | `Op = "upsert"` |
| `2026-03-29T18` | `Users\|u_xyz789\|profile` | `Op = "delete"` |

Khóa phân vùng là một lô theo giờ, nên việc tìm tất cả các thay đổi kể từ bản sao lưu cuối cùng trở thành một tập các **truy vấn điểm theo khóa phân vùng**, thao tác nhanh nhất mà Table Storage hỗ trợ. Dịch vụ sao lưu sẽ:

1. Truy vấn nhật ký thay đổi cho tất cả các phân vùng lô theo giờ kể từ watermark. Đây là một thao tác có chỉ mục, không phải một lần quét.
2. Với mỗi mục `upsert`, lấy thực thể hiện tại từ bảng dữ liệu theo đúng `PartitionKey`/`RowKey` của nó, cũng là một lần đọc điểm có chỉ mục.
3. Với mỗi mục `delete`, ghi lại tombstone trực tiếp từ nhật ký thay đổi. Không cần một bảng tombstones riêng.

Điều này làm cho chi phí sao lưu tỷ lệ thuận với số lượng thay đổi, chứ không phải tổng kích thước dữ liệu. Một truy vấn vào một bảng chỉ mục gọn nhẹ thay thế cho 20 lần quét toàn bảng. Nó cũng hợp nhất cơ chế tombstone: nhật ký thay đổi nắm bắt các thao tác tạo, cập nhật, và xóa một cách đồng nhất, nên bảng `Tombstones` riêng trở nên dư thừa.

### Vì sao chưa làm

Sự đánh đổi là chi phí phụ trên đường ghi. Mọi thao tác thay đổi trong mọi kho sẽ cần thêm một lần ghi vào bảng nhật ký thay đổi. Hạ tầng gần như đã có sẵn: `ITombstoneWriter` đã được tiêm vào mọi kho và được gọi ở mỗi lần xóa. Việc mở rộng nó thành một `IChangeTracker` cũng kích hoạt ở các lần upsert là một lần tái cấu trúc đơn giản.

Nhưng "đơn giản" không có nghĩa là "miễn phí". Nó thêm độ trễ vào mọi thao tác hướng đến người dùng (một lần ghi Table Storage bổ sung), làm tăng số giao dịch lưu trữ, và giới thiệu một mối lo ngại mới về tính nhất quán (điều gì xảy ra nếu lần ghi dữ liệu thành công nhưng lần ghi nhật ký thay đổi thất bại?). Ở khối lượng hiện tại, 20 lần quét được lọc theo timestamp hoàn tất trong vài giây và tốn một phần nhỏ của một xu. Nhật ký thay đổi sẽ là nước đi đúng đắn nếu các bảng phát triển đến hàng triệu thực thể, nhưng hiện tại, cách tiếp cận đơn giản hơn thắng.

## Tóm tắt

Cách tiếp cận này cố ý giữ sự đơn giản. Thay vì xây dựng một pipeline change-data-capture phức tạp hoặc dựa vào các tính năng đặc thù của Azure vốn có thể không tồn tại cho Table Storage, Authagonal dùng một mẩu siêu dữ liệu mà Azure *thực sự* đảm bảo, đó là `Timestamp` do máy chủ quản lý, kết hợp với việc theo dõi tombstone ở cấp ứng dụng cho các thao tác xóa.

Kết quả là một hệ thống sao lưu mà:

- Tạo ra các tệp JSONL dễ đọc cho con người, khả chuyển
- Hỗ trợ các chế độ đầy đủ và tăng dần với quản lý watermark tự động
- Nắm bắt chính xác các thao tác tạo, cập nhật, *và* xóa
- Xử lý việc đặt tiền tố bảng đa tenant một cách trong suốt
- Kết hợp gọn gàng (hợp nhất, rollup, khôi phục chọn lọc)
- Chạy như một công cụ độc lập không phụ thuộc vào máy chủ Authagonal

Sự trừu tượng hóa lưu trữ nghĩa là cùng một logic có thể nhắm đến đĩa cục bộ, Azure Blob Storage, S3, hoặc bất kỳ đích nào khác. Định dạng đủ đơn giản đến mức ngay cả khi không có công cụ khôi phục, một người vận hành vẫn có thể tái tạo dữ liệu bằng `jq` và Azure CLI.
