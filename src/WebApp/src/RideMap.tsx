import { useEffect, useRef, useState } from 'react';
import type { TileEvent } from 'leaflet';
import { CircleMarker, MapContainer, TileLayer, Tooltip, useMap } from 'react-leaflet';
import { LocateFixed, MapPin } from 'lucide-react';
import { places, useDemo } from './demo';
import type { Place } from './demo';

function MapControls({ pickup, destination }: { pickup: Place; destination?: Place }) {
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
  useEffect(recenter, [map, pickup.id, destination?.id]);
  return <button className="map-recenter" onClick={recenter} aria-label="Đưa bản đồ về chuyến đi" title="Về vị trí chuyến đi"><LocateFixed size={21} /></button>;
}

export default function RideMap() {
  const { trip } = useDemo();
  const pickup = trip?.pickup ?? places[0];
  const destination = trip?.destination;
  const [tileError, setTileError] = useState(false);
  const [tileAttempt, setTileAttempt] = useState(0);
  const tileTimers = useRef(new Map<HTMLElement, ReturnType<typeof setTimeout>>());
  function clearTileTimer({ tile }: TileEvent) {
    clearTimeout(tileTimers.current.get(tile));
    tileTimers.current.delete(tile);
  }
  useEffect(() => {
    const timers = tileTimers.current;
    return () => { timers.forEach(clearTimeout); timers.clear(); };
  }, []);
  return <>
    <MapContainer center={[pickup.lat, pickup.lng]} zoom={14} scrollWheelZoom={false} className="ride-map" zoomControl={true}>
      <TileLayer key={tileAttempt} url="https://tile.openstreetmap.org/{z}/{x}/{y}.png" attribution='&copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener noreferrer">OpenStreetMap</a> contributors' maxZoom={19} eventHandlers={{
        tileloadstart: event => {
          clearTileTimer(event);
          tileTimers.current.set(event.tile, setTimeout(() => {
            clearTileTimer(event);
            setTileError(true);
            // Replacing the source cancels a stalled image request without retrying it.
            (event.tile as HTMLImageElement).src = 'data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs=';
          }, 15000));
        },
        tileload: clearTileTimer,
        tileunload: clearTileTimer,
        tileerror: event => { clearTileTimer(event); setTileError(true); },
      }} />
      <CircleMarker center={[pickup.lat, pickup.lng]} radius={9} pathOptions={{ color: '#ffffff', weight: 4, fillColor: '#047857', fillOpacity: 1 }}><Tooltip direction="top" permanent offset={[0, -11]}>{trip ? `Đón: ${pickup.name}` : pickup.name}</Tooltip></CircleMarker>
      {destination && <CircleMarker center={[destination.lat, destination.lng]} radius={9} pathOptions={{ color: '#ffffff', weight: 4, fillColor: '#f59e0b', fillOpacity: 1 }}><Tooltip direction="top" permanent offset={[0, -11]}>Đến: {destination.name}</Tooltip></CircleMarker>}
      <MapControls pickup={pickup} destination={destination} />
    </MapContainer>
    {tileError && <div className="map-error" role="status"><MapPin size={16} className="shrink-0" /><span>Một số ô bản đồ chưa tải được. Hãy kiểm tra kết nối mạng.</span><button className="ml-auto shrink-0 rounded border border-amber-300 px-2 py-1 font-semibold" onClick={() => { setTileError(false); setTileAttempt(attempt => attempt + 1); }}>Thử lại</button></div>}
  </>;
}
