using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;
using SwiftRide.TripService.Application.Payments;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;
using Xunit;

namespace SwiftRide.TripService.Tests.Application;

public class TripPaymentTests
{
    private readonly Mock<ITripReader> _reader = new();
    private readonly Mock<ITripWriter> _writer = new();
    private readonly Mock<ITripUnitOfWork> _uow = new();
    private readonly Mock<IActiveAssignmentStore> _assignmentStore = new();
    private readonly Mock<ITripWorkStore> _workStore = new();
    private readonly Mock<IPaymentClient> _paymentClient = new();

    private TripPaymentCoordinator CreateSut()
    {
        var options = Options.Create(new PaymentWorkflowOptions());
        return new TripPaymentCoordinator(
            _reader.Object,
            _writer.Object,
            _uow.Object,
            _assignmentStore.Object,
            _workStore.Object,
            _paymentClient.Object,
            options,
            TimeProvider.System,
            NullLogger<TripPaymentCoordinator>.Instance);
    }

    private static TripContext CreateDroppedOffContext(decimal fareAmount = 50_000m, string currency = "VND")
    {
        var quoteId = Guid.NewGuid();
        var fare = new FareSnapshot(quoteId, fareAmount, currency);
        var tripId = Guid.NewGuid();

        var snapshot = new TripSnapshot(
            tripId,
            "rider-1",
            TripStatus.PaymentPending,
            fare,
            isFareLocked: true,
            candidateDriverId: "drv-cand",
            offeredDriverId: null,
            assignedDriverId: "drv-1",
            rejectionCount: 0,
            maxDriverRejections: 3,
            rejectedDriverIds: Array.Empty<string>(),
            paymentIdempotencyKey: $"trip-{tripId:D}-payment",
            paymentId: null,
            lastPaymentFailure: null);

        var trip = Trip.Restore(snapshot);
        return new TripContext(
            trip,
            "Pickup", "Address 1", 10.7, 106.6,
            "Dest", "Address 2", 10.8, 106.7,
            "card", "card.success.v1",
            5.0m, 15.0m,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ExecuteInitialPayment_Settled_MatchesFare_TransitionsToPaid_AndReleasesRider()
    {
        var sut = CreateSut();
        var ctx = CreateDroppedOffContext(50_000m, "VND");
        var paymentId = Guid.NewGuid();

        _paymentClient.Setup(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentObservation(
                paymentId,
                ctx.Trip.Id,
                ctx.Trip.RiderId,
                ctx.Trip.AssignedDriverId,
                50_000m,
                "VND",
                RemotePaymentStatus.Settled,
                null,
                false,
                null,
                null));

        var settled = await sut.ExecuteInitialPaymentAsync(ctx, CancellationToken.None);

        Assert.True(settled);
        Assert.Equal(TripStatus.Paid, ctx.Trip.Status);
        Assert.Equal(paymentId, ctx.Trip.PaymentId);

        _assignmentStore.Verify(a => a.ReleaseRider(ctx.Trip.Id), Times.Once);
        _workStore.Verify(w => w.StageUnownedCompletion(ctx.Trip.Id), Times.Once);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteInitialPayment_202Pending_KeepsPaymentPending_StagesRetry()
    {
        var sut = CreateSut();
        var ctx = CreateDroppedOffContext(50_000m, "VND");
        var paymentId = Guid.NewGuid();

        _paymentClient.Setup(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentObservation(
                paymentId,
                ctx.Trip.Id,
                ctx.Trip.RiderId,
                ctx.Trip.AssignedDriverId,
                50_000m,
                "VND",
                RemotePaymentStatus.Pending,
                null,
                false,
                new PaymentProcessingObservation(1, DateTimeOffset.UtcNow.AddSeconds(30), false, false),
                null));

        var settled = await sut.ExecuteInitialPaymentAsync(ctx, CancellationToken.None);

        Assert.False(settled);
        Assert.Equal(TripStatus.PaymentPending, ctx.Trip.Status);
        Assert.Null(ctx.Trip.PaymentId);

        _assignmentStore.Verify(a => a.ReleaseRider(ctx.Trip.Id), Times.Never);
        _workStore.Verify(w => w.StageUnownedRetry(ctx.Trip.Id, It.IsAny<DateTimeOffset>(), "payment_pending"), Times.Once);
    }

    [Fact]
    public async Task ExecuteInitialPayment_TransientException_KeepsPaymentPending_StagesRetry()
    {
        var sut = CreateSut();
        var ctx = CreateDroppedOffContext(50_000m, "VND");

        _paymentClient.Setup(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceClientTransientException("Payment service unavailable"));

        var settled = await sut.ExecuteInitialPaymentAsync(ctx, CancellationToken.None);

        Assert.False(settled);
        Assert.Equal(TripStatus.PaymentPending, ctx.Trip.Status);

        _assignmentStore.Verify(a => a.ReleaseRider(ctx.Trip.Id), Times.Never);
        _workStore.Verify(w => w.StageUnownedRetry(ctx.Trip.Id, It.IsAny<DateTimeOffset>(), "transient_error"), Times.Once);
    }

    [Fact]
    public async Task ExecuteInitialPayment_SettledMismatch_DoesNotMarkPaid()
    {
        var sut = CreateSut();
        var ctx = CreateDroppedOffContext(50_000m, "VND");

        // Amount returned is 60_000 (does not match locked fare 50_000)
        _paymentClient.Setup(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentObservation(
                Guid.NewGuid(),
                ctx.Trip.Id,
                ctx.Trip.RiderId,
                ctx.Trip.AssignedDriverId,
                60_000m,
                "VND",
                RemotePaymentStatus.Settled,
                null,
                false,
                null,
                null));

        var settled = await sut.ExecuteInitialPaymentAsync(ctx, CancellationToken.None);

        Assert.False(settled);
        Assert.Equal(TripStatus.PaymentPending, ctx.Trip.Status);
        _assignmentStore.Verify(a => a.ReleaseRider(ctx.Trip.Id), Times.Never);
    }

    [Fact]
    public async Task ExecuteInitialPayment_HasSettledRefund_DoesNotMarkPaid()
    {
        var sut = CreateSut();
        var ctx = CreateDroppedOffContext(50_000m, "VND");

        _paymentClient.Setup(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentObservation(
                Guid.NewGuid(),
                ctx.Trip.Id,
                ctx.Trip.RiderId,
                ctx.Trip.AssignedDriverId,
                50_000m,
                "VND",
                RemotePaymentStatus.Settled,
                null,
                true, // Already refunded!
                null,
                null));

        var settled = await sut.ExecuteInitialPaymentAsync(ctx, CancellationToken.None);

        Assert.False(settled);
        Assert.Equal(TripStatus.PaymentPending, ctx.Trip.Status);
        _assignmentStore.Verify(a => a.ReleaseRider(ctx.Trip.Id), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("other-driver-id")]
    public async Task ExecuteInitialPayment_Settled_MissingOrMismatchDriverId_DoesNotMarkPaid(string? driverId)
    {
        var sut = CreateSut();
        var ctx = CreateDroppedOffContext(50_000m, "VND");

        _paymentClient.Setup(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentObservation(
                Guid.NewGuid(),
                ctx.Trip.Id,
                ctx.Trip.RiderId,
                driverId,
                50_000m,
                "VND",
                RemotePaymentStatus.Settled,
                null,
                false,
                null,
                null));

        var settled = await sut.ExecuteInitialPaymentAsync(ctx, CancellationToken.None);

        Assert.False(settled);
        Assert.Equal(TripStatus.PaymentPending, ctx.Trip.Status);
        _assignmentStore.Verify(a => a.ReleaseRider(ctx.Trip.Id), Times.Never);
    }
}
