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

  return <div className="address-search">
    <label className="block text-xs font-semibold text-stone-700">
      {label}
      <span className="mt-2 flex gap-2">
        <input aria-label={label} value={query} autoComplete="off" maxLength={200} placeholder="Nhập địa chỉ cần tìm…" disabled={disabled || loading}
          onChange={event => { setQuery(event.target.value); setResults(null); setError(''); }}
          onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); void search(); } }} className="min-w-0 flex-1 rounded-lg border border-stone-200 bg-white p-2 text-xs font-normal" />
        <button type="button" disabled={disabled || loading || query.trim().length < 3} onClick={() => void search()} aria-label={`Tìm ${label.toLowerCase()}`} className="flex items-center gap-1 rounded-lg bg-emerald-700 px-3 text-xs text-white"><Search size={13} aria-hidden="true" />{loading ? 'Đang tìm…' : 'Tìm'}</button>
      </span>
    </label>
    <p className="mt-2 text-xs font-medium text-emerald-900">{place.name}</p>
    <p className="mt-1 break-words text-[10px] leading-4 text-stone-500">{place.address}<br />{place.lat.toFixed(5)}, {place.lng.toFixed(5)}</p>
    <button type="button" disabled={disabled} aria-pressed={selecting} onClick={onSelectMap} className={`mt-2 inline-flex items-center gap-1 rounded-lg border px-2 py-1.5 text-[10px] ${selecting ? 'border-emerald-600 bg-emerald-50 text-emerald-800' : 'border-stone-200 text-stone-600'}`}><MapPin size={12} aria-hidden="true" />{selecting ? 'Đang chọn: nhấn vị trí trên bản đồ' : 'Chọn trên bản đồ'}</button>
    {error && <p role="alert" className="error-message mt-2">{error}</p>}
    {results && <div className="mt-2" aria-live="polite">
      {results.length === 0 ? <p className="text-xs text-stone-500">Không tìm thấy địa chỉ. Thử ghi rõ đường, quận và thành phố.</p> : <ul aria-label={`Kết quả ${label.toLowerCase()}`} className="max-h-52 overflow-y-auto rounded-lg border border-stone-200 bg-white">
        {results.map(result => <li key={result.id}><button type="button" disabled={disabled} onClick={() => onChange(result)} className="w-full border-b border-stone-100 p-3 text-left text-xs leading-5 hover:bg-emerald-50">{result.address}</button></li>)}
      </ul>}
    </div>}
  </div>;
}
