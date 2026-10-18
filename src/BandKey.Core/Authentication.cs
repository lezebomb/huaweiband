namespace BandKey.Core;

// Device authorization is deliberately separate from Windows identity authentication.
public interface IWindowsAuthenticationBackend
{
    bool IsAvailable { get; }
    bool IsSimulation { get; }
    Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken cancellationToken);
}
public sealed record AuthenticationRequest(Challenge Challenge, PublicDevice Device, string Source);
public sealed record AuthenticationResult(bool WindowsAuthenticated, string Reason);
public sealed class UnavailableAuthenticationBackend : IWindowsAuthenticationBackend
{
    public bool IsAvailable => false;
    public bool IsSimulation => false;
    public Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new AuthenticationResult(false, "authentication-backend-unavailable"));
}

/// <summary>Device authorization, backend authentication and observed unlock are separate outcomes.</summary>
public sealed class AuthenticationCoordinator
{
    private readonly AuthorizationBroker broker;
    private readonly IWindowsAuthenticationBackend backend;
    private readonly Func<bool?> sessionLocked;
    private readonly Func<long> now;
    private readonly TimeSpan timeout;
    public AuthenticationCoordinator(AuthorizationBroker broker, IWindowsAuthenticationBackend backend,
        Func<bool?> sessionLocked, Func<long>? now = null, TimeSpan? timeout = null)
    {
        this.broker = broker; this.backend = backend; this.sessionLocked = sessionLocked;
        this.now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds()); this.timeout = timeout ?? TimeSpan.FromSeconds(10);
    }
    public async Task<ApprovalResult> AcceptAsync(Approval approval, CancellationToken cancellationToken = default)
    {
        var authorization = broker.Accept(approval, now(), out var device);
        if (!authorization.Authorized) return authorization;
        if (device == null || !broker.IsDeviceCurrent(device) || !broker.IsContextCurrent(approval.Challenge, now())) return new(true, false, "authentication-context-changed");
        if (device.TestOnly && !backend.IsSimulation) return new(true, false, "test-source-blocked-from-live-authentication", true);
        if (!backend.IsAvailable) return new(true, false, "authentication-backend-unavailable");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            timeoutSource.Token.ThrowIfCancellationRequested();
            var result = await backend.AuthenticateAsync(new(approval.Challenge, device, approval.Source), timeoutSource.Token).WaitAsync(timeoutSource.Token);
            if (backend.IsSimulation) return new(true, false, result.WindowsAuthenticated ? "simulation-completed" : "simulation-rejected", true);
            if (!result.WindowsAuthenticated) return new(true, false, "windows-authentication-rejected");
            if (!broker.IsDeviceCurrent(device) || !broker.IsSameLockSession(approval.Challenge) || now() >= approval.Challenge.ExpiresAt)
                return new(true, false, "authentication-context-changed");
            return sessionLocked() == false ? new(true, true, "windows-authentication-confirmed") : new(true, false, "awaiting-windows-session-confirmation");
        }
        catch (OperationCanceledException) { return new(true, false, cancellationToken.IsCancellationRequested ? "authentication-cancelled" : "authentication-timeout", backend.IsSimulation); }
        catch (Exception) { return new(true, false, "authentication-backend-error", backend.IsSimulation); }
    }
}
