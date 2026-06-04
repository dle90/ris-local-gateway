namespace Medisync.RisLocalGateway.Core.Configuration;

public sealed class GatewayConfig
{
    public RisConfig Ris { get; set; } = new();

    public DicomConfig Dicom { get; set; } = new();

    public PacsConfig Pacs { get; set; } = new();

    public static GatewayConfig CreateDefault() => new()
    {
        Ris = new RisConfig
        {
            BaseUrl = "https://ris.example.com",
            Username = string.Empty,
            ProtectedPassword = string.Empty,
            Endpoints = RisEndpoints.CreateDefault(),
        },
        Dicom = new DicomConfig
        {
            AeTitle = "RIS_GW",
            Port = 11112,
            StorageDirectory = string.Empty,
        },
        Pacs = new PacsConfig
        {
            StowUrl = "http://localhost:8080/wado/studies",
            TimeoutSeconds = 120,
            ScanIntervalSeconds = 15,
            MaxRetries = 10,
            MaxParallelForwards = 4,
            CompressionEnabled = true,
        },
    };
}

public sealed class RisConfig
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ProtectedPassword { get; set; } = string.Empty;
    public RisEndpoints Endpoints { get; set; } = RisEndpoints.CreateDefault();
}

/// <summary>
/// Sub-URL của từng API của RIS. Cho phép cập nhật path khi RIS đổi version (v2, v3…)
/// mà không cần rebuild gateway.
/// Full URL = BaseUrl + Endpoint path (vd "http://ris.local" + "/v1/lgs/work-list").
/// </summary>
public sealed class RisEndpoints
{
    public string SignIn { get; set; } = string.Empty;
    public string HealthCheck { get; set; } = string.Empty;
    public string WorkList { get; set; } = string.Empty;
    public string MppsInProgress { get; set; } = string.Empty;
    public string MppsCompleted { get; set; } = string.Empty;
    public string MppsDiscontinued { get; set; } = string.Empty;
    public string InstanceMetadata { get; set; } = string.Empty;

    public static RisEndpoints CreateDefault() => new()
    {
        SignIn           = "/v1/integration/auth/token",
        HealthCheck      = "/v1/lgs/local-gateway-server/actions/health",
        WorkList         = "/v1/lgs/local-gateway-server/actions/get-work-list",
        MppsInProgress   = "/v1/lgs/local-gateway-server/actions/mpps-in-progress",
        MppsCompleted    = "/v1/lgs/local-gateway-server/actions/mpps-completed",
        MppsDiscontinued = "/v1/lgs/local-gateway-server/actions/mpps-discontinued",
        InstanceMetadata = "/v1/lgs/local-gateway-server/actions/instance-metadata",
    };
}

public sealed class DicomConfig
{
    public string AeTitle { get; set; } = "RIS_GW";
    public int Port { get; set; } = 11112;
    public string StorageDirectory { get; set; } = string.Empty;
}

/// <summary>
/// Kết nối tới PACS để forward ảnh qua STOW-RS (đi qua dicomweb-proxy).
/// </summary>
public sealed class PacsConfig
{
    /// <summary>Endpoint STOW-RS đầy đủ, vd "http://localhost:8080/wado/studies".</summary>
    public string StowUrl { get; set; } = string.Empty;

    /// <summary>Timeout cho mỗi lần STOW 1 instance (giây).</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Chu kỳ quét spool để forward nền (giây).</summary>
    public int ScanIntervalSeconds { get; set; } = 15;

    /// <summary>Số lần forward thất bại tối đa trước khi chuyển dead-letter (spool/failed).</summary>
    public int MaxRetries { get; set; } = 10;

    /// <summary>
    /// Số instance non-prime forward SONG SONG mỗi vòng quét. An toàn >1 nhờ SpoolForwarder
    /// dùng PRIME-PER-SERIES: instance đầu mỗi series gửi trước (tuần tự, qua prime-lane) để
    /// Orthanc tạo bản ghi cha (Patient/Study/Series), rồi phần còn lại mới song song → không
    /// đua tạo study/series. (Trước khi có prime-per-series thì phải để 1 để tránh 400 tạm thời.)
    /// </summary>
    public int MaxParallelForwards { get; set; } = 4;

    /// <summary>Nén JPEG 2000 Lossless trước khi STOW (luôn chỉ nén ảnh đang uncompressed).</summary>
    public bool CompressionEnabled { get; set; } = true;
}
