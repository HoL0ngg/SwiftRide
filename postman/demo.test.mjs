import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { Script, runInNewContext } from 'node:vm';
import test from 'node:test';
import { expect } from '../src/WebApp/node_modules/chai/index.js';

const load = name => JSON.parse(readFileSync(new URL(name, import.meta.url)));
const flatten = items => items.flatMap(item => item.item ? flatten(item.item) : [item]);
const smoke = load('./SwiftRide.Gateway.Smoke.postman_collection.json');

test('invalid pickup is tested after driver assignment and before en-route', () => {
  const requests = load('./SwiftRide.Gateway.Demo.postman_collection.json').item[0].item;
  const accept = requests.findIndex(r => r.name === '01 driver-accept');
  const pickup = requests.findIndex(r => r.name === '01 Invalid pickup');
  const enRoute = requests.findIndex(r => r.name === '01 en-route');
  assert.ok(accept >= 0 && accept < pickup && pickup < enRoute,
    'Before acceptance pickup fails authorization (403), so it cannot test invalid state (409)');
});

test('Runner request names are unique so polling cannot jump to a different step', () => {
  const requests = flatten(load('./SwiftRide.Gateway.Demo.postman_collection.json').item);
  const names = requests.map(request => request.name);
  assert.equal(new Set(names).size, names.length);
});

test('smoke waits for an asynchronous driver offer after confirming the quote', () => {
  const wait = smoke.item.find(item => item.name === '11b Wait for driver');
  assert.ok(wait, 'Missing driver polling request');
  const values = new Map([['trip_id', 'trip-1'], ['fare_amount', '50000'], ['offer_polls', 0]]);
  const run = body => {
    let next;
    const failures = [];
    const pm = {
      expect, response: { code: 200, json: () => ({ currency: 'VND', ...body }) }, info: { requestName: wait.name },
      variables: { get: k => values.get(k), set: (k, v) => values.set(k, v) },
      execution: { setNextRequest: v => { next = v; } },
      test: (_, fn) => { try { fn(); } catch (error) { failures.push(error); } },
    };
    for (const e of wait.event.filter(e => e.listen === 'test')) runInNewContext(e.script.exec.join('\n'), { pm });
    return { next, failures };
  };
  assert.equal(run({ id: 'trip-1', status: 'DriverOffered', fareAmount: '50000', offeredDriverId: null }).next, wait.name);
  const ready = run({ id: 'trip-1', status: 'DriverOffered', fareAmount: '50000', offeredDriverId: 'driver-1' });
  assert.deepEqual(ready.failures, []);
  assert.equal(values.get('offered_driver_id'), 'driver-1');
  assert.equal(ready.next, undefined);
  assert.equal(run({ id: 'trip-1', status: 'Failed', fareAmount: '50000' }).failures.length, 1);
});

