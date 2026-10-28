-- Run as the migration administrator. Fixtures and temporary role membership are rolled back.
begin;
set local statement_timeout = '15s';
set local lock_timeout = '5s';
do $$
begin
    if to_regclass('paydb.refunds') is null or to_regclass('paydb.refund_ledger_entries') is null then
        raise exception 'Refund tables are missing';
    end if;
end;
$$;
grant swiftride_payment to postgres;
set local role swiftride_payment;
do $$
<<verify>>
declare
    payment_id uuid := gen_random_uuid();
    trip_id uuid := gen_random_uuid();
    refund_id uuid := gen_random_uuid();
begin
    insert into paydb.payments(id, trip_id, rider_id, amount, method, idempotency_key, request_fingerprint)
    values (payment_id, trip_id, 'refund-schema-test', 65000, 'wallet', 'trip-' || trip_id || '-payment', repeat('a',64));
    begin
        insert into paydb.refunds(payment_id, amount, idempotency_key, request_fingerprint, reason)
        values (payment_id, 65000, 'payment-' || payment_id || '-refund', repeat('b',64), 'trip_invalidated');
        raise exception 'Unsettled payment was refunded';
    exception when check_violation then null;
    end;
    update paydb.payments set status = 'settled', settled_at = now() where id = payment_id;
    insert into paydb.ledger_entries(payment_id, side, account_code, amount)
    values (payment_id, 'debit', 'mock_gateway', 65000), (payment_id, 'credit', 'ride_revenue', 65000);
    set constraints all immediate;
    set constraints all deferred;
    begin
        insert into paydb.refunds(payment_id, amount, idempotency_key, request_fingerprint, reason)
        values (payment_id, 65001, 'payment-' || payment_id || '-refund', repeat('b',64), 'trip_invalidated');
        raise exception 'Over-refund was accepted';
    exception when check_violation then null;
    end;
    begin
        insert into paydb.refunds(payment_id, amount, idempotency_key, request_fingerprint, reason)
        values (payment_id, 64000, 'payment-' || payment_id || '-refund', repeat('b',64), 'trip_invalidated');
        raise exception 'Partial refund was accepted by full-refund policy';
    exception when check_violation then null;
    end;
    insert into paydb.refunds(id, payment_id, amount, idempotency_key, request_fingerprint, reason)
    values (refund_id, payment_id, 65000, 'payment-' || payment_id || '-refund', repeat('b',64), 'trip_invalidated');
    begin
        insert into paydb.refunds(payment_id, amount, idempotency_key, request_fingerprint, reason)
        values (payment_id, 65000, 'payment-' || payment_id || '-refund', repeat('b',64), 'trip_invalidated');
        raise exception 'Duplicate refund was accepted';
    exception when unique_violation then null;
    end;
    begin
        update paydb.refunds set amount = 1 where id = refund_id;
        raise exception 'Service role changed refund payload';
    exception when insufficient_privilege then null;
    end;
    begin
        update paydb.refunds set status = 'settled', settled_at = now() where id = refund_id;
        set constraints all immediate;
        raise exception 'Refund settled without ledger';
    exception when check_violation then null;
    end;
    set constraints all deferred;
    begin
        update paydb.refunds set status = 'settled', settled_at = now() where id = refund_id;
        insert into paydb.refund_ledger_entries(refund_id, side, account_code, amount)
        values (refund_id, 'debit', 'ride_revenue', 65000), (refund_id, 'credit', 'mock_gateway', 64000);
        set constraints all immediate;
        raise exception 'Unbalanced refund ledger was accepted';
    exception when check_violation then null;
    end;
    set constraints all deferred;
    begin
        update paydb.refunds set status = 'settled', settled_at = now() where id = refund_id;
        insert into paydb.refund_ledger_entries(refund_id, side, account_code, amount)
        values (refund_id, 'debit', 'mock_gateway', 65000), (refund_id, 'credit', 'ride_revenue', 65000);
        set constraints all immediate;
        raise exception 'Refund ledger did not reverse the original charge';
    exception when check_violation then null;
    end;
    set constraints all deferred;
    update paydb.refunds set status = 'failed', failure_code = 'mock_timeout' where id = refund_id;
    set constraints all immediate;
    set constraints all deferred;
    update paydb.refunds set status = 'pending' where id = refund_id;
    update paydb.refunds set status = 'settled', settled_at = now(), failure_code = null where id = refund_id;
    insert into paydb.refund_ledger_entries(refund_id, side, account_code, amount)
    values (refund_id, 'debit', 'ride_revenue', 65000), (refund_id, 'credit', 'mock_gateway', 65000);
    set constraints all immediate;
    if exists (
        select account_code from (
            select account_code, side, amount from paydb.ledger_entries where ledger_entries.payment_id = verify.payment_id
            union all
            select account_code, side, amount from paydb.refund_ledger_entries where refund_ledger_entries.refund_id = verify.refund_id
        ) entries group by account_code
        having sum(case when side = 'debit' then amount else -amount end) <> 0
    ) then
        raise exception 'Full refund did not cancel the original account balances';
    end if;
    begin
        update paydb.refunds set status = 'pending', settled_at = null where id = refund_id;
        raise exception 'Settled refund was changed';
    exception when check_violation then null;
    end;
    begin
        delete from paydb.refund_ledger_entries where refund_ledger_entries.refund_id = verify.refund_id;
        raise exception 'Service role deleted refund ledger';
    exception when insufficient_privilege then null;
    end;
end;
$$;
reset role;
do $$
begin
    if has_table_privilege('anon', 'paydb.refunds', 'select')
       or has_table_privilege('authenticated', 'paydb.refunds', 'insert')
       or has_table_privilege('swiftride_matching', 'paydb.refunds', 'select') then
        raise exception 'Refund privileges leaked';
    end if;
    if exists (select 1 from pg_class where oid in ('paydb.refunds'::regclass,
        'paydb.refund_ledger_entries'::regclass) and not relrowsecurity) then
        raise exception 'Refund RLS is disabled';
    end if;
    begin
        update paydb.refund_ledger_entries set amount = 0;
        raise exception 'Refund ledger mutation was accepted';
    exception when check_violation then null;
    end;
end;
$$;
select 'PASS: full refund, duplicate/over-refund rejection, retry record reuse, balanced reversal, immutable history, isolated roles and RLS' as result;
rollback;
