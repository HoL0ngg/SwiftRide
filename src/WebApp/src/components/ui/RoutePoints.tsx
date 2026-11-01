import { MapPin } from 'lucide-react';

type Stop = {
  name: string;
  address?: string;
};

type Props = {
  pickup: Stop;
  destination: Stop;
};

/** Vertical pickup -> destination list shared by rider and driver trip cards. */
export default function RoutePoints({ pickup, destination }: Props) {
  return (
    <div className="route">
      <div className="route-point">
        <span className="route-icon">
          <span className="route-dot" aria-hidden="true" />
        </span>
        <div>
          <p className="route-label">Điểm đón</p>
          <p className="route-name">{pickup.name}</p>
          {pickup.address && <p className="route-address">{pickup.address}</p>}
        </div>
      </div>
      <div className="route-point">
        <span className="route-icon route-pin">
          <MapPin size={22} fill="currentColor" aria-hidden="true" />
        </span>
        <div>
          <p className="route-label">Điểm đến</p>
          <p className="route-name">{destination.name}</p>
          {destination.address && <p className="route-address">{destination.address}</p>}
        </div>
      </div>
    </div>
  );
}
