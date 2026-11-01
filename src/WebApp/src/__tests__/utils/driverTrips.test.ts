import { describe, expect, it } from 'vitest';
import type { TripResponse, TripStatus } from '../../types/tripTypes';
import { isAcceptedByDriver, summarizeEarnings } from '../../utils/driverTrips';

function trip(status: TripStatus, fareAmount: string | null): TripResponse {
  return {
    id: `trip-${status}-${fareAmount}`,
    riderId: 'rider-1',
    status,
    candidateDriverId: null,
    offeredDriverId: null,
    assignedDriverId: null,
    rejectionCount: 0,
    rematchPending: false,
    quoteId: null,
    fareAmount,
    currency: 'VND',
    distanceKm: 1,
    estimatedMinutes: 5,
    pickupName: 'A',
    pickupAddress: 'A',
    pickup: { latitude: 0, longitude: 0 },
    destinationName: 'B',
    destinationAddress: 'B',
    destination: { latitude: 0, longitude: 0 },
    paymentMethod: 'wallet',
    createdAt: '2026-10-17T10:00:00Z',
    updatedAt: '2026-10-17T10:00:00Z',
  };
}

describe('summarizeEarnings', () => {
  it('counts and sums only Paid trips', () => {
    const summary = summarizeEarnings([
      trip('Paid', '50000'),
      trip('Paid', '75000'),
      trip('Cancelled', '800000'),
      trip('Failed', '900000'),
      trip('PaymentPending', '300000'),
    ]);
    expect(summary).toEqual({ completedCount: 2, totalVnd: '125000' });
  });

  it('is zero for an empty history and ignores a Paid trip without a fare', () => {
    expect(summarizeEarnings([])).toEqual({ completedCount: 0, totalVnd: '0' });
    expect(summarizeEarnings([trip('Paid', null)])).toEqual({ completedCount: 1, totalVnd: '0' });
  });

  it('keeps precision for sums beyond Number.MAX_SAFE_INTEGER', () => {
    const summary = summarizeEarnings([trip('Paid', '9007199254740993'), trip('Paid', '1')]);
    expect(summary.totalVnd).toBe('9007199254740994');
  });
});

describe('isAcceptedByDriver', () => {
  it.each<TripStatus>(['DriverAccepted', 'EnRoute', 'PickedUp', 'DroppedOff', 'PaymentPending'])(
    'is true once the driver owns the trip (%s)',
    status => expect(isAcceptedByDriver(status)).toBe(true)
  );

  it.each<TripStatus>(['Requested', 'Quoted', 'DriverOffered', 'Paid', 'Cancelled', 'Failed'])(
    'is false while only offered or after the trip ended (%s)',
    status => expect(isAcceptedByDriver(status)).toBe(false)
  );

  it('is false when there is no trip', () => {
    expect(isAcceptedByDriver(null)).toBe(false);
    expect(isAcceptedByDriver(undefined)).toBe(false);
  });
});
