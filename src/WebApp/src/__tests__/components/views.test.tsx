import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { TripResponse, TripStatus } from '../../types/tripTypes';
import type { Place } from '../../types/place';
import type { TripState, TripStore } from '../../store/tripStore';
import { createAuthSession } from '../../services/auth';
import type { TripApi } from '../../services/tripApi';
import RiderView from '../../components/RiderView';
import DriverView from '../../components/DriverView';

const testPlaces: Place[] = [
  { id: 'pickup', name: 'Chợ Bến Thành', address: 'Quận 1, TP.HCM', lat: 10.7725, lng: 106.698 },
  { id: 'destination', name: 'Landmark 81', address: 'Bình Thạnh, TP.HCM', lat: 10.7951, lng: 106.7219 },
];

function mockTrip(status: TripStatus, fare = '50000'): TripResponse {
  return {
    id: `trip-${status}`,
    riderId: 'rider-1',
    status,
    candidateDriverId: 'driver-1',
    offeredDriverId: 'driver-1',
    assignedDriverId: ['DriverAccepted', 'EnRoute', 'PickedUp', 'DroppedOff', 'PaymentPending', 'Paid'].includes(status) ? 'driver-1' : null,
    rejectionCount: 0,
    rematchPending: false,
    quoteId: 'quote-123',
    fareAmount: fare,
    currency: 'VND',
    distanceKm: 4.6,
    estimatedMinutes: 18,
    pickupName: testPlaces[0].name,
    pickupAddress: testPlaces[0].address,
    pickup: { latitude: testPlaces[0].lat, longitude: testPlaces[0].lng },
    destinationName: testPlaces[1].name,
    destinationAddress: testPlaces[1].address,
    destination: { latitude: testPlaces[1].lat, longitude: testPlaces[1].lng },
    paymentMethod: 'wallet',
    createdAt: '2026-10-17T10:00:00Z',
    updatedAt: '2026-10-17T10:00:00Z',
  };
}

function createMockStore(trip: TripResponse | null = null, history: TripResponse[] = []): TripStore {
  const state: TripState = {
    actor: { userId: 'rider-1', role: 'rider' },
    trip,
    offeredTrip: trip?.status === 'DriverOffered' ? trip : null,
    activeTrip: trip,
    history,
    loading: false,
    error: null,
    lastSyncAt: null,
  };
  return {
    getState: () => state,
    subscribe: () => () => {},
    startPolling: vi.fn(),
    stopPolling: vi.fn(),
    poll: vi.fn(),
    setActor: vi.fn(),
    createTrip: vi.fn(),
    confirmQuote: vi.fn(),
    cancelTrip: vi.fn(),
    driverAccept: vi.fn(),
    driverReject: vi.fn(),
    driverEnRoute: vi.fn(),
    driverPickup: vi.fn(),
    driverDropoff: vi.fn(),
    refreshHistory: vi.fn(),
    clearError: vi.fn(),
    destroy: vi.fn(),
  };
}

const mockSession = createAuthSession({ userId: 'rider-1', role: 'rider' });
mockSession.setToken('test-token');

const mockApi = {} as TripApi;

const text = (html: string) => html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
const buttons = (html: string) => Array.from(html.matchAll(/<button\b[^>]*>([\s\S]*?)<\/button>/g), match => text(match[1]));

