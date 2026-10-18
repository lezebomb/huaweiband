namespace BandKey.Core;

// Device authorization is deliberately separate from Windows identity authentication.
public interface IWindowsAuthenticationBackend
{
    bool IsAvailable { get; }
    Task<AuthenticationResult> AuthenticateAsync(CancellationToken cancellationToken);
}
public sealed record AuthenticationResult(bool WindowsAuthenticated, string Reason);
public sealed class UnavailableAuthenticationBackend : IWindowsAuthenticationBackend
{
    public bool IsAvailable => false;
    public Task<AuthenticationResult> AuthenticateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AuthenticationResult(false, "尚未部署经虚拟机验证的 Windows 登录后端。请使用原有 PIN 或密码。"));
}
