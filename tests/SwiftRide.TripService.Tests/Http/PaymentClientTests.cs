using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;
using SwiftRide.TripService.Infrastructure.Http;

namespace SwiftRide.TripService.Tests.Http;

public class PaymentClientTests
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
    public async Task Payment201Failed_IsNotSettled()
    {
        var tripId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var json = $$"""
        {
            "id": "{{paymentId}}",
            "tripId": "{{tripId}}",
            "riderId": "rider-1",
            "driverId": "driver-1",
            "amount": 50000,
            "currency": "VND",
            "method": "Card",
            "status": "Failed",
            "failureCode": "CARD_DECLINED",
            "createdAt": "2026-10-29T10:00:00Z",
            "settledAt": null,
            "ledgerEntries": [],
            "refund": null
        }
        """;

        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5002") };
        var client = new PaymentClient(httpClient);

        var request = new ChargeRequest(tripId, "rider-1", "driver-1", 50000m, "VND", "Card", "mock-token", $"trip-{tripId:D}-payment");
        var result = await client.ChargeAsync(request, CancellationToken.None);

        Assert.Equal(RemotePaymentStatus.Failed, result.Status);
        Assert.Equal("CARD_DECLINED", result.FailureCode);
    }

    [Fact]
    public async Task ChargeAsync_201Settled_ReturnsSettled()
    {
        var tripId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var json = $$"""
        {
            "id": "{{paymentId}}",
            "tripId": "{{tripId}}",
            "riderId": "rider-1",
            "driverId": "driver-1",
            "amount": 50000,
            "currency": "VND",
            "method": "Card",
            "status": "Settled",
            "failureCode": null,
            "createdAt": "2026-10-29T10:00:00Z",
            "settledAt": "2026-10-29T10:00:05Z",
            "ledgerEntries": [],
            "refund": null
        }
        """;

        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5002") };
        var client = new PaymentClient(httpClient);

        var request = new ChargeRequest(tripId, "rider-1", "driver-1", 50000m, "VND", "Card", "mock-token", $"trip-{tripId:D}-payment");
        var result = await client.ChargeAsync(request, CancellationToken.None);

        Assert.Equal(RemotePaymentStatus.Settled, result.Status);
        Assert.Equal(50000m, result.Amount);
    }

    [Fact]
    public async Task ChargeAsync_200ReplaySettled_ReturnsSettled()
    {
        var tripId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var json = $$"""
        {
            "id": "{{paymentId}}",
            "tripId": "{{tripId}}",
            "riderId": "rider-1",
            "driverId": "driver-1",
            "amount": 50000,
            "currency": "VND",
            "method": "Card",
            "status": "Settled",
            "failureCode": null,
            "createdAt": "2026-10-29T10:00:00Z",
            "settledAt": "2026-10-29T10:00:05Z",
            "ledgerEntries": [],
            "refund": null
        }
        """;

        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5002") };
        var client = new PaymentClient(httpClient);

        var request = new ChargeRequest(tripId, "rider-1", "driver-1", 50000m, "VND", "Card", "mock-token", $"trip-{tripId:D}-payment");
        var result = await client.ChargeAsync(request, CancellationToken.None);

        Assert.Equal(RemotePaymentStatus.Settled, result.Status);
    }

    [Fact]
    public async Task ChargeAsync_202PendingWithRetryAfter_ParsesPendingAndRetryAfter()
    {
        var tripId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var json = $$"""
        {
            "id": "{{paymentId}}",
            "tripId": "{{tripId}}",
            "riderId": "rider-1",
            "driverId": "driver-1",
            "amount": 50000,
            "currency": "VND",
            "method": "Card",
            "status": "Pending",
            "failureCode": null,
            "createdAt": "2026-10-29T10:00:00Z",
            "settledAt": null,
            "ledgerEntries": [],
            "refund": null,
            "processing": {
                "attemptsStarted": 1,
                "leaseUntilUtc": "2026-10-29T10:01:00Z",
                "canRetry": false,
                "requiresAction": false
            }
        }
        """;

        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return Task.FromResult(response);
            }
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5002") };
        var client = new PaymentClient(httpClient);

        var request = new ChargeRequest(tripId, "rider-1", "driver-1", 50000m, "VND", "Card", "mock-token", $"trip-{tripId:D}-payment");
        var result = await client.ChargeAsync(request, CancellationToken.None);

        Assert.Equal(RemotePaymentStatus.Pending, result.Status);
        Assert.NotNull(result.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter.Value);
        Assert.NotNull(result.Processing);
        Assert.Equal(1, result.Processing.AttemptsStarted);
    }

    [Fact]
    public async Task ChargeAsync_WithSettledRefund_SetsHasSettledRefundTrue()
    {
        var tripId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var json = $$"""
        {
            "id": "{{paymentId}}",
            "tripId": "{{tripId}}",
            "riderId": "rider-1",
            "driverId": "driver-1",
            "amount": 50000,
            "currency": "VND",
            "method": "Card",
            "status": "Settled",
            "failureCode": null,
            "createdAt": "2026-10-29T10:00:00Z",
            "settledAt": "2026-10-29T10:00:05Z",
            "ledgerEntries": [],
            "refund": {
                "id": "11111111-2222-3333-4444-555555555555",
                "amount": 50000,
                "currency": "VND",
                "reason": "Test refund",
                "status": "Settled",
                "createdAt": "2026-10-29T10:05:00Z",
                "settledAt": "2026-10-29T10:05:10Z",
                "ledgerEntries": []
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

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5002") };
        var client = new PaymentClient(httpClient);

        var request = new ChargeRequest(tripId, "rider-1", "driver-1", 50000m, "VND", "Card", "mock-token", $"trip-{tripId:D}-payment");
        var result = await client.ChargeAsync(request, CancellationToken.None);

        Assert.True(result.HasSettledRefund);
    }

    [Fact]
    public async Task ChargeAsync_409Conflict_ThrowsServiceClientConflictException()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("Conflict", Encoding.UTF8, "text/plain")
            })
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5002") };
        var client = new PaymentClient(httpClient);

        var request = new ChargeRequest(Guid.NewGuid(), "rider-1", "driver-1", 50000m, "VND", "Card", "mock-token", "key-1");
        await Assert.ThrowsAsync<ServiceClientConflictException>(() => client.ChargeAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task ChargeAsync_503_ThrowsServiceClientTransientExceptionAndDoesNotRetry()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5002") };
        var client = new PaymentClient(httpClient);

        var request = new ChargeRequest(Guid.NewGuid(), "rider-1", "driver-1", 50000m, "VND", "Card", "mock-token", "key-1");
        await Assert.ThrowsAsync<ServiceClientTransientException>(() => client.ChargeAsync(request, CancellationToken.None));

        Assert.Equal(1, testHandler.RequestCount);
    }

    [Fact]
    public async Task GetByTripAsync_404_ReturnsNull()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
        };

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://localhost:5002") };
        var client = new PaymentClient(httpClient);

        var result = await client.GetByTripAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public void Constructor_InvalidBaseAddress_ThrowsArgumentException()
    {
        var httpClientNull = new HttpClient();
        Assert.Throws<ArgumentException>(() => new PaymentClient(httpClientNull));

        var httpClientFtp = new HttpClient { BaseAddress = new Uri("ftp://localhost") };
        Assert.Throws<ArgumentException>(() => new PaymentClient(httpClientFtp));
    }
}
