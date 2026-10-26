-- Apply after matchdb-paydb.sql. Full refund only; original charge history stays unchanged.
create table paydb.refunds (
    id uuid primary key default gen_random_uuid(),
    payment_id uuid not null unique references paydb.payments(id),
    amount numeric not null check (amount >= 0 and amount < 1000000000000000000 and amount = trunc(amount)),
    currency text not null default 'VND' check (currency = 'VND'),
    idempotency_key text not null unique,
    request_fingerprint text not null check (request_fingerprint ~ '^[0-9a-f]{64}$'),
    reason text not null check (length(btrim(reason)) between 1 and 500),
    status text not null default 'pending' check (status in ('pending', 'settled', 'failed')),
    gateway_reference text,
    failure_code text,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    settled_at timestamptz,
    check (idempotency_key = 'payment-' || payment_id::text || '-refund'),
    check ((status = 'settled') = (settled_at is not null))
);

create table paydb.refund_ledger_entries (
    refund_id uuid not null references paydb.refunds(id),
    side text not null check (side in ('debit', 'credit')),
    account_code text not null check (length(btrim(account_code)) > 0),
    amount numeric not null check (amount >= 0 and amount < 1000000000000000000 and amount = trunc(amount)),
    currency text not null default 'VND' check (currency = 'VND'),
    created_at timestamptz not null default now(),
    primary key (refund_id, side)
);

create function paydb.protect_refund() returns trigger
language plpgsql set search_path = '' as $$
declare
    payment paydb.payments%rowtype;
begin
    if tg_op = 'UPDATE' then
        if old.status = 'settled' or
           row(new.id, new.payment_id, new.amount, new.currency, new.idempotency_key,
               new.request_fingerprint, new.reason, new.created_at)
           is distinct from
           row(old.id, old.payment_id, old.amount, old.currency, old.idempotency_key,
               old.request_fingerprint, old.reason, old.created_at) then
            raise exception 'Refund payload and settled refunds are immutable' using errcode = '23514';
        end if;
    end if;
    select * into strict payment from paydb.payments where id = new.payment_id for update;
    if payment.status <> 'settled' or new.amount <> payment.amount or new.currency <> payment.currency then
        raise exception 'Full refund requires a settled payment with matching amount and currency'
            using errcode = '23514';
    end if;
    new.updated_at := now();
    return new;
end;
$$;
create trigger refund_immutable before insert or update on paydb.refunds
    for each row execute function paydb.protect_refund();

create trigger refund_ledger_append_only before update or delete or truncate
    on paydb.refund_ledger_entries for each statement execute function paydb.reject_ledger_mutation();

create function paydb.validate_refund_ledger() returns trigger
language plpgsql set search_path = '' as $$
declare
    refund paydb.refunds%rowtype;
    entry_count integer;
    valid_count integer;
    target_id uuid;
begin
    if tg_table_name = 'refunds' then target_id := new.id;
    else target_id := new.refund_id;
    end if;
    select * into strict refund from paydb.refunds where id = target_id for update;
    select count(*), count(*) filter (
        where entry.amount = refund.amount and entry.currency = refund.currency
        and exists (
            select 1 from paydb.ledger_entries original
            where original.payment_id = refund.payment_id and original.side <> entry.side
              and original.account_code = entry.account_code
              and original.amount = entry.amount and original.currency = entry.currency
        ))
    into entry_count, valid_count
    from paydb.refund_ledger_entries entry where entry.refund_id = target_id;
    if (refund.status = 'settled' and (entry_count <> 2 or valid_count <> 2))
       or (refund.status <> 'settled' and entry_count <> 0) then
        raise exception 'Settled refund requires two entries reversing the original charge; other refunds require none'
            using errcode = '23514';
    end if;
    return null;
end;
$$;
create constraint trigger refund_ledger_check after insert or update on paydb.refunds
    deferrable initially deferred for each row execute function paydb.validate_refund_ledger();
create constraint trigger ledger_refund_check after insert on paydb.refund_ledger_entries
    deferrable initially deferred for each row execute function paydb.validate_refund_ledger();

alter table paydb.refunds enable row level security;
alter table paydb.refund_ledger_entries enable row level security;
create policy payment_backend on paydb.refunds to swiftride_payment using (true) with check (true);
create policy payment_backend on paydb.refund_ledger_entries to swiftride_payment using (true) with check (true);
revoke all on paydb.refunds, paydb.refund_ledger_entries from public, anon, authenticated, service_role;
revoke execute on function paydb.protect_refund(), paydb.validate_refund_ledger()
    from public, anon, authenticated, service_role;
grant select, insert on paydb.refunds, paydb.refund_ledger_entries to swiftride_payment;
grant update (status, gateway_reference, failure_code, settled_at) on paydb.refunds to swiftride_payment;
grant execute on function paydb.protect_refund(), paydb.validate_refund_ledger() to swiftride_payment;
