namespace Shape.Core;

public sealed class ApiException : Exception
{
    public string ApiCode { get; }
    public int Status { get; }
    public string? Ref { get; }
    public IReadOnlyDictionary<string, object?>? Details { get; }

    public ApiException(string apiCode, string message, int status = 400, string? @ref = null)
        : base(message)
    {
        ApiCode = apiCode;
        Status = status;
        Ref = @ref;
    }

    public ApiException(
        string apiCode,
        string message,
        int status,
        string? @ref,
        IReadOnlyDictionary<string, object?>? details,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ApiCode = apiCode;
        Status = status;
        Ref = @ref;
        Details = details;
    }
}
