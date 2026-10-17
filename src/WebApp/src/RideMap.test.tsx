import type { ReactNode } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import RideMap from './RideMap';

type TileEvent = { tile: HTMLImageElement };
type TileProps = {
  url: string;
  attribution: string;
  eventHandlers: Record<string, (event: TileEvent) => void>;
};

const captured = vi.hoisted(() => ({ tiles: null as TileProps | null }));

vi.mock('react-leaflet', () => ({
  MapContainer: ({ children }: { children: ReactNode }) => <div>{children}</div>,
  CircleMarker: ({ children }: { children: ReactNode }) => <div>{children}</div>,
  Tooltip: ({ children }: { children: ReactNode }) => <span>{children}</span>,
  TileLayer: (props: TileProps) => {
    captured.tiles = props;
    return null;
  },
  useMap: () => ({ fitBounds: vi.fn(), setView: vi.fn(), invalidateSize: vi.fn(), getContainer: vi.fn() }),
}));

vi.mock('./demo', () => ({
  useDemo: () => ({ trip: null }),
  places: [{ id: 'ben-thanh', name: 'Chợ Bến Thành', address: 'Quận 1', lat: 10.7725, lng: 106.698 }],
}));

beforeEach(() => {
  vi.useFakeTimers();
  vi.stubGlobal('window', globalThis);
  captured.tiles = null;
  renderToStaticMarkup(<RideMap />);
});

afterEach(() => {
  vi.clearAllTimers();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('OpenStreetMap tiles', () => {
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
