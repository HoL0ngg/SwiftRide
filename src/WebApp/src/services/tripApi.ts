import type { AuthSession } from './auth';
import type {
  CreateTripInput,
  DriverFeedApiResponse,
  TripHistoryPageApiResponse,
  TripResponse,
} from '../types/tripTypes';

export interface TripApi {
  createTrip(input: CreateTripInput, idempotencyKey?: string): Promise<TripResponse>;
  getCurrentTrip(): Promise<TripResponse | null>;
  getTripById(id: string): Promise<TripResponse>;
  getDriverFeed(): Promise<DriverFeedApiResponse>;
  getHistory(cursor?: string, limit?: number): Promise<TripHistoryPageApiResponse>;
  confirmQuote(tripId: string, quoteId: string, idempotencyKey?: string): Promise<TripResponse>;
  cancelTrip(tripId: string, reason?: string, idempotencyKey?: string): Promise<TripResponse>;
  driverAccept(tripId: string, idempotencyKey?: string): Promise<TripResponse>;
  driverReject(tripId: string, idempotencyKey?: string): Promise<TripResponse>;
  driverEnRoute(tripId: string, idempotencyKey?: string): Promise<TripResponse>;
  driverPickup(tripId: string, idempotencyKey?: string): Promise<TripResponse>;
  driverDropoff(tripId: string, idempotencyKey?: string): Promise<TripResponse>;
  updateDriverLocation(
    driverId: string,
    latitude: number,
    longitude: number,
    isOnline: boolean
  ): Promise<void>;
}

