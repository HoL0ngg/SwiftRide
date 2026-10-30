import { afterEach, describe, expect, it, vi } from 'vitest';
import { searchLocations } from '../../services/locations';
import { createAuthSession } from '../../services/auth';

afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); vi.useRealTimers(); });

describe('Gateway address search', () => {
  it('encodes the query, authenticates through the Gateway and maps coordinates', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify([
      { id: 'osm-123', displayName: 'Chợ Bến Thành, TP.HCM', latitude: 10.7725, longitude: 106.698 },
    ])));
    vi.stubGlobal('fetch', fetch);
    expect(await searchLocations(' Chợ & Bến Thành ', 'provided-jwt')).toEqual([
      { id: 'osm-123', name: 'Chợ Bến Thành', address: 'Chợ Bến Thành, TP.HCM', lat: 10.7725, lng: 106.698 },
    ]);
    expect(fetch).toHaveBeenCalledWith('/locations/search?query=Ch%E1%BB%A3%20%26%20B%E1%BA%BFn%20Th%C3%A0nh', expect.objectContaining({ headers: { Authorization: 'Bearer provided-jwt' }, signal: expect.any(AbortSignal) }));
  });

  it('reuses existing AuthSession token without requesting dev token again', async () => {
    const session = createAuthSession({ userId: 'demo-rider', role: 'rider' });
    session.setToken('shared-session-token');

    const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify([
      { id: 'p1', displayName: 'Địa điểm 1', latitude: 10.1, longitude: 106.1 },
    ])));
    vi.stubGlobal('fetch', fetchMock);

    const places = await searchLocations('Địa điểm', session);
    expect(places).toHaveLength(1);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith(
      '/locations/search?query=%C4%90%E1%BB%8Ba%20%C4%91i%E1%BB%83m',
      expect.objectContaining({ headers: { Authorization: 'Bearer shared-session-token' } })
    );
  });

  it('obtains a development token only in development', async () => {
    vi.stubEnv('DEV', true);
    const fetch = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ access_token: 'dev-jwt' })))
      .mockResolvedValueOnce(new Response('[]'));
    vi.stubGlobal('fetch', fetch);
    expect(await searchLocations('Hà Nội', '')).toEqual([]);
    expect(fetch).toHaveBeenNthCalledWith(1, '/dev/token?userId=demo-rider&role=rider', expect.objectContaining({ method: 'POST' }));
    expect(fetch).toHaveBeenNthCalledWith(2, expect.stringContaining('/locations/search?'), expect.objectContaining({ headers: { Authorization: 'Bearer dev-jwt' } }));
  });

  it('requires a supplied token in production without calling the dev endpoint', async () => {
    vi.stubEnv('DEV', false);
    const fetch = vi.fn();
    vi.stubGlobal('fetch', fetch);
    await expect(searchLocations('Hà Nội', '')).rejects.toThrow('JWT');
    expect(fetch).not.toHaveBeenCalled();
  });

  it.each([401, 429, 503])('reports HTTP %s without quietly using seed places', async status => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ title: 'Search unavailable', detail: 'Try later' }), { status })));
    await expect(searchLocations('Hà Nội', 'jwt')).rejects.toThrow(new RegExp(String(status)));
  });

  it('rejects invalid upstream coordinates', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify([
      { id: 'bad', displayName: 'Bad point', latitude: 91, longitude: 106 },
    ]))));
    await expect(searchLocations('Hà Nội', 'jwt')).rejects.toThrow('không hợp lệ');
  });

  it('aborts a stalled request after 12 seconds', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', (_url: string, { signal }: RequestInit) => new Promise((_resolve, reject) => {
      signal!.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    }));
    const result = expect(searchLocations('Hà Nội', 'jwt')).rejects.toThrow('quá thời gian');
    await vi.advanceTimersByTimeAsync(12_000);
    await result;
    expect(vi.getTimerCount()).toBe(0);
  });
});
