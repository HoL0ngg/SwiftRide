using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Domain.States;

internal sealed class PaymentPendingState : TripState
{
    public override TripStatus Status => TripStatus.PaymentPending;
    public override bool IsTerminal => false;

    public override void SettlePayment(Trip trip, PaymentSettlement settlement)
    {
        if (settlement == null)
            throw new TripDomainException("InvalidPaymentResult", "Settlement cannot be null.");

        if (settlement.TripId != trip.Id)
            throw new TripDomainException("PaymentMismatch", "Trip ID mismatch.");

        if (trip.Fare == null || settlement.Amount != trip.Fare.Amount)
            throw new TripDomainException("PaymentMismatch", "Fare amount mismatch.");

        if (settlement.Currency != trip.Fare.Currency)
            throw new TripDomainException("PaymentMismatch", "Currency mismatch.");

        if (settlement.IdempotencyKey != trip.PaymentIdempotencyKey)
            throw new TripDomainException("PaymentMismatch", "Idempotency key mismatch.");

        trip.SetPaymentId(settlement.PaymentId);
        trip.ClearLastPaymentFailure();
        trip.TransitionTo(new PaidState());
    }

    public override void RecordPaymentFailure(Trip trip, PaymentFailureKind failureKind)
    {
        if (!Enum.IsDefined(failureKind))
            throw new TripDomainException("InvalidPaymentResult", "Undefined payment failure kind.");

        trip.SetLastPaymentFailure(failureKind);
    }
}
