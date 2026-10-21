using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SwiftRide.Api.Tests.Gateway;

public sealed record UpstreamRequest(
    string Method, string Path, string QueryString, bool HasAuthorization);

public sealed class UpstreamStub : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<UpstreamRequest> _requests = new();
    private readonly TaskCompletionSource _cancellationTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private UpstreamStub(WebApplication app)
    {
        _app = app;
    }

    public Uri Address { get; private set; } = null!;
    public IReadOnlyList<UpstreamRequest> Requests => _requests.ToArray();
    public Task CancellationObserved => _cancellationTcs.Task;

    public static async Task<UpstreamStub> StartAsync(
        string name,
        bool stallResponse = false,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var stub = new UpstreamStub(app);

        app.Map("{**catch-all}", async (HttpContext context) =>
        {
            var hasAuth = context.Request.Headers.ContainsKey("Authorization");
            stub._requests.Enqueue(new UpstreamRequest(
                context.Request.Method,
                context.Request.Path.Value ?? "",
                context.Request.QueryString.Value ?? "",
                hasAuth));

            if (stallResponse)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    stub._cancellationTcs.TrySetResult();
                    throw;
                }
            }

            context.Response.Headers["X-Upstream"] = name;
            context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        });

        await app.StartAsync(cancellationToken);
        stub.Address = new Uri(app.Urls.Single());
        return stub;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync();
        }
        catch
        {
            // Ignore shutdown errors
        }
        await _app.DisposeAsync();
    }
}
