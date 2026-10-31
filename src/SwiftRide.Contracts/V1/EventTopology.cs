namespace SwiftRide.Contracts.V1;

public static class EventTopology
{
    public static class Exchanges
    {
        public const string TripAccepted = "swiftride.v1.trip-accepted";
        public const string RideCompleted = "swiftride.v1.ride-completed";
        public const string PaymentSettled = "swiftride.v1.payment-settled";
        public const string PaymentFailed = "swiftride.v1.payment-failed";
        public const string PaymentCompensationRequested = "swiftride.v1.payment-compensation-requested";
        public const string PaymentRefunded = "swiftride.v1.payment-refunded";
        public const string PaymentCompensationFailed = "swiftride.v1.payment-compensation-failed";
    }

    public static class Queues
    {
        public const string PaymentRideCompleted = "payment-ride-completed";
        public const string TripPaymentSettled = "trip-payment-settled";
        public const string TripPaymentFailed = "trip-payment-failed";
        public const string PaymentCompensation = "payment-compensation";
        public const string TripPaymentRefunded = "trip-payment-refunded";
        public const string TripPaymentCompensationFailed = "trip-payment-compensation-failed";
    }
}
