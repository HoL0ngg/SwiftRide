import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createTripStore } from '../../store/tripStore';
import type { TripApi } from '../../services/tripApi';
import type { DriverFeedApiResponse, TripResponse } from '../../types/tripTypes';

const sampleTrip: TripResponse = {
  id: 't-1',
  riderId: 'rider-1',
  status: 'Requested',
  candidateDriverId: null,
  offeredDriverId: null,
  assignedDriverId: null,
  rejectionCount: 0,
  rematchPending: false,
  quoteId: null,
  fareAmount: null,
  currency: null,
  distanceKm: 4.2,
  estimatedMinutes: 12,
  pickupName: 'A',
  pickupAddress: 'A',
  pickup: { latitude: 10.1, longitude: 106.1 },
  destinationName: 'B',
  destinationAddress: 'B',
  destination: { latitude: 10.2, longitude: 106.2 },
  paymentMethod: 'wallet',
  createdAt: '2026-10-29T10:00:00Z',
  updatedAt: '2026-10-29T10:00:00Z',
};

describe('tripStore', () => {
  let mockApi: TripApi;

  beforeEach(() => {
    vi.useFakeTimers();
    mockApi = {
      createTrip: vi.fn(),
      getCurrentTrip: vi.fn().mockResolvedValue(null),
      getTripById: vi.fn(),
      getDriverFeed: vi.fn().mockResolvedValue({ offeredTrip: null, activeTrip: null }),
      getHistory: vi.fn().mockResolvedValue({ items: [], nextCursor: null }),
      confirmQuote: vi.fn(),
      cancelTrip: vi.fn(),
      driverAccept: vi.fn(),
      driverReject: vi.fn(),
      driverEnRoute: vi.fn(),
      driverPickup: vi.fn(),
      driverDropoff: vi.fn(),
      updateDriverLocation: vi.fn(),
    };
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('polls current trip for rider every 3s and notifies subscribers', async () => {
    const store = createTripStore(mockApi, { userId: 'rider-1', role: 'rider' });
    const listener = vi.fn();
    store.subscribe(listener);

    vi.mocked(mockApi.getCurrentTrip).mockResolvedValueOnce(sampleTrip);

    store.startPolling();
    // Flush initial immediate poll
    await vi.advanceTimersByTimeAsync(10);
    expect(mockApi.getCurrentTrip).toHaveBeenCalledTimes(1);
    expect(store.getState().trip?.id).toBe('t-1');
    expect(listener).toHaveBeenCalled();

    // After 3 seconds, polls again
    vi.mocked(mockApi.getCurrentTrip).mockResolvedValueOnce({
      ...sampleTrip,
      status: 'Quoted',
      fareAmount: '50000',
    });
    await vi.advanceTimersByTimeAsync(3000);
    expect(mockApi.getCurrentTrip).toHaveBeenCalledTimes(2);
    expect(store.getState().trip?.status).toBe('Quoted');

    store.stopPolling();
  });

  it('polls driver feed for driver role', async () => {
    const store = createTripStore(mockApi, { userId: 'driver-1', role: 'driver' });
    const feed: DriverFeedApiResponse = {
      offeredTrip: { ...sampleTrip, status: 'DriverOffered' },
      activeTrip: null,
    };
    vi.mocked(mockApi.getDriverFeed).mockResolvedValue(feed);

    store.startPolling();
    await vi.advanceTimersByTimeAsync(10);
    expect(mockApi.getDriverFeed).toHaveBeenCalledTimes(1);
    expect(store.getState().offeredTrip?.status).toBe('DriverOffered');
    expect(store.getState().activeTrip).toBeNull();
    store.stopPolling();
  });

  it('single-flight: does not trigger overlapping polls if previous poll is in flight', async () => {
    let resolveSlowPoll: (res: TripResponse | null) => void;
    const slowPollPromise = new Promise<TripResponse | null>(resolve => {
      resolveSlowPoll = resolve;
    });

    vi.mocked(mockApi.getCurrentTrip).mockImplementation(() => slowPollPromise);

    const store = createTripStore(mockApi, { userId: 'rider-1', role: 'rider' });
    store.startPolling();

    await vi.advanceTimersByTimeAsync(10);
    expect(mockApi.getCurrentTrip).toHaveBeenCalledTimes(1);

    // Advance 3s while slowPollPromise is still pending
    await vi.advanceTimersByTimeAsync(3000);
    // Should still be only 1 call because first is not finished
    expect(mockApi.getCurrentTrip).toHaveBeenCalledTimes(1);

    // Resolve first poll
    resolveSlowPoll!(sampleTrip);
    await vi.advanceTimersByTimeAsync(10);

    // Advance another 3s after resolution
    await vi.advanceTimersByTimeAsync(3000);
    expect(mockApi.getCurrentTrip).toHaveBeenCalledTimes(2);

    store.stopPolling();
  });

  it('retains existing trip on polling errors (no UI reset to empty/demo)', async () => {
    const store = createTripStore(mockApi, { userId: 'rider-1', role: 'rider' });
    vi.mocked(mockApi.getCurrentTrip).mockResolvedValueOnce(sampleTrip);

    store.startPolling();
    await vi.advanceTimersByTimeAsync(10);
    expect(store.getState().trip?.id).toBe('t-1');

    // Next poll fails with network error
    vi.mocked(mockApi.getCurrentTrip).mockRejectedValueOnce(new Error('Network error'));
    await vi.advanceTimersByTimeAsync(3000);

    // Trip must still be retained
    expect(store.getState().trip?.id).toBe('t-1');
    expect(store.getState().error).toContain('Network error');

    store.stopPolling();
  });

  it('switches actor cleanly, aborting previous poll generation', async () => {
    let resolveRiderPoll: (res: TripResponse | null) => void;
    const riderPollPromise = new Promise<TripResponse | null>(resolve => {
      resolveRiderPoll = resolve;
    });

    vi.mocked(mockApi.getCurrentTrip).mockImplementation(() => riderPollPromise);

    const store = createTripStore(mockApi, { userId: 'rider-1', role: 'rider' });
    store.startPolling();
    await vi.advanceTimersByTimeAsync(10);
    expect(mockApi.getCurrentTrip).toHaveBeenCalledTimes(1);

    // Switch to driver
    const driverFeed: DriverFeedApiResponse = { offeredTrip: null, activeTrip: null };
    vi.mocked(mockApi.getDriverFeed).mockResolvedValue(driverFeed);

    store.setActor({ userId: 'driver-1', role: 'driver' });
    await vi.advanceTimersByTimeAsync(10);
    expect(mockApi.getDriverFeed).toHaveBeenCalledTimes(1);

    // Stale rider poll finishes after switch
    resolveRiderPoll!(sampleTrip);
    await vi.advanceTimersByTimeAsync(10);

    // Store state should reflect driver, not stale rider poll
    expect(store.getState().actor.role).toBe('driver');
    expect(store.getState().trip).toBeNull();

    store.stopPolling();
  });

  it('executes confirmQuote and updates trip state immediately', async () => {
    const store = createTripStore(mockApi, { userId: 'rider-1', role: 'rider' });
    const confirmedTrip: TripResponse = { ...sampleTrip, status: 'DriverOffered' };
    vi.mocked(mockApi.confirmQuote).mockResolvedValue(confirmedTrip);

    const res = await store.confirmQuote('t-1', 'quote-123');
    expect(res.status).toBe('DriverOffered');
    expect(store.getState().trip?.status).toBe('DriverOffered');
  });
});
