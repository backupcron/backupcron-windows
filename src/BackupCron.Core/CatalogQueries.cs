namespace BackupCron.Core;

internal static class CatalogQueries
{
    public const string Postgres = """
    SELECT json_build_object('sizeBytes', pg_database_size(current_database()), 'tables', COALESCE(json_agg(
      json_build_object('schema', n.nspname, 'name', c.relname,
        'objectType', CASE WHEN c.relkind = 'v' THEN 'view' ELSE 'table' END,
        'sizeBytes', CASE WHEN c.relkind IN ('v', 'p') THEN 0 ELSE pg_total_relation_size(c.oid) END,
        'parentTables', COALESCE((SELECT json_agg(json_build_object('schema', pn.nspname, 'name', pc.relname))
          FROM pg_inherits i JOIN pg_class pc ON pc.oid = i.inhparent JOIN pg_namespace pn ON pn.oid = pc.relnamespace
          WHERE i.inhrelid = c.oid), '[]'::json),
        'requiredTables', COALESCE((SELECT json_agg(json_build_object('schema', dn.nspname, 'name', dc.relname))
          FROM (SELECT confrelid AS oid FROM pg_constraint WHERE conrelid = c.oid AND contype = 'f'
            UNION SELECT d.refobjid FROM pg_rewrite rw JOIN pg_depend d ON d.objid = rw.oid
            WHERE rw.ev_class = c.oid AND d.classid = 'pg_rewrite'::regclass
              AND d.refclassid = 'pg_class'::regclass AND d.refobjid <> c.oid) refs
          JOIN pg_class dc ON dc.oid = refs.oid JOIN pg_namespace dn ON dn.oid = dc.relnamespace), '[]'::json))
      ORDER BY n.nspname, c.relname), '[]'::json))
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.relkind IN ('r', 'p', 'v', 'm') AND n.nspname <> 'information_schema' AND n.nspname !~ '^pg_'
    """;
    public const string MySql = """
    SELECT JSON_OBJECT('sizeBytes', (SELECT SUM(DATA_LENGTH + INDEX_LENGTH) FROM information_schema.TABLES
      WHERE TABLE_SCHEMA = DATABASE()), 'tables', COALESCE(JSON_ARRAYAGG(JSON_OBJECT(
      'schema', t.TABLE_SCHEMA, 'name', t.TABLE_NAME, 'objectType', IF(t.TABLE_TYPE = 'VIEW', 'view', 'table'),
      'sizeBytes', IF(t.TABLE_TYPE = 'VIEW', 0, t.DATA_LENGTH + t.INDEX_LENGTH), 'parentTables', JSON_ARRAY(),
      'requiredTables', JSON_MERGE_PRESERVE(
        COALESCE((SELECT JSON_ARRAYAGG(JSON_OBJECT('schema', k.REFERENCED_TABLE_SCHEMA, 'name', k.REFERENCED_TABLE_NAME))
          FROM information_schema.KEY_COLUMN_USAGE k WHERE k.TABLE_SCHEMA = t.TABLE_SCHEMA
          AND k.TABLE_NAME = t.TABLE_NAME AND k.REFERENCED_TABLE_NAME IS NOT NULL), JSON_ARRAY()),
        COALESCE((SELECT JSON_ARRAYAGG(JSON_OBJECT('schema', v.TABLE_SCHEMA, 'name', v.TABLE_NAME))
          FROM information_schema.VIEW_TABLE_USAGE v WHERE v.VIEW_SCHEMA = t.TABLE_SCHEMA
          AND v.VIEW_NAME = t.TABLE_NAME), JSON_ARRAY())))), JSON_ARRAY()))
    FROM information_schema.TABLES t WHERE t.TABLE_SCHEMA = DATABASE()
    """;
}
