import { useEffect, useSyncExternalStore } from 'react';
import type { TripState, TripStore } from './tripStore';

export function useTrip(store: TripStore): TripState {
  useEffect(() => {
    store.startPolling();
    return () => {
      store.stopPolling();
    };
  }, [store]);

  return useSyncExternalStore(
    store.subscribe,
    store.getState,
    store.getState
  );
}
