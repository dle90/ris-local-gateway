namespace Medisync.RisLocalGateway.Core.Ris;

/// <summary>
/// Client-side wrapper cho kết quả gọi RIS API. Tách success/failure rõ ràng,
/// consumer không phải biết HTTP details (status code, parse logic).
/// </summary>
public sealed class RisResponse<T>
{
    public bool IsSuccess { get; private init; }
    public T? Data { get; private init; }
    public string? ErrorMessage { get; private init; }
    public int HttpStatusCode { get; private init; }

    private RisResponse() { }

    public static RisResponse<T> Success(T data, int httpStatusCode = 200) => new()
    {
        IsSuccess = true,
        Data = data,
        HttpStatusCode = httpStatusCode,
    };

    public static RisResponse<T> Failure(int httpStatusCode, string errorMessage) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage,
        HttpStatusCode = httpStatusCode,
    };
}

/// <summary>
/// Wrapper cho RIS call không có payload (vd 3 MPPS endpoint trả result=null).
/// Chỉ quan tâm IsSuccess/HttpStatusCode/ErrorMessage.
/// </summary>
public sealed class RisResponse
{
    public bool IsSuccess { get; private init; }
    public string? ErrorMessage { get; private init; }
    public int HttpStatusCode { get; private init; }

    private RisResponse() { }

    public static RisResponse Success(int httpStatusCode = 200) => new()
    {
        IsSuccess = true,
        HttpStatusCode = httpStatusCode,
    };

    public static RisResponse Failure(int httpStatusCode, string errorMessage) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage,
        HttpStatusCode = httpStatusCode,
    };
}
