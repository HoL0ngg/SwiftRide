import type { TripResponse, TripStatus } from '../types/tripTypes';
import { sumVnd } from './money';

const ACCEPTED_STATUSES: TripStatus[] = ['DriverAccepted', 'EnRoute', 'PickedUp', 'DroppedOff', 'PaymentPending'];

/** True once the driver owns the trip: going offline or moving the map pin is blocked from here on. */
export function isAcceptedByDriver(status: TripStatus | null | undefined): boolean {
  return !!status && ACCEPTED_STATUSES.includes(status);
}

export interface EarningsSummary {
  completedCount: number;
  /** Whole VND as a string, so large sums never lose precision. */
  totalVnd: string;
}

/** Only `Paid` trips count; cancelled, failed and pending ones never add to earnings. */
export function summarizeEarnings(history: TripResponse[]): EarningsSummary {
  const paid = history.filter(trip => trip.status === 'Paid');
  return { completedCount: paid.length, totalVnd: sumVnd(paid.map(trip => trip.fareAmount)) };
}
