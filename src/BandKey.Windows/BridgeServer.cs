using System.Net;
using System.Security.Authentication;
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
        Action<ApprovalResult> result, CancellationToken cancellationToken = default, AuthenticationCoordinator? authentication = null, WindowsGrantRelay? windowsRelay = null, FixedPhoneChannel? fixedPhone = null,bool tls12Compatibility=false)
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
            server.Listen(IPAddress.Parse(settings.ListenAddress), settings.Port, listen => listen.UseHttps(https=>{
                https.ServerCertificate=cert;
                if(tls12Compatibility)https.SslProtocols=SslProtocols.Tls12;
            }));
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
        // This route exists only in the explicitly started, VM-guarded phone experiment.
        if (windowsRelay != null)
        {
            app.MapPost("/v2/windows/poll", (PollRequest request) =>
            {
                try { var prompt = windowsRelay.Poll(request); return prompt == null ? Results.NoContent() : Results.Json(prompt); }
                catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
                catch (Exception error) when (error is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException) { return Results.BadRequest(); }
                catch (IOException) { return Results.StatusCode(503); }
            });
            app.MapPost("/v2/windows/decision", (WindowsGrantDecision request) =>
            {
                try { var status = windowsRelay.Decide(request); return Results.Json(status, statusCode: status.Authorized || status.Code == "user-denied" ? 200 : 403); }
                catch (Exception error) when (error is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException) { return Results.BadRequest(); }
                catch (IOException) { return Results.StatusCode(503); }
            });
        }
        if(fixedPhone!=null){
            app.MapPost("/v2/windows/fixed/context",(PollRequest request)=>{
                try{return Results.Json(fixedPhone.GetContext(request));}
                catch(UnauthorizedAccessException){return Results.Unauthorized();}
                catch(Exception e)when(e is ArgumentException or FormatException){return Results.BadRequest();}
            });
            FixedPhoneResult AttachPrompt(FixedPhoneResult status,PollRequest? poll){
                if(!status.Accepted || poll==null || windowsRelay==null || !fixedPhone.CanSubmit)return status;
                try{
                    var prompt=windowsRelay.Poll(poll);
                    return status with{CanPoll=prompt!=null,Prompt=prompt,NativeState=fixedPhone.Snapshot.NativeState};
                }catch(Exception error)when(error is UnauthorizedAccessException or ArgumentException or FormatException or CryptographicException){
                    return status with{Prompt=null}; // The state proof does not replace the separate signed poll.
                }
            }
            app.MapPost("/v2/windows/fixed/state",(BandConnectionReport request)=>{
                try{var status=AttachPrompt(fixedPhone.Observe(request),request.Poll);return Results.Json(status,statusCode:status.Accepted?200:403);}
                catch(Exception e)when(e is ArgumentException or FormatException){return Results.BadRequest();}
                catch(IOException){return Results.StatusCode(503);}
            });
            app.MapPost("/v2/windows/fixed/renew",async (RequestRenewalDecision request,CancellationToken aborted)=>{
                try{
                    var status=fixedPhone.Renew(request);
                    // Native notification timer is 250ms. A bounded wait can deliver its
                    // fresh, independently signed challenge in this response, with no approval.
                    if(status.Accepted && request.Poll!=null && windowsRelay!=null){
                        for(int i=0;i<4 && !fixedPhone.CanSubmit;i++)await Task.Delay(100,aborted);
                        status=AttachPrompt(status,request.Poll);
                    }
                    return Results.Json(status,statusCode:status.Accepted?200:403);
                }
                catch(Exception e)when(e is ArgumentException or FormatException){return Results.BadRequest();}
                catch(IOException){return Results.StatusCode(503);}
            });
        }
        await app.StartAsync(cancellationToken);
    }
    public async ValueTask DisposeAsync()
    { if (app != null) { await app.StopAsync(); await app.DisposeAsync(); app = null; } }
    public static string CertificatePin(X509Certificate2 cert) => Convert.ToHexStringLower(SHA256.HashData(cert.RawData));
}
