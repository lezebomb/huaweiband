using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BandKey.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace BandKey.Windows;

public sealed class BridgeServer : IAsyncDisposable
{
    private WebApplication? app;
    public async Task StartAsync(AppSettings settings, X509Certificate2 cert, AuthorizationBroker broker,
        Action<ApprovalResult> result, CancellationToken cancellationToken = default, AuthenticationCoordinator? authentication = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxRequestBodySize = 8192;
            server.Limits.MaxConcurrentConnections = 8;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            server.Listen(IPAddress.Parse(settings.ListenAddress), settings.Port, listen => listen.UseHttps(cert));
        });
        app = builder.Build();
        var flow = authentication ?? new AuthenticationCoordinator(broker, new UnavailableAuthenticationBackend(), () => null);
        app.UseRateLimiter();
        // Only the signed protocol is exposed. Configuration and enrollment have no network API.
        app.MapPost("/v1/poll", (PollRequest request) =>
        {
            try
            {
                var challenge = broker.Poll(request, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                return challenge == null ? Results.NoContent() : Results.Json(challenge);
            }
            catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException or FormatException or NullReferenceException)
            { return Results.Unauthorized(); }
        });
        app.MapPost("/v1/approval", async (Approval request, HttpContext context) =>
        {
            try
            {
                var status = await flow.AcceptAsync(request, context.RequestAborted); result(status);
                return Results.Json(status, statusCode: status.Authorized ? 200 : 403);
            }
            catch (Exception e) when (e is ArgumentException or FormatException or NullReferenceException)
            { return Results.BadRequest(); }
        });
        await app.StartAsync(cancellationToken);
    }
    public async ValueTask DisposeAsync()
    { if (app != null) { await app.StopAsync(); await app.DisposeAsync(); app = null; } }
    public static string CertificatePin(X509Certificate2 cert) => Convert.ToHexStringLower(SHA256.HashData(cert.RawData));
}
