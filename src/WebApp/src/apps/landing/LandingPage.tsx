import { ArrowRight, CarFront, UserRound } from 'lucide-react';
import BrandMark from '../../components/ui/BrandMark';

/** Old links used `/?role=driver`; map them to the dedicated pages. Returns null for plain `/`. */
export function legacyRoleRedirect(search: string): string | null {
  const role = new URLSearchParams(search).get('role');
  if (role === 'driver') return '/driver/';
  if (role === 'rider') return '/rider/';
  return null;
}

export default function LandingPage() {
  return (
    <div className="landing">
      <BrandMark />
      <div className="landing-hero">
        <span className="eyebrow">Chạm một nhịp, đi một chuyến</span>
        <h1 className="landing-title" style={{ marginTop: 10 }}>Bạn dùng SwiftRide với vai trò nào?</h1>
        <p className="muted" style={{ marginTop: 12, fontSize: 16 }}>
          Hai giao diện riêng, cùng kết nối qua một API Gateway. Mở mỗi giao diện trên một tab để thử trọn vẹn một chuyến đi.
        </p>
      </div>
      <div className="role-grid">
        <a className="role-card" href="/rider/">
          <span className="role-card-icon"><UserRound size={28} aria-hidden="true" /></span>
          <div>
            <h2 className="panel-title">Tôi cần đi xe</h2>
            <p className="muted" style={{ marginTop: 6 }}>Chọn điểm đến, xem giá rõ ràng và theo dõi chuyến đi.</p>
          </div>
          <span className="btn btn-primary" style={{ alignSelf: 'flex-start' }}>
            Mở giao diện hành khách <ArrowRight size={18} aria-hidden="true" />
          </span>
        </a>
        <a className="role-card role-card--driver" href="/driver/">
          <span className="role-card-icon"><CarFront size={28} aria-hidden="true" /></span>
          <div>
            <h2 className="panel-title">Tôi lái xe</h2>
            <p className="muted" style={{ marginTop: 6 }}>Bật trực tuyến, nhận chuyến, đón khách và xem thu nhập.</p>
          </div>
          <span className="btn btn-secondary" style={{ alignSelf: 'flex-start' }}>
            Mở giao diện tài xế <ArrowRight size={18} aria-hidden="true" />
          </span>
        </a>
      </div>
    </div>
  );
}
