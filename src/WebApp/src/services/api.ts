import { createAuthSession, type Actor, type AuthSession, type AuthSessionOptions } from './auth';
import { createTripApi, type TripApi } from './tripApi';
import { createTripStore, type TripStore } from '../store/tripStore';

export interface SwiftRideClient {
  session: AuthSession;
  api: TripApi;
  store: TripStore;
}

export function createSwiftRideClient(
  initialActor: Actor,
  options?: AuthSessionOptions
): SwiftRideClient {
  const session = createAuthSession(initialActor, options);
  const api = createTripApi(session);
  const store = createTripStore(api, initialActor);
  return { session, api, store };
}

export * from './auth';
export * from './tripApi';
export * from '../store/tripStore';
export * from '../types/tripTypes';
export * from '../utils/money';
export * from '../types/place';
export * from '../store/useTrip';
