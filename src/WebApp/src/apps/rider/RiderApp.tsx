import { useEffect, useMemo, useState } from 'react';
import { History, MapPinned, Pointer } from 'lucide-react';
import RiderView from '../../components/RiderView';
import RideMap from '../../components/RideMap';
import AuthStatus from '../../components/AuthStatus';
import BrandMark from '../../components/ui/BrandMark';
import Avatar from '../../components/ui/Avatar';
import TripHistoryList from '../../components/ui/TripHistoryList';
import type { Place } from '../../types/place';
import { createSwiftRideClient } from '../../services/api';
import { useTrip } from '../../store/useTrip';

type Page = 'book' | 'history';

const RIDER_ID = 'demo-rider';

const defaultPickup: Place = {
  id: 'pickup-ben-thanh',
  name: 'Chợ Bến Thành',
  address: 'Lê Lợi, Quận 1, TP. Hồ Chí Minh',
  lat: 10.7725,
  lng: 106.698,
};

const defaultDestination: Place = {
  id: 'dest-landmark-81',
  name: 'Landmark 81',
  address: '720A Điện Biên Phủ, Phường 22, Bình Thạnh, TP. Hồ Chí Minh',
  lat: 10.7951,
  lng: 106.7219,
};

function pageFromHash(): Page {
  return typeof location !== 'undefined' && location.hash === '#history' ? 'history' : 'book';
}

export default function RiderApp() {
  const [page, setPage] = useState<Page>(pageFromHash);
  const client = useMemo(() => createSwiftRideClient({ userId: RIDER_ID, role: 'rider' }), []);
  // The only place that starts polling; RiderView just reads the shared state.
  const state = useTrip(client.store);

  const [route, setRoute] = useState({ pickup: defaultPickup, destination: defaultDestination });
  const [selection, setSelection] = useState<'pickup' | 'destination' | null>(null);

  useEffect(() => {
    if (location.search.includes('code=') || location.hash.includes('code=')) {
      void client.session.completeSignIn().catch(err => {
        console.error('Lỗi hoàn tất đăng nhập OIDC:', err);
      });
    }
  }, [client]);

  useEffect(() => {
    if (page === 'history') void client.store.refreshHistory();
  }, [page, client]);

  function goTo(next: Page) {
    setPage(next);
    setSelection(null);
    window.history.replaceState(null, '', next === 'history' ? '#history' : location.pathname + location.search);
  }

  function changePlace(kind: 'pickup' | 'destination', place: Place) {
    setRoute(current => ({ ...current, [kind]: place }));
    setSelection(null);
  }

  const trip = state.trip;
  const activeTripMap = trip
    ? {
        pickup: {
          id: 'pickup',
          name: trip.pickupName || route.pickup.name,
          address: trip.pickupAddress || route.pickup.address,
          lat: trip.pickup.latitude,
          lng: trip.pickup.longitude,
        },
        destination: {
          id: 'destination',
          name: trip.destinationName || route.destination.name,
          address: trip.destinationAddress || route.destination.address,
          lat: trip.destination.latitude,
          lng: trip.destination.longitude,
        },
      }
    : null;

  return (
    <div className="app-frame">
      <a className="skip-link" href="#main-content">Đến nội dung chính</a>
      <header className="topbar">
        <BrandMark />
        <nav className="tabs" aria-label="Điều hướng hành khách">
          <button className="tab" aria-current={page === 'book' ? 'page' : undefined} onClick={() => goTo('book')}>
            <MapPinned size={18} aria-hidden="true" />
            Đặt xe
          </button>
          <button className="tab" aria-current={page === 'history' ? 'page' : undefined} onClick={() => goTo('history')}>
            <History size={18} aria-hidden="true" />
            Lịch sử
          </button>
        </nav>
        <div className="topbar-actions">
          <AuthStatus session={client.session} />
          <Avatar userId={RIDER_ID} />
        </div>
      </header>

      {page === 'book' ? (
        <main id="main-content" className="stage">
          <div className="stage-map">
            <RideMap
              preview={route}
              activeTrip={activeTripMap}
              onSelect={selection ? place => changePlace(selection, place) : undefined}
            />
          </div>
          {!trip && selection && (
            <div className="map-hint" role="status">
              <Pointer size={16} aria-hidden="true" />
              {selection === 'pickup' ? 'Nhấn bản đồ để chọn điểm đón' : 'Nhấn bản đồ để chọn điểm đến'}
            </div>
          )}
          <section className="float-panel" aria-label="Đặt và theo dõi chuyến">
            <div className="float-panel__body">
              <RiderView
                pickup={route.pickup}
                destination={route.destination}
                selection={selection}
                onChange={changePlace}
                onSelectMap={kind => setSelection(current => (current === kind ? null : kind))}
                onSwap={() => {
                  setRoute(current => ({ pickup: current.destination, destination: current.pickup }));
                  setSelection(null);
                }}
                store={client.store}
                session={client.session}
              />
            </div>
          </section>
        </main>
      ) : (
        <main id="main-content" className="page-scroll">
          <div className="page-inner">
            <span className="eyebrow">Hành khách</span>
            <h1 className="page-title" style={{ marginTop: 6 }}>Những chuyến đi của bạn</h1>
            <p className="muted" style={{ marginTop: 6 }}>Chuyến đã thanh toán, đã hủy hoặc không thành công sẽ hiện ở đây.</p>
            <TripHistoryList
              items={state.history}
              emptyTitle="Hành trình đầu tiên đang đợi"
              emptyHint="Đặt chuyến đầu tiên và nó sẽ xuất hiện tại đây."
            />
            <div style={{ marginTop: 24 }}>
              <button className="btn btn-primary" onClick={() => goTo('book')}>
                <MapPinned size={18} aria-hidden="true" />
                Đặt xe
              </button>
            </div>
          </div>
        </main>
      )}
    </div>
  );
}
