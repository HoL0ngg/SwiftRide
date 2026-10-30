import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  createAuthSession,
  isTokenExpiredOrExpiring,
  parseJwtPayload,
  type Actor,
} from '../../services/auth';
import type { User, UserManager } from 'oidc-client-ts';

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.useRealTimers();
});

function base64UrlEncode(str: string): string {
  let b64 = '';
  if (typeof btoa === 'function') {
    b64 = btoa(encodeURIComponent(str).replace(/%([0-9A-F]{2})/g, (_, p1) => String.fromCharCode(parseInt(p1, 16))));
  } else {
    const g = globalThis as unknown as {
      Buffer?: { from(data: string, encoding?: string): { toString(enc: string): string } };
    };
    if (g.Buffer) {
      b64 = g.Buffer.from(str, 'utf8').toString('base64');
    }
  }
  return b64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function createMockJwt(payload: Record<string, unknown>): string {
  const header = { alg: 'HS256', typ: 'JWT' };
  const b64Header = base64UrlEncode(JSON.stringify(header));
  const b64Payload = base64UrlEncode(JSON.stringify(payload));
  const b64Signature = 'mock-sig';
  return `${b64Header}.${b64Payload}.${b64Signature}`;
}

describe('JWT Payload Parsing and Expiration Helper', () => {
  it('parses valid JWT payload correctly', () => {
    const token = createMockJwt({
      sub: 'user-123',
      role: 'rider',
      exp: 1893456000,
    });

    const parsed = parseJwtPayload(token);
    expect(parsed).not.toBeNull();
    expect(parsed?.sub).toBe('user-123');
    expect(parsed?.role).toBe('rider');
    expect(parsed?.exp).toBe(1893456000);
  });

  it('returns null for non-JWT strings', () => {
    expect(parseJwtPayload('random-string-token')).toBeNull();
    expect(parseJwtPayload('')).toBeNull();
  });

  it('detects expiring or expired tokens correctly', () => {
    const nowSec = Math.floor(Date.now() / 1000);

    // Far future -> not expiring
    const validToken = createMockJwt({ exp: nowSec + 3600 });
    expect(isTokenExpiredOrExpiring(validToken, 30)).toBe(false);

    // Expiring within 15 seconds (buffer is 30s) -> expiring
    const expiringSoonToken = createMockJwt({ exp: nowSec + 15 });
    expect(isTokenExpiredOrExpiring(expiringSoonToken, 30)).toBe(true);

    // Already expired in past -> expired
    const expiredToken = createMockJwt({ exp: nowSec - 60 });
    expect(isTokenExpiredOrExpiring(expiredToken, 30)).toBe(true);

    // Non-JWT token without exp claim -> false (fallback)
    expect(isTokenExpiredOrExpiring('plain-opaque-token', 30)).toBe(false);
  });
});

describe('AuthSession Dev Mode Expiry & Lifecycle', () => {
  const testActor: Actor = { userId: 'rider-01', role: 'rider' };

  it('refetches token if cached token is expired or expiring within 30 seconds', async () => {
    vi.stubEnv('DEV', true);
    const nowSec = Math.floor(Date.now() / 1000);

    const expiringToken = createMockJwt({ sub: 'rider-01', role: 'rider', exp: nowSec + 10 });
    const freshToken = createMockJwt({ sub: 'rider-01', role: 'rider', exp: nowSec + 3600 });

    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ access_token: expiringToken }), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ access_token: freshToken }), { status: 200 }));

    vi.stubGlobal('fetch', fetchMock);

    const session = createAuthSession(testActor, { authMode: 'dev' });

    // First call fetches expiring token
    const token1 = await session.getToken();
    expect(token1).toBe(expiringToken);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    // Second call notices token is expiring within 30s, invalidates cache and fetches fresh token
    const token2 = await session.getToken();
    expect(token2).toBe(freshToken);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('notifies listeners when token is cleared or updated', async () => {
    vi.stubEnv('DEV', true);
    const validToken = createMockJwt({ sub: 'rider-01', role: 'rider', exp: Math.floor(Date.now() / 1000) + 3600 });

    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ access_token: validToken }), { status: 200 })
    );
    vi.stubGlobal('fetch', fetchMock);

    const session = createAuthSession(testActor, { authMode: 'dev' });
    const stateChanges: boolean[] = [];

    const unsubscribe = session.onAuthStateChanged(isAuth => {
      stateChanges.push(isAuth);
    });

    // Sign in -> state should become true
    await session.signIn();
    expect(session.isAuthenticated()).toBe(true);
    expect(stateChanges).toContain(true);

    // Clear token -> state should become false
    session.clearToken();
    expect(session.isAuthenticated()).toBe(false);
    expect(stateChanges[stateChanges.length - 1]).toBe(false);

    unsubscribe();
  });
});

