using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Repositories;
using SwiftRide.PaymentService.Infrastructure.Persistence;

namespace SwiftRide.PaymentService.Tests;

public sealed class InMemoryPayments : IPaymentReader, IPaymentWriter, IUnitOfWork
{
    private readonly List<Payment> _items = [];
    public Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(_items.FirstOrDefault(p => p.Id == id));
    public Task<Payment?> GetByIdempotencyKeyAsync(string key, CancellationToken ct) => Task.FromResult(_items.FirstOrDefault(p => p.IdempotencyKey == key));
    public Task<Payment?> GetByTripIdAsync(Guid tripId, CancellationToken ct) => Task.FromResult(_items.FirstOrDefault(p => p.TripId == tripId));
    public void Add(Payment p) => _items.Add(p);
    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct) => operation(ct);
}

public class PaymentEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _http;
    private const string TestKey = "test-internal-payment-key";

    public PaymentEndpointTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__PaymentDB", "Host=unused;Database=x");  // chỉ để khởi động, không kết nối
        Environment.SetEnvironmentVariable("Security__InternalApiKey", TestKey);
        Environment.SetEnvironmentVariable("Messaging__Transport", "InMemory");
        var store = new InMemoryPayments();
        _http = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IPaymentReader>(store);
            s.AddSingleton<IPaymentWriter>(store);
            s.AddSingleton<IUnitOfWork>(store);
            s.AddSingleton<IPaymentProcessingStore, InMemoryPaymentProcessingStore>();
            s.AddSingleton<IMockOperationStore, InMemoryMockOperationStore>();
        })).CreateClient();
    }

    private async Task<HttpResponseMessage> Post(Guid trip, string token = "tok_card_ok", decimal amount = 85000, string? apiKey = TestKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/payments")
        { Content = JsonContent.Create(new { tripId = trip, riderId = "rider-1", driverId = "drv-1", amount, method = "Card", paymentToken = token }) };
        req.Headers.Add("Idempotency-Key", $"trip-{trip}-payment");
        if (apiKey is not null)
        {
            req.Headers.Add("X-Internal-Api-Key", apiKey);
        }
        return await _http.SendAsync(req);
    }

    [Fact]
    public async Task MissingApiKey_Is401()
    {
        var res = await Post(Guid.NewGuid(), apiKey: null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task WrongApiKey_Is401()
    {
        var res = await Post(Guid.NewGuid(), apiKey: "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact] public async Task New_Is201() => Assert.Equal(HttpStatusCode.Created, (await Post(Guid.NewGuid())).StatusCode);

    [Fact]
    public async Task Replay_Is200()
    { var t = Guid.NewGuid(); await Post(t); Assert.Equal(HttpStatusCode.OK, (await Post(t)).StatusCode); }

    [Fact]
    public async Task DifferentAmount_Is409()
    { var t = Guid.NewGuid(); await Post(t); Assert.Equal(HttpStatusCode.Conflict, (await Post(t, amount: 90000)).StatusCode); }

    [Fact]
    public async Task DifferentToken_Is409()
    { var t = Guid.NewGuid(); await Post(t, token: "tok_card_ok"); Assert.Equal(HttpStatusCode.Conflict, (await Post(t, token: "tok_card_other")).StatusCode); }

    [Fact]
    public async Task GatewayTimeout_Is503_ThenRetryInLease_Is202()
    {
        var t = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(t, "tok_card_timeout")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(t, "tok_card_timeout")).StatusCode);
    }

    [Fact]
    public async Task Declined_Is201_WithFailedStatus()
    {
        var r = await Post(Guid.NewGuid(), "tok_card_declined");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Contains("Failed", await r.Content.ReadAsStringAsync());
    }
}
