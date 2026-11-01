import { useCallback, useEffect, useRef, useState } from 'react';
import type { TripApi } from '../services/tripApi';
import { createDriverHeartbeat } from '../services/driverLocation';

export interface DriverPosition {
  latitude: number;
  longitude: number;
}

/** Default position (central Ho Chi Minh City) until the driver picks one on the map. */
export const DEFAULT_DRIVER_POSITION: DriverPosition = { latitude: 10.7769, longitude: 106.7009 };

const HEARTBEAT_INTERVAL_MS = 30_000;

/**
 * Keeps the driver's location fresh in Matching while online.
 * `moveTo` changes the position and, when online, pushes it immediately instead of waiting for the next heartbeat.
 */
export function useDriverPresence(api: TripApi, driverId: string, online: boolean) {
  const [position, setPosition] = useState<DriverPosition>(DEFAULT_DRIVER_POSITION);
  const positionRef = useRef(position);
  const onlineRef = useRef(online);
  onlineRef.current = online;

  useEffect(() => {
    if (!online) return;
    const heartbeat = createDriverHeartbeat({
      driverId,
      getCoordinates: () => positionRef.current,
      updateLocation: (id, latitude, longitude, isOnline) =>
        api.updateDriverLocation(id, latitude, longitude, isOnline),
      intervalMs: HEARTBEAT_INTERVAL_MS,
    });
    heartbeat.start();
    return () => heartbeat.stop();
  }, [api, driverId, online]);

  const moveTo = useCallback(
    (next: DriverPosition) => {
      positionRef.current = next;
      setPosition(next);
      if (onlineRef.current) {
        api.updateDriverLocation(driverId, next.latitude, next.longitude, true).catch(() => {
          // The next heartbeat retries; the map pin already shows the new position.
        });
      }
    },
    [api, driverId]
  );

  return { position, moveTo };
}
