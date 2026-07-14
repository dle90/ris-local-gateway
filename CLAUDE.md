# Quy ước codebase — RIS Local Gateway

Gateway DICOM cài tại site: nhận DICOM từ modality (C-ECHO / MWL / MPPS / C-STORE), forward MWL/MPPS
lên RIS (HTTPS) và ảnh lên PACS (STOW-RS). 4 project:

| Project | Vai trò |
|---|---|
| `Core` | Config model + persistence (`ConfigStore`), RIS client (`IRisClient`), request/response models, util dùng chung |
| `Dicom` | fo-dicom SCP provider, mapper DICOM ↔ model, STOW client, spool |
| `Service` | Windows Service host: worker DICOM + forwarder nền |
| `ConfigApp` | WPF GUI cấu hình (ghi `config.json`) |

Luồng phụ thuộc một chiều: `ConfigApp → Core`, `Service → Dicom → Core`.

---

## 1. Mọi object trao đổi PHẢI có struct Request/Response — KHÔNG bóc field trực tiếp từ JSON/DICOM

**Mọi payload gửi đi / nhận về (HTTP body, DICOM dataset) PHẢI được biểu diễn bằng một model C# tường
minh** đặt ở `Core/Ris/Models/` (`[JsonPropertyName]` cho từng field). **KHÔNG** đọc field kiểu
động — không `JsonDocument`/`JObject["field"]`, không `dataset.GetSingleValueOrDefault(...)` rải rác
trong lớp nghiệp vụ (provider/service).

### Vì sao
- Shape dữ liệu tập trung một chỗ → đổi field chỉ sửa model, không phải dò khắp code.
- Đối chiếu được 1-1 với đối tác (his-core `LgsXxxRequest`) — mỗi model có xmldoc trỏ tới counterpart.
- Tránh gõ sai key JSON/DICOM tag mỗi nơi một kiểu; field optional khai `string?` phản ánh đúng "có thể vắng".

### Cách làm đúng
- Request gửi RIS: `LgsMppsInProgressRequest`, `LgsGetWorkListRequest`… Response nhận về: `LgsGetWorkListResponse`,
  envelope `RisBaseResponse<T>`, lỗi `RisErrorResponse`. Mỗi model 1 file trong `Core/Ris/Models/`.
- Đích deserialize phải là model đã khai báo; parse an toàn qua `JsonUtil.TryDeserialize<T>` (không tự `JsonSerializer` rải rác).

```csharp
// ✘ Sai — bóc field động từ JSON
var msg = JsonDocument.Parse(body).RootElement.GetProperty("message").GetString();

// ✓ Đúng — deserialize vào model đã khai báo
var err = JsonUtil.TryDeserialize<RisErrorResponse>(body);
var msg = err?.Message;
```

## 2. Chuyển đổi giữa DICOM/entity và model PHẢI qua mapper — không nhét vào provider/service

**Việc bóc DICOM tag → request model, và ghép response model → DICOM dataset, PHẢI nằm trong một
mapper** ở `Dicom/Mappers/` (đặt tên `To...`). Provider/service chỉ **điều phối**: nhận request →
gọi mapper → gọi client → trả kết quả. KHÔNG đọc/ghép `DicomTag` trực tiếp trong provider.

### Vì sao
- Provider mỏng, dễ đọc (chỉ thấy luồng nghiệp vụ, không lẫn ~250 dòng map tag).
- Logic map tập trung, tái dùng + test được như pure function (không cần dựng cả association).
- Đổi field DICOM chỉ sửa mapper.

### Cách làm đúng
- `WorklistDicomMapper.ToLgsGetWorkListRequest(cfind)` / `.ToWorklistDataset(item)`.
- `MppsDicomMapper.ToMppsInProgressRequest(sop, ds)` / `.ToMppsCompletedRequest(...)` / `.ToMppsDiscontinuedRequest(...)`.
- Helper bóc tag dùng chung (`NullIfEmpty`, `AddIfPresent`) ở `Dicom/Mappers/DicomMapHelpers.cs`.
- Mapper là **pure function**: chỉ nhận dataset/model, trả model/dataset — không I/O, không config, không log nghiệp vụ.

