# RIS Local Gateway

Local DICOM gateway cài đặt tại site bệnh viện. Lắng nghe các giao tiếp DICOM từ modality (CR/CT/MR/...) và chuyển tiếp lên RIS qua HTTPS.

## Kiến trúc

```
[Modality] ──DICOM──▶ [Gateway Service (Windows Service)] ──HTTPS──▶ [RIS Cloud]
                            ▲
                            │ đọc/ghi config.json
                            │
                     [Gateway ConfigApp (WPF)]
```

| Project | Vai trò |
|---|---|
| `Medisync.RisLocalGateway.Core` | Config model, persistence, password protector (DPAPI), RIS client interface + stub |
| `Medisync.RisLocalGateway.Dicom` | fo-dicom SCP: C-ECHO, C-FIND (MWL), MPPS (N-CREATE/N-SET), C-STORE |
| `Medisync.RisLocalGateway.Service` | Windows Service host, chạy DICOM listener 24/7, watch config file để reload |
| `Medisync.RisLocalGateway.ConfigApp` | WPF GUI để cấu hình URL/tài khoản RIS, AE Title, port DICOM |

## Yêu cầu

- Windows 10/11 hoặc Windows Server 2019+
- .NET 8 SDK (build) / .NET 8 Desktop Runtime (chạy)

## Build

```pwsh
dotnet restore
dotnet build -c Release
```

## Chạy thử (dev)

ConfigApp:
```pwsh
dotnet run --project src/Medisync.RisLocalGateway.ConfigApp
```

Service (chạy như console app khi không cài làm Windows Service):
```pwsh
dotnet run --project src/Medisync.RisLocalGateway.Service
```

## Cài đặt Windows Service

Build self-contained binary trước:
```pwsh
dotnet publish src/Medisync.RisLocalGateway.Service -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Đăng ký service (PowerShell, chạy as Administrator):
```pwsh
sc.exe create "Medisync RIS Local Gateway" `
    binPath= "C:\Program Files\Medisync\RisLocalGateway\Medisync.RisLocalGateway.Service.exe" `
    start= auto `
    DisplayName= "Medisync RIS Local Gateway"

# Tự khởi động lại nếu service crash (lần 1/2 sau 5s, lần 3 sau 30s; reset bộ đếm sau 1 ngày)
sc.exe failure "Medisync RIS Local Gateway" reset= 86400 actions= restart/5000/restart/5000/restart/30000

# Khởi động lại CẢ khi service tự thoát non-zero (không chỉ khi crash)
sc.exe failureflag "Medisync RIS Local Gateway" 1

sc.exe start "Medisync RIS Local Gateway"
```

> **Tự phục hồi 2 lớp:** (1) Windows SCM tự restart cả tiến trình khi crash (cấu hình `sc failure` ở trên);
> (2) bên trong tiến trình, `GatewayWorker` có **watchdog** quét mỗi 30s — nếu DICOM SCP chết ngầm (listener
> không còn LISTEN trong khi tiến trình vẫn sống) thì tự dựng lại listener mà không cần restart service.

Gỡ:
```pwsh
sc.exe stop   "Medisync RIS Local Gateway"
sc.exe delete "Medisync RIS Local Gateway"
```

## Vị trí file

| Loại | Đường dẫn |
|---|---|
| Config | `%ProgramData%\Medisync\RisLocalGateway\config.json` |
| Logs | `%ProgramData%\Medisync\RisLocalGateway\logs\gateway-YYYYMMDD.log` |

Mật khẩu trong config được mã hoá bằng **DPAPI scope LocalMachine** — chỉ decrypt được trên cùng máy.

## Hành vi DICOM service (đã nối RIS/PACS thật)

| Service | Hành vi |
|---|---|
| C-ECHO | Trả Success |
| C-FIND MWL | Gọi `IRisClient.FetchWorkListAsync(...)`, trả mỗi worklist item thành `DicomCFindResponse(Pending)` + Success cuối |
| MPPS N-CREATE | Parse dataset → `IRisClient.NotifyMppsInProgressAsync(...)` |
| MPPS N-SET | Theo status COMPLETED/DISCONTINUED → `NotifyMppsCompletedAsync` / `NotifyMppsDiscontinuedAsync` |
| C-STORE | **Store-and-forward**: spool ra đĩa + trả Success ngay; `SpoolForwarder` (nền) transcode (mặc định JPEG-LS Lossless) + STOW-RS lên PACS qua dicomweb-proxy, kèm Bearer token, có retry + dead-letter |

Logic DICOM ở [src/Medisync.RisLocalGateway.Dicom/RisGatewayDicomProvider.cs](src/Medisync.RisLocalGateway.Dicom/RisGatewayDicomProvider.cs); forward nền ở [src/Medisync.RisLocalGateway.Service/SpoolForwarder.cs](src/Medisync.RisLocalGateway.Service/SpoolForwarder.cs) + [src/Medisync.RisLocalGateway.Dicom/PacsStowClient.cs](src/Medisync.RisLocalGateway.Dicom/PacsStowClient.cs).

### Độ bền forward (store-and-forward)

- Ảnh C-STORE được spool atomic ra đĩa (`StorageDirectory\spool`, mặc định `%ProgramData%\Medisync\RisLocalGateway\spool`) → trả Success cho modality NGAY, không chặn theo WAN, không mất ảnh nếu PACS lỗi.
- `SpoolForwarder` quét nền, **prime-per-series** (gửi 1 instance đầu/series trước để Orthanc tạo bản ghi cha, rồi phần còn lại song song) chống đua tạo study/series.
- Phân loại lỗi STOW: lỗi **hạ tầng tạm thời** (PACS/RIS/mạng down, thiếu token, 401/403/timeout/5xx) → thử lại tới cap an toàn cao (`Pacs.MaxRetries`, mặc định 1000 ≈ 4h); lỗi **vĩnh viễn** (ảnh bị từ chối 400/409/413/415/422, file hỏng) → chuyển dead-letter (`spool\failed`) ngay.
- File `.dcm` dead-letter nằm ở `spool\failed`; **lý do fail** ghi sang folder RIÊNG `spool\failed-reasons\<sop>.error.json` (category Permanent/Retryable/Corrupt, số lần thử, lỗi cuối, series, thời điểm UTC). Cần xử lý thủ công (chưa có cơ chế re-drive tự động).

## Test với modality giả

Dùng **fo-dicom DicomEchoTest** hoặc **dcm4che storescu** từ máy khác:
```pwsh
# C-ECHO test (cần fo-dicom CLI hoặc DCMTK)
echoscu -aet TEST_SCU -aec RIS_GW <gateway-ip> 4646
```
