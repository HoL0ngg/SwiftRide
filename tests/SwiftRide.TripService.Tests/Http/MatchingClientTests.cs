using System.Net;
using System.Text;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;
using SwiftRide.TripService.Infrastructure.Http;

namespace SwiftRide.TripService.Tests.Http;

public class MatchingClientTests
{
    private class TestHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Handler { get; set; }
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (Handler != null)
            {
                return await Handler(request, cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task MatchAsync_MatchedResponse_ReturnsMatchedOutcomeWithCandidate()
    {
        var json = """
        {
            "status": "Matched",
            "candidate": {
                "driverId": "driver-99",
                "distanceKm": 2.75
            }
        }
        """;

        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5001") };
        var client = new MatchingClient(httpClient);

        var outcome = await client.MatchAsync(new MatchInput(new GeoCoordinate(10.76, 106.66), []), CancellationToken.None);

        Assert.Equal(MatchStatus.Matched, outcome.Status);
        Assert.NotNull(outcome.Candidate);
        Assert.Equal("driver-99", outcome.Candidate.DriverId);
        Assert.Equal(2.75, outcome.Candidate.DistanceKm);
    }

    [Fact]
    public async Task MatchAsync_NoDriverAvailableResponse_ReturnsNoDriverOutcome()
    {
        var json = """
        {
            "status": "NoDriverAvailable",
            "candidate": null
        }
        """;

        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5001") };
        var client = new MatchingClient(httpClient);

        var outcome = await client.MatchAsync(new MatchInput(new GeoCoordinate(10.76, 106.66), ["driver-1"]), CancellationToken.None);

        Assert.Equal(MatchStatus.NoDriverAvailable, outcome.Status);
        Assert.Null(outcome.Candidate);
    }

    [Fact]
    public async Task MatchAsync_Transient503ThenSuccess_RetriesOnceAndSucceeds()
    {
        var json = """
        {
            "status": "Matched",
            "candidate": {
                "driverId": "driver-retry",
                "distanceKm": 1.2
            }
        }
        """;

        var attempts = 0;
        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5001") };
        var client = new MatchingClient(httpClient);

        var outcome = await client.MatchAsync(new MatchInput(new GeoCoordinate(10.76, 106.66), []), CancellationToken.None);

        Assert.Equal(2, testHandler.RequestCount);
        Assert.Equal(MatchStatus.Matched, outcome.Status);
        Assert.NotNull(outcome.Candidate);
    }

    [Fact]
    public async Task MatchAsync_Cancelled_DoesNotRetry()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromCanceled<HttpResponseMessage>(ct)
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5001") };
        var client = new MatchingClient(httpClient);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.MatchAsync(new MatchInput(new GeoCoordinate(10.76, 106.66), []), cts.Token));

        Assert.True(testHandler.RequestCount <= 1);
    }

    [Fact]
    public async Task QuoteAsync_201WithDecimalTime_ParsesCorrectly()
    {
        var quoteId = Guid.NewGuid();
        var json = $$"""
        {
            "quoteId": "{{quoteId}}",
            "amount": 35000,
            "currency": "VND",
            "distanceKm": 4.5,
            "estimatedMinutes": 12.5,
            "breakdown": {
                "basePrice": 20000
            },
            "createdAt": "2026-10-29T10:00:00Z"
        }
        """;

        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5001") };
        var client = new MatchingClient(httpClient);

        var quote = await client.QuoteAsync(new QuoteInput(new GeoCoordinate(10.76, 106.66), new GeoCoordinate(10.78, 106.69)), CancellationToken.None);

        Assert.Equal(quoteId, quote.QuoteId);
        Assert.Equal(35000m, quote.Amount);
        Assert.Equal("VND", quote.Currency);
        Assert.Equal(4.5m, quote.DistanceKm);
        Assert.Equal(12.5m, quote.EstimatedMinutes);
    }

    [Fact]
    public async Task QuoteAsync_TimeoutOrError_DoesNotRetry()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.GatewayTimeout))
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5001") };
        var client = new MatchingClient(httpClient);

        await Assert.ThrowsAsync<ServiceClientTransientException>(() =>
            client.QuoteAsync(new QuoteInput(new GeoCoordinate(10.76, 106.66), new GeoCoordinate(10.78, 106.69)), CancellationToken.None));

        Assert.Equal(1, testHandler.RequestCount);
    }

    [Fact]
    public void Constructor_InvalidBaseAddress_ThrowsArgumentException()
    {
        var httpClientNull = new HttpClient();
        Assert.Throws<ArgumentException>(() => new MatchingClient(httpClientNull));

        var httpClientFtp = new HttpClient { BaseAddress = new Uri("ftp://localhost") };
        Assert.Throws<ArgumentException>(() => new MatchingClient(httpClientFtp));
    }

    [Fact]
    public async Task InternalKeyHandler_AttachesHeader()
    {
        HttpRequestMessage? captured = null;
        var innerHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) =>
            {
                captured = req;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        };

        var handler = new InternalKeyHandler("secret-key") { InnerHandler = innerHandler };
        var invoker = new HttpMessageInvoker(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:5001/test");
        await invoker.SendAsync(request, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.True(captured.Headers.Contains("X-Internal-Api-Key"));
        Assert.Equal("secret-key", captured.Headers.GetValues("X-Internal-Api-Key").First());
    }

    [Fact]
    public async Task MatchAsync_400BadRequest_ThrowsServiceClientException_AndDoesNotRetry()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5001") };
        var client = new MatchingClient(httpClient);

        await Assert.ThrowsAsync<ServiceClientException>(() =>
            client.MatchAsync(new MatchInput(new GeoCoordinate(10.76, 106.66), []), CancellationToken.None));

        Assert.Equal(1, testHandler.RequestCount);
    }

    [Fact]
    public async Task MatchAsync_401Unauthorized_ThrowsServiceClientAuthException_AndDoesNotRetry()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5001") };
        var client = new MatchingClient(httpClient);

        await Assert.ThrowsAsync<ServiceClientAuthException>(() =>
            client.MatchAsync(new MatchInput(new GeoCoordinate(10.76, 106.66), []), CancellationToken.None));

        Assert.Equal(1, testHandler.RequestCount);
    }

    [Fact]
    public async Task MatchAsync_409Conflict_ThrowsServiceClientConflictException_AndDoesNotRetry()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict))
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5001") };
        var client = new MatchingClient(httpClient);

        await Assert.ThrowsAsync<ServiceClientConflictException>(() =>
            client.MatchAsync(new MatchInput(new GeoCoordinate(10.76, 106.66), []), CancellationToken.None));

        Assert.Equal(1, testHandler.RequestCount);
    }

    [Fact]
    public async Task MatchAsync_503Persistent_RetriesMaxAttempts_AndThrowsServiceClientTransientException()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5001") };
        var client = new MatchingClient(httpClient);

        await Assert.ThrowsAsync<ServiceClientTransientException>(() =>
            client.MatchAsync(new MatchInput(new GeoCoordinate(10.76, 106.66), []), CancellationToken.None));

        Assert.Equal(2, testHandler.RequestCount);
    }
}