```csharp
// ✘ Sai — provider tự bóc tag
var payload = new LgsMppsInProgressRequest {
    SopInstanceUid = sop,
    Modality = ds.GetSingleValueOrDefault(DicomTag.Modality, ""),
    // …20 dòng đọc tag ở đây
};

// ✓ Đúng — provider gọi mapper
var payload = MppsDicomMapper.ToMppsInProgressRequest(sop, request.Dataset);
```

## 3. KHÔNG hot-load config từ ổ đĩa — nạp vào memory, đọc từ memory, sửa thì invalidate

**Cấu hình đọc từ MEMORY qua `ConfigStore.Load()`; KHÔNG đọc `config.json` từ đĩa trên hot path**
(mỗi C-FIND / MPPS / forward STOW). `ConfigStore` nạp file vào bản in-memory một lần rồi giữ lại.
**Mỗi khi cấu hình thay đổi thì invalidate bản in-memory:**
- Cùng tiến trình sửa (ConfigApp): `Save()` / `SaveAsync()` ghi đĩa xong cập nhật cache ngay.
- Tiến trình khác sửa file (ConfigApp ghi, Service đọc): `FileSystemWatcher` của `GatewayWorker` gọi
  `ConfigStore.Reload()` để đọc lại đĩa + thay cache.

### Vì sao
- Đọc + deserialize JSON từ đĩa mỗi request là I/O thừa trên đường nóng của gateway.
- Một nguồn sự thật in-memory cho toàn tiến trình, tránh mỗi thành phần tự đọc đĩa một kiểu (lệch trạng thái).

### Cách làm đúng
- Consumer (HttpRisClient, PacsStowClient, SpoolForwarder, SpoolStore…) luôn gọi `_configStore.Load()`
  (memory), KHÔNG tự `File.ReadAllText(config)`.
- `Load()` trả instance cache DÙNG CHUNG — chỉ ĐỌC. Luồng CHỈNH SỬA (ConfigApp Save) phải dùng
  `LoadCopy()` (deep copy) rồi `Save(copy)` — không mutate object cache (Save lỗi sẽ làm memory lệch đĩa).
- Khi save một section, mutate TỪNG field UI quản lý trên bản copy — KHÔNG `new XxxConfig{...}` thay cả
  section (sẽ reset ngầm các field không có trên UI, vd `CompressionCodec`, `Htj2kModalities`).
- Chỉ 2 điểm được chạm đĩa: `Save/SaveAsync` (ghi) và `Reload` (đọc lại khi invalidate).
- Watcher phải gọi `Reload()` (KHÔNG `Load()`): `Load()` trả cache cũ nên không phát hiện thay đổi để so sánh.
  `Reload()` đọc lỗi → giữ bản in-memory cũ (không thay bằng default). Watchdog của GatewayWorker còn so
  `LastWriteTimeUtc` mỗi 30s làm fallback cho trường hợp watcher trượt event.

```csharp
// ✘ Sai — đọc đĩa mỗi lần forward
var pacs = JsonSerializer.Deserialize<GatewayConfig>(File.ReadAllText(path)).Pacs;

// ✓ Đúng — đọc từ memory
var pacs = _configStore.Load().Pacs;

// ✓ Đúng — file bị tiến trình khác sửa → invalidate + nạp lại
private async void OnConfigChanged(...) {
    var newCfg = _configStore.Reload();   // ép đọc đĩa + thay cache
    // …so sánh với config cũ để quyết định restart SCP…
}
```

---

## Util & helper dùng chung

Tách một chỗ, tái dùng chéo cả gateway lẫn ConfigApp — không mỗi nơi tự viết lại:
- `Core/Utils/JsonUtil.cs` — `TryDeserialize<T>` (parse an toàn, lỗi → null).
- `Core/Utils/TextUtil.cs` — `Truncate` (cắt chuỗi cho log).
- `Core/Ris/UrlHelper.cs` — normalize + combine URL.
- `Core/Ris/RisAuth.cs` — đăng nhập RIS (dùng chung HttpRisClient + ConfigApp "Kiểm tra tài khoản").
- `Core/Configuration/SecretProtector.cs` — mã hoá mật khẩu (DPAPI LocalMachine).
