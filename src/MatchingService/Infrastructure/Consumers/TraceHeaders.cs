using MassTransit;

namespace SwiftRide.MatchingService.Infrastructure.Consumers;

internal static class TraceHeaders
{
    private const string TraceParent = "traceparent";

    /// <summary>Copies the W3C trace context of the consumed request onto the reply so traces stay connected.</summary>
    public static void Forward(ConsumeContext source, PublishContext target)
    {
        if (source.Headers.TryGetHeader(TraceParent, out var value) && value is string traceParent && traceParent.Length > 0)
        {
            target.Headers.Set(TraceParent, traceParent);
        }
    }
}
