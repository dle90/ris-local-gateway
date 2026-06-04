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

sc.exe start "Medisync RIS Local Gateway"
```

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

## DICOM stub hiện tại

Tất cả service trả về **Success** mà không thực sự xử lý:

| Service | Hành vi stub | TODO khi nối RIS thật |
|---|---|---|
| C-ECHO | Trả Success | (giữ nguyên) |
| C-FIND MWL | Trả 0 record + Success | Gọi `IRisClient.FetchWorklistAsync(...)`, yield `DicomCFindResponse(Pending)` cho mỗi item |
| MPPS N-CREATE | Trả Success | Parse dataset → `IRisClient.NotifyMppsInProgressAsync(...)` |
| MPPS N-SET | Trả Success | Parse status → `IRisClient.NotifyMppsCompletedAsync(...)` |
| C-STORE | Trả Success, không lưu file | Ghi file vào `StorageDirectory` + `IRisClient.UploadInstanceMetadataAsync(...)` |

Code hook nằm ở [src/Medisync.RisLocalGateway.Dicom/RisGatewayDicomProvider.cs](src/Medisync.RisLocalGateway.Dicom/RisGatewayDicomProvider.cs) — tìm các comment `// TODO`.

## Test với modality giả

Dùng **fo-dicom DicomEchoTest** hoặc **dcm4che storescu** từ máy khác:
```pwsh
# C-ECHO test (cần fo-dicom CLI hoặc DCMTK)
echoscu -aet TEST_SCU -aec RIS_GW <gateway-ip> 11112
```
