namespace Shape.Auth;

public sealed class NullGuard : IAuthGuard
{
    public Dictionary<string, object?> Authorize(Dictionary<string, string> headers) => new();
}
