import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { TripResponse } from '../../types/tripTypes';
import DriverApp from '../../apps/driver/DriverApp';

const captured = vi.hoisted(() => ({
  trip: null as TripResponse | null,
  presence: vi.fn(() => ({ position: { latitude: 0, longitude: 0 }, moveTo: vi.fn() })),
}));

vi.mock('../../services/api', () => ({ createSwiftRideClient: () => ({ store: {}, api: {}, session: {} }) }));
vi.mock('../../store/useTrip', () => ({ useTrip: () => ({ trip: captured.trip, history: [] }) }));
vi.mock('../../store/useDriverPresence', () => ({ useDriverPresence: captured.presence }));
vi.mock('../../components/RideMap', () => ({ default: () => null }));
vi.mock('../../components/DriverView', () => ({ default: () => null }));
vi.mock('../../components/AuthStatus', () => ({ default: () => null }));

describe('driver vehicle selection', () => {
  it.each(['DriverOffered', 'DriverAccepted'] as const)(
    'keeps the server vehicle type while offline with a %s trip', status => {
      captured.trip = {
        id: 'trip-car', status, vehicleType: 'car',
        pickup: { latitude: 0, longitude: 0 },
        destination: { latitude: 1, longitude: 1 },
      } as TripResponse;

      const html = renderToStaticMarkup(<DriverApp />);

      expect(html).toMatch(/<select[^>]*aria-label="Loại xe tài xế"[^>]*disabled/);
      expect(html).toMatch(/<option value="car" selected="">Xe 4 bánh/);
      expect(captured.presence).toHaveBeenLastCalledWith(
        expect.anything(), 'demo-driver', false, 'car');
    }
  );
});
