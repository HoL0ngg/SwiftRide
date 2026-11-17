namespace SwiftRide.MatchingService.Domain;

public static class VehicleTypes
{
    public const string Motorbike = "motorbike";
    public const string Car = "car";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Motorbike => Motorbike,
        Car => Car,
        _ => throw new MatchingDomainException("Unsupported vehicle type.")
    };
}
