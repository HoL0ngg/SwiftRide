namespace SwiftRide.Contracts.V1;

public static class EventTopology
{
    public static class Exchanges
    {
        public const string TripAccepted = "SwiftRide.Contracts.V1:TripAcceptedV1";
        public const string RideCompleted = "SwiftRide.Contracts.V1:RideCompletedV1";
        public const string PaymentSettled = "SwiftRide.Contracts.V1:PaymentSettledV1";
        public const string PaymentFailed = "SwiftRide.Contracts.V1:PaymentFailedV1";
        public const string PaymentCompensationRequested = "SwiftRide.Contracts.V1:PaymentCompensationRequestedV1";
        public const string PaymentRefunded = "SwiftRide.Contracts.V1:PaymentRefundedV1";
        public const string PaymentCompensationFailed = "SwiftRide.Contracts.V1:PaymentCompensationFailedV1";
        public const string QuoteRequested = "SwiftRide.Contracts.V1:QuoteRequestedV1";
        public const string QuoteReady = "SwiftRide.Contracts.V1:QuoteReadyV1";
        public const string QuoteFailed = "SwiftRide.Contracts.V1:QuoteFailedV1";
        public const string DriverMatchRequested = "SwiftRide.Contracts.V1:DriverMatchRequestedV1";
        public const string DriverMatched = "SwiftRide.Contracts.V1:DriverMatchedV1";
        public const string DriverMatchFailed = "SwiftRide.Contracts.V1:DriverMatchFailedV1";
    }

    public static class Queues
    {
        public const string PaymentRideCompleted = "payment-ride-completed";
        public const string TripPaymentSettled = "trip-payment-settled";
        public const string TripPaymentFailed = "trip-payment-failed";
        public const string PaymentCompensation = "payment-compensation";
        public const string TripPaymentRefunded = "trip-payment-refunded";
        public const string TripPaymentCompensationFailed = "trip-payment-compensation-failed";
        public const string MatchingQuoteRequested = "matching-quote-requested";
        public const string MatchingDriverMatchRequested = "matching-driver-match-requested";
        public const string TripQuoteReady = "trip-quote-ready";
        public const string TripQuoteFailed = "trip-quote-failed";
        public const string TripDriverMatched = "trip-driver-matched";
        public const string TripDriverMatchFailed = "trip-driver-match-failed";
    }
}