describe('rider booking and trip controls', () => {
  it('starts with two labeled places and requests a quote before confirmation', () => {
    const store = createMockStore(null);
    const html = renderToStaticMarkup(
      <RiderView
        pickup={testPlaces[0]}
        destination={testPlaces[1]}
        selection={null}
        onChange={() => {}}
        onSelectMap={() => {}}
        onSwap={() => {}}
        store={store}
        session={mockSession}
      />
    );
    expect(html).toMatch(/<input[^>]+aria-label="Điểm đón"/);
    expect(html).toMatch(/<input[^>]+aria-label="Điểm đến"/);
    expect(buttons(html)).toContain('Xem giá chuyến đi');
    expect(buttons(html).filter(label => label === 'Tìm')).toHaveLength(2);
    expect(buttons(html)).not.toContain('Xác nhận giá &amp; đặt xe');
    expect(text(html)).toContain('Ví SwiftPay');
    expect(text(html)).toContain('Thẻ thanh toán');
  });

  it('offers price confirmation and rejection on a quote', () => {
    const store = createMockStore(mockTrip('Quoted'));
    const html = renderToStaticMarkup(
      <RiderView
        pickup={testPlaces[0]}
        destination={testPlaces[1]}
        selection={null}
        onChange={() => {}}
        onSelectMap={() => {}}
        onSwap={() => {}}
        store={store}
        session={mockSession}
      />
    );
    const actions = buttons(html);
    expect(actions).toContain('Xác nhận giá &amp; đặt xe');
    expect(actions).toContain('Chọn lại hành trình');
  });

  it('allows cancellation while waiting for driver acceptance', () => {
    const store = createMockStore(mockTrip('DriverOffered'));
    const html = renderToStaticMarkup(
      <RiderView
        pickup={testPlaces[0]}
        destination={testPlaces[1]}
        selection={null}
        onChange={() => {}}
        onSelectMap={() => {}}
        onSwap={() => {}}
        store={store}
        session={mockSession}
      />
    );
    expect(buttons(html)).toContain('Hủy chuyến đi');
  });

  it.each<TripStatus>(['DriverAccepted', 'EnRoute', 'PickedUp', 'DroppedOff', 'PaymentPending', 'Paid'])(
    'does not offer cancellation once the trip is %s',
    status => {
      const store = createMockStore(mockTrip(status));
      const html = renderToStaticMarkup(
        <RiderView
          pickup={testPlaces[0]}
          destination={testPlaces[1]}
          selection={null}
          onChange={() => {}}
          onSelectMap={() => {}}
          onSwap={() => {}}
          store={store}
          session={mockSession}
        />
      );
      const actions = buttons(html);
      expect(actions).not.toContain('Hủy chuyến đi');
      expect(actions).not.toContain('Chọn lại hành trình');
    }
  );
});

describe('payment state is visible to both roles', () => {
  it('keeps PaymentPending distinct from a paid trip in rider view', () => {
    const store = createMockStore(mockTrip('PaymentPending'));
    const html = renderToStaticMarkup(
      <RiderView
        pickup={testPlaces[0]}
        destination={testPlaces[1]}
        selection={null}
        onChange={() => {}}
        onSelectMap={() => {}}
        onSwap={() => {}}
        store={store}
        session={mockSession}
      />
    );
    expect(text(html)).toContain('Đang xử lý thanh toán');
    expect(text(html)).not.toContain('Đã thanh toán');
    expect(text(html)).toContain('Hệ thống đang đối soát và xử lý thanh toán tự động');
  });

  it('keeps PaymentPending distinct from a paid trip in driver view', () => {
    const store = createMockStore(mockTrip('PaymentPending'));
    const html = renderToStaticMarkup(
      <DriverView store={store} api={mockApi} driverId="driver-1" />
    );
    expect(text(html)).toContain('Đang xử lý thanh toán');
    expect(text(html)).not.toContain('Đã thanh toán');
    expect(text(html)).toContain('Hệ thống đang tự động quyết toán');
  });
});

describe('driver actions and completed trip totals', () => {
  it('shows acceptance and rejection only while a driver offer is waiting', () => {
    const store = createMockStore(mockTrip('DriverOffered'));
    const actions = buttons(renderToStaticMarkup(
      <DriverView store={store} api={mockApi} driverId="driver-1" />
    ));
    expect(actions).toContain('Nhận chuyến');
    expect(actions).toContain('Từ chối');
    expect(actions).not.toContain('Bắt đầu đến đón');
    expect(actions).not.toContain('Đã đón khách');
    expect(actions).not.toContain('Đã trả khách');
  });

  it.each([
    ['DriverAccepted', 'Bắt đầu đến đón'],
    ['EnRoute', 'Đã đón khách'],
    ['PickedUp', 'Đã trả khách'],
  ] as const)('shows only the next lifecycle action for %s', (status, expected) => {
    const store = createMockStore(mockTrip(status));
    const actions = buttons(renderToStaticMarkup(
      <DriverView store={store} api={mockApi} driverId="driver-1" />
    ));
    const lifecycle = ['Bắt đầu đến đón', 'Đã đón khách', 'Đã trả khách', 'Nhận chuyến', 'Từ chối'];
    expect(actions.filter(action => lifecycle.includes(action))).toEqual([expected]);
  });

  it('counts and sums only paid trips in the driver statistics', () => {
    const history = [
      mockTrip('Paid', '50000'),
      { ...mockTrip('Paid', '75000'), id: 'paid-2' },
      mockTrip('Cancelled', '800000'),
      mockTrip('Failed', '900000'),
    ];
    const store = createMockStore(null, history);
    const content = text(renderToStaticMarkup(
      <DriverView store={store} api={mockApi} driverId="driver-1" />
    ));
    expect(content).toMatch(/Chuyến hoàn tất\s+2\s+chuyến/);
    expect(content).toContain('125.000 ₫');
  });
});
