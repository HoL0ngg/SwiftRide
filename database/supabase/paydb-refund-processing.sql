-- paydb-refund-processing.sql
-- Provides hardened idempotency, transient recovery, and lease/fencing guarantees for paydb.refunds.
-- Pre-requisites: matchdb-paydb.sql, paydb-refunds.sql, paydb-processing.sql.

-- 1. Ensure swiftride_payment permissions on refunds and refund_ledger_entries
GRANT SELECT, INSERT ON paydb.refunds, paydb.refund_ledger_entries TO swiftride_payment;
GRANT UPDATE (status, gateway_reference, failure_code, settled_at) ON paydb.refunds TO swiftride_payment;
GRANT EXECUTE ON FUNCTION paydb.protect_refund(), paydb.validate_refund_ledger() TO swiftride_payment;

-- 2. Refund processing lease and fencing table
create table if not exists paydb.refund_processing (
    refund_id uuid primary key references paydb.refunds(id) on delete cascade deferrable initially deferred,
    payment_id uuid not null references paydb.payments(id) on delete cascade,
    trip_id uuid not null,
    payment_key text not null,
    reason text,
    attempts_started integer not null default 0,
    owner_id uuid,
    lease_until timestamptz,
    outcome text not null default 'pending' check (outcome in ('pending', 'settled', 'failed')),
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

alter table paydb.refund_processing enable row level security;
do $$
begin
    if not exists (select 1 from pg_policies where schemaname = 'paydb' and tablename = 'refund_processing' and policyname = 'refund_processing_backend') then
        create policy refund_processing_backend on paydb.refund_processing to swiftride_payment using (true) with check (true);
    end if;
end $$;

grant select, insert, update, delete on paydb.refund_processing to swiftride_payment;
