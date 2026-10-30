import type { ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import RideMap, { MapControls } from '../../components/RideMap';
import { DomEvent } from 'leaflet';

vi.mock('leaflet', () => ({ DomEvent: { disableClickPropagation: vi.fn() } }));
vi.mock('react', async importOriginal => ({ ...await importOriginal<typeof import('react')>(), useEffect: () => {} }));

type TileEvent = { tile: HTMLImageElement };
type TileProps = {
  url: string;
  attribution: string;
  eventHandlers: Record<string, (event: TileEvent) => void>;
};

const captured = vi.hoisted(() => ({ tiles: null as TileProps | null, markers: [] as number[][],
  click: null as null | ((event: { latlng: { wrap: () => { lat: number; lng: number } } }) => void) }));

vi.mock('react-leaflet', () => ({
  MapContainer: ({ children }: { children: ReactNode }) => <div>{children}</div>,
  CircleMarker: ({ children, center }: { children: ReactNode; center: number[] }) => { captured.markers.push(center); return <div>{children}</div>; },
  Tooltip: ({ children }: { children: ReactNode }) => <span>{children}</span>,
  TileLayer: (props: TileProps) => {
    captured.tiles = props;
    return null;
  },
  useMap: () => ({ fitBounds: vi.fn(), setView: vi.fn(), invalidateSize: vi.fn(), getContainer: vi.fn() }),
  useMapEvents: ({ click }: { click: typeof captured.click }) => { captured.click = click; },
}));

beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal('window', globalThis);
  captured.tiles = null;
  captured.markers = [];
  renderToStaticMarkup(<RideMap />);
});

afterEach(() => {
  vi.clearAllTimers();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('OpenStreetMap tiles', () => {
  it('protects the recenter button at the native Leaflet event boundary', () => {
    const control = MapControls({ pickup: { id: 'p', name: 'Pickup', address: 'A', lat: 21, lng: 105 } });
    const button = {} as HTMLButtonElement;
    expect(control.props.ref).toBeTypeOf('function');
    control.props.ref(button);
    expect(DomEvent.disableClickPropagation).toHaveBeenCalledWith(button);
  });

  it('previews both chosen coordinates before a trip exists', () => {
    captured.markers = [];
    const preview = {
      pickup: { id: 'p', name: 'Pickup', address: 'A', lat: 21, lng: 105 },
      destination: { id: 'd', name: 'Destination', address: 'B', lat: 21.1, lng: 105.1 },
    };
    renderToStaticMarkup(<RideMap preview={preview} />);
    expect(captured.markers).toEqual([[21, 105], [21.1, 105.1]]);
  });

  it('returns the clicked coordinate to the booking form when selection is enabled', () => {
    const select = vi.fn();
    renderToStaticMarkup(<RideMap onSelect={select} />);
    captured.click!({ latlng: { wrap: () => ({ lat: 21.03, lng: 105.84 }) } });
    expect(select).toHaveBeenCalledWith(expect.objectContaining({ lat: 21.03, lng: 105.84, address: '21.03000, 105.84000' }));
  });

  it('uses real OpenStreetMap tiles and credits the contributors', () => {
    expect(captured.tiles?.url).toBe('https://tile.openstreetmap.org/{z}/{x}/{y}.png');
    expect(captured.tiles?.attribution).toContain('OpenStreetMap');
    expect(captured.tiles?.attribution).toContain('https://www.openstreetmap.org/copyright');
  });

  it('cancels a stalled tile after 15 seconds', () => {
    const tile = { src: 'https://tile.openstreetmap.org/14/13047/7700.png', complete: false } as HTMLImageElement;
    captured.tiles!.eventHandlers.tileloadstart({ tile });
    vi.advanceTimersByTime(14_999);
    expect(tile.src).toMatch(/^https:/);
    vi.advanceTimersByTime(1);
    expect(tile.src).toMatch(/^data:image\//);
    expect(vi.getTimerCount()).toBe(0);
  });

  it.each(['tileload', 'tileunload', 'tileerror'])('clears the timeout on %s without replacing the image', (event) => {
    const url = 'https://tile.openstreetmap.org/14/13047/7700.png';
    const tile = { src: url, complete: false } as HTMLImageElement;
    captured.tiles!.eventHandlers.tileloadstart({ tile });
    expect(vi.getTimerCount()).toBe(1);
    captured.tiles!.eventHandlers[event]({ tile });
    expect(vi.getTimerCount()).toBe(0);
    vi.advanceTimersByTime(15_000);
    expect(tile.src).toBe(url);
  });
});
