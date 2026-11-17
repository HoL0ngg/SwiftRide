-- Optional course demo only: apply after matchdb-vehicle-types.sql.
-- Re-run to refresh locations; the matching freshness window still applies.
insert into matchdb.driver_locations (driver_id, latitude, longitude, is_online, updated_at, vehicle_type)
values
    ('demo-driver', 10.772, 106.698, true, now(), 'motorbike'),
    ('demo-driver-2', 10.773, 106.699, true, now(), 'motorbike'),
    ('demo-driver-car', 10.774, 106.700, true, now(), 'car')
on conflict (driver_id) do update set
    latitude = excluded.latitude,
    longitude = excluded.longitude,
    is_online = excluded.is_online,
    updated_at = excluded.updated_at,
    vehicle_type = excluded.vehicle_type;
