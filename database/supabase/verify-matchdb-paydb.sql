-- All fixtures are rolled back; run as the migration administrator.
begin;
-- Temporary membership is rolled back together with fixtures.
grant swiftride_matching, swiftride_payment to postgres;
set local statement_timeout = '15s';
set local lock_timeout = '5s';
set local role swiftride_matching;
insert into matchdb.driver_locations(driver_id, latitude, longitude, is_online)
values ('__swiftride_schema_check__', 10.759, 106.682, true);
insert into matchdb.driver_locations(driver_id, latitude, longitude, is_online)
values ('__swiftride_schema_check__', 10.760, 106.683, true)
on conflict (driver_id) do update set latitude = excluded.latitude, longitude = excluded.longitude;
do $$
begin
    begin
        insert into matchdb.driver_locations(driver_id, latitude, longitude) values ('invalid', 91, 0);
        raise exception 'Invalid latitude was accepted';
    exception when check_violation then null;
    end;
    if has_schema_privilege(current_user, 'paydb', 'usage') then
        raise exception 'Matching can access Payment schema';
    end if;
end;
$$;
insert into matchdb.quotes(distance_km, estimated_minutes, cost_per_km, cost_per_minute,
    surge, toll, promotion_factor, tax, amount)
values (5, 15, 10000, 1000, 1, 0, 1, 0, 65000);
reset role;
set local role swiftride_payment;
do $$
<<verify>>
declare
    payment_id uuid := gen_random_uuid();
    trip_id uuid := gen_random_uuid();
begin
    if has_schema_privilege(current_user, 'matchdb', 'usage') then
        raise exception 'Payment can access Matching schema';
    end if;
    insert into paydb.payments(id, trip_id, rider_id, amount, method, idempotency_key, request_fingerprint)
    values (payment_id, trip_id, 'schema-test', 65000, 'wallet', 'trip-' || trip_id || '-payment', repeat('a',64));
    begin
        insert into paydb.payments(trip_id, rider_id, amount, method, idempotency_key, request_fingerprint)
        values (trip_id, 'schema-test', 65000, 'wallet', 'trip-' || trip_id || '-payment', repeat('a',64));
        raise exception 'Duplicate payment was accepted';
    exception when unique_violation then null;
    end;
    begin
        update paydb.payments set status = 'settled', settled_at = now() where id = payment_id;
        set constraints all immediate;
        raise exception 'Settlement without ledger was accepted';
    exception when check_violation then null;
    end;
    set constraints all deferred;
    begin
        update paydb.payments set status = 'settled', settled_at = now() where id = payment_id;
        insert into paydb.ledger_entries(payment_id, side, account_code, amount)
        values (payment_id, 'debit', 'mock_gateway', 65000), (payment_id, 'credit', 'ride_revenue', 64000);
        set constraints all immediate;
        raise exception 'Unbalanced ledger was accepted';
    exception when check_violation then null;
    end;
    set constraints all deferred;
    update paydb.payments set status = 'settled', settled_at = now() where id = payment_id;
    insert into paydb.ledger_entries(payment_id, side, account_code, amount)
    values (payment_id, 'debit', 'mock_gateway', 65000), (payment_id, 'credit', 'ride_revenue', 65000);
    set constraints all immediate;
    begin
        update paydb.payments set status = 'pending', settled_at = null where id = payment_id;
        raise exception 'Settled payment was changed';
    exception when check_violation then null;
    end;
    begin
        delete from paydb.ledger_entries where ledger_entries.payment_id = verify.payment_id;
        raise exception 'Payment role deleted ledger entries';
    exception when insufficient_privilege then null;
    end;
end;
$$;
reset role;
do $$
begin
    if has_schema_privilege('anon', 'matchdb', 'usage')
       or has_schema_privilege('authenticated', 'paydb', 'usage') then
        raise exception 'Client roles have schema access';
    end if;
    begin
        update paydb.ledger_entries set amount = 0;
        raise exception 'Ledger update was accepted';
    exception when check_violation then null;
    end;
end;
$$;
select 'PASS: location upsert/validation, quote insert, isolated roles, duplicate payment rejection, atomic balanced settlement, immutable ledger' as result;
rollback;
