import { afterEach, describe, expect, it, vi } from 'vitest';
import { createAuthSession } from '../../services/auth';

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.useRealTimers();
});

describe('auth session', () => {
  it('fetches dev token in DEV mode and caches in memory', async () => {
    vi.stubEnv('DEV', true);
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ access_token: 'test-dev-token-rider' }), { status: 200 })
    );
    vi.stubGlobal('fetch', fetchMock);

    const session = createAuthSession({ userId: 'demo-rider', role: 'rider' });
    const token1 = await session.getToken();
    expect(token1).toBe('test-dev-token-rider');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith(
      '/dev/token?userId=demo-rider&role=rider',
      expect.objectContaining({ method: 'POST', signal: expect.any(AbortSignal) })
    );

    // Second call should use in-memory cache
    const token2 = await session.getToken();
    expect(token2).toBe('test-dev-token-rider');
    expect(fetchMock).toHaveBeenCalledTimes(1);

    const authHeader = await session.getAuthorizationHeader();
    expect(authHeader).toBe('Bearer test-dev-token-rider');
  });

  it('rejects in production if no token provided and does not call dev endpoint', async () => {
    vi.stubEnv('DEV', false);
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);

    const session = createAuthSession({ userId: 'prod-rider', role: 'rider' });
    await expect(session.getToken()).rejects.toThrow(/JWT/);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('accepts explicitly set token in production without calling dev endpoint', async () => {
    vi.stubEnv('DEV', false);
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);

    const session = createAuthSession({ userId: 'prod-rider', role: 'rider' });
    session.setToken('explicit-prod-jwt');
    const token = await session.getToken();
    expect(token).toBe('explicit-prod-jwt');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('switches cache when actor changes', async () => {
    vi.stubEnv('DEV', true);
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ access_token: 'token-rider' }), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ access_token: 'token-driver' }), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    const session = createAuthSession({ userId: 'demo-rider', role: 'rider' });
    expect(await session.getToken()).toBe('token-rider');

    session.setActor({ userId: 'demo-driver', role: 'driver' });
    expect(await session.getToken()).toBe('token-driver');
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock).toHaveBeenNthCalledWith(
      2,
      '/dev/token?userId=demo-driver&role=driver',
      expect.anything()
    );
  });

  it('aborts after 20 seconds timeout', async () => {
    vi.stubEnv('DEV', true);
    vi.useFakeTimers();
    vi.stubGlobal('fetch', (_url: string, { signal }: RequestInit) =>
      new Promise((_resolve, reject) => {
        signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
      })
    );

    const session = createAuthSession({ userId: 'timeout-user', role: 'rider' });
    const promise = session.getToken();
    const rejection = expect(promise).rejects.toThrow(/thời gian|quá hạn|timeout/i);
    await vi.advanceTimersByTimeAsync(20_000);
    await rejection;
  });
});
