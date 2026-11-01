import { useEffect, useSyncExternalStore } from 'react';
import type { TripState, TripStore } from './tripStore';

/** Read-only subscription to the store. Does not control polling. */
export function useTripState(store: TripStore): TripState {
  return useSyncExternalStore(
    store.subscribe,
    store.getState,
    store.getState
  );
}

/**
 * Starts polling while the calling component is mounted and returns the state.
 * Call this once per store, in the app shell, so child views unmounting
 * (for example when switching tabs) can never stop the shared polling loop.
 */
export function useTrip(store: TripStore): TripState {
  useEffect(() => {
    store.startPolling();
    return () => {
      store.stopPolling();
    };
  }, [store]);

  return useTripState(store);
}
