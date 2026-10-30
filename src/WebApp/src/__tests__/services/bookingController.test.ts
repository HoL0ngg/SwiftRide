import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createSwiftRideClient } from '../../services/api';
import type { TripResponse } from '../../types/tripTypes';
import { formatVnd, sumVnd } from '../../utils/money';

const sampleTrip: TripResponse = {
  id: 't-booking-1',
  riderId: 'rider-1',
  status: 'Quoted',
  candidateDriverId: 'drv-1',
  offeredDriverId: null,
  assignedDriverId: null,
  rejectionCount: 0,
  rematchPending: false,
  quoteId: 'q-100',
  fareAmount: '75000',
  currency: 'VND',
  distanceKm: 5.0,
  estimatedMinutes: 15,
  pickupName: 'Chợ Bến Thành',
  pickupAddress: 'Quận 1',
  pickup: { latitude: 10.7725, longitude: 106.698 },
  destinationName: 'Landmark 81',
  destinationAddress: 'Bình Thạnh',
  destination: { latitude: 10.7951, longitude: 106.7219 },
  paymentMethod: 'wallet',
  createdAt: '2026-10-29T10:00:00Z',
  updatedAt: '2026-10-29T10:00:00Z',
};

describe('Booking and Driver Controller flow', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('create -> confirm -> driver accept -> dropoff calls correct routes with idempotency keys and does not self-assign Paid', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);

    const client = createSwiftRideClient({ userId: 'rider-1', role: 'rider' });
    client.session.setToken('jwt-rider');

    // 1. Create Trip (POST /trips) -> returns 201 with Quoted trip
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(sampleTrip), { status: 201, headers: { Location: `/trips/${sampleTrip.id}` } })
    );

    const created = await client.store.createTrip({
      pickup: sampleTrip.pickup,
      destination: sampleTrip.destination,
      pickupName: sampleTrip.pickupName,
      destinationName: sampleTrip.destinationName,
      paymentMethod: 'wallet',
    });

    expect(created.id).toBe(sampleTrip.id);
    expect(created.status).toBe('Quoted');
    expect(fetchMock).toHaveBeenNthCalledWith(
      1,
      '/trips',
      expect.objectContaining({
        method: 'POST',
        headers: expect.objectContaining({
          Authorization: 'Bearer jwt-rider',
          'Idempotency-Key': expect.any(String),
        }),
      })
    );

    // 2. Confirm quote (POST /trips/{id}/confirm-quote) -> returns DriverOffered
    const offeredTrip: TripResponse = { ...sampleTrip, status: 'DriverOffered', offeredDriverId: 'drv-1' };
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(offeredTrip), { status: 200 })
    );

    const confirmed = await client.store.confirmQuote(sampleTrip.id, sampleTrip.quoteId!);
    expect(confirmed.status).toBe('DriverOffered');
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      `/trips/${sampleTrip.id}/confirm-quote`,
      expect.objectContaining({
        method: 'POST',
        headers: expect.objectContaining({
          Authorization: 'Bearer jwt-rider',
          'Idempotency-Key': expect.any(String),
        }),
      })
    );

    // 3. Switch to Driver and Accept (POST /trips/{id}/driver-accept)
    const driverClient = createSwiftRideClient({ userId: 'drv-1', role: 'driver' });
    driverClient.session.setToken('jwt-driver');

    const acceptedTrip: TripResponse = { ...sampleTrip, status: 'DriverAccepted', assignedDriverId: 'drv-1' };
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(acceptedTrip), { status: 200 })
    );

    const accepted = await driverClient.store.driverAccept(sampleTrip.id);
    expect(accepted.status).toBe('DriverAccepted');
    expect(fetchMock).toHaveBeenNthCalledWith(
      3,
      `/trips/${sampleTrip.id}/driver-accept`,
      expect.objectContaining({
        method: 'POST',
        headers: expect.objectContaining({
          Authorization: 'Bearer jwt-driver',
          'Idempotency-Key': expect.any(String),
        }),
      })
    );

    // 4. Dropoff (POST /trips/{id}/dropoff) -> DroppedOff (UI NEVER auto-sets Paid!)
    const droppedOffTrip: TripResponse = { ...sampleTrip, status: 'DroppedOff', assignedDriverId: 'drv-1' };
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify(droppedOffTrip), { status: 200 })
    );

    const droppedOff = await driverClient.store.driverDropoff(sampleTrip.id);
    expect(droppedOff.status).toBe('DroppedOff');
    expect(droppedOff.status).not.toBe('Paid');
    expect(driverClient.store.getState().trip?.status).toBe('DroppedOff');
  });

  it('driver statistics sums only Paid trips using sumVnd and formatVnd', () => {
    const history: TripResponse[] = [
      { ...sampleTrip, id: 't1', status: 'Paid', fareAmount: '50000' },
      { ...sampleTrip, id: 't2', status: 'Paid', fareAmount: '75000' },
      { ...sampleTrip, id: 't3', status: 'Cancelled', fareAmount: '800000' },
      { ...sampleTrip, id: 't4', status: 'Failed', fareAmount: '900000' },
    ];

    const paidTrips = history.filter(t => t.status === 'Paid');
    expect(paidTrips).toHaveLength(2);

    const totalVnd = sumVnd(paidTrips.map(t => t.fareAmount));
    expect(totalVnd).toBe('125000');
    expect(formatVnd(totalVnd)).toBe('125.000 ₫');
  });

  it('switching actor clears old trip data and prevents leaking previous actor responses', async () => {
    const client = createSwiftRideClient({ userId: 'rider-1', role: 'rider' });
    client.session.setToken('jwt-rider');

    // Simulate current trip for rider
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify(sampleTrip), { status: 200 })
    );
    vi.stubGlobal('fetch', fetchMock);

    client.store.startPolling();
    await vi.advanceTimersByTimeAsync(10);
    expect(client.store.getState().trip?.id).toBe(sampleTrip.id);

    // Switch actor to driver
    client.session.setActor({ userId: 'driver-2', role: 'driver' });
    client.session.setToken('jwt-driver-2');
    client.store.setActor({ userId: 'driver-2', role: 'driver' });

    // Store state should be reset for new actor
    expect(client.store.getState().actor.role).toBe('driver');
    expect(client.store.getState().trip).toBeNull();
    client.store.stopPolling();
  });
});
