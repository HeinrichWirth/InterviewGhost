namespace DesktopAgentWpf.Services;

public sealed class TokenAuth
{
    private readonly string _token;

    public TokenAuth(string token)
    {
        _token = token;
    }

    public string Token => _token;

    public bool IsValid(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return string.Equals(_token, token, StringComparison.Ordinal);
    }
}
