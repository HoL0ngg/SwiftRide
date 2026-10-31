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

public class TripPaymentRecoveryTests
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

    private static TripContext CreatePaymentPendingContext(Guid tripId)
    {
        var quoteId = Guid.NewGuid();
        var fare = new FareSnapshot(quoteId, 50_000m, "VND");

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
    public async Task Resume_WhenPaymentAlreadySettled_FinalizesToPaid_WithoutCallingCharge()
    {
        var sut = CreateSut();
        var tripId = Guid.NewGuid();
        var ctx = CreatePaymentPendingContext(tripId);
        var paymentId = Guid.NewGuid();

        _reader.Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        _paymentClient.Setup(p => p.GetByTripAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentObservation(
                paymentId,
                tripId,
                ctx.Trip.RiderId,
                ctx.Trip.AssignedDriverId,
                50_000m,
                "VND",
                RemotePaymentStatus.Settled,
                null,
                false,
                null,
                null));

        var lease = new TripWorkLease(TripWorkKind.Payment, tripId, tripId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(30), 2);

        var success = await sut.ResumeAsync(lease, CancellationToken.None);

        Assert.True(success);
        Assert.Equal(TripStatus.Paid, ctx.Trip.Status);
        Assert.Equal(paymentId, ctx.Trip.PaymentId);

        _paymentClient.Verify(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _assignmentStore.Verify(a => a.ReleaseRider(tripId), Times.Once);
        _workStore.Verify(w => w.StageCompletion(lease), Times.Once);
        _uow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Resume_WhenPaymentNotFound_CallsCharge()
    {
        var sut = CreateSut();
        var tripId = Guid.NewGuid();
        var ctx = CreatePaymentPendingContext(tripId);
        var paymentId = Guid.NewGuid();

        _reader.Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        _paymentClient.Setup(p => p.GetByTripAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PaymentObservation?)null);

        _paymentClient.Setup(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentObservation(
                paymentId,
                tripId,
                ctx.Trip.RiderId,
                ctx.Trip.AssignedDriverId,
                50_000m,
                "VND",
                RemotePaymentStatus.Settled,
                null,
                false,
                null,
                null));

        var lease = new TripWorkLease(TripWorkKind.Payment, tripId, tripId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(30), 1);

        var success = await sut.ResumeAsync(lease, CancellationToken.None);

        Assert.True(success);
        _paymentClient.Verify(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _assignmentStore.Verify(a => a.ReleaseRider(tripId), Times.Once);
        _workStore.Verify(w => w.StageCompletion(lease), Times.Once);
    }

    [Fact]
    public async Task Resume_WhenExceededMaxAttempts_StagesRequiresAction_AndDoesNotCallNetwork()
    {
        var sut = CreateSut();
        var tripId = Guid.NewGuid();
        var ctx = CreatePaymentPendingContext(tripId);

        _reader.Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var lease = new TripWorkLease(TripWorkKind.Payment, tripId, tripId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(30), 21, MaxAttempts: 20);

        var success = await sut.ResumeAsync(lease, CancellationToken.None);

        Assert.False(success);
        _paymentClient.Verify(p => p.GetByTripAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _paymentClient.Verify(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _workStore.Verify(w => w.StageRequiresAction(lease, "max_attempts_exceeded"), Times.Once);
    }

    [Fact]
    public async Task Resume_AtExactMaxAttempt_StillRunsNetworkCall()
    {
        var sut = CreateSut();
        var tripId = Guid.NewGuid();
        var ctx = CreatePaymentPendingContext(tripId);

        _reader.Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        // Attempt 20 of 20 (the last allowed attempt)
        var lease = new TripWorkLease(TripWorkKind.Payment, tripId, tripId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(30), 20, MaxAttempts: 20);

        _paymentClient.Setup(p => p.GetByTripAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PaymentObservation?)null);

        _paymentClient.Setup(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentObservation(
                Guid.NewGuid(),
                tripId,
                ctx.Trip.RiderId,
                ctx.Trip.AssignedDriverId,
                50_000m,
                "VND",
                RemotePaymentStatus.Settled,
                null,
                false,
                null,
                null));

        var success = await sut.ResumeAsync(lease, CancellationToken.None);

        Assert.True(success);
        _paymentClient.Verify(p => p.GetByTripAsync(tripId, It.IsAny<CancellationToken>()), Times.Once);
        _paymentClient.Verify(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Resume_PastDeadline_DoesNotCallNetwork_AndStagesRequiresAction()
    {
        var sut = CreateSut();
        var tripId = Guid.NewGuid();
        var ctx = CreatePaymentPendingContext(tripId);

        _reader.Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var pastDeadline = DateTimeOffset.UtcNow.AddMinutes(-5);
        var lease = new TripWorkLease(TripWorkKind.Payment, tripId, tripId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(30), 1, MaxAttempts: 20, DeadlineUtc: pastDeadline);

        var success = await sut.ResumeAsync(lease, CancellationToken.None);

        Assert.False(success);
        _paymentClient.Verify(p => p.GetByTripAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _paymentClient.Verify(p => p.ChargeAsync(It.IsAny<ChargeRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _workStore.Verify(w => w.StageRequiresAction(lease, "deadline_expired"), Times.Once);
    }

    [Fact]
    public async Task Resume_JobInheritsOldBudget_DoesNotUseNewRuntimeOptions()
    {
        // Runtime options configured with 100 max attempts
        var options = Options.Create(new PaymentWorkflowOptions { MaxAttempts = 100 });
        var sut = new TripPaymentCoordinator(
            _reader.Object,
            _writer.Object,
            _uow.Object,
            _assignmentStore.Object,
            _workStore.Object,
            _paymentClient.Object,
            options,
            TimeProvider.System,
            NullLogger<TripPaymentCoordinator>.Instance);

        var tripId = Guid.NewGuid();
        var ctx = CreatePaymentPendingContext(tripId);

        _reader.Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        // Job was saved with MaxAttempts = 3; current attempt is 4 -> should be rejected!
        var lease = new TripWorkLease(TripWorkKind.Payment, tripId, tripId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(30), 4, MaxAttempts: 3);

        var success = await sut.ResumeAsync(lease, CancellationToken.None);

        Assert.False(success);
        _paymentClient.Verify(p => p.GetByTripAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _workStore.Verify(w => w.StageRequiresAction(lease, "max_attempts_exceeded"), Times.Once);
    }

    [Fact]
    public async Task Resume_NetworkFailureAtMaxAttempts_StagesRequiresAction_WithoutSchedulingNextRetry()
    {
        var sut = CreateSut();
        var tripId = Guid.NewGuid();
        var ctx = CreatePaymentPendingContext(tripId);

        _reader.Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var lease = new TripWorkLease(TripWorkKind.Payment, tripId, tripId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(30), 5, MaxAttempts: 5);

        _paymentClient.Setup(p => p.GetByTripAsync(tripId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceClientTransientException("Transport down"));

        var success = await sut.ResumeAsync(lease, CancellationToken.None);

        Assert.False(success);
        _workStore.Verify(w => w.StageRequiresAction(lease, "max_attempts_exhausted"), Times.Once);
        _workStore.Verify(w => w.StageRetry(It.IsAny<TripWorkLease>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Resume_AuthFailure_StagesRequiresAction_WithoutRetry()
    {
        var sut = CreateSut();
        var tripId = Guid.NewGuid();
        var ctx = CreatePaymentPendingContext(tripId);

        _reader.Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var lease = new TripWorkLease(TripWorkKind.Payment, tripId, tripId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(30), 1, MaxAttempts: 5);

        _paymentClient.Setup(p => p.GetByTripAsync(tripId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceClientAuthException("Invalid API key"));

        var success = await sut.ResumeAsync(lease, CancellationToken.None);

        Assert.False(success);
        _workStore.Verify(w => w.StageRequiresAction(lease, "auth_failure"), Times.Once);
        _workStore.Verify(w => w.StageRetry(It.IsAny<TripWorkLease>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Resume_ConflictFailure_StagesRequiresAction_WithoutRetry()
    {
        var sut = CreateSut();
        var tripId = Guid.NewGuid();
        var ctx = CreatePaymentPendingContext(tripId);

        _reader.Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var lease = new TripWorkLease(TripWorkKind.Payment, tripId, tripId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(30), 1, MaxAttempts: 5);

        _paymentClient.Setup(p => p.GetByTripAsync(tripId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceClientConflictException("Conflict"));

        var success = await sut.ResumeAsync(lease, CancellationToken.None);

        Assert.False(success);
        _workStore.Verify(w => w.StageRequiresAction(lease, "conflict_failure"), Times.Once);
        _workStore.Verify(w => w.StageRetry(It.IsAny<TripWorkLease>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Resume_CallerCancellation_ThrowsDirectly_WithoutStagingRetryOrRequiresAction()
    {
        var sut = CreateSut();
        var tripId = Guid.NewGuid();
        var ctx = CreatePaymentPendingContext(tripId);

        _reader.Setup(r => r.LoadAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ctx);

        var lease = new TripWorkLease(TripWorkKind.Payment, tripId, tripId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(30), 1, MaxAttempts: 5);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _paymentClient.Setup(p => p.GetByTripAsync(tripId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.ResumeAsync(lease, cts.Token));

        _workStore.Verify(w => w.StageRetry(It.IsAny<TripWorkLease>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>()), Times.Never);
        _workStore.Verify(w => w.StageRequiresAction(It.IsAny<TripWorkLease>(), It.IsAny<string>()), Times.Never);
    }
}
