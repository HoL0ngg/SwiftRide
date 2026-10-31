-- Verification script for paydb-processing.sql
do $$
begin
    assert (select count(*) from information_schema.tables where table_schema = 'paydb' and table_name = 'payment_processing') = 1,
        'payment_processing table missing';
    assert (select count(*) from information_schema.tables where table_schema = 'paydb' and table_name = 'mock_gateway_operations') = 1,
        'mock_gateway_operations table missing';
end;
$$;
