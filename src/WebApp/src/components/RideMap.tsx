import { useEffect, useRef, useState } from 'react';
import type { TileEvent } from 'leaflet';
import { DomEvent } from 'leaflet';
import { CircleMarker, MapContainer, TileLayer, Tooltip, useMap, useMapEvents } from 'react-leaflet';
import { LocateFixed, MapPin } from 'lucide-react';
import type { Place } from '../types/place';

export const defaultHcmCenter: Place = {
  id: 'hcm-center',
  name: 'TP. Hồ Chí Minh',
  address: 'Bến Nghé, Quận 1, TP. Hồ Chí Minh',
  lat: 10.7769,
  lng: 106.7009,
};

export function MapControls({ pickup, destination }: { pickup: Place; destination?: Place }) {
  const map = useMap();
  function recenter() {
    if (destination) map.fitBounds([[pickup.lat, pickup.lng], [destination.lat, destination.lng]], { padding: [65, 95], maxZoom: 15 });
    else map.setView([pickup.lat, pickup.lng], 14);
  }
  useEffect(() => {
    const observer = new ResizeObserver(() => map.invalidateSize());
    observer.observe(map.getContainer());
    return () => observer.disconnect();
  }, [map]);
  useEffect(recenter, [map, pickup.lat, pickup.lng, destination?.lat, destination?.lng]);
  return <button ref={element => { if (element) DomEvent.disableClickPropagation(element); }} className="map-recenter" onClick={recenter} aria-label="Đưa bản đồ về chuyến đi" title="Về vị trí chuyến đi"><LocateFixed size={21} /></button>;
}

function MapSelection({ onSelect }: { onSelect?: (place: Place) => void }) {
  useMapEvents({ click: ({ latlng }) => {
    if (!onSelect) return;
    const point = latlng.wrap();
    if (Math.abs(point.lat) > 90) return;
    const address = `${point.lat.toFixed(5)}, ${point.lng.toFixed(5)}`;
    onSelect({ id: `map-${point.lat}-${point.lng}`, name: 'Điểm đã chọn trên bản đồ', address, lat: point.lat, lng: point.lng });
  } });
  return null;
}

type Props = {
  preview?: { pickup: Place; destination: Place };
  activeTrip?: { pickup: Place; destination: Place } | null;
  onSelect?: (place: Place) => void;
};

export default function RideMap({ preview, activeTrip, onSelect }: Props = {}) {
  const trip = activeTrip !== undefined ? activeTrip : preview;
  const pickup = trip?.pickup ?? preview?.pickup ?? defaultHcmCenter;
  const destination = trip?.destination ?? preview?.destination;
  const [tileError, setTileError] = useState(false);
  const [tileAttempt, setTileAttempt] = useState(0);
  const tileTimers = useRef(new Map<HTMLElement, ReturnType<typeof setTimeout>>());
  const failedTiles = useRef(new Set<HTMLElement>());

  function clearTileTimer({ tile }: TileEvent) {
    clearTimeout(tileTimers.current.get(tile));
    tileTimers.current.delete(tile);
  }

  function registerFailure(tile: HTMLElement) {
    failedTiles.current.add(tile);
    if (failedTiles.current.size >= 3 || (typeof navigator !== 'undefined' && !navigator.onLine)) {
      setTileError(true);
    }
  }

  function registerSuccess(tile: HTMLElement) {
    failedTiles.current.delete(tile);
    if (failedTiles.current.size < 3) {
      setTileError(false);
    }
  }

  useEffect(() => {
    const timers = tileTimers.current;
    const failures = failedTiles.current;
    return () => {
      timers.forEach(clearTimeout);
      timers.clear();
      failures.clear();
    };
  }, []);

  return <>
    <MapContainer center={[pickup.lat, pickup.lng]} zoom={14} scrollWheelZoom={false} className="ride-map" zoomControl={true}>
      <TileLayer key={tileAttempt} url="https://tile.openstreetmap.org/{z}/{x}/{y}.png" attribution='&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener noreferrer">OpenStreetMap</a> contributors' maxZoom={19} eventHandlers={{
        tileloadstart: event => {
          clearTileTimer(event);
          tileTimers.current.set(event.tile, setTimeout(() => {
            clearTileTimer(event);
            registerFailure(event.tile);
            (event.tile as HTMLImageElement).src = 'data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs=';
          }, 15000));
        },
        tileload: event => {
          clearTileTimer(event);
          registerSuccess(event.tile);
        },
        tileunload: event => {
          clearTileTimer(event);
          registerSuccess(event.tile);
        },
        tileerror: event => {
          clearTileTimer(event);
          registerFailure(event.tile);
        },
      }} />
      <CircleMarker center={[pickup.lat, pickup.lng]} radius={9} pathOptions={{ color: '#ffffff', weight: 4, fillColor: '#047857', fillOpacity: 1 }}>
        <Tooltip direction="top" permanent offset={[0, -11]}>{trip ? `Đón: ${pickup.name}` : pickup.name}</Tooltip>
      </CircleMarker>
      {destination && (
        <CircleMarker center={[destination.lat, destination.lng]} radius={9} pathOptions={{ color: '#ffffff', weight: 4, fillColor: '#f59e0b', fillOpacity: 1 }}>
          <Tooltip direction="top" permanent offset={[0, -11]}>Đến: {destination.name}</Tooltip>
        </CircleMarker>
      )}
      <MapControls pickup={pickup} destination={destination} />
      <MapSelection onSelect={trip ? undefined : onSelect} />
    </MapContainer>
    {tileError && <div className="map-error" role="status"><MapPin size={16} className="shrink-0" /><span>Một số ô bản đồ chưa tải được. Hãy kiểm tra kết nối mạng.</span><button className="ml-auto shrink-0 rounded border border-amber-300 px-2 py-1 font-semibold" onClick={() => { failedTiles.current.clear(); setTileError(false); setTileAttempt(attempt => attempt + 1); }}>Thử lại</button></div>}
  </>;
}
