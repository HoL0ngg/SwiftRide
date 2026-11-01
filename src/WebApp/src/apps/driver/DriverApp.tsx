import { useEffect, useMemo, useState } from 'react';
import { Navigation, Wallet } from 'lucide-react';
import DriverView from '../../components/DriverView';
import RideMap from '../../components/RideMap';
import AuthStatus from '../../components/AuthStatus';
import BrandMark from '../../components/ui/BrandMark';
import Avatar from '../../components/ui/Avatar';
import TripHistoryList from '../../components/ui/TripHistoryList';
import type { Place } from '../../types/place';
import { createSwiftRideClient } from '../../services/api';
import { useTrip } from '../../store/useTrip';
import { useDriverPresence } from '../../store/useDriverPresence';
import { formatVnd } from '../../utils/money';
import { isAcceptedByDriver, summarizeEarnings } from '../../utils/driverTrips';

type Page = 'trips' | 'earnings';

// Must equal the JWT `sub`: Matching only lets a driver update their own location.
const DRIVER_ID = 'demo-driver';

function pageFromHash(): Page {
  return typeof location !== 'undefined' && location.hash === '#earnings' ? 'earnings' : 'trips';
}

export default function DriverApp() {
  const [page, setPage] = useState<Page>(pageFromHash);
  const [online, setOnline] = useState(true);
  const client = useMemo(() => createSwiftRideClient({ userId: DRIVER_ID, role: 'driver' }), []);
  // The only place that starts polling; DriverView just reads the shared state.
  const state = useTrip(client.store);
  const { position, moveTo } = useDriverPresence(client.api, DRIVER_ID, online);

  const trip = state.trip;
  const accepted = isAcceptedByDriver(trip?.status);
  const earnings = summarizeEarnings(state.history);

  useEffect(() => {
    if (location.search.includes('code=') || location.hash.includes('code=')) {
      void client.session.completeSignIn().catch(err => {
        console.error('Lỗi hoàn tất đăng nhập OIDC:', err);
      });
    }
  }, [client]);

  // Earnings come from history, so reload it on entry and whenever the current trip changes
  // (for example it just became Paid, or it left the feed).
  const tripId = trip?.id ?? null;
  const tripStatus = trip?.status ?? null;
  useEffect(() => {
    void client.store.refreshHistory();
  }, [client, tripId, tripStatus, page]);

  function goTo(next: Page) {
    setPage(next);
    window.history.replaceState(null, '', next === 'earnings' ? '#earnings' : location.pathname + location.search);
  }

  const driverPlace: Place = {
    id: 'driver-position',
    name: 'Vị trí của bạn',
    address: '',
    lat: position.latitude,
    lng: position.longitude,
  };

  const activeTripMap = trip
    ? {
        pickup: {
          id: 'pickup',
          name: trip.pickupName || 'Điểm đón',
          address: trip.pickupAddress || '',
          lat: trip.pickup.latitude,
          lng: trip.pickup.longitude,
        },
        destination: {
          id: 'destination',
          name: trip.destinationName || 'Điểm đến',
          address: trip.destinationAddress || '',
          lat: trip.destination.latitude,
          lng: trip.destination.longitude,
        },
      }
    : null;

  return (
    <div className="app-frame">
      <a className="skip-link" href="#main-content">Đến nội dung chính</a>
      <header className="topbar topbar--dark">
        <BrandMark roleLabel="Tài xế" href="/driver/" />
        <nav className="tabs" aria-label="Điều hướng tài xế">
          <button className="tab" aria-current={page === 'trips' ? 'page' : undefined} onClick={() => goTo('trips')}>
            <Navigation size={18} aria-hidden="true" />
            Chuyến
          </button>
          <button className="tab" aria-current={page === 'earnings' ? 'page' : undefined} onClick={() => goTo('earnings')}>
            <Wallet size={18} aria-hidden="true" />
            Thu nhập
          </button>
        </nav>
        <div className="topbar-actions">
          <span className="pill hide-on-mobile" style={{ background: 'rgb(255 255 255 / 0.14)', color: '#fff' }}>
            Đã nhận {formatVnd(earnings.totalVnd)}
          </span>
          <button
            type="button"
            role="switch"
            aria-checked={online}
            aria-label="Trạng thái nhận chuyến"
            disabled={accepted}
            className="online-switch"
            onClick={() => setOnline(current => !current)}
          >
            {online ? 'Trực tuyến' : 'Ngoại tuyến'}
            <span className="switch-track" aria-hidden="true">
              <span className="switch-knob" />
            </span>
          </button>
          <AuthStatus session={client.session} inverted />
          <Avatar userId={DRIVER_ID} />
        </div>
      </header>

      {page === 'trips' ? (
        <main id="main-content" className="stage">
          <div className="stage-map">
            <RideMap
              activeTrip={activeTripMap}
              focus={driverPlace}
              driver={online ? { lat: position.latitude, lng: position.longitude } : undefined}
              onSelect={
                online && !accepted
                  ? place => moveTo({ latitude: place.lat, longitude: place.lng })
                  : undefined
              }
            />
          </div>
          <section className="float-panel" aria-label="Quản lý chuyến đi">
            <div className="float-panel__body">
              <DriverView store={client.store} online={online} />
            </div>
          </section>
        </main>
      ) : (
        <main id="main-content" className="page-scroll">
          <div className="page-inner">
            <span className="eyebrow">Tài xế</span>
            <h1 className="page-title" style={{ marginTop: 6 }}>Thu nhập của bạn</h1>
            <p className="muted" style={{ marginTop: 6 }}>Chỉ tính các chuyến đã thanh toán trong 20 chuyến gần nhất.</p>
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
            <TripHistoryList
              items={state.history}
              emptyTitle="Chưa có chuyến nào"
              emptyHint="Hoàn tất chuyến đầu tiên để xem thu nhập tại đây."
            />
          </div>
        </main>
      )}
    </div>
  );
}
