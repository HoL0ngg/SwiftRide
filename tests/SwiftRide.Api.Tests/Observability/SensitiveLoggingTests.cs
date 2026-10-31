using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using SwiftRide.ApiGateway.Observability;
using SwiftRide.MatchingService.Api.Observability;
using SwiftRide.PaymentService.Api.Observability;
using SwiftRide.TripService.Api.Observability;
using Xunit;

namespace SwiftRide.Api.Tests.Observability;

public static class SensitiveLogFilter
{
    private static readonly Regex BearerRegex = new(
        @"Bearer\s+[A-Za-z0-9\-_=]+\.[A-Za-z0-9\-_=]+(?:\.[A-Za-z0-9\-_=]*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CvvRegex = new(
        @"""(?:cvv|cvc|cardCode)""\s*:\s*""?[0-9]{3,4}""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CardRegex = new(
        @"""(?:cardNumber|pan|accountNumber)""\s*:\s*""?[0-9]{13,19}""?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TokenRegex = new(
        @"""(?:paymentToken|mockToken|token)""\s*:\s*""[^""]+""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Redact(string? logMessage)
    {
        if (string.IsNullOrEmpty(logMessage))
            return string.Empty;

        var sanitized = BearerRegex.Replace(logMessage, "Bearer [REDACTED]");
        sanitized = CvvRegex.Replace(sanitized, @"""cvv"": ""[REDACTED]""");
        sanitized = CardRegex.Replace(sanitized, @"""cardNumber"": ""[REDACTED]""");
        sanitized = TokenRegex.Replace(sanitized, @"""paymentToken"": ""[REDACTED]""");
        return sanitized;
    }
}

public class SensitiveLoggingTests
{
    [Fact]
    public void Bearer_Token_Is_Redacted()
    {
        var rawLog = "Incoming HTTP Request with Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIn0.signature";
        var sanitized = SensitiveLogFilter.Redact(rawLog);

        Assert.DoesNotContain("eyJhbGciOiJIUzI1Ni", sanitized);
        Assert.Contains("Authorization: Bearer [REDACTED]", sanitized);
    }

    [Fact]
    public void Card_Numbers_And_CVV_Are_Redacted()
    {
        var payload = "Processing request: {\"cardNumber\": \"4111222233334444\", \"cvv\": \"123\", \"amount\": 50000}";
        var sanitized = SensitiveLogFilter.Redact(payload);

        Assert.DoesNotContain("4111222233334444", sanitized);
        Assert.DoesNotContain("123", sanitized);
        Assert.Contains(@"""cardNumber"": ""[REDACTED]""", sanitized);
        Assert.Contains(@"""cvv"": ""[REDACTED]""", sanitized);
        Assert.Contains(@"""amount"": 50000", sanitized);
    }

    [Fact]
    public void Payment_Token_Is_Redacted()
    {
        var payload = "Charging payment: {\"paymentToken\": \"mock-stripe-token-secret-9988\", \"tripId\": \"1111-2222\"}";
        var sanitized = SensitiveLogFilter.Redact(payload);

        Assert.DoesNotContain("mock-stripe-token-secret-9988", sanitized);
        Assert.Contains(@"""paymentToken"": ""[REDACTED]""", sanitized);
    }

    [Fact]
    public void CorrelationSanitizer_Neutralizes_CRLF_Log_Injection()
    {
        // Attempts log splitting: fake info log inserted on new line
        var maliciousHeader = "valid-prefix\r\nINFO: 2026-10-30 Administrator authenticated successfully";
        var sanitized = SwiftRide.ApiGateway.Observability.CorrelationSanitizer.SanitizeOrGenerate(maliciousHeader);

        Assert.DoesNotContain("\r", sanitized);
        Assert.DoesNotContain("\n", sanitized);
        Assert.DoesNotContain("Administrator authenticated", sanitized);
        Assert.True(Guid.TryParse(sanitized, out _), "Must fallback to secure GUID when special characters detected.");
    }

    [Fact]
    public void All_Services_Telemetry_Meters_And_Activities_Are_Registered()
    {
        // Gateway
        Assert.Equal("SwiftRide.Gateway", GatewayTelemetry.ServiceName);
        Assert.NotNull(GatewayTelemetry.ActivitySource);
        Assert.NotNull(GatewayTelemetry.Meter);

        // TripService
        Assert.Equal("SwiftRide.TripService", TripTelemetry.ServiceName);
        Assert.NotNull(TripTelemetry.TripsCreated);
        Assert.NotNull(TripTelemetry.TripsCompleted);
        Assert.NotNull(TripTelemetry.TripsFailed);

        // MatchingService
        Assert.Equal("SwiftRide.MatchingService", MatchingTelemetry.ServiceName);
        Assert.NotNull(MatchingTelemetry.MatchingDuration);

        // PaymentService
        Assert.Equal("SwiftRide.PaymentService", PaymentTelemetry.ServiceName);
        Assert.NotNull(PaymentTelemetry.PaymentsSettled);
        Assert.NotNull(PaymentTelemetry.PaymentsFailed);
        Assert.NotNull(PaymentTelemetry.PaymentsRefunded);
    }
}
