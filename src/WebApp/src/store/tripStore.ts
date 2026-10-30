import type { Actor } from '../services/auth';
import type { TripApi } from '../services/tripApi';
import type {
  CreateTripInput,
  TripResponse,
} from '../types/tripTypes';

export interface TripState {
  actor: Actor;
  trip: TripResponse | null;
  offeredTrip: TripResponse | null;
  activeTrip: TripResponse | null;
  history: TripResponse[];
  loading: boolean;
  error: string | null;
  lastSyncAt: number | null;
}

export interface TripStore {
  getState(): TripState;
  subscribe(listener: () => void): () => void;
  startPolling(): void;
  stopPolling(): void;
  poll(): Promise<void>;
  setActor(actor: Actor): void;
  createTrip(input: CreateTripInput): Promise<TripResponse>;
  confirmQuote(tripId: string, quoteId: string): Promise<TripResponse>;
  cancelTrip(tripId: string, reason?: string): Promise<TripResponse>;
  driverAccept(tripId: string): Promise<TripResponse>;
  driverReject(tripId: string): Promise<TripResponse>;
  driverEnRoute(tripId: string): Promise<TripResponse>;
  driverPickup(tripId: string): Promise<TripResponse>;
  driverDropoff(tripId: string): Promise<TripResponse>;
  refreshHistory(): Promise<void>;
  clearError(): void;
  destroy(): void;
}

const DEFAULT_POLL_INTERVAL_MS = 3_000;
const RATE_LIMIT_BACKOFF_MS = 10_000;

