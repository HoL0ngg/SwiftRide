using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SwiftRide.TripService.Api;

namespace SwiftRide.Api.Tests.FullFlow;

[Collection("TripDatabase")]
public class BookingRecoveryTests
{
    [LocalFullFlowFact]
    public async Task Idempotent_command_replays_do_not_duplicate_mutations()
    {
        await using var host = await FullFlowHost.StartAsync();

        await using (var tripDb = host.CreateTripDbContext())
        {
            await tripDb.Database.MigrateAsync();
            await tripDb.Database.ExecuteSqlRawAsync("DELETE FROM TripPaymentJobs; DELETE FROM TripDriverRejections; DELETE FROM ActiveDriverAssignments; DELETE FROM ActiveRiderTrips; DELETE FROM CommandReceipts; DELETE FROM Trips;");
        }

        var driverId = $"driver-replay-{Guid.NewGuid():N}";
        var driverToken = host.CreateToken(driverId, "driver");
        using var driverClient = new HttpClient { BaseAddress = host.GatewayClient.BaseAddress };
        driverClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", driverToken);
        await driverClient.PutAsJsonAsync($"/drivers/{driverId}/location", new
        {
            latitude = 10.7725,
            longitude = 106.6980,
            isOnline = true
        });

        var riderId = $"rider-replay-{Guid.NewGuid():N}";
        var riderToken = host.CreateToken(riderId, "rider");
        using var riderClient = new HttpClient { BaseAddress = host.GatewayClient.BaseAddress };
        riderClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", riderToken);

        var createKey = Guid.NewGuid().ToString();
        var createRequest = new CreateTripRequest(
            Pickup: new GeoPointRequest(10.7725, 106.6980),
            PickupName: "Chợ Bến Thành",
            PickupAddress: "Lê Lợi, Quận 1",
            Destination: new GeoPointRequest(10.7951, 106.7219),
            DestinationName: "Landmark 81",
            DestinationAddress: "Bình Thạnh",
            PaymentMethod: "wallet",
            Mode: "standard");

        // First create
        var msg1 = new HttpRequestMessage(HttpMethod.Post, "/trips") { Content = JsonContent.Create(createRequest) };
        msg1.Headers.Add("Idempotency-Key", createKey);
        var res1 = await riderClient.SendAsync(msg1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);
        var trip1 = await res1.Content.ReadFromJsonAsync<TripResponse>();

        // Replay same create with same key
        var msg2 = new HttpRequestMessage(HttpMethod.Post, "/trips") { Content = JsonContent.Create(createRequest) };
        msg2.Headers.Add("Idempotency-Key", createKey);
        var res2 = await riderClient.SendAsync(msg2);
        Assert.True(res2.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK);
        var trip2 = await res2.Content.ReadFromJsonAsync<TripResponse>();
        Assert.Equal(trip1!.Id, trip2!.Id);

        // Assert exactly one record in database
        await using (var tripDb = host.CreateTripDbContext())
        {
            var count = await tripDb.Trips.CountAsync(t => t.Id == trip1.Id);
            Assert.Equal(1, count);
        }
    }

    [LocalFullFlowFact]
    public async Task Rider_can_cancel_quoted_trip_cleanly()
    {
        await using var host = await FullFlowHost.StartAsync();

        await using (var tripDb = host.CreateTripDbContext())
        {
            await tripDb.Database.MigrateAsync();
            await tripDb.Database.ExecuteSqlRawAsync("DELETE FROM TripPaymentJobs; DELETE FROM TripDriverRejections; DELETE FROM ActiveDriverAssignments; DELETE FROM ActiveRiderTrips; DELETE FROM CommandReceipts; DELETE FROM Trips;");
        }

        var driverId = $"driver-cancel-{Guid.NewGuid():N}";
        var driverToken = host.CreateToken(driverId, "driver");
        using var driverClient = new HttpClient { BaseAddress = host.GatewayClient.BaseAddress };
        driverClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", driverToken);
        await driverClient.PutAsJsonAsync($"/drivers/{driverId}/location", new
        {
            latitude = 10.7725,
            longitude = 106.6980,
            isOnline = true
        });

        var riderId = $"rider-cancel-{Guid.NewGuid():N}";
        var riderToken = host.CreateToken(riderId, "rider");
        using var riderClient = new HttpClient { BaseAddress = host.GatewayClient.BaseAddress };
        riderClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", riderToken);

        var createRequest = new CreateTripRequest(
            Pickup: new GeoPointRequest(10.7725, 106.6980),
            PickupName: "A",
            PickupAddress: "A",
            Destination: new GeoPointRequest(10.7951, 106.7219),
            DestinationName: "B",
            DestinationAddress: "B",
            PaymentMethod: "wallet",
            Mode: "standard");

        var msg = new HttpRequestMessage(HttpMethod.Post, "/trips") { Content = JsonContent.Create(createRequest) };
        msg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var res = await riderClient.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var trip = await res.Content.ReadFromJsonAsync<TripResponse>();
        Assert.Equal("Quoted", trip!.Status);

        // Cancel trip
        var cancelMsg = new HttpRequestMessage(HttpMethod.Post, $"/trips/{trip.Id}/cancel")
        {
            Content = JsonContent.Create(new CancelTripRequest("Changed my mind"))
        };
        cancelMsg.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var cancelRes = await riderClient.SendAsync(cancelMsg);
        Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);

        var cancelledTrip = await cancelRes.Content.ReadFromJsonAsync<TripResponse>();
        Assert.Equal("Cancelled", cancelledTrip!.Status);

        await using (var tripDb = host.CreateTripDbContext())
        {
            var record = await tripDb.Trips.SingleAsync(t => t.Id == trip.Id);
            Assert.Equal("Cancelled", record.Status);
        }
    }
}
