using Microsoft.EntityFrameworkCore;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Trips;
using SwiftRide.TripService.Domain;
using SwiftRide.TripService.Domain.ValueObjects;

namespace SwiftRide.TripService.Infrastructure.Persistence;

public sealed class TripRepository : ITripReader, ITripWriter
{
    private readonly TripDbContext _db;

    public TripRepository(TripDbContext db)
    {
        _db = db;
    }

    public async Task<TripContext?> LoadAsync(Guid id, CancellationToken ct)
    {
        var record = await _db.Trips
            .Include(t => t.Rejections)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        return record?.ToContext();
    }

    public void Add(TripContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var snap = context.Trip.ToSnapshot();
        var record = new TripRecord
        {
            Id = snap.Id,
            RiderId = snap.RiderId,
            Status = snap.Status.ToString(),
            QuoteId = snap.Fare?.QuoteId,
            FareAmount = snap.Fare?.Amount,
            FareCurrency = snap.Fare?.Currency,
            IsFareLocked = snap.IsFareLocked,
            CandidateDriverId = snap.CandidateDriverId,
            OfferedDriverId = snap.OfferedDriverId,
            AssignedDriverId = snap.AssignedDriverId,
            RejectionCount = snap.RejectionCount,
            MaxDriverRejections = snap.MaxDriverRejections,
            PaymentIdempotencyKey = snap.PaymentIdempotencyKey,
            PaymentId = snap.PaymentId,
            LastPaymentFailure = snap.LastPaymentFailure?.ToString(),

            PickupName = context.PickupName,
            PickupAddress = context.PickupAddress,
            PickupLat = context.PickupLat,
            PickupLon = context.PickupLon,
            DestName = context.DestName,
            DestAddress = context.DestAddress,
            DestLat = context.DestLat,
            DestLon = context.DestLon,
            PaymentMethod = context.PaymentMethod,
            MockTokenAlias = context.MockTokenAlias,
            DistanceKm = context.DistanceKm,
            EstimatedMinutes = context.EstimatedMinutes,
            CreatedAt = context.CreatedAt,
            UpdatedAt = context.UpdatedAt,
            RowVersion = context.RowVersion,
            PaymentTransport = context.PaymentTransport,
            PricingMode = context.PricingMode
        };

        if (snap.RejectedDriverIds != null)
        {
            foreach (var rejected in snap.RejectedDriverIds)
            {
                record.Rejections.Add(new TripDriverRejectionRecord
                {
                    TripId = snap.Id,
                    DriverId = rejected
                });
            }
        }

        _db.Trips.Add(record);
    }

    public void Update(TripContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var snap = context.Trip.ToSnapshot();
        var record = _db.Trips.Local.FirstOrDefault(t => t.Id == snap.Id);

        var isNewAttach = false;
        if (record is null)
        {
            record = new TripRecord { Id = snap.Id };
            _db.Trips.Attach(record);
            isNewAttach = true;
        }

        if (isNewAttach && context.RowVersion is not null)
        {
            _db.Entry(record).Property(r => r.RowVersion).OriginalValue = context.RowVersion;
        }
        else if (record.RowVersion is not null)
        {
            context.RowVersion = record.RowVersion;
        }

        record.RiderId = snap.RiderId;
        record.Status = snap.Status.ToString();
        record.QuoteId = snap.Fare?.QuoteId;
        record.FareAmount = snap.Fare?.Amount;
        record.FareCurrency = snap.Fare?.Currency;
        record.IsFareLocked = snap.IsFareLocked;
        record.CandidateDriverId = snap.CandidateDriverId;
        record.OfferedDriverId = snap.OfferedDriverId;
        record.AssignedDriverId = snap.AssignedDriverId;
        record.RejectionCount = snap.RejectionCount;
        record.MaxDriverRejections = snap.MaxDriverRejections;
        record.PaymentIdempotencyKey = snap.PaymentIdempotencyKey;
        record.PaymentId = snap.PaymentId;
        record.LastPaymentFailure = snap.LastPaymentFailure?.ToString();

        record.PickupName = context.PickupName;
        record.PickupAddress = context.PickupAddress;
        record.PickupLat = context.PickupLat;
        record.PickupLon = context.PickupLon;
        record.DestName = context.DestName;
        record.DestAddress = context.DestAddress;
        record.DestLat = context.DestLat;
        record.DestLon = context.DestLon;
        record.PaymentMethod = context.PaymentMethod;
        record.MockTokenAlias = context.MockTokenAlias;
        record.DistanceKm = context.DistanceKm;
        record.EstimatedMinutes = context.EstimatedMinutes;
        record.PaymentTransport = context.PaymentTransport;
        record.PricingMode = context.PricingMode;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        if (snap.RejectedDriverIds != null)
        {
            foreach (var driverId in snap.RejectedDriverIds)
            {
                if (!record.Rejections.Any(r => r.DriverId == driverId))
                {
                    record.Rejections.Add(new TripDriverRejectionRecord { TripId = snap.Id, DriverId = driverId });
                }
            }
        }
    }
}
