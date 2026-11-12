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

function execute(index, code, body, values = new Map(), listen = 'test') {
  const item = collection.item[index];
  const failures = [];
  let next;
  const pm = {
    expect,
    response: { code, json: () => body },
    info: { requestName: item.name },
    variables: {
      get: key => values.get(key),
      set: (key, value) => values.set(key, value),
      replaceIn: () => '33333333-3333-4333-8333-333333333333',
    },
    execution: { setNextRequest: name => { next = name; } },
    test: (_name, action) => { try { action(); } catch (error) { failures.push(error); } },
  };
  for (const event of item.event.filter(event => event.listen === listen)) {
    runInNewContext(event.script.exec.join('\n'), { pm }, { timeout: 1000 });
  }
  return { failures, next };
}

test('all scripts parse and requests stay on Gateway without committed credentials', () => {
  assert.equal(collection.item.length, 19);
  for (const item of collection.item) {
    assert.match(item.request.url, /^\{\{base_url\}\}\/(health|dev\/token|trips|drivers)([/?]|$)/);
    assert.ok(!item.request.header.some(header => header.key === 'X-Internal-Api-Key'));
    for (const event of item.event) new Script(event.script.exec.join('\n'));
    if (item.request.body) JSON.parse(item.request.body.raw);
  }
  assert.deepEqual(collection.variable.map(variable => variable.key), ['base_url']);
  assert.equal(collection.item[6].request.body.raw, collection.item[8].request.body.raw);
  assert.equal(collection.item[6].request.header.at(-1).value, collection.item[8].request.header.at(-1).value);
  assert.equal(collection.item[15].request.header.at(-1).value, collection.item[16].request.header.at(-1).value);
});

test('complete successful response sequence chains tokens, IDs and exact fare', () => {
  const values = new Map();
  execute(0, 200, null, values, 'prerequest');
  const responses = [
    [200, null], [200, { access_token: 'rider-token' }], [200, { access_token: 'driver-token' }],
    [204, null], [403, null], [401, null], [201, trip], [200, trip], [200, trip], [409, null],
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

test('both polling steps repeat transient states then stop at the hard limit', () => {
  for (const [index, counter, status] of [[7, 'quote_polls', 'Requested'], [17, 'paid_polls', 'PaymentPending']]) {
    const values = new Map([['trip_id', trip.id], ['fare_amount', trip.fareAmount], [counter, 0]]);
    for (let attempt = 1; attempt <= 30; attempt++) {
      const result = execute(index, 200, { ...trip, status }, values);
      assert.equal(values.get(counter), attempt);
      assert.equal(result.failures.length, attempt < 30 ? 0 : 1);
      assert.equal(result.next, attempt < 30 ? collection.item[index].name : null);
    }
  }
});

test('replay with wrong ID or changed fare stops the run', () => {
  for (const index of [8, 16]) {
    const values = new Map([['trip_id', trip.id], ['quote_id', trip.quoteId], ['fare_amount', trip.fareAmount]]);
    for (const wrong of [{ ...trip, id: 'wrong-id' }, { ...trip, fareAmount: '50001' }]) {
      const result = execute(index, 200, wrong, values);
      assert.equal(result.failures.length, 1);
      assert.equal(result.next, null);
    }
  }
});

test('failed matching, failed payment and malformed responses cannot pass polling', () => {
  const values = new Map([['trip_id', trip.id], ['fare_amount', trip.fareAmount]]);
  for (const index of [7, 17]) {
    for (const body of [{ ...trip, status: 'Failed' }, null, {}]) {
      const result = execute(index, 200, body, values);
      assert.equal(result.failures.length, 1);
      assert.equal(result.next, null);
    }
  }
});
