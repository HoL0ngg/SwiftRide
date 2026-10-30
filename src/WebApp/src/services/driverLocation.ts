export interface DriverHeartbeatOptions {
  driverId: string;
  getCoordinates: () => { latitude: number; longitude: number };
  updateLocation: (
    driverId: string,
    latitude: number,
    longitude: number,
    isOnline: boolean
  ) => Promise<void>;
  intervalMs?: number;
}

export interface DriverHeartbeat {
  start(): void;
  stop(): void;
  isRunning(): boolean;
}

export function createDriverHeartbeat(options: DriverHeartbeatOptions): DriverHeartbeat {
  const { driverId, getCoordinates, updateLocation, intervalMs = 30_000 } = options;
  let timerId: ReturnType<typeof setInterval> | null = null;
  let inFlight = false;
  let running = false;

  async function sendHeartbeat(isOnline: boolean): Promise<void> {
    if (inFlight) return;
    inFlight = true;
    try {
      const coords = getCoordinates();
      await updateLocation(driverId, coords.latitude, coords.longitude, isOnline);
    } catch {
      // Bounded error logging/handling; do not crash
    } finally {
      inFlight = false;
    }
  }

  return {
    start() {
      if (running) return;
      running = true;
      void sendHeartbeat(true);
      timerId = setInterval(() => {
        void sendHeartbeat(true);
      }, intervalMs);
    },
    stop() {
      if (!running) return;
      running = false;
      if (timerId !== null) {
        clearInterval(timerId);
        timerId = null;
      }
      void sendHeartbeat(false);
    },
    isRunning() {
      return running;
    },
  };
}
