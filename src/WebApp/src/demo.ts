import { useSyncExternalStore } from 'react';

export type Place = { id: string; name: string; address: string; lat: number; lng: number };
export const places: Place[] = [
  { id: 'ben-thanh', name: 'Chợ Bến Thành', address: 'Lê Lợi, Quận 1, TP. Hồ Chí Minh', lat: 10.7725, lng: 106.698 },
  { id: 'landmark', name: 'Landmark 81', address: '720A Điện Biên Phủ, Bình Thạnh', lat: 10.7951, lng: 106.7219 },
  { id: 'notre-dame', name: 'Nhà thờ Đức Bà', address: '01 Công xã Paris, Quận 1', lat: 10.7798, lng: 106.699 },
  { id: 'airport', name: 'Sân bay Tân Sơn Nhất', address: 'Trường Sơn, Tân Bình', lat: 10.8133, lng: 106.6627 },
  { id: 'saigon-uni', name: 'Đại học Sài Gòn', address: '273 An Dương Vương, Quận 5', lat: 10.7597, lng: 106.6822 },
  { id: 'nguyen-hue', name: 'Phố đi bộ Nguyễn Huệ', address: 'Nguyễn Huệ, Quận 1', lat: 10.7743, lng: 106.7039 },
];

const statuses = ['Quoted', 'DriverOffered', 'DriverAccepted', 'EnRoute', 'PickedUp', 'DroppedOff', 'PaymentPending', 'Paid', 'Cancelled', 'Failed'] as const;
export type TripStatus = typeof statuses[number];
export type Trip = {
  id: string;
  pickup: Place;
  destination: Place;
  fare: number;
  distanceKm: number;
  durationMinutes: number;
  status: TripStatus;
  rejections: number;
  createdAt: string;
  paymentMethod: 'wallet' | 'card';
};
export type DemoState = { trip: Trip | null; history: Trip[]; online: boolean };

const storageKey = 'swiftride-demo-v1';
const emptyState = (): DemoState => ({ trip: null, history: [], online: true });
const terminal = (status: TripStatus) => status === 'Paid' || status === 'Cancelled' || status === 'Failed';

function isPlace(value: unknown): value is Place {
  if (!value || typeof value !== 'object') return false;
  const place = value as Partial<Place>;
  return places.some(candidate => candidate.id === place.id && candidate.name === place.name &&
    candidate.address === place.address && candidate.lat === place.lat && candidate.lng === place.lng);
}

function isTrip(value: unknown): value is Trip {
  if (!value || typeof value !== 'object') return false;
  const trip = value as Partial<Trip>;
  return typeof trip.id === 'string' && trip.id.length > 0 && isPlace(trip.pickup) && isPlace(trip.destination) &&
    trip.pickup.id !== trip.destination.id && typeof trip.fare === 'number' && Number.isSafeInteger(trip.fare) && trip.fare >= 0 &&
    typeof trip.distanceKm === 'number' && Number.isFinite(trip.distanceKm) && trip.distanceKm > 0 &&
    typeof trip.durationMinutes === 'number' && Number.isSafeInteger(trip.durationMinutes) && trip.durationMinutes > 0 &&
    statuses.includes(trip.status as TripStatus) && Number.isInteger(trip.rejections) && trip.rejections! >= 0 && trip.rejections! <= 4 &&
    typeof trip.createdAt === 'string' && Number.isFinite(Date.parse(trip.createdAt)) &&
    (trip.paymentMethod === 'wallet' || trip.paymentMethod === 'card');
}

function readState(): DemoState {
  try {
    const value = JSON.parse(localStorage.getItem(storageKey) ?? 'null');
    if (value?.version === 1 && value.state && typeof value.state.online === 'boolean' &&
      (value.state.trip === null || isTrip(value.state.trip)) && Array.isArray(value.state.history) &&
      value.state.history.every((trip: unknown) => isTrip(trip) && terminal(trip.status)) &&
      new Set(value.state.history.map((trip: Trip) => trip.id)).size === value.state.history.length) {
      return value.state as DemoState;
    }
  } catch { /* Storage can be unavailable or contain an old, invalid demo. */ }
  return emptyState();
}

let state = readState();
const listeners = new Set<() => void>();
const emit = () => listeners.forEach(listener => listener());

function save(next: DemoState) {
  try {
    localStorage.setItem(storageKey, JSON.stringify({ version: 1, state: next }));
  } catch {
    throw new Error('Không thể lưu bản demo. Hãy cho phép bộ nhớ trình duyệt hoặc giải phóng dung lượng rồi thử lại.');
  }
  state = next;
  emit();
}

function onStorage(event: StorageEvent) {
  if (event.key === storageKey || event.key === null) {
    state = readState();
    emit();
  }
}

function subscribe(listener: () => void) {
  if (!listeners.size) {
    window.addEventListener('storage', onStorage);
    state = readState();
  }
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
    if (!listeners.size) window.removeEventListener('storage', onStorage);
  };
}

