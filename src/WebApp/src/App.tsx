import { useState } from 'react';
import { ArrowDownLeft, ArrowUpRight, CarFront, Check, ChevronRight, CircleHelp, Clock3, History, Leaf, MapPinned, ShieldCheck, Sparkles, UserRound, X, Zap } from 'lucide-react';
import RiderView from './RiderView';
import DriverView from './DriverView';
import RideMap from './RideMap';
import { formatMoney, useDemo } from './demo';

type Role = 'rider' | 'driver';
type Page = 'ride' | 'history' | 'help';

export default function App() {
  const [role, setRole] = useState<Role>(() => new URLSearchParams(location.search).get('role') === 'driver' ? 'driver' : 'rider');
  const [page, setPage] = useState<Page>('ride');
  const { trip, history } = useDemo();
  const [noticeOpen, setNoticeOpen] = useState(true);

  function switchRole(next: Role) {
    setRole(next);
    setPage('ride');
    const url = new URL(location.href);
    url.searchParams.set('role', next);
    window.history.replaceState(null, '', url);
  }

  return (
    <div className="app-shell">
      <a className="skip-link" href="#main-content">Đến nội dung chính</a>
      <aside className="sidebar" aria-label="Điều hướng chính">
        <a className="brand" href={`/?role=${role}`} aria-label="SwiftRide trang chủ"><span className="brand-icon"><Zap size={23} fill="currentColor" /></span><span>swift<span className="text-emerald-700">ride</span><span className="brand-dot">.</span></span></a>
        <div className="sidebar-caption">MỖI CHUYẾN ĐI, MỘT KHỞI ĐẦU</div>
        <div className="role-switch" aria-label="Chọn vai trò">
          <button className={role === 'rider' ? 'selected' : ''} onClick={() => switchRole('rider')} aria-pressed={role === 'rider'}><UserRound size={16} />Hành khách</button>
          <button className={role === 'driver' ? 'selected' : ''} onClick={() => switchRole('driver')} aria-pressed={role === 'driver'}><CarFront size={17} />Tài xế</button>
        </div>
        <nav className="main-nav">
          <button className={page === 'ride' ? 'active' : ''} onClick={() => setPage('ride')}><MapPinned size={20} /><span>{role === 'rider' ? 'Đặt chuyến' : 'Chuyến của bạn'}</span><ChevronRight className="nav-chevron" size={16} /></button>
          <button className={page === 'history' ? 'active' : ''} onClick={() => setPage('history')}><History size={20} /><span>Lịch sử chuyến đi</span>{history.length > 0 && <span className="nav-count">{history.length}</span>}</button>
          <button className={page === 'help' ? 'active' : ''} onClick={() => setPage('help')}><CircleHelp size={20} /><span>Hướng dẫn demo</span></button>
        </nav>
        <div className="sidebar-bottom">
          <div className="journey-note"><span className="note-icon"><Leaf size={21} /></span><strong>Đi nhẹ nhàng.<br />Sống trọn từng nhịp.</strong><p>Một hành trình mới đang chờ bạn phía trước.</p><div className="note-road"><span /><CarFront size={23} /></div></div>
          <div className="profile"><div className="avatar">{role === 'rider' ? 'MA' : 'HT'}</div><div><strong>{role === 'rider' ? 'Minh Anh' : 'Hoàng Tuấn'}</strong><span>{role === 'rider' ? 'Hành khách' : 'Đối tác tài xế'} · Demo</span></div><ShieldCheck size={18} className="text-emerald-700" /></div>
        </div>
      </aside>
      <div className="workspace">
        <header className="topbar"><div className="breadcrumb">SwiftRide <ChevronRight size={13} /><strong>{page === 'history' ? 'Lịch sử chuyến đi' : page === 'help' ? 'Hướng dẫn demo' : role === 'rider' ? 'Đặt chuyến' : 'Không gian tài xế'}</strong></div><div className="topbar-right"><span className="city-badge"><span />TP. Hồ Chí Minh</span><span className="topbar-avatar">{role === 'rider' ? 'MA' : 'HT'}</span></div></header>
        <main id="main-content" className="main-content">
          <div className="page-heading"><div><div className="eyebrow"><Sparkles size={14} /> {role === 'rider' ? 'ĐIỀU TỐT ĐẸP ĐANG Ở PHÍA TRƯỚC' : 'SẴN SÀNG CHO HÀNH TRÌNH MỚI'}</div><h1>{page === 'history' ? 'Những chuyến đi của bạn' : page === 'help' ? 'Cùng thử một chuyến nhé.' : role === 'rider' ? 'Hôm nay, mình đi đâu?' : 'Chào Tuấn, lên đường thôi.'}</h1><p>{page === 'ride' ? role === 'rider' ? 'Chọn điểm đến. Phần còn lại, để SwiftRide lo.' : 'Một chuyến xe chu đáo. Một ngày thêm ý nghĩa.' : 'Mọi thông tin trong bản trải nghiệm đều là dữ liệu mô phỏng.'}</p></div><span className="heading-badge"><ShieldCheck size={17} />An tâm trên từng cây số</span></div>
          {noticeOpen && <div className="demo-notice"><span className="demo-label">BẢN TRẢI NGHIỆM</span><p>Bản đồ OpenStreetMap thật · Chuyến đi & thanh toán mô phỏng</p><button className="icon-button" aria-label="Đóng thông báo demo" onClick={() => setNoticeOpen(false)}><X size={15} /></button></div>}
          {page === 'ride' ? <>
            <div className="ride-layout"><section className="booking-panel" aria-label={role === 'rider' ? 'Đặt và theo dõi chuyến' : 'Quản lý chuyến đi'}>{role === 'rider' ? <RiderView /> : <DriverView />}</section><section className="map-panel" aria-label="Bản đồ chuyến đi"><RideMap /><div className="map-top-label"><span className="map-label-icon"><MapPinned size={18} /></span><div><strong>Thành phố luôn chuyển động</strong><span>Khám phá hành trình của bạn</span></div></div><div className="map-bottom-label"><span className="live-dot" /><span>{trip && !['Cancelled', 'Failed'].includes(trip.status) ? 'Hai điểm đánh dấu là điểm đón và điểm đến' : 'Bản đồ tương tác · Kéo để khám phá'}</span></div></section></div>
            <div className="benefit-row"><div><span><ShieldCheck size={19} /></span><div><strong>An tâm đặt chuyến</strong><p>Giá được xác nhận trước khi đi</p></div></div><div><span><Clock3 size={19} /></span><div><strong>Rõ ràng từng chặng</strong><p>Theo dõi trạng thái chuyến đi</p></div></div><div><span><Leaf size={19} /></span><div><strong>Nhẹ nhàng mỗi ngày</strong><p>Trải nghiệm đơn giản, dễ sử dụng</p></div></div></div>
          </> : page === 'history' ? <section className="content-card"><div className="flex items-center justify-between gap-3"><h2 className="panel-title">Lịch sử của bạn</h2><span className="status-pill">{history.length} chuyến</span></div>{history.length === 0 ? <div className="empty-state"><History size={40} /><h3>Hành trình đầu tiên đang đợi</h3><p>Chuyến đã thanh toán, hủy hoặc không thành công sẽ xuất hiện tại đây.</p><button className="primary-button" onClick={() => setPage('ride')}>Bắt đầu trải nghiệm<ArrowUpRight size={17} /></button></div> : <div className="history-list">{history.map(item => <article key={item.id} className="history-item"><div className="history-icon">{item.status === 'Paid' ? <Check size={20} /> : <X size={20} />}</div><div className="min-w-0 flex-1"><h3>{item.pickup.name} <span className="text-slate-400">→</span> {item.destination.name}</h3><p>{new Date(item.createdAt).toLocaleString('vi-VN')} · {item.id}</p><span className={`history-status ${item.status === 'Paid' ? '' : 'text-amber-700'}`}>{item.status === 'Paid' ? 'Đã thanh toán' : item.status === 'Cancelled' ? 'Đã hủy chuyến' : 'Không thành công'}</span></div><strong>{formatMoney(item.fare)}</strong></article>)}</div>}</section> : <section className="content-card help-card"><div className="eyebrow">MỘT CHUYẾN ĐI, HAI VAI TRÒ</div><h2 className="panel-title">Trải nghiệm trọn vẹn trong hai tab</h2><p>Mở vai trò hành khách và tài xế trên cùng trình duyệt. Dữ liệu demo tự đồng bộ giữa hai tab.</p><ol><li><strong>Hành khách đặt chuyến</strong><span>Chọn điểm đón, điểm đến, xem và xác nhận giá.</span></li><li><strong>Tài xế nhận chuyến</strong><span>Nhận lời mời, bắt đầu đến đón, xác nhận đón và trả khách.</span></li><li><strong>Hoàn tất thanh toán demo</strong><span>Chọn mô phỏng thanh toán thành công để chuyển từ chờ thanh toán sang đã thanh toán.</span></li></ol><div className="help-links"><a className="primary-button" href="/?role=rider" target="_blank" rel="noopener noreferrer">Mở tab hành khách<ArrowUpRight size={17} /></a><a className="secondary-button" href="/?role=driver" target="_blank" rel="noopener noreferrer">Mở tab tài xế<ArrowUpRight size={17} /></a></div><p className="help-limit">Địa điểm, tài xế và giá là dữ liệu mẫu. Bản đồ nền tải thật từ OpenStreetMap; bản demo chưa tìm địa chỉ trực tuyến, dẫn đường hoặc theo dõi GPS.</p></section>}
          <footer className="page-footer"><span>© {new Date().getFullYear()} SwiftRide</span><span>Chạm một nhịp, đi một chuyến <ArrowDownLeft size={13} /></span></footer>
        </main>
      </div>
    </div>
  );
}
