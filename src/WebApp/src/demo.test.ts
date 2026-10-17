import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { DemoState } from './demo';

const storageKey = 'swiftride-demo-v1';
let storage: Map<string, string>;
let api: typeof import('./demo').demoApi;

function snapshot(): DemoState {
  return JSON.parse(storage.get(storageKey)!).state;
}

async function run<T>(operation: Promise<T>): Promise<T> {
  const result = operation.then(value => ({ value }), error => ({ error }));
  await vi.runAllTimersAsync();
  const settled = await result;
  if ('error' in settled) throw settled.error;
  return settled.value;
}

async function offered() {
  await run(api.quote('ben-thanh', 'landmark', 'wallet'));
  await run(api.confirmQuote());
}

async function accepted() {
  await offered();
  await run(api.driverAccept());
}

async function pending() {
  await accepted();
  await run(api.enRoute());
  await run(api.pickup());
  await run(api.dropoff());
}

beforeEach(async () => {
  vi.useFakeTimers();
  vi.resetModules();
  storage = new Map();
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => storage.get(key) ?? null,
    setItem: (key: string, value: string) => storage.set(key, value),
  });
  api = (await import('./demo')).demoApi;
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('mock trip lifecycle', () => {
  it('quotes a positive fare and preserves the agreed fare through payment', async () => {
    await run(api.quote('ben-thanh', 'landmark', 'card'));
    const quote = snapshot().trip!;
    expect(quote.status).toBe('Quoted');
    expect(quote.fare).toBeGreaterThan(0);
    expect(quote.paymentMethod).toBe('card');
    await run(api.confirmQuote());
    expect(snapshot().trip?.status).toBe('DriverOffered');
    await run(api.driverAccept());
    expect(snapshot().trip?.status).toBe('DriverAccepted');
    await run(api.enRoute());
    expect(snapshot().trip?.status).toBe('EnRoute');
    await run(api.pickup());
    expect(snapshot().trip?.status).toBe('PickedUp');
    await run(api.dropoff());
    expect(snapshot().trip?.status).toBe('PaymentPending');
    expect(snapshot().history).toHaveLength(0);
    await run(api.settlePayment());
    expect(snapshot().trip?.status).toBe('Paid');
    expect(snapshot().trip?.fare).toBe(quote.fare);
    expect(snapshot().history).toHaveLength(1);
  });

  it.each(['Quoted', 'DriverOffered'] as const)('allows cancellation from %s', async status => {
    await run(api.quote('ben-thanh', 'airport', 'wallet'));
    if (status === 'DriverOffered') await run(api.confirmQuote());
    await run(api.cancel());
    expect(snapshot().trip?.status).toBe('Cancelled');
    expect(snapshot().history).toHaveLength(1);
  });

  it('stops matching after the fourth rejection', async () => {
    await offered();
    for (let count = 1; count <= 3; count++) {
      await run(api.driverReject());
      expect(snapshot().trip?.status).toBe('DriverOffered');
      expect(snapshot().trip?.rejections).toBe(count);
    }
    await run(api.driverReject());
    expect(snapshot().trip?.status).toBe('Failed');
    expect(snapshot().history).toHaveLength(1);
  });

  it('settles duplicate and concurrent payment requests once', async () => {
    await pending();
    const fare = snapshot().trip?.fare;
    await run(Promise.all([api.settlePayment(), api.settlePayment()]));
    await run(api.settlePayment());
    expect(snapshot().trip?.status).toBe('Paid');
    expect(snapshot().trip?.fare).toBe(fare);
    expect(snapshot().history).toHaveLength(1);
  });

  it('rejects cancellation and a second booking after driver acceptance', async () => {
    await accepted();
    await expect(run(api.cancel())).rejects.toThrow();
    await expect(run(api.quote('ben-thanh', 'airport', 'card'))).rejects.toThrow();
    expect(snapshot().trip?.status).toBe('DriverAccepted');
  });

  it('rejects skipping pickup and payment steps', async () => {
    await offered();
    await expect(run(api.pickup())).rejects.toThrow();
    await expect(run(api.dropoff())).rejects.toThrow();
    await expect(run(api.settlePayment())).rejects.toThrow();
    expect(snapshot().trip?.status).toBe('DriverOffered');
  });

  it.each([
    ['missing', 'landmark'], ['ben-thanh', 'missing'], ['ben-thanh', 'ben-thanh'],
  ])('rejects invalid places %s / %s', async (pickup, destination) => {
    await expect(run(api.quote(pickup, destination, 'wallet'))).rejects.toThrow();
    expect(storage.has(storageKey)).toBe(false);
  });

  it('blocks accepting while offline and allows accepting after going online', async () => {
    await offered();
    await run(api.setOnline(false));
    await expect(run(api.driverAccept())).rejects.toThrow();
    await run(api.setOnline(true));
    await run(api.driverAccept());
    expect(snapshot().trip?.status).toBe('DriverAccepted');
  });

  it('keeps terminal history and availability when preparing a new trip', async () => {
    await pending();
    await run(api.settlePayment());
    const previousId = snapshot().trip?.id;
    await run(api.setOnline(false));
    await run(api.reset());
    expect(snapshot().trip).toBeNull();
    expect(snapshot().online).toBe(false);
    expect(snapshot().history[0].id).toBe(previousId);
    await run(api.setOnline(true));
    await run(api.quote('airport', 'nguyen-hue', 'card'));
    expect(snapshot().trip?.id).not.toBe(previousId);
    expect(snapshot().history[0].id).toBe(previousId);
  });

  it('rejects resetting an active trip', async () => {
    await offered();
    const id = snapshot().trip?.id;
    await expect(run(api.reset())).rejects.toThrow();
    expect(snapshot().trip?.id).toBe(id);
    expect(snapshot().trip?.status).toBe('DriverOffered');
  });

  it('reports no driver while offline and preserves the previous completed trip', async () => {
    await pending();
    await run(api.settlePayment());
    await run(api.setOnline(false));
    const previous = snapshot();
    await expect(run(api.quote('airport', 'nguyen-hue', 'card'))).rejects.toThrow('chưa có tài xế');
    expect(snapshot()).toEqual(previous);
  });
});