export function useDemo(): DemoState {
  return useSyncExternalStore(subscribe, () => state);
}

async function latency() {
  await new Promise<void>(resolve => setTimeout(resolve, 250));
  state = readState();
}

function requireTrip(allowed: TripStatus[]): Trip {
  const trip = state.trip;
  if (!trip || !allowed.includes(trip.status)) {
    throw new Error('Thao tác chưa phù hợp với trạng thái chuyến. Vui lòng kiểm tra lại.');
  }
  return trip;
}

function updateTrip(trip: Trip) {
  const history = terminal(trip.status)
    ? [trip, ...state.history.filter(item => item.id !== trip.id)]
    : state.history;
  save({ ...state, trip, history });
}

async function transition(from: TripStatus[], to: TripStatus) {
  await latency();
  const trip = requireTrip(from);
  updateTrip({ ...trip, status: to });
}

export const demoApi = {
  async quote(pickupId: string, destinationId: string, paymentMethod: 'wallet' | 'card'): Promise<void> {
    await latency();
    if (state.trip && !terminal(state.trip.status)) throw new Error('Hãy hoàn tất hoặc hủy chuyến hiện tại trước khi đặt chuyến mới.');
    if (!state.online) throw new Error('Hiện chưa có tài xế sẵn sàng. Vui lòng thử lại khi tài xế bật nhận chuyến.');
    const pickup = places.find(place => place.id === pickupId);
    const destination = places.find(place => place.id === destinationId);
    if (!pickup || !destination) throw new Error('Vui lòng chọn điểm đón và điểm đến trong danh sách.');
    if (pickup.id === destination.id) throw new Error('Điểm đón và điểm đến cần khác nhau.');
    if (paymentMethod !== 'wallet' && paymentMethod !== 'card') throw new Error('Phương thức thanh toán không hợp lệ.');
    const radians = (degrees: number) => degrees * Math.PI / 180;
    const latitude = radians(destination.lat - pickup.lat);
    const longitude = radians(destination.lng - pickup.lng);
    const haversine = Math.sin(latitude / 2) ** 2 + Math.cos(radians(pickup.lat)) * Math.cos(radians(destination.lat)) * Math.sin(longitude / 2) ** 2;
    // shortcut: straight-line distance × 1.3 approximates roads, replace with backend quotes when connecting the Gateway.
    const distanceKm = Math.round(6371 * 2 * Math.asin(Math.sqrt(haversine)) * 1.3 * 10) / 10;
    const durationMinutes = Math.max(3, Math.round(distanceKm * 4));
    const fare = Math.round((12000 + distanceKm * 7500 + durationMinutes * 300) / 1000) * 1000;
    updateTrip({ id: crypto.randomUUID(), pickup, destination, fare, distanceKm, durationMinutes,
      status: 'Quoted', rejections: 0, createdAt: new Date().toISOString(), paymentMethod });
  },
  confirmQuote: () => transition(['Quoted'], 'DriverOffered'),
  cancel: () => transition(['Quoted', 'DriverOffered'], 'Cancelled'),
  async driverAccept(): Promise<void> {
    await latency();
    if (!state.online) throw new Error('Bạn cần bật nhận chuyến trước khi nhận lời mời.');
    const trip = requireTrip(['DriverOffered']);
    updateTrip({ ...trip, status: 'DriverAccepted' });
  },
  async driverReject(): Promise<void> {
    await latency();
    const trip = requireTrip(['DriverOffered']);
    const rejections = trip.rejections + 1;
    updateTrip({ ...trip, rejections, status: rejections > 3 ? 'Failed' : 'DriverOffered' });
  },
  enRoute: () => transition(['DriverAccepted'], 'EnRoute'),
  pickup: () => transition(['EnRoute'], 'PickedUp'),
  async dropoff(): Promise<void> {
    await transition(['PickedUp'], 'DroppedOff');
    updateTrip({ ...requireTrip(['DroppedOff']), status: 'PaymentPending' });
  },
  async settlePayment(): Promise<void> {
    await latency();
    if (state.trip?.status === 'DroppedOff') updateTrip({ ...state.trip, status: 'PaymentPending' });
    const trip = requireTrip(['PaymentPending', 'Paid']);
    if (trip.status === 'Paid') return;
    updateTrip({ ...trip, status: 'Paid' });
  },
  async setOnline(online: boolean): Promise<void> {
    await latency();
    if (typeof online !== 'boolean') throw new Error('Trạng thái nhận chuyến không hợp lệ.');
    save({ ...state, online });
  },
  async reset(): Promise<void> {
    await latency();
    if (state.trip && !terminal(state.trip.status)) throw new Error('Hãy hoàn tất hoặc hủy chuyến trước khi đặt chuyến mới.');
    save({ ...state, trip: null });
  },
};

export function formatMoney(value: number): string {
  return new Intl.NumberFormat('vi-VN', { style: 'currency', currency: 'VND', maximumFractionDigits: 0 }).format(value);
}