export function generateIdempotencyKey(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
    const r = (Math.random() * 16) | 0;
    const v = c === 'x' ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

const ACTION_KEY_PREFIX = 'swiftride_action_key:';

export function getOrCreateActionKey(action: string, entityId?: string, payload?: unknown): string {
  const hash = payload ? JSON.stringify(payload) : '';
  const storageKey = `${ACTION_KEY_PREFIX}${action}:${entityId ?? 'root'}`;
  try {
    if (typeof sessionStorage !== 'undefined') {
      const stored = sessionStorage.getItem(storageKey);
      if (stored) {
        const parsed = JSON.parse(stored) as { hash: string; key: string };
        if (parsed.hash === hash && parsed.key) {
          return parsed.key;
        }
      }
      const newKey = generateIdempotencyKey();
      sessionStorage.setItem(storageKey, JSON.stringify({ hash, key: newKey }));
      return newKey;
    }
  } catch {
    // sessionStorage unavailable or restricted
  }
  return generateIdempotencyKey();
}

export function clearActionKey(action: string, entityId?: string): void {
  const storageKey = `${ACTION_KEY_PREFIX}${action}:${entityId ?? 'root'}`;
  try {
    if (typeof sessionStorage !== 'undefined') {
      sessionStorage.removeItem(storageKey);
    }
  } catch {
    // ignore
  }
}

export function createTripApi(session: AuthSession): TripApi {
  async function request<T>(
    path: string,
    init: RequestInit = {},
    idempotencyKey?: string
  ): Promise<T> {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 20_000);

    try {
      const authHeader = await session.getAuthorizationHeader();
      const headers: Record<string, string> = {
        Authorization: authHeader,
      };
      if (init.headers) {
        if (init.headers instanceof Headers) {
          init.headers.forEach((v, k) => {
            headers[k] = v;
          });
        } else if (Array.isArray(init.headers)) {
          for (const [k, v] of init.headers) {
            headers[k] = v;
          }
        } else {
          Object.assign(headers, init.headers);
        }
      }
      headers.Authorization = authHeader;
      if (idempotencyKey) {
        headers['Idempotency-Key'] = idempotencyKey;
      }
      if (init.body && !headers['Content-Type']) {
        headers['Content-Type'] = 'application/json';
      }

      const response = await fetch(path, {
        ...init,
        headers,
        signal: controller.signal,
      });

      if (response.status === 204) {
        return null as unknown as T;
      }

      if (!response.ok) {
        if (response.status === 401) {
          session.clearToken();
          throw new Error('Lỗi xác thực (HTTP 401). Phiên đăng nhập hết hạn hoặc không hợp lệ.');
        }

        let errorDetail = '';
        try {
          const body = await response.json();
          if (body && typeof body === 'object') {
            const b = body as Record<string, unknown>;
            if (b.detail || b.title) {
              errorDetail = [b.title, b.detail].filter(Boolean).join(': ');
            } else if (b.message) {
              errorDetail = String(b.message);
            }
          }
        } catch {
          // not JSON
        }

        if (response.status === 409) {
          throw new Error(`Xung đột thao tác (HTTP 409). ${errorDetail || 'Trạng thái chuyến đi đã thay đổi.'}`);
        }
        if (response.status === 429) {
          throw new Error('Hệ thống đang bận (HTTP 429). Hãy thử lại sau vài giây.');
        }

        const msg = errorDetail ? ` (${errorDetail})` : '';
        throw new Error(`Yêu cầu thất bại (HTTP ${response.status})${msg}.`);
      }

      const data = await response.json();
      return data as T;
    } catch (err: unknown) {
      if (controller.signal.aborted) {
        throw new Error('Yêu cầu máy chủ quá thời gian chờ (timeout 20s). Hãy thử lại.');
      }
      throw err;
    } finally {
      clearTimeout(timeout);
    }
  }

  async function postTripAction(
    action: string,
    tripId: string,
    payload?: unknown,
    idempotencyKey?: string
  ): Promise<TripResponse> {
    const key = idempotencyKey || getOrCreateActionKey(action, tripId, payload);
    const result = await request<TripResponse>(
      `/trips/${tripId}/${action}`,
      { method: 'POST', body: payload !== undefined ? JSON.stringify(payload) : undefined },
      key
    );
    clearActionKey(action, tripId);
    return result;
  }


  return {
    async createTrip(input: CreateTripInput, idempotencyKey?: string) {
      const key = idempotencyKey || getOrCreateActionKey('create-trip', 'new', input);
      const body = {
        pickup: input.pickup,
        pickupName: input.pickupName ?? '',
        pickupAddress: input.pickupAddress ?? '',
        destination: input.destination,
        destinationName: input.destinationName ?? '',
        destinationAddress: input.destinationAddress ?? '',
        paymentMethod: input.paymentMethod,
        mode: input.mode ?? 'standard',
      };
      const result = await request<TripResponse>('/trips', { method: 'POST', body: JSON.stringify(body) }, key);
      clearActionKey('create-trip', 'new');
      return result;
    },

    async getCurrentTrip() {
      return await request<TripResponse | null>('/trips/current', { method: 'GET' });
    },

    async getTripById(id: string) {
      return await request<TripResponse>(`/trips/${id}`, { method: 'GET' });
    },

    async getDriverFeed() {
      return await request<DriverFeedApiResponse>('/trips/feed', { method: 'GET' });
    },

    async getHistory(cursor?: string, limit?: number) {
      const params = new URLSearchParams();
      if (cursor) params.set('cursor', cursor);
      if (limit) params.set('limit', limit.toString());
      const query = params.toString() ? `?${params.toString()}` : '';
      return await request<TripHistoryPageApiResponse>(`/trips/history${query}`, { method: 'GET' });
    },

    confirmQuote(tripId: string, quoteId: string, idempotencyKey?: string) {
      return postTripAction('confirm-quote', tripId, { quoteId }, idempotencyKey);
    },

    cancelTrip(tripId: string, reason?: string, idempotencyKey?: string) {
      return postTripAction('cancel', tripId, { reason: reason ?? '' }, idempotencyKey);
    },

    driverAccept(tripId: string, idempotencyKey?: string) {
      return postTripAction('driver-accept', tripId, undefined, idempotencyKey);
    },

    driverReject(tripId: string, idempotencyKey?: string) {
      return postTripAction('driver-reject', tripId, undefined, idempotencyKey);
    },

    driverEnRoute(tripId: string, idempotencyKey?: string) {
      return postTripAction('en-route', tripId, undefined, idempotencyKey);
    },

    driverPickup(tripId: string, idempotencyKey?: string) {
      return postTripAction('pickup', tripId, undefined, idempotencyKey);
    },

    driverDropoff(tripId: string, idempotencyKey?: string) {
      return postTripAction('dropoff', tripId, undefined, idempotencyKey);
    },

    async updateDriverLocation(
      driverId: string,
      latitude: number,
      longitude: number,
      isOnline: boolean
    ) {
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), 10_000);
      try {
        const authHeader = await session.getAuthorizationHeader();
        const response = await fetch(`/drivers/${driverId}/location`, {
          method: 'PUT',
          headers: {
            Authorization: authHeader,
            'Content-Type': 'application/json',
          },
          body: JSON.stringify({ latitude, longitude, isOnline }),
          signal: controller.signal,
        });
        if (!response.ok) {
          throw new Error(`Cập nhật vị trí tài xế thất bại (HTTP ${response.status}).`);
        }
      } finally {
        clearTimeout(timeout);
      }
    },
  };
}
