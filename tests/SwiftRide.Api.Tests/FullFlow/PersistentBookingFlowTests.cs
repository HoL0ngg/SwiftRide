using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.TripService.Api;
using Xunit;

namespace SwiftRide.Api.Tests.FullFlow;

[Collection("PersistentFullFlow")]
public class PersistentBookingFlowTests
{
    private readonly FullFlowDatabaseFixture _fixture;

    public PersistentBookingFlowTests(FullFlowDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [LocalPersistentFullFlowFact]
    public async Task FullBookingFlow_ThroughGateway_WithThreeRealDatabases_ReachesPaidAndBalancesLedger()
    {
        await _fixture.CleanAsync();
        await using var host = await FullFlowHost.StartPersistentAsync();

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

        // Verify driver location directly in real PostgreSQL Matching DB
        await using (var matchDb = host.CreateMatchingDbContext())
        {
            var loc = await matchDb.DriverLocations.SingleOrDefaultAsync(l => l.DriverId == driverId);
            Assert.NotNull(loc);
            Assert.True(loc.IsOnline);
            Assert.Equal(10.7725, loc.Latitude);
            Assert.Equal(106.6980, loc.Longitude);
        }

        // 2. Rider requests a trip through Gateway
        var createRequest = new CreateTripRequest(
            Pickup: new GeoPointRequest(10.7725, 106.6980),
            PickupName: "Chợ Bến Thành",
            PickupAddress: "Lê Lợi, Quận 1",
            Destination: new GeoPointRequest(10.7951, 106.7219),
            DestinationName: "Landmark 81",
            DestinationAddress: "Bình Thạnh",
            PaymentMethod: "wallet",
            Mode: "standard");

        var createKey = $"create-trip-{Guid.NewGuid():N}";
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

        // Verify quote persisted in real PostgreSQL Matching DB
        await using (var matchDb = host.CreateMatchingDbContext())
        {
            var quoteRow = await matchDb.Quotes.SingleOrDefaultAsync(q => q.Id == quoteId);
            Assert.NotNull(quoteRow);
            Assert.Equal(createdTrip.FareAmount, quoteRow.Amount.ToString());
            Assert.Equal("VND", quoteRow.Currency);
        }

        // 3. Rider confirms quote
        var confirmMsg = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId}/confirm-quote")
        {
            Content = JsonContent.Create(new ConfirmQuoteRequest(quoteId))
        };
        confirmMsg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var confirmResponse = await riderClient.SendAsync(confirmMsg);
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);

        var offeredTrip = await confirmResponse.Content.ReadFromJsonAsync<TripResponse>();
        Assert.NotNull(offeredTrip);
        Assert.Equal("DriverOffered", offeredTrip.Status);
        Assert.Equal(driverId, offeredTrip.OfferedDriverId);

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

        var dropoffKey = $"dropoff-{tripId:N}";
        var dropoffMsg = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId}/dropoff");
        dropoffMsg.Headers.Add("Idempotency-Key", dropoffKey);
        var dropoffResponse = await driverClient.SendAsync(dropoffMsg);
        Assert.True(dropoffResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted);

        // 6. Poll until trip reaches Paid
        TripResponse? finalizedTrip = null;
        for (var i = 0; i < 30; i++)
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

        // 7. Verify real SQL Server TripDB
        await using (var tripDb = host.CreateTripDbContext())
        {
            var record = await tripDb.Trips.SingleAsync(t => t.Id == tripId);
            Assert.Equal("Paid", record.Status);
            Assert.NotNull(record.PaymentId);
            Assert.Equal(createdTrip.FareAmount, record.FareAmount.ToString());

            // Active rider slot must be released
            var riderSlot = await tripDb.ActiveRiderTrips.FirstOrDefaultAsync(r => r.RiderId == riderId);
            Assert.Null(riderSlot);
        }

        // 8. Verify real PostgreSQL PayDB: 1 payment, 2 balanced ledger entries
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
        }
    }

    [LocalPersistentFullFlowFact]
    public async Task WrongActor_CallingDriverEndpoints_ReturnsForbidden()
    {
        await _fixture.CleanAsync();
        await using var host = await FullFlowHost.StartPersistentAsync();

        var riderId = $"rider-{Guid.NewGuid():N}";
        var riderToken = host.CreateToken(riderId, "rider");

        using var riderClient = new HttpClient { BaseAddress = host.GatewayClient.BaseAddress };
        riderClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", riderToken);

        // Rider calls driver location endpoint -> 403 Forbidden
        var response = await riderClient.PutAsJsonAsync($"/drivers/{riderId}/location", new
        {
            latitude = 10.7725,
            longitude = 106.6980,
            isOnline = true
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
