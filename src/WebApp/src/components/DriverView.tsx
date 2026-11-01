import { useState } from 'react';
import {
  ArrowRight,
  Check,
  CheckCheck,
  Clock3,
  Flag,
  LoaderCircle,
  MapPinned,
  Navigation,
  Route,
  Wallet,
} from 'lucide-react';
import type { TripStore } from '../store/tripStore';
import type { TripStatus } from '../types/tripTypes';
import { useTripState } from '../store/useTrip';
import { formatVnd } from '../utils/money';
import { summarizeEarnings } from '../utils/driverTrips';
import RoutePoints from './ui/RoutePoints';
import StatusPill from './ui/StatusPill';

type Props = {
  store: TripStore;
  /** Driver availability. The switch itself lives in the app's top bar. */
  online: boolean;
};

export default function DriverView({ store, online }: Props) {
  const backendState = useTripState(store);

  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState('');

  const rawTrip = backendState.trip;
  const error = backendState.error || actionError;

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

  const earnings = summarizeEarnings(backendState.history);

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
      ? 'Có chuyến mới!'
      : trip?.status === 'DriverAccepted'
        ? 'Sẵn sàng đón khách'
        : trip?.status === 'EnRoute'
          ? 'Đang đến điểm đón'
          : trip?.status === 'PickedUp'
            ? 'Chúc bạn lái xe an toàn'
            : trip?.status === 'Paid'
              ? 'Hành trình trọn vẹn'
              : 'Đã đến nơi an toàn';

  return (
    <section aria-label="Không gian tài xế">
      {hasRide && trip ? (
        <div aria-live="polite">
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8 }}>
            {trip.status === 'DriverOffered' ? (
              <StatusPill tone="info">Lời mời chuyến mới</StatusPill>
            ) : trip.status === 'Paid' ? (
              <StatusPill tone="success"><CheckCheck size={14} aria-hidden="true" /> Đã thanh toán</StatusPill>
            ) : trip.status === 'PaymentPending' || trip.status === 'DroppedOff' ? (
              <StatusPill tone="warning"><Clock3 size={14} aria-hidden="true" /> Đang xử lý thanh toán</StatusPill>
            ) : (
              <StatusPill tone="info"><Navigation size={14} aria-hidden="true" /> Chuyến đang thực hiện</StatusPill>
            )}
            <span className="caption">#{trip.id.slice(-6).toUpperCase()}</span>
          </div>

          <h2 className="panel-title" style={{ marginTop: 12 }}>{rideHeading}</h2>

          <div style={{ display: 'flex', alignItems: 'flex-end', justifyContent: 'space-between', gap: 8, margin: '16px 0 20px' }}>
            <div>
              <p className="caption">Giá chuyến đã xác nhận</p>
              <p className="fare-amount">{trip.fareText}</p>
            </div>
            <StatusPill tone="neutral">
              <Wallet size={14} aria-hidden="true" /> {trip.paymentMethod === 'wallet' ? 'Ví điện tử' : 'Thẻ'}
            </StatusPill>
          </div>

          <div className="card card--soft">
            <RoutePoints pickup={trip.pickup} destination={trip.destination} />
            <div className="facts">
              <span><Route size={16} aria-hidden="true" /> {trip.distanceKm.toLocaleString('vi-VN', { maximumFractionDigits: 1 })} km</span>
              <span><Clock3 size={16} aria-hidden="true" /> Khoảng {trip.durationMinutes} phút</span>
            </div>
          </div>

          {error && <p role="alert" className="alert alert-error" style={{ marginTop: 16 }}>{error}</p>}

          {trip.status === 'DriverOffered' && (
            <div style={{ marginTop: 20 }}>
              {trip.rejections > 0 && (
                <p className="caption" style={{ marginBottom: 12 }}>
                  Chuyến đang được đề xuất lại · Đã từ chối {trip.rejections} lần.
                </p>
              )}
              <div style={{ display: 'flex', gap: 12 }}>
                <button
                  className="btn btn-secondary"
                  style={{ flex: 1 }}
                  disabled={busy}
                  onClick={() => void run(() => store.driverReject(trip.id))}
                >
                  Từ chối
                </button>
                <button
                  className="btn btn-primary"
                  style={{ flex: 1.8 }}
                  disabled={busy || !online}
                  onClick={() => void run(() => store.driverAccept(trip.id))}
                >
                  {busy ? (
                    <LoaderCircle size={18} className="animate-spin" aria-hidden="true" />
                  ) : (
                    <Check size={18} aria-hidden="true" />
                  )}{' '}
                  Nhận chuyến
                </button>
              </div>
              {!online && <p className="caption" style={{ marginTop: 12, textAlign: 'center' }}>Bật trực tuyến để nhận chuyến này.</p>}
            </div>
          )}

          {nextAction && (
            <div style={{ marginTop: 20 }}>
              <p className="caption" style={{ marginBottom: 12 }}>
                {trip.status === 'DriverAccepted'
                  ? 'Bắt đầu di chuyển khi bạn đã sẵn sàng.'
                  : trip.status === 'EnRoute'
                    ? 'Chỉ xác nhận khi hành khách đã lên xe.'
                    : 'Chỉ xác nhận khi đã đưa hành khách đến điểm đến.'}
              </p>
              <button className="btn btn-primary btn-block" disabled={busy} onClick={() => void run(nextAction.action)}>
                {busy ? (
                  <LoaderCircle size={18} className="animate-spin" aria-hidden="true" />
                ) : (
                  <nextAction.icon size={18} aria-hidden="true" />
                )}{' '}
                {nextAction.label} <ArrowRight size={18} aria-hidden="true" />
              </button>
            </div>
          )}

          {(trip.status === 'PaymentPending' || trip.status === 'DroppedOff') && (
            <div className="alert alert-warning" style={{ marginTop: 20 }}>
              <p style={{ fontWeight: 700 }}>Đã trả khách, đang chờ thanh toán</p>
              <p style={{ marginTop: 4 }}>Hệ thống đang tự động quyết toán và hoàn tất chuyến đi.</p>
            </div>
          )}
        </div>
      ) : (
        <div>
          <span className="eyebrow">Tài xế</span>
          <h2 className="panel-title" style={{ marginTop: 6 }}>
            {online ? 'Đang chờ chuyến mới' : 'Bạn đang ngoại tuyến'}
          </h2>
          <p className="muted" style={{ marginTop: 6 }}>
            {online
              ? 'Hệ thống sẽ tự cập nhật khi có chuyến phù hợp với bạn.'
              : 'Bật trực tuyến ở thanh trên cùng để bắt đầu nhận chuyến.'}
          </p>

          {online && (
            <p className="alert" style={{ display: 'flex', gap: 10, alignItems: 'flex-start', marginTop: 16, background: 'var(--color-brand-50)', color: 'var(--color-ink-700)' }}>
              <MapPinned size={18} aria-hidden="true" style={{ flexShrink: 0, marginTop: 2, color: 'var(--color-brand-500)' }} />
              Bấm vào bản đồ để cập nhật vị trí của bạn. Khách chỉ được ghép với tài xế ở gần.
            </p>
          )}

          {error && <p role="alert" className="alert alert-error" style={{ marginTop: 16 }}>{error}</p>}

          <div className="stat-row" style={{ marginTop: 20 }}>
            <div className="stat">
              <p className="caption">Chuyến hoàn tất</p>
              <p className="stat-value">{earnings.completedCount} chuyến</p>
            </div>
            <div className="stat">
              <p className="caption">Tổng thu nhập</p>
              <p className="stat-value" style={{ color: 'var(--color-brand-600)' }}>{formatVnd(earnings.totalVnd)}</p>
            </div>
          </div>
        </div>
      )}
    </section>
  );
}
