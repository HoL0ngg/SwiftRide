using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftRide.Contracts.V1;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.TripService.Api;
using Xunit;

namespace SwiftRide.Api.Tests.FullFlow;

[Collection("PersistentFullFlow")]
public class EventDrivenBookingFlowTests
{
    private readonly FullFlowDatabaseFixture _fixture;

    public EventDrivenBookingFlowTests(FullFlowDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class TripToPaymentBridge(IBus paymentBus) : IPublishObserver
    {
        private static readonly AsyncLocal<bool> InForwarding = new();

        public Task PrePublish<T>(PublishContext<T> context) where T : class => Task.CompletedTask;

        public Task PostPublish<T>(PublishContext<T> context) where T : class
        {
            if (InForwarding.Value) return Task.CompletedTask;

            if (context.Message is RideCompletedV1 or PaymentCompensationRequestedV1)
            {
                try
                {
                    InForwarding.Value = true;
                    return paymentBus.Publish(context.Message, context.CancellationToken);
                }
                finally
                {
                    InForwarding.Value = false;
                }
            }
            return Task.CompletedTask;
        }

        public Task PublishFault<T>(PublishContext<T> context, Exception exception) where T : class => Task.CompletedTask;
    }

    private sealed class PaymentToTripBridge(IBus tripBus) : IPublishObserver
    {
        private static readonly AsyncLocal<bool> InForwarding = new();

        public Task PrePublish<T>(PublishContext<T> context) where T : class => Task.CompletedTask;

        public Task PostPublish<T>(PublishContext<T> context) where T : class
        {
            if (InForwarding.Value) return Task.CompletedTask;

            if (context.Message is PaymentSettledV1 or PaymentFailedV1 or PaymentRefundedV1 or PaymentCompensationFailedV1)
            {
                try
                {
                    InForwarding.Value = true;
                    return tripBus.Publish(context.Message, context.CancellationToken);
                }
                finally
                {
                    InForwarding.Value = false;
                }
            }
            return Task.CompletedTask;
        }

        public Task PublishFault<T>(PublishContext<T> context, Exception exception) where T : class => Task.CompletedTask;
    }

    [LocalPersistentFullFlowFact]
    public async Task FullBookingFlow_EventsTransport_ThroughGateway_SettlesViaEventsAndBalancesLedger()
    {
        await _fixture.CleanAsync();

        await using var host = await FullFlowHost.StartPersistentAsync();

        var tripBus = host.TripApp.Services.GetRequiredService<IBus>();
        var paymentBus = host.PaymentApp.Services.GetRequiredService<IBus>();

        tripBus.ConnectPublishObserver(new TripToPaymentBridge(paymentBus));
        paymentBus.ConnectPublishObserver(new PaymentToTripBridge(tripBus));

        var riderId = $"rider-{Guid.NewGuid():N}";
        var driverId = $"driver-{Guid.NewGuid():N}";
        var riderToken = host.CreateToken(riderId, "rider");
        var driverToken = host.CreateToken(driverId, "driver");

        using var riderClient = new HttpClient { BaseAddress = host.GatewayClient.BaseAddress };
        riderClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", riderToken);

        using var driverClient = new HttpClient { BaseAddress = host.GatewayClient.BaseAddress };
        driverClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", driverToken);

        // 1. Driver goes online with coordinates near Ho Chi Minh City center via Gateway
        var driverLocationResponse = await driverClient.PutAsJsonAsync($"/drivers/{driverId}/location", new
        {
            latitude = 10.7725,
            longitude = 106.6980,
            isOnline = true
        });
        Assert.True(driverLocationResponse.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK);

        // 2. Rider requests a trip through Gateway with PaymentTransport = "Events"
        var createRequest = new CreateTripRequest(
            Pickup: new GeoPointRequest(10.7725, 106.6980),
            PickupName: "Nhà thờ Đức Bà",
            PickupAddress: "Công xã Paris, Bến Nghé, Quận 1",
            Destination: new GeoPointRequest(10.7951, 106.7219),
            DestinationName: "Landmark 81",
            DestinationAddress: "Bình Thạnh",
            PaymentMethod: "card",
            Mode: "standard",
            PaymentTransport: "Events");

        var createKey = $"create-trip-event-{Guid.NewGuid():N}";
        var createMsg = new HttpRequestMessage(HttpMethod.Post, "/trips")
        {
            Content = JsonContent.Create(createRequest)
        };
        createMsg.Headers.Add("Idempotency-Key", createKey);
        var createResponse = await riderClient.SendAsync(createMsg);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var createdTrip = await createResponse.Content.ReadFromJsonAsync<TripResponse>();
        Assert.NotNull(createdTrip);
        Assert.Equal("Quoted", createdTrip.Status);
        Assert.NotNull(createdTrip.QuoteId);
        Assert.NotNull(createdTrip.FareAmount);
        Assert.Equal(driverId, createdTrip.CandidateDriverId);

        var tripId = createdTrip.Id;
        var quoteId = Guid.Parse(createdTrip.QuoteId);

        // 3. Rider confirms quote
        var confirmMsg = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId}/confirm-quote")
        {
            Content = JsonContent.Create(new ConfirmQuoteRequest(quoteId))
        };
        confirmMsg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var confirmResponse = await riderClient.SendAsync(confirmMsg);
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);

