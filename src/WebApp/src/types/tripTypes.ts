export type TripStatus =
  | 'Requested'
  | 'Quoted'
  | 'DriverOffered'
  | 'DriverAccepted'
  | 'EnRoute'
  | 'PickedUp'
  | 'DroppedOff'
  | 'PaymentPending'
  | 'Paid'
  | 'Cancelled'
  | 'Failed';

export interface GeoPoint {
  latitude: number;
  longitude: number;
}

export interface CreateTripInput {
  pickup: GeoPoint;
  pickupName?: string;
  pickupAddress?: string;
  destination: GeoPoint;
  destinationName?: string;
  destinationAddress?: string;
  paymentMethod: string;
  mode?: string;
}

export interface ConfirmQuoteInput {
  quoteId: string;
}

export interface CancelTripInput {
  reason?: string;
}

export interface TripResponse {
  id: string;
  riderId: string;
  status: TripStatus;
  candidateDriverId: string | null;
  offeredDriverId: string | null;
  assignedDriverId: string | null;
  rejectionCount: number;
  rematchPending: boolean;
  quoteId: string | null;
  fareAmount: string | null;
  currency: string | null;
  distanceKm: number | null;
  estimatedMinutes: number | null;
  pickupName: string;
  pickupAddress: string;
  pickup: GeoPoint;
  destinationName: string;
  destinationAddress: string;
  destination: GeoPoint;
  paymentMethod: string;
  createdAt: string;
  updatedAt: string;
}

export interface DriverFeedApiResponse {
  offeredTrip: TripResponse | null;
  activeTrip: TripResponse | null;
}

export interface TripHistoryPageApiResponse {
  items: TripResponse[];
  nextCursor: string | null;
}

export function isTerminalStatus(status: TripStatus): boolean {
  return status === 'Paid' || status === 'Cancelled' || status === 'Failed';
}
