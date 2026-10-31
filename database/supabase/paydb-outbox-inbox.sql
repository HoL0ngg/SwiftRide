-- SwiftRide paydb outbox and inbox migration
create schema if not exists paydb;

create table if not exists paydb.integration_inbox (
    consumer_name varchar(150) not null,
    message_id uuid not null,
    received_at_utc timestamptz not null default now(),
    processed_at_utc timestamptz,
    last_error text,
    primary key (consumer_name, message_id)
);

create table if not exists paydb.integration_outbox (
    message_id uuid primary key,
    event_type varchar(150) not null,
    payload_json text not null,
    created_at_utc timestamptz not null default now(),
    next_attempt_utc timestamptz,
    attempts int not null default 0,
    max_attempts int not null default 10,
    owner uuid,
    lease_until_utc timestamptz,
    published_at_utc timestamptz,
    last_error text,
    requires_action boolean not null default false,
    correlation_id varchar(100) not null,
    causation_id uuid,
    trace_parent varchar(256)
);

create index if not exists idx_paydb_outbox_poll
on paydb.integration_outbox (published_at_utc, requires_action, next_attempt_utc);

grant all on table paydb.integration_inbox to test_owner;
grant all on table paydb.integration_outbox to test_owner;
grant select, insert, update on table paydb.integration_inbox to swiftride_payment;
grant select, insert, update on table paydb.integration_outbox to swiftride_payment;
