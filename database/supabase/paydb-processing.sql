-- Apply after matchdb-paydb.sql.
-- Provides technical state tracking and mock gateway operation idempotency for payment recovery.

create table paydb.payment_processing (
    payment_id uuid primary key references paydb.payments(id) on delete cascade deferrable initially deferred,
    request_hash text not null,
    version integer not null default 2,
    driver_id text not null,
    attempts_started integer not null default 0,
    owner_id uuid,
    lease_until timestamptz,
    outcome text not null default 'pending' check (outcome in ('pending', 'settled', 'failed')),
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create table paydb.mock_gateway_operations (
    id uuid primary key default gen_random_uuid(),
    operation text not null check (operation in ('charge', 'refund')),
    idempotency_key text not null,
    request_hash text not null,
    outcome text not null check (outcome in ('succeeded', 'declined', 'transient')),
    reference text,
    failure_code text,
    created_at timestamptz not null default now(),
    unique (operation, idempotency_key)
);

alter table paydb.payment_processing enable row level security;
alter table paydb.mock_gateway_operations enable row level security;

create policy payment_processing_backend on paydb.payment_processing to swiftride_payment using (true) with check (true);
create policy mock_gateway_backend on paydb.mock_gateway_operations to swiftride_payment using (true) with check (true);

grant select, insert, update, delete on paydb.payment_processing to swiftride_payment;
grant select, insert on paydb.mock_gateway_operations to swiftride_payment;
