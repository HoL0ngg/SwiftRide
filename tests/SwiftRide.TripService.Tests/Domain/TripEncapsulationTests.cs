using SwiftRide.TripService.Domain;

namespace SwiftRide.TripService.Tests.Domain;

public class TripEncapsulationTests
{
    [Fact]
    public void StateHandlers_AreNotExposedOutsideDomainAssembly()
    {
        var exportedStates = typeof(Trip).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "SwiftRide.TripService.Domain.States");

        Assert.Empty(exportedStates);
    }
}
