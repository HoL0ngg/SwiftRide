using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Domain.Entities;
using SwiftRide.PaymentService.Domain.Repositories;
namespace SwiftRide.PaymentService.Tests;

public sealed class InMemoryPayments : IPaymentReader, IPaymentWriter, IUnitOfWork
{
    private readonly List<Payment> _items = [];
    public Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(_items.FirstOrDefault(p => p.Id == id));
    public Task<Payment?> GetByIdempotencyKeyAsync(string key, CancellationToken ct) => Task.FromResult(_items.FirstOrDefault(p => p.IdempotencyKey == key));
    public Task<Payment?> GetByTripIdAsync(Guid tripId, CancellationToken ct) => Task.FromResult(_items.FirstOrDefault(p => p.TripId == tripId));
    public void Add(Payment p) => _items.Add(p);
    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

public class PaymentEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _http;
    public PaymentEndpointTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__PaymentDB", "Host=unused;Database=x");  // chỉ để khởi động, không kết nối
        var store = new InMemoryPayments();
        _http = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IPaymentReader>(store);
            s.AddSingleton<IPaymentWriter>(store);
            s.AddSingleton<IUnitOfWork>(store);
        })).CreateClient();
    }

    private async Task<HttpResponseMessage> Post(Guid trip, string token = "tok_card_ok", decimal amount = 85000)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/payments")
        { Content = JsonContent.Create(new { tripId = trip, riderId = "rider-1", driverId = "drv-1", amount, method = "Card", paymentToken = token }) };
        req.Headers.Add("Idempotency-Key", $"trip-{trip}-payment");
        return await _http.SendAsync(req);
    }

    [Fact] public async Task New_Is201() => Assert.Equal(HttpStatusCode.Created, (await Post(Guid.NewGuid())).StatusCode);

    [Fact]
    public async Task Replay_Is200()
    { var t = Guid.NewGuid(); await Post(t); Assert.Equal(HttpStatusCode.OK, (await Post(t)).StatusCode); }

    [Fact]
    public async Task DifferentAmount_Is409()
    { var t = Guid.NewGuid(); await Post(t); Assert.Equal(HttpStatusCode.Conflict, (await Post(t, amount: 90000)).StatusCode); }

    [Fact]
    public async Task GatewayTimeout_Is503_ThenRetryInLease_Is202()
    {
        var t = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Post(t, "tok_card_timeout")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(t, "tok_card_ok")).StatusCode);
    }

    [Fact]
    public async Task Declined_Is201_WithFailedStatus()
    {
        var r = await Post(Guid.NewGuid(), "tok_card_declined");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Contains("Failed", await r.Content.ReadAsStringAsync());
    }
}