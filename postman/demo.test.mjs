import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { Script, runInNewContext } from 'node:vm';
import test from 'node:test';
import { expect } from '../src/WebApp/node_modules/chai/index.js';

const load = name => JSON.parse(readFileSync(new URL(name, import.meta.url)));
const flatten = items => items.flatMap(item => item.item ? flatten(item.item) : [item]);
const smoke = load('./SwiftRide.Gateway.Smoke.postman_collection.json');

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
