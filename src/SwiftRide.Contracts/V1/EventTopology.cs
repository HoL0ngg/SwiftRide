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