        // 4. Driver accepts trip
        var acceptMsg = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId}/driver-accept");
        acceptMsg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var acceptResponse = await driverClient.SendAsync(acceptMsg);
        Assert.Equal(HttpStatusCode.OK, acceptResponse.StatusCode);

        // 5. Driver en-route -> pickup -> dropoff
        var enRouteMsg = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId}/en-route");
        enRouteMsg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var enRouteResponse = await driverClient.SendAsync(enRouteMsg);
        Assert.Equal(HttpStatusCode.OK, enRouteResponse.StatusCode);

        var pickupMsg = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId}/pickup");
        pickupMsg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var pickupResponse = await driverClient.SendAsync(pickupMsg);
        Assert.Equal(HttpStatusCode.OK, pickupResponse.StatusCode);

        var dropoffKey = $"dropoff-event-{tripId:N}";
        var dropoffMsg = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId}/dropoff");
        dropoffMsg.Headers.Add("Idempotency-Key", dropoffKey);
        var dropoffResponse = await driverClient.SendAsync(dropoffMsg);
        Assert.True(dropoffResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted);

        // 6. Poll until trip reaches Paid via event-driven outbox choreography
        TripResponse? finalizedTrip = null;
        for (var i = 0; i < 40; i++)
        {
            var pollResponse = await riderClient.GetAsync($"/trips/{tripId}");
            Assert.Equal(HttpStatusCode.OK, pollResponse.StatusCode);
            finalizedTrip = await pollResponse.Content.ReadFromJsonAsync<TripResponse>();
            if (finalizedTrip?.Status == "Paid")
            {
                break;
            }
            await Task.Delay(500);
        }

        Assert.NotNull(finalizedTrip);
        Assert.Equal("Paid", finalizedTrip.Status);

        // 7. Verify real SQL Server TripDB: Trip is Paid, payment job is Completed, Outbox has published event
        await using (var tripDb = host.CreateTripDbContext())
        {
            var record = await tripDb.Trips.SingleAsync(t => t.Id == tripId);
            Assert.Equal("Paid", record.Status);
            Assert.NotNull(record.PaymentId);
            Assert.Equal("Events", record.PaymentTransport);

            var job = await tripDb.TripPaymentJobs.SingleAsync(j => j.TripId == tripId);
            Assert.Equal("Completed", job.Status);
            Assert.Equal("Events", job.Transport);

            var outboxRideCompleted = await tripDb.IntegrationOutbox
                .FirstOrDefaultAsync(o => o.EventType == nameof(RideCompletedV1) && o.PayloadJson.Contains(tripId.ToString()));
            Assert.NotNull(outboxRideCompleted);
            Assert.NotNull(outboxRideCompleted.PublishedAtUtc);

            var inboxSettled = await tripDb.IntegrationInbox
                .FirstOrDefaultAsync(i => i.ProcessedAtUtc != null);
            Assert.NotNull(inboxSettled);

            // Verify ActiveRiderTrips released the rider (Issue 1 fix verification)
            var activeRiderHeld = await tripDb.ActiveRiderTrips.FirstOrDefaultAsync(a => a.RiderId == riderId);
            Assert.Null(activeRiderHeld);
        }

        // 8. Rider can now immediately book a second trip without conflict
        var secondCreateRequest = new CreateTripRequest(
            Pickup: new GeoPointRequest(10.7725, 106.6980),
            PickupName: "Ben Thanh Market",
            PickupAddress: "Ben Thanh, Q1",
            Destination: new GeoPointRequest(10.7951, 106.7219),
            DestinationName: "Independence Palace",
            DestinationAddress: "Ben Nghe, Q1",
            PaymentMethod: "card",
            Mode: "standard",
            PaymentTransport: "Events");

        var secondCreateKey = $"create-second-trip-{Guid.NewGuid():N}";
        var secondTripMsg = new HttpRequestMessage(HttpMethod.Post, "/trips")
        {
            Content = JsonContent.Create(secondCreateRequest)
        };
        secondTripMsg.Headers.Add("Idempotency-Key", secondCreateKey);
        var secondTripResponse = await riderClient.SendAsync(secondTripMsg);
        Assert.Equal(HttpStatusCode.Created, secondTripResponse.StatusCode);
        var secondTrip = await secondTripResponse.Content.ReadFromJsonAsync<TripResponse>();
        Assert.NotNull(secondTrip);
        Assert.Equal("Quoted", secondTrip.Status);

        // 9. Verify real PostgreSQL PayDB: Payment settled, balanced double-entry ledger, inbox and outbox published
        await using (var paymentDb = host.CreatePaymentDbContext())
        {
            var payment = await paymentDb.Payments.SingleAsync(p => p.TripId == tripId);
            Assert.Equal(PaymentStatus.Settled, payment.Status);
            Assert.Equal(decimal.Parse(createdTrip.FareAmount), payment.Amount.Amount);
            Assert.Equal("VND", payment.Amount.Currency);

            var ledgerEntries = await paymentDb.LedgerEntries.Where(l => l.PaymentId == payment.Id).ToListAsync();
            Assert.Equal(2, ledgerEntries.Count);
            var debit = ledgerEntries.Single(e => e.Direction == EntryDirection.Debit);
            var credit = ledgerEntries.Single(e => e.Direction == EntryDirection.Credit);
            Assert.Equal(debit.Amount, credit.Amount);
            Assert.Equal(payment.Amount, debit.Amount);

            var outboxSettled = await paymentDb.IntegrationOutbox
                .FirstOrDefaultAsync(o => o.EventType == nameof(PaymentSettledV1) && o.PayloadJson.Contains(tripId.ToString()));
            Assert.NotNull(outboxSettled);
            Assert.NotNull(outboxSettled.PublishedAtUtc);

            var inboxCompleted = await paymentDb.IntegrationInbox
                .FirstOrDefaultAsync(i => i.ProcessedAtUtc != null);
            Assert.NotNull(inboxCompleted);
        }
    }
}
