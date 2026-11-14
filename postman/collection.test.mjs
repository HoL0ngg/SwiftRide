import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { Script, runInNewContext } from 'node:vm';
import test from 'node:test';
import { expect } from '../src/WebApp/node_modules/chai/index.js';

// shortcut: simulate the pm surface used here; use Postman Runner to verify real HTTP behavior.
const collection = JSON.parse(readFileSync(new URL('./SwiftRide.Gateway.Smoke.postman_collection.json', import.meta.url)));
const trip = {
  id: '11111111-1111-4111-8111-111111111111',
  quoteId: '22222222-2222-4222-8222-222222222222',
  fareAmount: '50000', currency: 'VND', offeredDriverId: 'demo-driver',
  assignedDriverId: 'demo-driver', status: 'Quoted',
};

function execute(index, code, body, values = new Map(), listen = 'test', localValues = new Map(), environmentId = 'local-environment') {
  const item = collection.item[index];
  const failures = [];
  let next;
  const pm = {
    expect,
    response: { code, json: () => body },
    info: { requestName: item.name },
    environment: {
      id: environmentId,
      get: key => values.get(key),
      set: (key, value) => values.set(key, value),
    },
    variables: {
      get: key => localValues.has(key) ? localValues.get(key) : values.get(key),
      set: (key, value) => localValues.set(key, value),
      replaceIn: () => '33333333-3333-4333-8333-333333333333',
    },
    execution: { setNextRequest: name => { next = name; } },
    test: (_name, action) => { try { action(); } catch (error) { failures.push(error); } },
  };
  const events = [...(listen === 'prerequest' ? collection.event ?? [] : []), ...item.event];
  for (const event of events.filter(event => event.listen === listen)) {
    runInNewContext(event.script.exec.join('\n'), { pm }, { timeout: 1000 });
  }
  return { failures, next };
}

test('all scripts parse and requests stay on Gateway without committed credentials', () => {
  assert.equal(collection.item.length, 20);
  for (const item of collection.item) {
    assert.match(item.request.url, /^\{\{base_url\}\}\/(health|dev\/token|trips|drivers)([/?]|$)/);
    assert.ok(!item.request.header.some(header => header.key === 'X-Internal-Api-Key'));
    for (const event of item.event) new Script(event.script.exec.join('\n'));
    if (item.request.body) JSON.parse(item.request.body.raw);
  }
  for (const event of collection.event ?? []) new Script(event.script.exec.join('\n'));
  assert.deepEqual(collection.variable.map(variable => variable.key), ['base_url']);
  assert.equal(collection.item[6].request.body.raw, collection.item[8].request.body.raw);
  assert.equal(collection.item[6].request.header.at(-1).value, collection.item[8].request.header.at(-1).value);
  assert.equal(collection.item[16].request.header.at(-1).value, collection.item[17].request.header.at(-1).value);
  for (const index of [6, 8, 9]) {
    assert.equal(JSON.parse(collection.item[index].request.body.raw).paymentTransport, 'Events');
  }
});

test('complete successful response sequence chains tokens, IDs and exact fare', () => {
  const values = new Map();
  const localValues = new Map();
  execute(0, 200, null, values, 'prerequest', localValues);
  assert.equal(localValues.get('driver_polls'), 0);
  const responses = [
    [200, null], [200, { access_token: 'rider-token' }], [200, { access_token: 'driver-token' }],
    [204, null], [403, null], [401, null], [202, { ...trip, status: 'Requested' }], [200, trip], [200, trip], [409, null],
    [200, { ...trip, status: 'DriverOffered', offeredDriverId: null }],
    [200, { ...trip, status: 'DriverOffered' }], [200, { access_token: 'offered-driver-token' }],
    [200, { ...trip, status: 'DriverAccepted' }], [200, { ...trip, status: 'EnRoute' }],
    [200, { ...trip, status: 'PickedUp' }], [200, { ...trip, status: 'PaymentPending' }],
    [200, { ...trip, status: 'PaymentPending' }], [200, { ...trip, status: 'Paid' }], [204, null],
  ];
  responses.forEach(([code, body], index) => {
    const result = execute(index, code, body, values);
    assert.deepEqual(result.failures, [], collection.item[index].name);
    assert.equal(result.next, undefined);
  });
  assert.equal(values.get('trip_id'), trip.id);
  assert.equal(values.get('quote_id'), trip.quoteId);
  assert.equal(values.get('fare_amount'), '50000');
  assert.equal(values.get('driver_token'), 'offered-driver-token');
  assert.equal(values.get('initial_driver_token'), 'driver-token');
});

test('all three polling steps repeat transient states then stop at the hard limit', () => {
  for (const [index, counter, status] of [[7, 'quote_polls', 'Requested'], [11, 'driver_polls', 'DriverOffered'], [18, 'paid_polls', 'PaymentPending']]) {
    const values = new Map([['trip_id', trip.id], ['quote_id', trip.quoteId], ['fare_amount', trip.fareAmount]]);
    const localValues = new Map();
    for (let attempt = 1; attempt <= 30; attempt++) {
      const result = execute(index, 200, { ...trip, status, offeredDriverId: null }, values, 'test', localValues);
      assert.equal(localValues.get(counter), attempt);
      assert.equal(result.failures.length, attempt < 30 ? 0 : 1);
      assert.equal(result.next, attempt < 30 ? collection.item[index].name : null);
    }
  }
});

