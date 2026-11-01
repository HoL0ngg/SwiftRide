import { useRef, useState } from 'react';
import {
  ArrowRight,
  ArrowUpDown,
  CarFront,
  Check,
  CheckCircle2,
  Clock3,
  CreditCard,
  LoaderCircle,
  Navigation,
  ShieldCheck,
  Sparkles,
  User,
  Wallet,
} from 'lucide-react';
import type { Place } from '../types/place';
import type { TripStore } from '../store/tripStore';
import type { AuthSession } from '../services/auth';
import type { TripStatus } from '../types/tripTypes';
import { useTripState } from '../store/useTrip';
import { formatVnd } from '../utils/money';
import AddressSearch from './AddressSearch';
import RoutePoints from './ui/RoutePoints';
import StatusPill, { type PillTone } from './ui/StatusPill';

const statusCopy: Record<string, { title: string; description: string }> = {
  Requested: {
    title: 'Đang tìm chuyến đi',
    description: 'Hệ thống đang kiểm tra báo giá và tìm tài xế phù hợp cho bạn.',
  },
  Quoted: {
    title: 'Chuyến đi đã sẵn sàng',
    description: 'Xem giá và xác nhận để gửi lời mời đến tài xế.',
  },
  DriverOffered: {
    title: 'Đang kết nối tài xế',
    description: 'Lời mời đã được gửi. Đang đợi tài xế nhận chuyến.',
  },
  DriverAccepted: {
    title: 'Đã có tài xế đồng hành',
    description: 'Tài xế đã nhận chuyến và đang chuẩn bị đến đón bạn.',
  },
  EnRoute: {
    title: 'Tài xế đang đến đón',
    description: 'Hãy sẵn sàng tại điểm đón.',
  },
  PickedUp: {
    title: 'Chúc bạn một chuyến đi vui',
    description: 'Bạn đang trên đường đến điểm đến. Cứ thư giãn nhé.',
  },
  DroppedOff: {
    title: 'Bạn đã đến nơi',
    description: 'Chuyến đi đã kết thúc. Đang tự động xử lý thanh toán.',
  },
  PaymentPending: {
    title: 'Đang xử lý thanh toán',
    description: 'Chuyến đi đang được đối soát và xử lý thanh toán qua cổng thanh toán.',
  },
  Paid: {
    title: 'Cảm ơn bạn đã chọn SwiftRide',
    description: 'Thanh toán thành công. Hẹn gặp bạn ở chuyến tiếp theo!',
  },
  Cancelled: {
    title: 'Chuyến đi đã được hủy',
    description: 'Khi cần di chuyển, SwiftRide luôn sẵn sàng cùng bạn.',
  },
  Failed: {
    title: 'Chưa tìm được tài xế phù hợp',
    description: 'Hiện chưa thể kết nối chuyến đi này. Bạn có thể thử lại sau.',
  },
};

const pillByStatus: Record<string, { label: string; tone: PillTone }> = {
  Requested: { label: 'Đang tìm xe', tone: 'info' },
  Quoted: { label: 'Chờ xác nhận giá', tone: 'info' },
  DriverOffered: { label: 'Đang kết nối', tone: 'info' },
  DriverAccepted: { label: 'Đã có tài xế', tone: 'info' },
  EnRoute: { label: 'Tài xế đang đến', tone: 'info' },
  PickedUp: { label: 'Đang di chuyển', tone: 'info' },
  DroppedOff: { label: 'Đã đến nơi', tone: 'warning' },
  PaymentPending: { label: 'Đang xử lý', tone: 'warning' },
  Paid: { label: 'Hoàn tất', tone: 'success' },
  Cancelled: { label: 'Đã hủy', tone: 'danger' },
  Failed: { label: 'Không thành công', tone: 'danger' },
};

const journeySteps = [
  { label: 'Kết nối', states: ['Requested', 'DriverOffered'] },
  { label: 'Đến đón', states: ['DriverAccepted', 'EnRoute'] },
  { label: 'Di chuyển', states: ['PickedUp'] },
  { label: 'Hoàn tất', states: ['DroppedOff', 'PaymentPending', 'Paid'] },
];

