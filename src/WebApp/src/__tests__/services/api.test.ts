import { describe, expect, it } from 'vitest';
import { createSwiftRideClient } from '../../services/api';

describe('createSwiftRideClient', () => {
  it('creates client with session, api, and store tied to initial actor', () => {
    const client = createSwiftRideClient({ userId: 'demo-rider', role: 'rider' });
    expect(client.session).toBeDefined();
    expect(client.api).toBeDefined();
    expect(client.store).toBeDefined();
    expect(client.session.getActor()).toEqual({ userId: 'demo-rider', role: 'rider' });
    expect(client.store.getState().actor).toEqual({ userId: 'demo-rider', role: 'rider' });
  });
});
