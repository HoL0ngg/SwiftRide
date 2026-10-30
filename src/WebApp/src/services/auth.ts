import { UserManager, type UserManagerSettings } from 'oidc-client-ts';

export type ActorRole = 'rider' | 'driver';

export interface Actor {
  userId: string;
  role: ActorRole;
}

export interface JwtPayload {
  sub?: string;
  role?: string;
  exp?: number;
  iss?: string;
  aud?: string;
  [key: string]: unknown;
}

export function base64UrlDecode(str: string): string {
  let base64 = str.replace(/-/g, '+').replace(/_/g, '/');
  while (base64.length % 4 !== 0) {
    base64 += '=';
  }

  if (typeof atob === 'function') {
    const raw = atob(base64);
    return decodeURIComponent(
      Array.prototype.map.call(raw, (c: string) => '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2)).join('')
    );
  }

  const g = globalThis as unknown as {
    Buffer?: { from(data: string, encoding: string): { toString(enc: string): string } };
  };
  if (g.Buffer) {
    return g.Buffer.from(base64, 'base64').toString('utf8');
  }

  return '';
}

export function parseJwtPayload(token: string): JwtPayload | null {
  try {
    const parts = token.split('.');
    if (parts.length < 2) return null;
    const json = base64UrlDecode(parts[1]);
    if (!json) return null;
    return JSON.parse(json) as JwtPayload;
  } catch {
    return null;
  }
}

export function isTokenExpiredOrExpiring(token: string, bufferSeconds = 30): boolean {
  const payload = parseJwtPayload(token);
  if (!payload || typeof payload.exp !== 'number') {
    return false;
  }
  const now = Math.floor(Date.now() / 1000);
  return payload.exp - now <= bufferSeconds;
}

export interface AuthSessionOptions {
  authMode?: 'dev' | 'oidc';
  userManager?: UserManager;
  oidcSettings?: Partial<UserManagerSettings>;
}

export interface AuthSession {
  getActor(): Actor;
  setActor(actor: Actor): void;
  getToken(): Promise<string>;
  setToken(token: string | null): void;
  clearToken(): void;
  getAuthorizationHeader(): Promise<string>;
  signIn(): Promise<void>;
  completeSignIn(): Promise<void>;
  signOut(): Promise<void>;
  isAuthenticated(): boolean;
  getAuthMode(): 'dev' | 'oidc';
  onAuthStateChanged(listener: (isAuthenticated: boolean) => void): () => void;
}

export function createAuthSession(initialActor: Actor, options?: AuthSessionOptions): AuthSession {
  let currentActor: Actor = { ...initialActor };
  const tokenCache = new Map<string, string>();
  const listeners = new Set<(isAuthenticated: boolean) => void>();

  const authMode: 'dev' | 'oidc' =
    options?.authMode ??
    (import.meta.env.VITE_AUTH_MODE === 'oidc' ? 'oidc' : 'dev');

  function notify(isAuth: boolean) {
    listeners.forEach(fn => {
      try {
        fn(isAuth);
      } catch {
        // ignore listener errors
      }
    });
  }

  function actorKey(actor: Actor): string {
    return `${actor.role}:${actor.userId}`;
  }

  let userManager: UserManager | null = options?.userManager ?? null;
  if (!userManager && authMode === 'oidc') {
    const authority = (import.meta.env.VITE_OIDC_AUTHORITY as string | undefined) || '';
    const clientId = (import.meta.env.VITE_OIDC_CLIENT_ID as string | undefined) || 'swiftride-web';
    const redirectUri =
      (import.meta.env.VITE_OIDC_REDIRECT_URI as string | undefined) ||
      (typeof window !== 'undefined' ? window.location.origin : 'http://127.0.0.1:5173');
    const scope = (import.meta.env.VITE_OIDC_SCOPE as string | undefined) || 'openid profile role';

    if (authority) {
      userManager = new UserManager({
        authority,
        client_id: clientId,
        redirect_uri: redirectUri,
        response_type: 'code',
        scope,
        post_logout_redirect_uri: redirectUri,
        filterProtocolClaims: true,
        loadUserInfo: false,
        ...options?.oidcSettings,
      });
    }
  }

  async function fetchDevToken(actor: Actor): Promise<string> {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 20_000);
    try {
      const url = `/dev/token?userId=${encodeURIComponent(actor.userId)}&role=${encodeURIComponent(actor.role)}`;
      const response = await fetch(url, {
        method: 'POST',
        signal: controller.signal,
      });

      if (!response.ok) {
        throw new Error(`Không lấy được development token (HTTP ${response.status}).`);
      }

      const data = (await response.json()) as { access_token?: unknown } | null;
      if (typeof data?.access_token !== 'string' || !data.access_token.trim()) {
        throw new Error('Dữ liệu token không hợp lệ.');
      }
      return data.access_token;
    } catch (err: unknown) {
      if (controller.signal.aborted) {
        throw new Error('Yêu cầu lấy token quá thời gian chờ (timeout 20s).');
      }
      throw err;
    } finally {
      clearTimeout(timeout);
    }
  }

  const session: AuthSession = {
    getActor() {
      return { ...currentActor };
    },
    setActor(actor: Actor) {
      currentActor = { ...actor };
    },
    getAuthMode() {
      return authMode;
    },
    onAuthStateChanged(listener: (isAuthenticated: boolean) => void) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    isAuthenticated() {
      const key = actorKey(currentActor);
      const token = tokenCache.get(key);
      if (!token) return false;
      return !isTokenExpiredOrExpiring(token, 0);
    },
    async getToken() {
      const key = actorKey(currentActor);
      let existing = tokenCache.get(key);

      // If token expired or expiring soon, drop from cache
      if (existing && isTokenExpiredOrExpiring(existing, 30)) {
        tokenCache.delete(key);
        existing = undefined;
      }

      if (existing) {
        return existing;
      }

      if (authMode === 'oidc') {
        if (!userManager) {
          throw new Error('OIDC UserManager chưa được khởi tạo hoặc thiếu cấu hình VITE_OIDC_AUTHORITY.');
        }
        const user = await userManager.getUser();
        if (user && user.access_token && !user.expired && !isTokenExpiredOrExpiring(user.access_token, 30)) {
          tokenCache.set(key, user.access_token);
          notify(true);
          return user.access_token;
        }
        notify(false);
        throw new Error('Phiên đăng nhập OIDC đã hết hạn hoặc chưa đăng nhập. Vui lòng đăng nhập lại.');
      }

      // Dev mode
      if (!import.meta.env.DEV) {
        throw new Error('Vui lòng cung cấp JWT token hợp lệ để kết nối hệ thống.');
      }

      const token = await fetchDevToken(currentActor);
      tokenCache.set(key, token);
      notify(true);
      return token;
    },
    setToken(token: string | null) {
      const key = actorKey(currentActor);
      if (token) {
        tokenCache.set(key, token);
        notify(true);
      } else {
        tokenCache.delete(key);
        notify(false);
      }
    },
    clearToken() {
      const key = actorKey(currentActor);
      tokenCache.delete(key);
      notify(false);
    },
    async getAuthorizationHeader() {
      const token = await this.getToken();
      return `Bearer ${token}`;
    },
    async signIn() {
      if (authMode === 'oidc') {
        if (!userManager) {
          throw new Error('OIDC UserManager chưa được cấu hình.');
        }
        await userManager.signinRedirect();
        return;
      }

      // Dev mode: fetch and warm cache
      await this.getToken();
    },
    async completeSignIn() {
      if (authMode === 'oidc') {
        if (!userManager) {
          throw new Error('OIDC UserManager chưa được cấu hình.');
        }
        const user = await userManager.signinCallback();
        if (user) {
          if (user.profile) {
            const role = (user.profile.role as ActorRole) || currentActor.role || 'rider';
            const sub = user.profile.sub || currentActor.userId;
            currentActor = { userId: sub, role };
          }
          if (user.access_token) {
            tokenCache.set(actorKey(currentActor), user.access_token);
          }
        }
        if (typeof window !== 'undefined' && window.history?.replaceState) {
          const cleanUrl = window.location.pathname;
          const title = typeof document !== 'undefined' ? document.title : '';
          window.history.replaceState({}, title, cleanUrl);
        }
        notify(true);
        return;
      }

      // Dev mode is no-op
      return Promise.resolve();
    },
    async signOut() {
      const key = actorKey(currentActor);
      tokenCache.delete(key);
      if (authMode === 'oidc' && userManager) {
        try {
          await userManager.removeUser();
        } catch {
          // ignore
        }
      }
      notify(false);
    },
  };

  return session;
}
