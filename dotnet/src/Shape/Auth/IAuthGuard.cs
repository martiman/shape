namespace Shape.Auth;

public interface IAuthGuard
{
    Dictionary<string, object?> Authorize(Dictionary<string, string> headers);
}
