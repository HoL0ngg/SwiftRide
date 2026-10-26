-- SwiftRide MVP: apply once through Supabase apply_migration.
-- Backend-only schemas; login credentials are provisioned separately.
create schema matchdb;
create schema paydb;
create role swiftride_matching nologin;
create role swiftride_payment nologin;
revoke all on schema matchdb, paydb from public, anon, authenticated, service_role;
grant usage on schema matchdb to swiftride_matching;
grant usage on schema paydb to swiftride_payment;

create table matchdb.driver_locations (
    driver_id text primary key check (length(btrim(driver_id)) > 0),
    latitude double precision not null check (latitude between -90 and 90),
    longitude double precision not null check (longitude between -180 and 180),
    is_online boolean not null default false,
    updated_at timestamptz not null default now()
);
-- shortcut: scan online drivers and calculate distance in Matching, add PostGIS when demo data grows.
create index driver_locations_online_updated_idx
    on matchdb.driver_locations(updated_at) where is_online;

create table matchdb.quotes (
    id uuid primary key default gen_random_uuid(),
    distance_km numeric not null check (distance_km >= 0 and distance_km < 'Infinity'::numeric),
    estimated_minutes numeric not null check (estimated_minutes >= 0 and estimated_minutes < 'Infinity'::numeric),
    cost_per_km numeric not null check (cost_per_km >= 0 and cost_per_km < 'Infinity'::numeric),
    cost_per_minute numeric not null check (cost_per_minute >= 0 and cost_per_minute < 'Infinity'::numeric),
    surge numeric not null check (surge >= 1 and surge < 'Infinity'::numeric),
    toll numeric not null check (toll >= 0 and toll < 'Infinity'::numeric),
    promotion_factor numeric not null check (promotion_factor > 0 and promotion_factor <= 1),
    tax numeric not null check (tax >= 0 and tax < 'Infinity'::numeric),
    amount numeric not null check (amount >= 0 and amount < 1000000000000000000 and amount = trunc(amount)),
    currency text not null default 'VND' check (currency = 'VND'),
    created_at timestamptz not null default now()
);

create table paydb.payments (
    id uuid primary key default gen_random_uuid(),
    trip_id uuid not null unique,
    rider_id text not null check (length(btrim(rider_id)) > 0),
    amount numeric not null check (amount >= 0 and amount < 1000000000000000000 and amount = trunc(amount)),
    currency text not null default 'VND' check (currency = 'VND'),
    method text not null check (method in ('card', 'wallet')),
    idempotency_key text not null unique,
    request_fingerprint text not null check (request_fingerprint ~ '^[0-9a-f]{64}$'),
    status text not null default 'pending' check (status in ('pending', 'settled', 'failed')),
    gateway_reference text,
    failure_code text,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    settled_at timestamptz,
    check (idempotency_key = 'trip-' || trip_id::text || '-payment'),
    check ((status = 'settled') = (settled_at is not null))
);

-- One payment is one journal in this MVP; refunds get their own journals later.
create table paydb.ledger_entries (
    payment_id uuid not null references paydb.payments(id),
    side text not null check (side in ('debit', 'credit')),
    account_code text not null check (length(btrim(account_code)) > 0),
    amount numeric not null check (amount >= 0 and amount < 1000000000000000000 and amount = trunc(amount)),
    currency text not null default 'VND' check (currency = 'VND'),
    created_at timestamptz not null default now(),
    primary key (payment_id, side)
);

create function paydb.reject_ledger_mutation() returns trigger
language plpgsql set search_path = '' as $$
begin
    raise exception 'Ledger entries are append-only' using errcode = '23514';
end;
$$;
create trigger ledger_append_only before update or delete or truncate
    on paydb.ledger_entries for each statement execute function paydb.reject_ledger_mutation();

create function paydb.protect_payment() returns trigger
language plpgsql set search_path = '' as $$
begin
    if old.status = 'settled' or
       row(new.id, new.trip_id, new.rider_id, new.amount, new.currency, new.method,
           new.idempotency_key, new.request_fingerprint, new.created_at)
       is distinct from
       row(old.id, old.trip_id, old.rider_id, old.amount, old.currency, old.method,
           old.idempotency_key, old.request_fingerprint, old.created_at) then
        raise exception 'Payment payload and settled payments are immutable' using errcode = '23514';
    end if;
    new.updated_at := now();
    return new;
end;
$$;
create trigger payment_immutable before update on paydb.payments
    for each row execute function paydb.protect_payment();

-- Deferred checks allow the status and both entries to be written in one transaction.
create function paydb.validate_payment_ledger() returns trigger
language plpgsql set search_path = '' as $$
declare
    payment paydb.payments%rowtype;
    entry_count integer;
    valid_count integer;
    account_count integer;
    target_id uuid;
begin
    if tg_table_name = 'payments' then target_id := new.id;
    else target_id := new.payment_id;
    end if;
    select * into strict payment from paydb.payments where id = target_id for update;
    select count(*), count(*) filter (where amount = payment.amount and currency = payment.currency),
           count(distinct account_code)
    into entry_count, valid_count, account_count
    from paydb.ledger_entries where payment_id = target_id;
    if (payment.status = 'settled' and (entry_count <> 2 or valid_count <> 2 or account_count <> 2))
       or (payment.status <> 'settled' and entry_count <> 0) then
        raise exception 'Settled payment requires two balanced entries on distinct accounts; other payments require none'
            using errcode = '23514';
    end if;
    return null;
end;
$$;
create constraint trigger payment_ledger_check after insert or update on paydb.payments
    deferrable initially deferred for each row execute function paydb.validate_payment_ledger();
create constraint trigger ledger_payment_check after insert on paydb.ledger_entries
    deferrable initially deferred for each row execute function paydb.validate_payment_ledger();

alter table matchdb.driver_locations enable row level security;
alter table matchdb.quotes enable row level security;
alter table paydb.payments enable row level security;
alter table paydb.ledger_entries enable row level security;
create policy matching_backend on matchdb.driver_locations to swiftride_matching using (true) with check (true);
create policy matching_backend on matchdb.quotes to swiftride_matching using (true) with check (true);
create policy payment_backend on paydb.payments to swiftride_payment using (true) with check (true);
create policy payment_backend on paydb.ledger_entries to swiftride_payment using (true) with check (true);
revoke all on all tables in schema matchdb, paydb from public, anon, authenticated, service_role;
revoke execute on all functions in schema paydb from public, anon, authenticated, service_role;
grant select, insert, update on matchdb.driver_locations to swiftride_matching;
grant select, insert on matchdb.quotes to swiftride_matching;
grant select, insert on paydb.payments, paydb.ledger_entries to swiftride_payment;
grant update (status, gateway_reference, failure_code, settled_at) on paydb.payments to swiftride_payment;
grant execute on all functions in schema paydb to swiftride_payment;
alter default privileges in schema matchdb, paydb revoke all on tables from public, anon, authenticated, service_role;
alter default privileges in schema matchdb, paydb revoke execute on functions from public, anon, authenticated, service_role;
