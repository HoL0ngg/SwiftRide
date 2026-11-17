-- Apply to an existing matchdb before deploying vehicle selection.
-- Existing drivers and quotes retain the original motorbike pricing category.
begin;
alter table matchdb.driver_locations
    add column if not exists vehicle_type text not null default 'motorbike'
    check (vehicle_type in ('motorbike', 'car'));
alter table matchdb.quotes
    add column if not exists vehicle_type text not null default 'motorbike'
    check (vehicle_type in ('motorbike', 'car'));
commit;