describe('persistence', () => {
  it('checks the latest shared trip before applying an action from a stale tab', async () => {
    await offered();
    const shared = snapshot();
    shared.trip!.status = 'DriverAccepted';
    storage.set(storageKey, JSON.stringify({ version: 1, state: shared }));
    await expect(run(api.cancel())).rejects.toThrow();
    expect(snapshot().trip?.status).toBe('DriverAccepted');
  });

  it('restores a valid stored trip', async () => {
    await offered();
    const id = snapshot().trip?.id;
    vi.resetModules();
    api = (await import('./demo')).demoApi;
    await run(api.driverAccept());
    expect(snapshot().trip?.id).toBe(id);
    expect(snapshot().trip?.status).toBe('DriverAccepted');
  });

  it('recovers payment after a tab was interrupted at dropoff', async () => {
    await pending();
    const interrupted = snapshot();
    interrupted.trip!.status = 'DroppedOff';
    storage.set(storageKey, JSON.stringify({ version: 1, state: interrupted }));
    vi.resetModules();
    api = (await import('./demo')).demoApi;
    await run(api.settlePayment());
    expect(snapshot().trip?.status).toBe('Paid');
    expect(snapshot().history).toHaveLength(1);
  });

  it.each(['{broken', '{"version":99}', '{"version":1,"state":{"trip":{},"history":[],"online":true}}'])('recovers from invalid persisted data', async value => {
    storage.set(storageKey, value);
    vi.resetModules();
    api = (await import('./demo')).demoApi;
    await run(api.quote('ben-thanh', 'landmark', 'wallet'));
    expect(snapshot().trip?.status).toBe('Quoted');
    expect(snapshot().history).toEqual([]);
  });

  it('reports unavailable persistence instead of pretending the trip was saved', async () => {
    vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => { throw new Error('QuotaExceeded'); } });
    await expect(run(api.quote('ben-thanh', 'landmark', 'wallet'))).rejects.toThrow('Không thể lưu');
    expect(storage.has(storageKey)).toBe(false);
  });
});
