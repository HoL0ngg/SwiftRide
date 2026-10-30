import { useEffect, useRef, useState } from 'react';
import {
  ArrowRight,
  Check,
  CheckCheck,
  Clock3,
  Compass,
  Flag,
  LoaderCircle,
  MapPin,
  Navigation,
  Power,
  Route,
  Wallet,
} from 'lucide-react';
import type { TripStore } from '../store/tripStore';
import type { TripApi } from '../services/tripApi';
import type { TripResponse, TripStatus } from '../types/tripTypes';
import { useTrip } from '../store/useTrip';
import { formatVnd, sumVnd } from '../utils/money';
import { createDriverHeartbeat, type DriverHeartbeat } from '../services/driverLocation';

type Props = {
  store: TripStore;
  api: TripApi;
  driverId?: string;
};

export default function DriverView({ store, api, driverId = 'demo-driver' }: Props) {
  const backendState = useTrip(store);

  const [online, setOnline] = useState(true);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState('');
  const heartbeatRef = useRef<DriverHeartbeat | null>(null);

  const rawTrip = backendState.trip;
  const rawHistory = backendState.history;
  const error = backendState.error || actionError;

  useEffect(() => {
    if (online) {
      const heartbeat = createDriverHeartbeat({
        driverId,
        getCoordinates: () => ({ latitude: 10.7725, longitude: 106.698 }),
        updateLocation: (id, lat, lng, isOnline) =>
          api.updateDriverLocation(id, lat, lng, isOnline),
        intervalMs: 30_000,
      });
      heartbeatRef.current = heartbeat;
      heartbeat.start();
    } else {
      heartbeatRef.current?.stop();
      heartbeatRef.current = null;
    }

    return () => {
      heartbeatRef.current?.stop();
      heartbeatRef.current = null;
    };
  }, [api, driverId, online]);

  async function run(action: () => Promise<unknown>) {
    if (busy) return;
    setBusy(true);
    setActionError('');
    try {
      await action();
    } catch (cause) {
      console.error('Lỗi thao tác tài xế (DriverView):', cause);
      setActionError(cause instanceof Error ? cause.message : 'Chưa thể thực hiện thao tác. Vui lòng thử lại.');
    } finally {
      setBusy(false);
    }
  }

  const completed = rawHistory.filter(ride => ride.status === 'Paid');
  const totalValueVnd = sumVnd(completed.map(r => r.fareAmount));
  const formattedTotal = formatVnd(totalValueVnd);

  const trip = rawTrip
    ? {
        id: rawTrip.id,
        status: rawTrip.status as TripStatus,
        fareText: formatVnd(rawTrip.fareAmount),
        distanceKm: rawTrip.distanceKm != null ? Number(rawTrip.distanceKm) : 0,
        durationMinutes:
          rawTrip.estimatedMinutes != null
            ? Math.round(Number(rawTrip.estimatedMinutes))
            : 0,
        pickup: {
          name: rawTrip.pickupName || 'Điểm đón',
          address: rawTrip.pickupAddress || '',
        },
        destination: {
          name: rawTrip.destinationName || 'Điểm đến',
          address: rawTrip.destinationAddress || '',
        },
        paymentMethod: rawTrip.paymentMethod,
        rejections: rawTrip.rejectionCount ?? 0,
      }
    : null;

  const hasAcceptedTrip =
    !!trip &&
    ['DriverAccepted', 'EnRoute', 'PickedUp', 'DroppedOff', 'PaymentPending'].includes(trip.status);
  const hasRide =
    !!trip &&
    ['DriverOffered', 'DriverAccepted', 'EnRoute', 'PickedUp', 'DroppedOff', 'PaymentPending', 'Paid'].includes(trip.status);

  const nextAction =
    trip?.status === 'DriverAccepted'
      ? {
          label: 'Bắt đầu đến đón',
          action: () => store.driverEnRoute(trip.id),
          icon: Navigation,
        }
      : trip?.status === 'EnRoute'
        ? {
            label: 'Đã đón khách',
            action: () => store.driverPickup(trip.id),
            icon: Check,
          }
        : trip?.status === 'PickedUp'
          ? {
              label: 'Đã trả khách',
              action: () => store.driverDropoff(trip.id),
              icon: Flag,
            }
          : null;

  const rideHeading =
    trip?.status === 'DriverOffered'
      ? 'Một chuyến đi mới.'
      : trip?.status === 'DriverAccepted'
        ? 'Sẵn sàng đón khách.'
        : trip?.status === 'EnRoute'
          ? 'Đang đến điểm đón.'
          : trip?.status === 'PickedUp'
            ? 'Chúc bạn lái xe an toàn.'
            : trip?.status === 'Paid'
              ? 'Hành trình trọn vẹn!'
              : 'Đã đến nơi an toàn.';

  return (
    <section className="flex min-h-full flex-col" aria-label="Không gian tài xế">
      <div className="mb-7 flex items-center justify-between gap-3">
        <span className="eyebrow flex items-center gap-2">
          <Compass size={15} aria-hidden="true" /> ĐỐI TÁC SWIFTRIDE
        </span>
        <span className="rounded-full bg-emerald-50 px-3 py-1.5 text-[11px] font-semibold text-emerald-800">
          API Gateway
        </span>
      </div>

      <div className="mb-6 flex items-start justify-between gap-4">
        <div>
          <h2 className="panel-title">
            {hasRide ? rideHeading : <>Cầm lái hôm nay.<br />Kết nối hành trình.</>}
          </h2>
          <p className="muted mt-3 text-sm leading-relaxed">
            {hasRide ? 'Mỗi chặng đường, một trải nghiệm tốt hơn.' : 'Chủ động thời gian. Sẵn sàng cho chuyến đi mới.'}
          </p>
        </div>
      </div>

      <div className="mb-6 flex items-center justify-between gap-4 rounded-2xl border border-slate-200 bg-white p-4">
        <div className="flex items-center gap-3">
          <span
            className={`flex h-10 w-10 items-center justify-center rounded-full ${
              online ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-100 text-slate-500'
            }`}
          >
            <Power size={19} aria-hidden="true" />
          </span>
          <div>
            <p className="text-sm font-semibold text-slate-900">
              {online ? 'Bạn đang trực tuyến' : 'Bạn đang ngoại tuyến'}
            </p>
            <p className="mt-1 text-xs text-slate-500">
              {hasAcceptedTrip
                ? 'Hoàn thành chuyến trước khi nghỉ'
                : online
                  ? 'Sẵn sàng nhận lời mời chuyến'
                  : 'Bật để bắt đầu nhận chuyến'}
            </p>
          </div>
        </div>
        <button
          type="button"
          role="switch"
          aria-checked={online}
          aria-label="Trạng thái nhận chuyến"
          disabled={busy || hasAcceptedTrip}
          onClick={() => setOnline(!online)}
          className={`relative h-7 w-12 shrink-0 rounded-full transition-colors disabled:cursor-not-allowed disabled:opacity-60 ${
            online ? 'bg-emerald-700' : 'bg-slate-300'
          }`}
        >
          <span
            className={`absolute top-1 h-5 w-5 rounded-full bg-white shadow-sm transition-transform ${
              online ? 'left-1 translate-x-5' : 'left-1 translate-x-0'
            }`}
          />
        </button>
      </div>

      <div className="mb-6 rounded-2xl border border-slate-200 bg-white p-4">
        <div className="flex items-center justify-between">
          <span className="text-xs font-semibold text-slate-600">Chuyến hoàn tất</span>
          <span className="rounded-full bg-slate-100 px-2.5 py-0.5 text-xs font-semibold text-slate-700">
            {completed.length} chuyến
          </span>
        </div>
        <div className="mt-2 flex items-baseline justify-between">
          <span className="text-xs text-slate-500">Tổng thu nhập</span>
          <strong className="text-xl font-bold text-emerald-800">{formattedTotal}</strong>
        </div>
      </div>

      {error && <p role="alert" className="error-message mb-4">{error}</p>}

      {hasRide && trip ? (
        <div
          aria-live="polite"
          className="rounded-[24px] border border-slate-200 bg-white p-5 shadow-[0_8px_30px_-20px_rgba(15,23,42,0.25)]"
        >
          <div className="mb-5 flex items-center justify-between gap-2">
            <span
              className={`status-pill ${
                trip.status === 'Paid' ? 'bg-emerald-100 text-emerald-800' : 'bg-emerald-50 text-emerald-700'
              }`}
            >
              {trip.status === 'DriverOffered' ? (
                <>
                  <span className="h-1.5 w-1.5 rounded-full bg-emerald-600" /> Lời mời chuyến mới
                </>
              ) : trip.status === 'Paid' ? (
                <>
                  <CheckCheck size={14} /> Đã thanh toán
                </>
              ) : trip.status === 'PaymentPending' || trip.status === 'DroppedOff' ? (
                <>
                  <Clock3 size={14} /> Đang xử lý thanh toán
                </>
              ) : (
                <>
                  <Navigation size={14} /> Chuyến đang thực hiện
                </>
              )}
            </span>
            <span className="text-[11px] font-medium text-slate-400">
              #{trip.id.slice(-6).toUpperCase()}
            </span>
          </div>

          <div className="mb-6 flex items-end justify-between gap-2">
            <div>
              <p className="mb-1 text-xs text-slate-500">Giá chuyến đã xác nhận</p>
              <p className="text-[32px] font-bold leading-tight tracking-tight text-slate-900">
                {trip.fareText}
              </p>
            </div>
            <span className="mb-1 flex items-center gap-1.5 rounded-lg bg-slate-50 px-2 py-1.5 text-xs text-slate-600">
              <Wallet size={14} aria-hidden="true" />{' '}
              {trip.paymentMethod === 'wallet' ? 'Ví điện tử' : 'Thẻ'}
            </span>
          </div>

          <div className="relative mb-5 space-y-6">
            <span className="absolute bottom-7 left-[9px] top-6 border-l border-dashed border-slate-300" aria-hidden="true" />
            <div className="relative flex gap-3">
              <span className="mt-1 flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-emerald-50">
                <span className="h-2 w-2 rounded-full bg-emerald-600" />
              </span>
              <div>
                <p className="mb-1 text-[10px] font-semibold uppercase tracking-[.12em] text-slate-400">Điểm đón</p>
                <p className="text-sm font-semibold text-slate-900">{trip.pickup.name}</p>
                <p className="mt-1 text-xs leading-relaxed text-slate-500">{trip.pickup.address}</p>
              </div>
            </div>
            <div className="relative flex gap-3">
              <span className="mt-1 flex h-5 w-5 shrink-0 items-center justify-center rounded-md bg-orange-50 text-orange-600">
                <MapPin size={14} aria-hidden="true" />
              </span>
              <div>
                <p className="mb-1 text-[10px] font-semibold uppercase tracking-[.12em] text-slate-400">Điểm đến</p>
                <p className="text-sm font-semibold text-slate-900">{trip.destination.name}</p>
                <p className="mt-1 text-xs leading-relaxed text-slate-500">{trip.destination.address}</p>
              </div>
            </div>
          </div>

          <div className="mb-5 flex items-center gap-5 border-y border-slate-100 py-3 text-xs text-slate-600">
            <span className="flex items-center gap-2">
              <Route size={15} aria-hidden="true" /> {trip.distanceKm.toLocaleString('vi-VN', { maximumFractionDigits: 1 })} km
            </span>
            <span className="flex items-center gap-2">
              <Clock3 size={15} aria-hidden="true" /> Khoảng {trip.durationMinutes} phút
            </span>
          </div>

          {trip.status === 'DriverOffered' && (
            <>
              {trip.rejections > 0 && (
                <p className="mb-4 text-xs leading-relaxed text-slate-500">
                  Chuyến đang được đề xuất lại · Đã từ chối {trip.rejections} lần.
                </p>
              )}
              <div className="flex gap-3">
                <button
                  className="secondary-button flex-1"
                  disabled={busy}
                  onClick={() => void run(() => store.driverReject(trip.id))}
                >
                  Từ chối
                </button>
                <button
                  className="primary-button flex-[1.7]"
                  disabled={busy || !online}
                  onClick={() => void run(() => store.driverAccept(trip.id))}
                >
                  {busy ? (
                    <LoaderCircle size={17} className="animate-spin" aria-hidden="true" />
                  ) : (
                    <Check size={18} aria-hidden="true" />
                  )}{' '}
                  Nhận chuyến
                </button>
              </div>
              {!online && <p className="mt-3 text-center text-xs text-slate-500">Bật trực tuyến để nhận chuyến này.</p>}
            </>
          )}

          {nextAction && (
            <>
              <p className="mb-4 text-xs leading-relaxed text-slate-500">
                {trip.status === 'DriverAccepted'
                  ? 'Bắt đầu di chuyển khi bạn đã sẵn sàng.'
                  : trip.status === 'EnRoute'
                    ? 'Chỉ xác nhận khi hành khách đã lên xe.'
                    : 'Chỉ xác nhận khi đã đưa hành khách đến điểm đến.'}
              </p>
              <button className="primary-button w-full" disabled={busy} onClick={() => void run(nextAction.action)}>
                {busy ? (
                  <LoaderCircle size={17} className="animate-spin" aria-hidden="true" />
                ) : (
                  <nextAction.icon size={18} aria-hidden="true" />
                )}{' '}
                {nextAction.label} <ArrowRight size={17} className="ml-auto" aria-hidden="true" />
              </button>
            </>
          )}

          {(trip.status === 'PaymentPending' || trip.status === 'DroppedOff') && (
            <div className="rounded-xl bg-amber-50 p-4">
              <p className="text-sm font-semibold text-amber-900">Đã trả khách, đang chờ thanh toán</p>
              <p className="mt-2 text-xs leading-relaxed text-amber-800">
                Hệ thống đang tự động quyết toán và hoàn tất chuyến đi.
              </p>
            </div>
          )}
        </div>
      ) : (
        <div className="rounded-[24px] border border-dashed border-slate-200 bg-white/70 p-8 text-center">
          <p className="text-sm font-semibold text-slate-800">Chưa có chuyến đang diễn ra</p>
          <p className="mt-1 text-xs text-slate-500">
            {online
              ? 'Hệ thống sẽ tự động cập nhật khi có chuyến mới phù hợp với bạn.'
              : 'Hãy bật trực tuyến để nhận chuyến mới.'}
          </p>
        </div>
      )}
    </section>
  );
}
