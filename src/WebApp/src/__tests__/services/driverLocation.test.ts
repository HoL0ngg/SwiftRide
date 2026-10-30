import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createDriverHeartbeat } from '../../services/driverLocation';

describe('driverLocation heartbeat', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('sends location immediately when online, then every interval', async () => {
    const updateLocation = vi.fn().mockResolvedValue(undefined);
    const coords = { latitude: 10.77, longitude: 106.69 };

    const heartbeat = createDriverHeartbeat({
      driverId: 'drv-1',
      getCoordinates: () => coords,
      updateLocation,
      intervalMs: 30_000,
    });

    heartbeat.start();
    // Flush initial immediate call
    await vi.advanceTimersByTimeAsync(0);
    expect(updateLocation).toHaveBeenCalledTimes(1);
    expect(updateLocation).toHaveBeenCalledWith('drv-1', 10.77, 106.69, true);

    // After 30s, sends second heartbeat
    coords.latitude = 10.78;
    await vi.advanceTimersByTimeAsync(30_000);
    expect(updateLocation).toHaveBeenCalledTimes(2);
    expect(updateLocation).toHaveBeenLastCalledWith('drv-1', 10.78, 106.69, true);

    heartbeat.stop();
  });

  it('sends isOnline false when stopped and clears recurring timer', async () => {
    const updateLocation = vi.fn().mockResolvedValue(undefined);
    const heartbeat = createDriverHeartbeat({
      driverId: 'drv-1',
      getCoordinates: () => ({ latitude: 10.77, longitude: 106.69 }),
      updateLocation,
      intervalMs: 30_000,
    });

    heartbeat.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(updateLocation).toHaveBeenCalledTimes(1);

    heartbeat.stop();
    await vi.advanceTimersByTimeAsync(0);
    expect(updateLocation).toHaveBeenCalledTimes(2);
    expect(updateLocation).toHaveBeenLastCalledWith('drv-1', 10.77, 106.69, false);

    // Advancing timer should not trigger more calls
    await vi.advanceTimersByTimeAsync(60_000);
    expect(updateLocation).toHaveBeenCalledTimes(2);
  });

  it('prevents overlapping heartbeats if network is slow', async () => {
    let resolveSlowUpdate: () => void;
    const slowUpdatePromise = new Promise<void>(resolve => {
      resolveSlowUpdate = resolve;
    });

    const updateLocation = vi.fn().mockImplementation(() => slowUpdatePromise);
    const heartbeat = createDriverHeartbeat({
      driverId: 'drv-1',
      getCoordinates: () => ({ latitude: 10.77, longitude: 106.69 }),
      updateLocation,
      intervalMs: 30_000,
    });

    heartbeat.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(updateLocation).toHaveBeenCalledTimes(1);

    // Advance 30s while first call is in flight
    await vi.advanceTimersByTimeAsync(30_000);
    expect(updateLocation).toHaveBeenCalledTimes(1);

    resolveSlowUpdate!();
    await vi.advanceTimersByTimeAsync(0);

    // Now after next interval it can trigger
    await vi.advanceTimersByTimeAsync(30_000);
    expect(updateLocation).toHaveBeenCalledTimes(2);

    heartbeat.stop();
  });
});