test('tokens and trip data survive separate Send executions without running Health first', () => {
  const values = new Map();
  execute(1, 200, null, values, 'prerequest');
  assert.ok(values.get('rider_id'));
  assert.ok(values.get('driver_id'));
  assert.ok(values.get('create_key'));
  assert.ok(values.get('dropoff_key'));
  assert.deepEqual(execute(1, 200, { access_token: 'rider-token' }, values).failures, []);
  assert.deepEqual(execute(2, 200, { access_token: 'driver-token' }, values).failures, []);
  assert.equal(values.get('rider_token'), 'rider-token');
  assert.equal(values.get('initial_driver_token'), 'driver-token');
  assert.equal(values.get('driver_token'), 'driver-token');
  const saved = new Map(values);
  execute(0, 200, null, values, 'prerequest');
  assert.deepEqual(values, saved);
  assert.deepEqual(execute(6, 202, { ...trip, status: 'Requested' }, values).failures, []);
  assert.equal(values.get('trip_id'), trip.id);
  assert.deepEqual(execute(7, 200, trip, values).failures, []);
  assert.equal(values.get('quote_id'), trip.quoteId);
  assert.equal(values.get('fare_amount'), trip.fareAmount);
  assert.deepEqual(execute(11, 200, { ...trip, status: 'DriverOffered' }, values).failures, []);
  assert.equal(values.get('offered_driver_id'), trip.offeredDriverId);
  assert.deepEqual(execute(12, 200, { access_token: 'offered-token' }, values).failures, []);
  assert.equal(values.get('driver_token'), 'offered-token');
  assert.equal(values.get('initial_driver_token'), 'driver-token');
});

test('requests require an active Postman environment', () => {
  assert.throws(() => execute(1, 200, null, new Map(), 'prerequest', new Map(), null), /Select a Postman environment/);
});

test('replay with wrong ID or changed fare stops the run', () => {
  for (const index of [8, 17]) {
    const values = new Map([['trip_id', trip.id], ['quote_id', trip.quoteId], ['fare_amount', trip.fareAmount]]);
    for (const wrong of [{ ...trip, id: 'wrong-id' }, { ...trip, fareAmount: '50001' }]) {
      const result = execute(index, 200, wrong, values);
      assert.equal(result.failures.length, 1);
      assert.equal(result.next, null);
    }
  }
});

test('failed matching, failed payment and malformed responses cannot pass polling', () => {
  const values = new Map([['trip_id', trip.id], ['quote_id', trip.quoteId], ['fare_amount', trip.fareAmount]]);
  for (const index of [7, 11, 18]) {
    for (const body of [{ ...trip, status: 'Failed' }, null, {}]) {
      const result = execute(index, 200, body, values);
      assert.equal(result.failures.length, 1);
      assert.equal(result.next, null);
    }
  }
});

test('event matching creation rejects synchronous REST responses', () => {
  for (const [code, status] of [[201, 'Quoted'], [202, 'Quoted'], [201, 'Requested']]) {
    const result = execute(6, code, { ...trip, status });
    assert.equal(result.failures.length, 1);
    assert.equal(result.next, null);
  }
});

test('Create trip accepts a known replay without changing the saved trip or idempotency key', () => {
  const values = new Map([['trip_id', trip.id], ['create_key', 'original-key']]);
  for (const status of ['Quoted', 'DriverOffered', 'PaymentPending', 'Paid']) {
    const result = execute(6, 200, { ...trip, status }, values);
    assert.deepEqual(result.failures, []);
    assert.equal(result.next, undefined);
    assert.equal(values.get('trip_id'), trip.id);
    assert.equal(values.get('create_key'), 'original-key');
  }
});

test('Create trip rejects unknown replay identities and malformed replay responses', () => {
  for (const [values, body] of [
    [new Map(), trip],
    [new Map([['trip_id', 'another-trip']]), trip],
    [new Map([['trip_id', trip.id]]), { ...trip, status: 'Unknown' }],
    [new Map([['trip_id', trip.id]]), null],
  ]) {
    const result = execute(6, 200, body, values);
    assert.equal(result.failures.length, 1);
    assert.equal(result.next, null);
  }
});

test('driver polling preserves the agreed quote and waits before using a driver token', () => {
  const values = new Map([['trip_id', trip.id], ['quote_id', trip.quoteId], ['fare_amount', trip.fareAmount], ['driver_polls', 0]]);
  const pending = execute(11, 200, { ...trip, status: 'DriverOffered', offeredDriverId: null }, values);
  assert.deepEqual(pending.failures, []);
  assert.equal(pending.next, collection.item[11].name);
  assert.equal(values.has('offered_driver_id'), false);
  for (const wrong of [{ ...trip, id: 'wrong' }, { ...trip, fareAmount: '50001' }, { ...trip, quoteId: 'wrong' }, { ...trip, offeredDriverId: '' }, { ...trip, offeredDriverId: undefined }]) {
    const result = execute(11, 200, { ...wrong, status: 'DriverOffered' }, values);
    assert.equal(result.failures.length, 1);
    assert.equal(result.next, null);
  }
  const ready = execute(11, 200, { ...trip, status: 'DriverOffered' }, values);
  assert.deepEqual(ready.failures, []);
  assert.equal(ready.next, undefined);
  assert.equal(values.get('offered_driver_id'), trip.offeredDriverId);
});
