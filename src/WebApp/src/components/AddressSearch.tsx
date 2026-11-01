import { useEffect, useRef, useState } from 'react';
import { MapPin, Search } from 'lucide-react';
import type { Place } from '../types/place';
import { searchLocations } from '../services/locations';
import type { AuthSession } from '../services/auth';

type Props = {
  label: string;
  place: Place;
  token?: string | AuthSession;
  disabled: boolean;
  selecting: boolean;
  onChange: (place: Place) => void;
  onSelectMap: () => void;
};

export default function AddressSearch({ label, place, token = '', disabled, selecting, onChange, onSelectMap }: Props) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<Place[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const requestId = useRef(0);
  useEffect(() => {
    requestId.current++;
    setQuery('');
    setResults(null);
    setError('');
    setLoading(false);
    return () => { requestId.current++; };
  }, [place]);

  async function search() {
    if (loading || disabled) return;
    const id = ++requestId.current;
    setLoading(true);
    setError('');
    setResults(null);
    try {
      const found = await searchLocations(query, token);
      if (id === requestId.current) setResults(found);
    } catch (reason) {
      console.error('Lỗi tìm địa chỉ qua Gateway:', reason);
      if (id === requestId.current) setError(reason instanceof Error ? reason.message : 'Chưa tìm được địa chỉ. Vui lòng thử lại.');
    } finally {
      if (id === requestId.current) setLoading(false);
    }
  }

  return <div className={`place-card address-search${selecting ? ' place-card--selecting' : ''}`}>
    <p className="route-label">{label}</p>
    <p className="route-name" style={{ marginTop: 4 }}>{place.name}</p>
    <p className="route-address">{place.address}</p>
    <div className="place-row">
      <input className="text-input" aria-label={label} value={query} autoComplete="off" maxLength={200} placeholder="Nhập địa chỉ cần tìm…" disabled={disabled || loading}
        onChange={event => { setQuery(event.target.value); setResults(null); setError(''); }}
        onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); void search(); } }} />
      <button type="button" className="btn btn-primary btn-sm" disabled={disabled || loading || query.trim().length < 3} onClick={() => void search()} aria-label={`Tìm ${label.toLowerCase()}`}><Search size={16} aria-hidden="true" />{loading ? 'Đang tìm…' : 'Tìm'}</button>
    </div>
    <div style={{ marginTop: 10 }}>
      <button type="button" className="chip-btn" disabled={disabled} aria-pressed={selecting} onClick={onSelectMap}><MapPin size={14} aria-hidden="true" />{selecting ? 'Đang chọn: nhấn vị trí trên bản đồ' : 'Chọn trên bản đồ'}</button>
    </div>
    {error && <p role="alert" className="alert alert-error" style={{ marginTop: 10 }}>{error}</p>}
    {results && <div aria-live="polite">
      {results.length === 0 ? <p className="caption" style={{ marginTop: 10 }}>Không tìm thấy địa chỉ. Thử ghi rõ đường, quận và thành phố.</p> : <ul aria-label={`Kết quả ${label.toLowerCase()}`} className="result-list">
        {results.map(result => <li key={result.id}><button type="button" className="result-item" disabled={disabled} onClick={() => onChange(result)}>{result.address}</button></li>)}
      </ul>}
    </div>}
  </div>;
}
