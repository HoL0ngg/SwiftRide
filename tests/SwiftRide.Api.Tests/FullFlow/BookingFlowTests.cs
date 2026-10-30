using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SwiftRide.PaymentService.Domain.Enums;
using SwiftRide.TripService.Api;

namespace SwiftRide.Api.Tests.FullFlow;

[Collection("TripDatabase")]
public class BookingFlowTests
{
    [LocalFullFlowFact]
    public async Task Complete_booking_flow_through_gateway_reaches_paid_with_balanced_ledger()
    {
        await using var host = await FullFlowHost.StartAsync();

        // 1. Prepare DB: ensure Trip DB is clean
        await using (var tripDb = host.CreateTripDbContext())
        {
            await tripDb.Database.MigrateAsync();
            await tripDb.Database.ExecuteSqlRawAsync("DELETE FROM TripPaymentJobs; DELETE FROM TripDriverRejections; DELETE FROM ActiveDriverAssignments; DELETE FROM ActiveRiderTrips; DELETE FROM CommandReceipts; DELETE FROM Trips;");
        }

        var riderId = $"rider-{Guid.NewGuid():N}";
        var driverId = $"driver-{Guid.NewGuid():N}";
        var riderToken = host.CreateToken(riderId, "rider");
        var driverToken = host.CreateToken(driverId, "driver");

        using var riderClient = new HttpClient { BaseAddress = host.GatewayClient.BaseAddress };
        riderClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", riderToken);

        using var driverClient = new HttpClient { BaseAddress = host.GatewayClient.BaseAddress };
        driverClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", driverToken);

        // 2. Driver goes online with coordinates near Ho Chi Minh City center
        var driverLocationResponse = await driverClient.PutAsJsonAsync($"/drivers/{driverId}/location", new
        {
            latitude = 10.7725,
            longitude = 106.6980,
            isOnline = true
        });
        Assert.True(driverLocationResponse.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK);

        // 3. Rider requests a trip
        var createRequest = new CreateTripRequest(
            Pickup: new GeoPointRequest(10.7725, 106.6980),
            PickupName: "Chợ Bến Thành",
            PickupAddress: "Lê Lợi, Quận 1",
            Destination: new GeoPointRequest(10.7951, 106.7219),
            DestinationName: "Landmark 81",
            DestinationAddress: "Bình Thạnh",
            PaymentMethod: "wallet",
            Mode: "standard");

        var createMsg = new HttpRequestMessage(HttpMethod.Post, "/trips")
        {
            Content = JsonContent.Create(createRequest)
        };
        createMsg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
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

        // 4. Rider confirms quote
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

        // 5. Driver checks feed
        var feedResponse = await driverClient.GetAsync("/trips/feed");
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);
        var feed = await feedResponse.Content.ReadFromJsonAsync<DriverFeedApiResponse>();
        Assert.NotNull(feed?.OfferedTrip);
        Assert.Equal(tripId, feed.OfferedTrip.Id);

        // 6. Driver accepts trip
        var acceptMsg = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId}/driver-accept");
        acceptMsg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var acceptResponse = await driverClient.SendAsync(acceptMsg);
        Assert.Equal(HttpStatusCode.OK, acceptResponse.StatusCode);

        var acceptedTrip = await acceptResponse.Content.ReadFromJsonAsync<TripResponse>();
        Assert.NotNull(acceptedTrip);
        Assert.Equal("DriverAccepted", acceptedTrip.Status);
        Assert.Equal(driverId, acceptedTrip.AssignedDriverId);

        // 7. Driver starts heading to rider (en-route)
        var enRouteMsg = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId}/en-route");
        enRouteMsg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var enRouteResponse = await driverClient.SendAsync(enRouteMsg);
        Assert.Equal(HttpStatusCode.OK, enRouteResponse.StatusCode);

        // 8. Driver picks up rider
        var pickupMsg = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId}/pickup");
        pickupMsg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var pickupResponse = await driverClient.SendAsync(pickupMsg);
        Assert.Equal(HttpStatusCode.OK, pickupResponse.StatusCode);

        // 9. Driver drops off rider
        var dropoffMsg = new HttpRequestMessage(HttpMethod.Post, $"/trips/{tripId}/dropoff");
        dropoffMsg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var dropoffResponse = await driverClient.SendAsync(dropoffMsg);
        var dropoffBody = await dropoffResponse.Content.ReadAsStringAsync();
        Assert.True(dropoffResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, $"Dropoff failed with {dropoffResponse.StatusCode}: {dropoffBody}");

        // 10. Poll until Trip reaches Paid (automatic initial settlement or worker reconciliation)
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

        // 11. Assert database state
        await using (var tripDb = host.CreateTripDbContext())
        {
            var record = await tripDb.Trips.SingleAsync(t => t.Id == tripId);
            Assert.Equal("Paid", record.Status);
            Assert.NotNull(record.PaymentId);
            Assert.Equal(createdTrip.FareAmount, record.FareAmount.ToString());
        }

        await using (var paymentDb = host.CreatePaymentDbContext())
        {
            var payment = await paymentDb.Payments.SingleAsync(p => p.TripId == tripId);
            Assert.Equal(PaymentStatus.Settled, payment.Status);

            var ledgerEntries = await paymentDb.LedgerEntries.Where(l => l.PaymentId == payment.Id).ToListAsync();
            Assert.Equal(2, ledgerEntries.Count);
            var debit = ledgerEntries.Single(e => e.Direction == EntryDirection.Debit);
            var credit = ledgerEntries.Single(e => e.Direction == EntryDirection.Credit);
            Assert.Equal(debit.Amount, credit.Amount);
            Assert.Equal(payment.Amount, debit.Amount);
        }
    }
}
