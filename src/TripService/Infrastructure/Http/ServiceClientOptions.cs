namespace SwiftRide.TripService.Infrastructure.Http;

public sealed class MatchingClientOptions
{
    public const string SectionName = "MatchingService";
    public string BaseUrl { get; set; } = string.Empty;
    public string InternalApiKey { get; set; } = string.Empty;
}

public sealed class PaymentClientOptions
{
    public const string SectionName = "PaymentService";
    public string BaseUrl { get; set; } = string.Empty;
    public string InternalApiKey { get; set; } = string.Empty;
}
