using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations;

/// <summary>
/// Row-Level Security — บังคับ campaign isolation ที่ชั้น DB อีกชั้น
/// ไม่พึ่ง WHERE ในโค้ดอย่างเดียว (PROJECT-CONTEXT §3 ข้อบังคับสถาปัตยกรรม)
///
/// วิธีใช้: แอปต้องต่อ DB ด้วย role `dndez_app` (ไม่ใช่ owner) แล้วตั้งค่าต่อ request:
///   SET LOCAL app.user_id     = '&lt;user guid&gt;';
///   SET LOCAL app.campaign_id = '&lt;campaign guid&gt;';
/// ถ้าไม่ตั้ง = มองไม่เห็นข้อมูลอะไรเลย (fail closed ไม่ใช่ fail open)
/// </summary>
public partial class RowLevelSecurity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            -- ── role ที่แอปใช้ต่อ (ห้ามเป็น owner เพราะ owner bypass RLS) ──────────
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dndez_app') THEN
                    CREATE ROLE dndez_app LOGIN PASSWORD 'dndez_app';
                END IF;
            END $$;

            GRANT USAGE ON SCHEMA public TO dndez_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO dndez_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO dndez_app;

            -- ── helper: อ่านค่าจาก session setting (ไม่ตั้ง = NULL = ไม่เห็นอะไรเลย) ──
            CREATE OR REPLACE FUNCTION app_current_campaign() RETURNS uuid
                LANGUAGE sql STABLE AS
            $$ SELECT NULLIF(current_setting('app.campaign_id', true), '')::uuid $$;

            CREATE OR REPLACE FUNCTION app_current_user_id() RETURNS uuid
                LANGUAGE sql STABLE AS
            $$ SELECT NULLIF(current_setting('app.user_id', true), '')::uuid $$;

            -- SECURITY DEFINER เพื่อไม่ให้ policy ของ campaign_members วนเรียกตัวเอง
            CREATE OR REPLACE FUNCTION app_is_dm() RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER AS
            $$
                SELECT EXISTS (
                    SELECT 1 FROM campaign_members m
                    WHERE m.campaign_id = app_current_campaign()
                      AND m.user_id     = app_current_user_id()
                      AND m.role        = 'Dm'
                )
            $$;

            -- ⚠️ สำคัญ: ต้องเช็คว่า user เป็นสมาชิกแคมเปญนั้นจริง ไม่ใช่แค่ campaign_id ตรงกับที่ตั้งมา
            -- ถ้าเช็คแค่ค่าที่ตั้ง แอปที่บั๊ก (หรือถูกเจาะ) ตั้ง campaign_id เป็นของคนอื่นก็อ่านได้ทันที
            CREATE OR REPLACE FUNCTION app_is_member() RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER AS
            $$
                SELECT EXISTS (
                    SELECT 1 FROM campaign_members m
                    WHERE m.campaign_id = app_current_campaign()
                      AND m.user_id     = app_current_user_id()
                )
            $$;

            -- ── 1. ทุกตารางที่มีคอลัมน์ campaign_id ใช้ policy มาตรฐานเดียวกัน ──────
            DO $$
            DECLARE t text;
            BEGIN
                FOR t IN
                    SELECT c.table_name
                    FROM information_schema.columns c
                    JOIN information_schema.tables tb
                      ON tb.table_name = c.table_name AND tb.table_schema = c.table_schema
                    WHERE c.table_schema = 'public'
                      AND c.column_name  = 'campaign_id'
                      AND tb.table_type  = 'BASE TABLE'
                LOOP
                    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
                    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
                    EXECUTE format($f$
                        CREATE POLICY campaign_isolation ON %I
                        USING (campaign_id = app_current_campaign() AND app_is_member())
                        WITH CHECK (campaign_id = app_current_campaign() AND app_is_member())
                    $f$, t);
                END LOOP;
            END $$;

            -- character_presets มี campaign_id แต่แถว Global (campaign_id IS NULL) ต้องอ่านได้ทุกคน
            DROP POLICY IF EXISTS campaign_isolation ON character_presets;
            CREATE POLICY preset_visibility ON character_presets
                USING (scope = 'Global' OR (campaign_id = app_current_campaign() AND app_is_member()))
                WITH CHECK (scope = 'Global' OR (campaign_id = app_current_campaign() AND app_is_member()));

            -- ── 2. ตารางลูกของ session — ผูกผ่าน session_id ──────────────────────
            DO $$
            DECLARE t text;
            BEGIN
                FOREACH t IN ARRAY ARRAY[
                    'messages', 'private_messages', 'scenes', 'proposals',
                    'rest_phases', 'recap_snapshots', 'dice_rolls'
                ]
                LOOP
                    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
                    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
                    EXECUTE format($f$
                        CREATE POLICY campaign_isolation ON %I
                        USING (EXISTS (
                            SELECT 1 FROM sessions s
                            WHERE s.id = %I.session_id AND s.campaign_id = app_current_campaign()) AND app_is_member())
                        WITH CHECK (EXISTS (
                            SELECT 1 FROM sessions s
                            WHERE s.id = %I.session_id AND s.campaign_id = app_current_campaign()) AND app_is_member())
                    $f$, t, t, t);
                END LOOP;
            END $$;

            -- ── 3. ตารางลูกของ character ──────────────────────────────────────────
            DO $$
            DECLARE t text;
            BEGIN
                FOREACH t IN ARRAY ARRAY['character_resources', 'death_save_states', 'character_reviews']
                LOOP
                    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
                    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
                    EXECUTE format($f$
                        CREATE POLICY campaign_isolation ON %I
                        USING (EXISTS (
                            SELECT 1 FROM characters c
                            WHERE c.id = %I.character_id AND c.campaign_id = app_current_campaign()) AND app_is_member())
                        WITH CHECK (EXISTS (
                            SELECT 1 FROM characters c
                            WHERE c.id = %I.character_id AND c.campaign_id = app_current_campaign()) AND app_is_member())
                    $f$, t, t, t);
                END LOOP;
            END $$;

            ALTER TABLE player_rest_actions ENABLE ROW LEVEL SECURITY;
            ALTER TABLE player_rest_actions FORCE ROW LEVEL SECURITY;
            CREATE POLICY campaign_isolation ON player_rest_actions
                USING (EXISTS (
                    SELECT 1 FROM rest_phases r JOIN sessions s ON s.id = r.session_id
                    WHERE r.id = player_rest_actions.rest_phase_id
                      AND s.campaign_id = app_current_campaign()) AND app_is_member())
                WITH CHECK (EXISTS (
                    SELECT 1 FROM rest_phases r JOIN sessions s ON s.id = r.session_id
                    WHERE r.id = player_rest_actions.rest_phase_id
                      AND s.campaign_id = app_current_campaign()) AND app_is_member());

            -- ── 4. ⚠️ source import = DM เท่านั้น (เนื้อหามีลิขสิทธิ์ของ DM คนนั้น) ──
            DROP POLICY IF EXISTS campaign_isolation ON source_documents;
            CREATE POLICY dm_only ON source_documents
                USING (campaign_id = app_current_campaign() AND app_is_dm())
                WITH CHECK (campaign_id = app_current_campaign() AND app_is_dm());

            ALTER TABLE raw_chunks ENABLE ROW LEVEL SECURITY;
            ALTER TABLE raw_chunks FORCE ROW LEVEL SECURITY;
            CREATE POLICY dm_only ON raw_chunks
                USING (campaign_id = app_current_campaign() AND app_is_dm())
                WITH CHECK (campaign_id = app_current_campaign() AND app_is_dm());

            -- ── 5. ตารางระดับผู้ใช้ — เห็นเฉพาะของตัวเอง (api_keys สำคัญที่สุด) ─────
            DO $$
            DECLARE t text;
            BEGIN
                FOREACH t IN ARRAY ARRAY['api_keys', 'storage_quotas']
                LOOP
                    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
                    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
                    EXECUTE format($f$
                        CREATE POLICY own_rows_only ON %I
                        USING (user_id = app_current_user_id())
                        WITH CHECK (user_id = app_current_user_id())
                    $f$, t);
                END LOOP;
            END $$;

            -- rules_corpus_entries = SRD 5.2 (CC-BY) อ่านได้ทุกคน ไม่ต้องมี RLS
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            DECLARE t text;
            BEGIN
                FOR t IN
                    SELECT DISTINCT tablename FROM pg_policies WHERE schemaname = 'public'
                LOOP
                    EXECUTE format('ALTER TABLE %I DISABLE ROW LEVEL SECURITY', t);
                END LOOP;
                FOR t IN SELECT DISTINCT tablename FROM pg_policies WHERE schemaname = 'public'
                LOOP
                    EXECUTE format('DROP POLICY IF EXISTS campaign_isolation ON %I', t);
                    EXECUTE format('DROP POLICY IF EXISTS dm_only ON %I', t);
                    EXECUTE format('DROP POLICY IF EXISTS own_rows_only ON %I', t);
                    EXECUTE format('DROP POLICY IF EXISTS preset_visibility ON %I', t);
                END LOOP;
            END $$;

            DROP FUNCTION IF EXISTS app_is_member();
            DROP FUNCTION IF EXISTS app_is_dm();
            DROP FUNCTION IF EXISTS app_current_user_id();
            DROP FUNCTION IF EXISTS app_current_campaign();
            """);
    }
}