type Props = {
  pickup: Place;
  destination: Place;
  selection: 'pickup' | 'destination' | null;
  onChange: (kind: 'pickup' | 'destination', place: Place) => void;
  onSelectMap: (kind: 'pickup' | 'destination') => void;
  onSwap: () => void;
  store: TripStore;
  session: AuthSession;
};

export default function RiderView({
  pickup,
  destination,
  selection,
  onChange,
  onSelectMap,
  onSwap,
  store,
  session,
}: Props) {
  const backendState = useTripState(store);
  const [paymentMethod, setPaymentMethod] = useState<'wallet' | 'card'>('wallet');
  const [actionLoading, setActionLoading] = useState(false);
  const [actionError, setActionError] = useState('');
  const submitting = useRef(false);

  const rawTrip = backendState.trip;
  const error = backendState.error || actionError;
  const loading = backendState.loading || actionLoading;

  async function run(action: () => Promise<unknown>) {
    if (submitting.current) return;
    submitting.current = true;
    setActionLoading(true);
    setActionError('');
    try {
      await action();
    } catch (reason) {
      console.error('Lỗi thao tác chuyến đi (RiderView):', reason);
      setActionError(reason instanceof Error ? reason.message : 'Chưa thể thực hiện thao tác. Vui lòng thử lại.');
    } finally {
      submitting.current = false;
      setActionLoading(false);
    }
  }

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
          name: rawTrip.pickupName || pickup.name,
          address: rawTrip.pickupAddress || pickup.address,
        },
        destination: {
          name: rawTrip.destinationName || destination.name,
          address: rawTrip.destinationAddress || destination.address,
        },
        paymentMethod: rawTrip.paymentMethod,
        quoteId: rawTrip.quoteId,
      }
    : null;

  const terminal = trip && ['Paid', 'Cancelled', 'Failed'].includes(trip.status);
  const quoted = trip?.status === 'Quoted';
  const copy = trip ? statusCopy[trip.status] : undefined;
  const pill = trip ? pillByStatus[trip.status] : undefined;
  const currentStep = trip ? journeySteps.findIndex(step => step.states.includes(trip.status)) : -1;
  const canCancel = trip && ['Quoted', 'DriverOffered'].includes(trip.status);
  const sameSpot = pickup.lat === destination.lat && pickup.lng === destination.lng;

  return (
    <section aria-label="Đặt chuyến cho hành khách">
      {!trip ? (
        <>
          <span className="eyebrow">Hành khách</span>
          <h2 className="panel-title" style={{ marginTop: 6 }}>Hôm nay, mình đi đâu?</h2>
          <p className="muted" style={{ marginTop: 6 }}>
            Chọn điểm đón và điểm đến. Giá được báo rõ ràng trước khi bạn đặt xe.
          </p>

          <form
            style={{ marginTop: 20 }}
            onSubmit={event => {
              event.preventDefault();
              void run(() =>
                store.createTrip({
                  pickup: { latitude: pickup.lat, longitude: pickup.lng },
                  pickupName: pickup.name,
                  pickupAddress: pickup.address,
                  destination: { latitude: destination.lat, longitude: destination.lng },
                  destinationName: destination.name,
                  destinationAddress: destination.address,
                  paymentMethod,
                  mode: 'standard',
                })
              );
            }}
          >
            <div style={{ display: 'grid', gap: 10 }}>
              <AddressSearch
                label="Điểm đón"
                place={pickup}
                token={session}
                disabled={loading}
                selecting={selection === 'pickup'}
                onChange={place => onChange('pickup', place)}
                onSelectMap={() => onSelectMap('pickup')}
              />
              <button
                type="button"
                className="chip-btn"
                style={{ justifySelf: 'center' }}
                aria-label="Đổi điểm đón và điểm đến"
                disabled={loading}
                onClick={onSwap}
              >
                <ArrowUpDown size={14} aria-hidden="true" />
                Đổi hai điểm
              </button>
              <AddressSearch
                label="Điểm đến"
                place={destination}
                token={session}
                disabled={loading}
                selecting={selection === 'destination'}
                onChange={place => onChange('destination', place)}
                onSelectMap={() => onSelectMap('destination')}
              />
            </div>

            <hr className="divider" />
            <h3 style={{ fontSize: 14, fontWeight: 700, marginBottom: 10 }}>Thanh toán</h3>
            <div className="option-grid" role="group" aria-label="Phương thức thanh toán">
              {([
                { value: 'wallet', label: 'Ví SwiftPay', Icon: Wallet },
                { value: 'card', label: 'Thẻ thanh toán', Icon: CreditCard },
              ] as const).map(({ value, label, Icon }) => (
                <button
                  key={value}
                  type="button"
                  className="option"
                  disabled={loading}
                  aria-pressed={paymentMethod === value}
                  onClick={() => setPaymentMethod(value)}
                >
                  <Icon size={18} aria-hidden="true" />
                  <span style={{ flex: 1 }}>{label}</span>
                  {paymentMethod === value && <Check size={16} aria-hidden="true" />}
                </button>
              ))}
            </div>

            {error && <p role="alert" className="alert alert-error" style={{ marginTop: 16 }}>{error}</p>}
            <button type="submit" disabled={loading || sameSpot} className="btn btn-primary btn-block" style={{ marginTop: 20 }}>
              {loading ? (
                <LoaderCircle size={18} className="animate-spin" aria-hidden="true" />
              ) : (
                <Navigation size={18} aria-hidden="true" />
              )}
              <span>{loading ? 'Đang tìm chuyến phù hợp…' : 'Xem giá chuyến đi'}</span>
              {!loading && <ArrowRight size={18} aria-hidden="true" />}
            </button>
            {sameSpot && (
              <p className="caption" style={{ marginTop: 8, color: 'var(--color-warning)' }}>
                Vui lòng chọn điểm đến khác điểm đón.
              </p>
            )}
          </form>
        </>
      ) : (
        <>
          {pill && (
            <StatusPill tone={pill.tone}>
              {trip.status === 'Paid' ? (
                <CheckCircle2 size={14} aria-hidden="true" />
              ) : trip.status === 'DroppedOff' || trip.status === 'PaymentPending' ? (
                <Clock3 size={14} aria-hidden="true" />
              ) : (
                <Sparkles size={14} aria-hidden="true" />
              )}
              {pill.label}
            </StatusPill>
          )}
          <h2 className="panel-title" style={{ marginTop: 10 }}>{copy?.title}</h2>
          <p className="muted" style={{ marginTop: 4 }}>{copy?.description}</p>

          {!quoted && !terminal && (
            <ol aria-label="Tiến trình chuyến đi" className="stepper">
              {journeySteps.map((step, index) => (
                <li key={step.label} aria-current={index === currentStep ? 'step' : undefined} className="step">
                  {index < journeySteps.length - 1 && (
                    <span className={`step-line${index < currentStep ? ' step-line--done' : ''}`} />
                  )}
                  <span className={`step-dot${index <= currentStep ? ' step-dot--active' : ''}`}>
                    {index < currentStep ? <Check size={14} aria-hidden="true" /> : index + 1}
                  </span>
                  <span className={`step-label${index === currentStep ? ' step-label--active' : ''}`}>{step.label}</span>
                </li>
              ))}
            </ol>
          )}

          {!quoted && !terminal && trip.status !== 'DriverOffered' && trip.status !== 'Requested' && (
            <div className="card" style={{ display: 'flex', alignItems: 'center', gap: 14, marginBottom: 16 }}>
              <span className="avatar" style={{ width: 48, height: 48 }}>
                <User size={22} aria-hidden="true" />
              </span>
              <div style={{ minWidth: 0 }}>
                <p style={{ fontWeight: 600 }}>Tài xế SwiftRide</p>
                <p className="caption">Đối tác vận chuyển</p>
              </div>
            </div>
          )}

          <div className="card card--soft" style={{ marginTop: 16 }}>
            <RoutePoints pickup={trip.pickup} destination={trip.destination} />
            <div className="facts">
              <span><Clock3 size={16} aria-hidden="true" />Khoảng {trip.durationMinutes} phút</span>
              <span>{trip.distanceKm.toFixed(1)} km</span>
            </div>
          </div>

          <div className="service-card" style={{ marginTop: 16 }}>
            <span className="service-icon">
              <CarFront size={30} strokeWidth={1.5} aria-hidden="true" />
            </span>
            <div style={{ flex: 1 }}>
              <p style={{ fontWeight: 700 }}>SwiftCar</p>
              <p className="caption">4 chỗ · Thoải mái mỗi ngày</p>
            </div>
            <div style={{ textAlign: 'right' }}>
              <p style={{ fontSize: 20, fontWeight: 700, letterSpacing: '-0.3px' }}>{trip.fareText}</p>
              <p className="caption">{quoted ? 'Giá trước khi xác nhận' : 'Giá đã xác nhận'}</p>
            </div>
          </div>
          <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: 12, padding: '0 4px' }} className="caption">
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
              {trip.paymentMethod === 'wallet' ? <Wallet size={15} aria-hidden="true" /> : <CreditCard size={15} aria-hidden="true" />}
              {trip.paymentMethod === 'wallet' ? 'Ví SwiftPay' : 'Thẻ thanh toán'}
            </span>
            <span>
              {trip.status === 'Paid'
                ? 'Đã thanh toán'
                : trip.status === 'DroppedOff' || trip.status === 'PaymentPending'
                  ? 'Đang xử lý'
                  : 'Thanh toán sau chuyến đi'}
            </span>
          </div>

          {error && <p role="alert" className="alert alert-error" style={{ marginTop: 16 }}>{error}</p>}
          <div style={{ display: 'grid', gap: 10, marginTop: 20 }}>
            {quoted && (
              <button
                disabled={loading}
                onClick={() => void run(() => store.confirmQuote(trip.id, trip.quoteId!))}
                className="btn btn-primary btn-block"
              >
                {loading ? <LoaderCircle size={18} className="animate-spin" aria-hidden="true" /> : <Check size={18} aria-hidden="true" />}
                Xác nhận giá &amp; đặt xe
                <ArrowRight size={18} aria-hidden="true" />
              </button>
            )}
            {(trip.status === 'DroppedOff' || trip.status === 'PaymentPending') && (
              <div className="alert alert-warning" style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <LoaderCircle size={16} className="animate-spin" aria-hidden="true" />
                <span>Hệ thống đang đối soát và xử lý thanh toán tự động...</span>
              </div>
            )}
            {terminal && (
              <button
                disabled={loading}
                onClick={() => {
                  store.setActor(store.getState().actor);
                }}
                className="btn btn-primary btn-block"
              >
                <Sparkles size={18} aria-hidden="true" />
                Đặt một chuyến mới
                <ArrowRight size={18} aria-hidden="true" />
              </button>
            )}
            {canCancel && (
              <button
                disabled={loading}
                onClick={() => void run(() => store.cancelTrip(trip.id))}
                className="btn btn-secondary btn-block"
              >
                {quoted ? 'Chọn lại hành trình' : 'Hủy chuyến đi'}
              </button>
            )}
          </div>
          <p className="caption" style={{ display: 'flex', gap: 8, justifyContent: 'center', marginTop: 20, textAlign: 'center' }}>
            <ShieldCheck size={16} aria-hidden="true" style={{ flexShrink: 0, color: 'var(--color-brand-500)' }} />
            {quoted ? 'Giá được giữ nguyên sau khi bạn xác nhận.' : 'Thông tin chuyến đi được cập nhật liên tục.'}
          </p>
        </>
      )}
    </section>
  );
}
