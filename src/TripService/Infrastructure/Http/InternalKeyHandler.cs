using System.Net.Http.Headers;

namespace SwiftRide.TripService.Infrastructure.Http;

public sealed class InternalKeyHandler : DelegatingHandler
{
    private readonly string _apiKey;

    public InternalKeyHandler(string apiKey)
    {
        _apiKey = apiKey;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Remove("X-Internal-Api-Key");
            request.Headers.Add("X-Internal-Api-Key", _apiKey);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
