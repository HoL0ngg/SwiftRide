using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Tests.Infrastructure;

public class VehicleTypePersistenceTests
{
    [Theory]
    [InlineData("motorbike")]
    [InlineData("car")]
    public void RestoredTrip_PreservesVehicleTypeInReadModel(string vehicleType)
    {
        var record = new TripRecord
        {
            Id = Guid.NewGuid(),
            RiderId = "rider-1",
            Status = "Requested",
            MaxDriverRejections = 3,
            VehicleType = vehicleType
        };

        var context = record.ToContext();

        Assert.Equal(vehicleType, context.VehicleType);
        Assert.Equal(vehicleType, TripMapper.ToReadModel(context).VehicleType);
        Assert.Equal("motorbike", new TripRecord().VehicleType);
    }
}