export function createTripStore(api: TripApi, initialActor: Actor): TripStore {
  let state: TripState = {
    actor: { ...initialActor },
    trip: null,
    offeredTrip: null,
    activeTrip: null,
    history: [],
    loading: false,
    error: null,
    lastSyncAt: null,
  };

  const listeners = new Set<() => void>();
  let timerId: ReturnType<typeof setTimeout> | null = null;
  let isPolling = false;
  let inFlight = false;
  let generation = 0;

  function notify() {
    for (const listener of listeners) {
      listener();
    }
  }

  function updateState(partial: Partial<TripState>) {
    state = { ...state, ...partial };
    notify();
  }

  function scheduleNextPoll(delayMs: number = DEFAULT_POLL_INTERVAL_MS) {
    if (!isPolling) return;
    if (timerId !== null) {
      clearTimeout(timerId);
    }
    timerId = setTimeout(() => {
      void pollInternal();
    }, delayMs);
  }

  async function pollInternal(): Promise<void> {
    if (!isPolling || inFlight) return;
    inFlight = true;
    const currentGen = generation;

    try {
      if (state.actor.role === 'rider') {
        const currentTrip = await api.getCurrentTrip();
        if (currentGen !== generation) return;

        updateState({
          trip: currentTrip,
          error: null,
          lastSyncAt: Date.now(),
        });
      } else {
        const feed = await api.getDriverFeed();
        if (currentGen !== generation) return;

        updateState({
          offeredTrip: feed.offeredTrip,
          activeTrip: feed.activeTrip,
          trip: feed.activeTrip ?? feed.offeredTrip,
          error: null,
          lastSyncAt: Date.now(),
        });
      }
      scheduleNextPoll(DEFAULT_POLL_INTERVAL_MS);
    } catch (err: unknown) {
      if (currentGen !== generation) return;
      const message = err instanceof Error ? err.message : String(err);
      const isRateLimited = message.includes('429');
      updateState({
        error: message,
      });
      scheduleNextPoll(isRateLimited ? RATE_LIMIT_BACKOFF_MS : DEFAULT_POLL_INTERVAL_MS);
    } finally {
      inFlight = false;
    }
  }

  return {
    getState() {
      return state;
    },

    subscribe(listener: () => void) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },

    startPolling() {
      if (isPolling) return;
      isPolling = true;
      void pollInternal();
    },

    stopPolling() {
      isPolling = false;
      if (timerId !== null) {
        clearTimeout(timerId);
        timerId = null;
      }
    },

    async poll() {
      await pollInternal();
    },

    setActor(newActor: Actor) {
      generation++;
      if (timerId !== null) {
        clearTimeout(timerId);
        timerId = null;
      }
      inFlight = false;

      state = {
        ...state,
        actor: { ...newActor },
        trip: null,
        offeredTrip: null,
        activeTrip: null,
        history: [],
        error: null,
        loading: false,
      };
      notify();

      if (isPolling) {
        void pollInternal();
      }
    },

    async createTrip(input: CreateTripInput) {
      updateState({ loading: true, error: null });
      try {
        const trip = await api.createTrip(input);
        updateState({ trip, loading: false });
        return trip;
      } catch (err: unknown) {
        const error = err instanceof Error ? err.message : String(err);
        updateState({ loading: false, error });
        throw err;
      }
    },

    async confirmQuote(tripId: string, quoteId: string) {
      updateState({ loading: true, error: null });
      try {
        const trip = await api.confirmQuote(tripId, quoteId);
        updateState({ trip, loading: false });
        return trip;
      } catch (err: unknown) {
        const error = err instanceof Error ? err.message : String(err);
        updateState({ loading: false, error });
        throw err;
      }
    },

    async cancelTrip(tripId: string, reason?: string) {
      updateState({ loading: true, error: null });
      try {
        const trip = await api.cancelTrip(tripId, reason);
        updateState({ trip, loading: false });
        return trip;
      } catch (err: unknown) {
        const error = err instanceof Error ? err.message : String(err);
        updateState({ loading: false, error });
        throw err;
      }
    },

    async driverAccept(tripId: string) {
      updateState({ loading: true, error: null });
      try {
        const trip = await api.driverAccept(tripId);
        updateState({ trip, activeTrip: trip, offeredTrip: null, loading: false });
        return trip;
      } catch (err: unknown) {
        const error = err instanceof Error ? err.message : String(err);
        updateState({ loading: false, error });
        throw err;
      }
    },

    async driverReject(tripId: string) {
      updateState({ loading: true, error: null });
      try {
        const trip = await api.driverReject(tripId);
        updateState({ trip: null, offeredTrip: null, loading: false });
        return trip;
      } catch (err: unknown) {
        const error = err instanceof Error ? err.message : String(err);
        updateState({ loading: false, error });
        throw err;
      }
    },

    async driverEnRoute(tripId: string) {
      updateState({ loading: true, error: null });
      try {
        const trip = await api.driverEnRoute(tripId);
        updateState({ trip, activeTrip: trip, loading: false });
        return trip;
      } catch (err: unknown) {
        const error = err instanceof Error ? err.message : String(err);
        updateState({ loading: false, error });
        throw err;
      }
    },

    async driverPickup(tripId: string) {
      updateState({ loading: true, error: null });
      try {
        const trip = await api.driverPickup(tripId);
        updateState({ trip, activeTrip: trip, loading: false });
        return trip;
      } catch (err: unknown) {
        const error = err instanceof Error ? err.message : String(err);
        updateState({ loading: false, error });
        throw err;
      }
    },

    async driverDropoff(tripId: string) {
      updateState({ loading: true, error: null });
      try {
        const trip = await api.driverDropoff(tripId);
        updateState({ trip, activeTrip: null, loading: false });
        return trip;
      } catch (err: unknown) {
        const error = err instanceof Error ? err.message : String(err);
        updateState({ loading: false, error });
        throw err;
      }
    },

    async refreshHistory() {
      try {
        const res = await api.getHistory(undefined, 20);
        updateState({ history: res.items });
      } catch (err: unknown) {
        const error = err instanceof Error ? err.message : String(err);
        updateState({ error });
      }
    },

    clearError() {
      updateState({ error: null });
    },

    destroy() {
      this.stopPolling();
      generation++;
      listeners.clear();
    },
  };
}