test('demo includes all scenarios, bounded waits and Gateway-only business requests', () => {
  const demo = load('./SwiftRide.Gateway.Demo.postman_collection.json');
  for (const scenario of ['Success', 'No driver', 'Cancel quoted', 'Cancel offered', 'Driver rejects', 'Rejection limit', 'Payment declined', 'Payment timeout']) {
    assert.ok(demo.item.some(folder => folder.name.includes(scenario)), scenario);
  }
  const requests = flatten(demo.item);
  assert.ok(requests.some(r => r.name.includes('Wait for driver')));
  for (const r of requests) {
    assert.match(r.request.url, /^\{\{base_url\}\}\//);
    for (const e of r.event ?? []) new Script(e.script.exec.join('\n'));
    assert.ok(!JSON.stringify(r).includes('X-Internal-Api-Key'));
  }
  assert.equal(demo.variable.find(v => v.key === 'payment_transport').value, 'Events');
});

test('observer binds all thirteen events and reads with requeue without business queue access', () => {
  const observer = load('./SwiftRide.RabbitMQ.Observe.postman_collection.json');
  const requests = flatten(observer.item);
  assert.equal(requests.filter(r => r.request.url.includes('/bindings/')).length, 13);
  const get = requests.find(r => r.request.url.endsWith('/get'));
  assert.equal(JSON.parse(get.request.body.raw).ackmode, 'ack_requeue_true');
  assert.ok(requests.every(r => !r.request.url.includes('payment-ride-completed')));
  assert.ok(!observer.variable.some(v => /password/i.test(v.key) && v.value));
});

test('RabbitMQ duplicate-message folder republishes the same event and asserts one settlement', () => {
  const collection = load('./SwiftRide.Gateway.Demo.rabbitmqCrashed.json');
  const folder = collection.item.flatMap(item => item.item ?? [])
    .find(item => item.name === '02 Duplicate RideCompleted message');
  assert.ok(folder, 'Missing duplicate-message folder');
  const requests = flatten(folder.item);
  const publish = requests.find(request => request.name === '02 Publish RideCompleted twice');
  const readSettled = requests.find(request => request.name === '02 Verify one PaymentSettled');
  assert.ok(publish && readSettled, 'Missing duplicate-message requests');
  const duplicateScripts = requests.flatMap(request => request.event ?? [])
    .map(event => event.script.exec.join('\n')).join('\n');
  assert.doesNotMatch(duplicateScripts, /pm\.variables\.(?:get|set)\(['"]duplicate_/,
    'Duplicate test state must persist in collection variables between individual sends');
  assert.match(duplicateScripts, /pm\.collectionVariables\.set\('duplicate_event_id'/);
  assert.ok(publish.request.url.includes('/api/exchanges/%2F/SwiftRide.Contracts.V1%3ARideCompletedV1/publish'));
  assert.equal(folder.auth.type, 'basic');
  assert.ok(requests.every(request => request.request.auth === undefined),
    'Requests must inherit Management API credentials from the folder');
  for (const request of requests) {
    for (const event of request.event ?? []) new Script(event.script.exec.join('\n'));
  }
  const preRequest = publish.event.find(event => event.listen === 'prerequest');
  const responseScript = publish.event.find(event => event.listen === 'test');
  assert.ok(preRequest && responseScript, 'Publish request needs preparation and post-response scripts');
  assert.match(preRequest.script.exec.join('\n'), /messageType.*RideCompletedV1/);
  const eventValues = new Map([
    ['duplicate_event_id', 'event-1'],
    ['duplicate_trip_id', 'trip-1'],
    ['duplicate_quote_id', 'quote-1'],
    ['duplicate_run_id', 'run-1'],
    ['duplicate_occurred_at', '2026-11-25T00:00:00.000Z'],
  ]);
  const preparedBodies = [];
  for (let attempt = 0; attempt < 2; attempt++) {
    let updatedBody;
    const pm = {
      collectionVariables: { get: key => eventValues.get(key) },
      request: { body: { update: body => { updatedBody = body; } } },
    };
    runInNewContext(preRequest.script.exec.join('\n'), { pm });
    preparedBodies.push(updatedBody.raw);
  }
  assert.equal(preparedBodies[0], preparedBodies[1], 'Both deliveries must use the exact same event envelope');
  const published = JSON.parse(preparedBodies[0]);
  const envelope = JSON.parse(published.payload);
  assert.equal(envelope.message.eventId, 'event-1');
  assert.equal(envelope.message.tripId, 'trip-1');
  const verifyScript = readSettled.event.find(event => event.listen === 'test').script.exec.join('\n');
  const verifySettlements = messages => {
    const failures = [];
    const pm = {
      expect,
      response: { code: 200, json: () => messages },
      collectionVariables: { get: key => key === 'duplicate_trip_id' ? 'trip-1' : undefined },
      execution: { setNextRequest: () => {} },
      info: { requestName: readSettled.name },
      test: (_name, action) => { try { action(); } catch (error) { failures.push(error); } },
    };
    runInNewContext(verifyScript, { pm });
    return failures;
  };
  const settlement = { payload: JSON.stringify({ message: { tripId: 'trip-1' } }) };
  assert.deepEqual(verifySettlements([settlement]), []);
  assert.equal(verifySettlements([settlement, settlement]).length, 1,
    'Two settlement events for the trip must fail the Postman assertion');
  const values = new Map([['duplicate_publish_attempt', 0]]);
  for (let attempt = 1; attempt <= 2; attempt++) {
    let next;
    const failures = [];
    const pm = {
      expect,
      response: { code: 200, json: () => ({ routed: true }) },
      info: { requestName: publish.name },
      collectionVariables: { get: key => values.get(key), set: (key, value) => values.set(key, value) },
      execution: { setNextRequest: name => { next = name; } },
      test: (_name, action) => { try { action(); } catch (error) { failures.push(error); } },
    };
    for (const event of publish.event.filter(event => event.listen === 'test')) {
      runInNewContext(event.script.exec.join('\n'), { pm });
    }
    assert.deepEqual(failures, [], `publish attempt ${attempt}`);
    assert.equal(next, attempt === 1 ? publish.name : undefined);
  }
  assert.equal(values.get('duplicate_publish_attempt'), 2);
  assert.ok(readSettled.event.some(event =>
    event.listen === 'test' && event.script.exec.join('\n').includes('to.have.lengthOf(1)')),
  'The observer response must assert exactly one PaymentSettled event');
});

test('every demo quote poll accepts a valid quote and fails malformed or exhausted responses', () => {
  const demo = load('./SwiftRide.Gateway.Demo.postman_collection.json');
  for (const item of flatten(demo.item).filter(r => r.name.endsWith('Wait for Quoted'))) {
    const run = (body, initial = 0) => {
      const values = new Map([['trip_id', 'trip-1'], ['quote_polls', initial]]);
      const failures = [];
      let next;
      const pm = {
        expect, response: { code: 200, json: () => body }, info: { requestName: item.name },
        variables: { get: k => values.get(k), set: (k, v) => values.set(k, v) },
        execution: { setNextRequest: value => { next = value; } },
        test: (_, fn) => { try { fn(); } catch (error) { failures.push(error); } },
      };
      for (const e of item.event) runInNewContext(e.script.exec.join('\n'), { pm });
      return { failures, next };
    };
    const quote = { id: 'trip-1', status: 'Quoted', quoteId: 'quote-1', fareAmount: '50000', currency: 'VND' };
    assert.deepEqual(run(quote).failures, [], item.name);
    assert.equal(run({ ...quote, fareAmount: 'NaN' }).failures.length, 1);
    assert.equal(run({ id: 'trip-1', status: 'Requested' }).next, item.name);
    assert.equal(run({ id: 'trip-1', status: 'Requested' }, 59).next, null);
  }
});
