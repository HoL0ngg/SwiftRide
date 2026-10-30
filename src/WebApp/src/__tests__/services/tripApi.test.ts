import { afterEach, describe, expect, it, vi } from 'vitest';
import { createAuthSession } from '../../services/auth';
import { createTripApi } from '../../services/tripApi';
import type { TripResponse } from '../../types/tripTypes';

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.useRealTimers();
});

const mockTrip: TripResponse = {
  id: 'a0000000-0000-0000-0000-000000000001',
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
  distanceKm: 5.5,
  estimatedMinutes: 15.0,
  pickupName: 'Bến Thành',
  pickupAddress: 'Quận 1',
  pickup: { latitude: 10.7725, longitude: 106.698 },
  destinationName: 'Landmark 81',
  destinationAddress: 'Bình Thạnh',
  destination: { latitude: 10.7951, longitude: 106.7219 },
  paymentMethod: 'wallet',
  createdAt: '2026-10-29T10:00:00Z',
  updatedAt: '2026-10-29T10:00:00Z',
};

describe('tripApi', () => {
  it('creates trip with idempotency key and auth header, preserving null fare in Requested', async () => {
    const session = createAuthSession({ userId: 'rider-1', role: 'rider' });
    session.setToken('test-token');

    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(mockTrip), {
        status: 201,
        headers: { Location: `/trips/${mockTrip.id}` },
      })
    );
    vi.stubGlobal('fetch', fetchMock);

    const api = createTripApi(session);
    const result = await api.createTrip(
      {
        pickup: { latitude: 10.7725, longitude: 106.698 },
        destination: { latitude: 10.7951, longitude: 106.7219 },
        pickupName: 'Bến Thành',
        destinationName: 'Landmark 81',
        paymentMethod: 'wallet',
      },
      'key-123'
    );

    expect(result.id).toBe(mockTrip.id);
    expect(result.status).toBe('Requested');
    expect(result.fareAmount).toBeNull();
    expect(fetchMock).toHaveBeenCalledWith(
      '/trips',
      expect.objectContaining({
        method: 'POST',
        headers: expect.objectContaining({
          Authorization: 'Bearer test-token',
          'Idempotency-Key': 'key-123',
          'Content-Type': 'application/json',
        }),
      })
    );
  });

  it('getCurrentTrip returns null on 204 No Content', async () => {
    const session = createAuthSession({ userId: 'rider-1', role: 'rider' });
    session.setToken('test-token');

    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })));

    const api = createTripApi(session);
    const result = await api.getCurrentTrip();
    expect(result).toBeNull();
  });

  it('preserves huge fareAmount string precision without Number coercion', async () => {
    const session = createAuthSession({ userId: 'rider-1', role: 'rider' });
    session.setToken('test-token');

    const quotedTrip: TripResponse = {
      ...mockTrip,
      status: 'Quoted',
      fareAmount: '999999999999999999',
      currency: 'VND',
    };

    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(quotedTrip), { status: 200 })));

    const api = createTripApi(session);
    const result = await api.getCurrentTrip();
    expect(result?.fareAmount).toBe('999999999999999999');
    expect(typeof result?.fareAmount).toBe('string');
  });

  it('getDriverFeed returns offered and active trips', async () => {
    const session = createAuthSession({ userId: 'driver-1', role: 'driver' });
    session.setToken('driver-token');

    const feed = {
      offeredTrip: { ...mockTrip, status: 'DriverOffered' },
      activeTrip: null,
    };
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(feed), { status: 200 })));

    const api = createTripApi(session);
    const result = await api.getDriverFeed();
    expect(result.offeredTrip?.status).toBe('DriverOffered');
    expect(result.activeTrip).toBeNull();
  });

  it('confirmQuote sends quoteId and idempotency key', async () => {
    const session = createAuthSession({ userId: 'rider-1', role: 'rider' });
    session.setToken('test-token');

    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ ...mockTrip, status: 'DriverOffered' }), { status: 200 })
    );
    vi.stubGlobal('fetch', fetchMock);

    const api = createTripApi(session);
    const res = await api.confirmQuote(mockTrip.id, 'quote-abc', 'key-confirm');
    expect(res.status).toBe('DriverOffered');
    expect(fetchMock).toHaveBeenCalledWith(
      `/trips/${mockTrip.id}/confirm-quote`,
      expect.objectContaining({
        method: 'POST',
        headers: expect.objectContaining({
          'Idempotency-Key': 'key-confirm',
        }),
        body: JSON.stringify({ quoteId: 'quote-abc' }),
      })
    );
  });

  it('driverDropoff calls dropoff endpoint and returns updated trip', async () => {
    const session = createAuthSession({ userId: 'driver-1', role: 'driver' });
    session.setToken('test-token');

    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ ...mockTrip, status: 'DroppedOff' }), { status: 200 })
    );
    vi.stubGlobal('fetch', fetchMock);

    const api = createTripApi(session);
    const res = await api.driverDropoff(mockTrip.id, 'key-dropoff');
    expect(res.status).toBe('DroppedOff');
    expect(fetchMock).toHaveBeenCalledWith(
      `/trips/${mockTrip.id}/dropoff`,
      expect.objectContaining({
        method: 'POST',
        headers: expect.objectContaining({
          'Idempotency-Key': 'key-dropoff',
        }),
      })
    );
  });

  it('throws on 401 without silently falling back to mock', async () => {
    const session = createAuthSession({ userId: 'rider-1', role: 'rider' });
    session.setToken('expired-token');

    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('Unauthorized', { status: 401 })));

    const api = createTripApi(session);
    await expect(api.getCurrentTrip()).rejects.toThrow(/401|xác thực|phiên/i);
  });

  it('throws on 409 Conflict with backend problem details', async () => {
    const session = createAuthSession({ userId: 'rider-1', role: 'rider' });
    session.setToken('test-token');

    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ title: 'InvalidTransition', detail: 'Trip is not quoted' }), {
          status: 409,
          headers: { 'Content-Type': 'application/problem+json' },
        })
      )
    );

    const api = createTripApi(session);
    await expect(api.confirmQuote(mockTrip.id, 'quote-123')).rejects.toThrow(/InvalidTransition|not quoted|409/);
  });

  it('aborts after 20 seconds timeout', async () => {
    vi.useFakeTimers();
    const session = createAuthSession({ userId: 'rider-1', role: 'rider' });
    session.setToken('test-token');

    vi.stubGlobal('fetch', (_url: string, { signal }: RequestInit) =>
      new Promise((_resolve, reject) => {
        signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
      })
    );

    const api = createTripApi(session);
    const promise = api.getCurrentTrip();
    const rejection = expect(promise).rejects.toThrow(/thời gian|quá hạn|timeout/i);
    await vi.advanceTimersByTimeAsync(20_000);
    await rejection;
  });
});