describe('AuthSession OIDC Mode Flow & PKCE Handling', () => {
  const testActor: Actor = { userId: 'oidc-user', role: 'rider' };

  it('delegates signIn to userManager.signinRedirect', async () => {
    const signinRedirectMock = vi.fn().mockResolvedValue(undefined);
    const mockUserManager = {
      signinRedirect: signinRedirectMock,
      signinCallback: vi.fn(),
      getUser: vi.fn(),
      removeUser: vi.fn(),
    } as unknown as UserManager;

    const session = createAuthSession(testActor, {
      authMode: 'oidc',
      userManager: mockUserManager,
    });

    expect(session.getAuthMode()).toBe('oidc');
    await session.signIn();
    expect(signinRedirectMock).toHaveBeenCalledTimes(1);
  });

  it('completes sign in, updates actor and cleans URL parameters', async () => {
    const validToken = createMockJwt({ sub: 'oidc-driver', role: 'driver', exp: Math.floor(Date.now() / 1000) + 3600 });
    const mockUser: Partial<User> = {
      access_token: validToken,
      profile: {
        sub: 'oidc-driver',
        role: 'driver',
        iss: 'https://auth.example.com',
        aud: 'swiftride-web',
        exp: Math.floor(Date.now() / 1000) + 3600,
        iat: Math.floor(Date.now() / 1000),
      },
    };

    const signinCallbackMock = vi.fn().mockResolvedValue(mockUser);
    const mockUserManager = {
      signinRedirect: vi.fn(),
      signinCallback: signinCallbackMock,
      getUser: vi.fn().mockResolvedValue(mockUser),
      removeUser: vi.fn(),
    } as unknown as UserManager;

    const replaceStateMock = vi.fn();
    vi.stubGlobal('window', {
      location: { pathname: '/trips', search: '?code=authcode123&state=state123' },
      history: { replaceState: replaceStateMock },
    });
    vi.stubGlobal('document', { title: 'SwiftRide' });

    const session = createAuthSession(testActor, {
      authMode: 'oidc',
      userManager: mockUserManager,
    });

    await session.completeSignIn();

    expect(signinCallbackMock).toHaveBeenCalledTimes(1);
    const currentActor = session.getActor();
    expect(currentActor.userId).toBe('oidc-driver');
    expect(currentActor.role).toBe('driver');

    // Verifies clean URL was replaced
    expect(replaceStateMock).toHaveBeenCalledWith({}, 'SwiftRide', '/trips');

    const token = await session.getToken();
    expect(token).toBe(validToken);
  });

  it('clears session on signOut', async () => {
    const removeUserMock = vi.fn().mockResolvedValue(undefined);
    const mockUserManager = {
      signinRedirect: vi.fn(),
      signinCallback: vi.fn(),
      getUser: vi.fn(),
      removeUser: removeUserMock,
    } as unknown as UserManager;

    const session = createAuthSession(testActor, {
      authMode: 'oidc',
      userManager: mockUserManager,
    });

    session.setToken('some-token');
    await session.signOut();

    expect(removeUserMock).toHaveBeenCalledTimes(1);
    expect(session.isAuthenticated()).toBe(false);
  });
});
