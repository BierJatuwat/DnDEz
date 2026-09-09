using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 1 — RLS ของตารางใหม่ `action_catalog_entries` (คลังกลไก)
///
/// ⚠️ **ตารางนี้ไม่ผูกแคมเปญ** (ไม่มี `campaign_id` เหมือน `rules_corpus_entries`) — RLS ที่นี่จึง
/// **ไม่ได้มีไว้แยก tenant** เพราะไม่มี tenant ให้แยก แต่มีไว้เพื่อ **integrity**: คลังกลไกคือชุด
/// ข้อมูลที่ rules engine เชื่อถือเป็นฐานคำนวณ (ระยะ/ราคา/รูปทอย) ถ้า role แอปเขียนทับได้ ผู้เล่น
/// จะถูกกฎที่ผิดโดยไม่มีใครรู้ตัว → กติกาที่งานนี้ล็อก: **`dndez_app` อ่านได้อย่างเดียว เขียนไม่ได้เลย
/// ทุกกรณี** การเพิ่ม/แก้คลังทำผ่าน migration (role เจ้าของ `dndez`) เท่านั้น
///
/// กับดักที่เทสต์นี้จับ (เหตุผลเต็มอยู่ใน doc comment ของ migration และใน
/// <see cref="EncounterRlsTests"/>): `ALTER DEFAULT PRIVILEGES ... GRANT SELECT, INSERT, UPDATE,
/// DELETE ON TABLES TO dndez_app` ใน migration `RowLevelSecurity` (20260821094727) **ยังมีผลกับ
/// ตารางที่สร้างทีหลัง** ส่วน `DO $$` loop ที่ติด policy ให้อัตโนมัติ **ไม่มีผล** (รันครั้งเดียว)
/// ⇒ ตารางใหม่ **fail open** ถ้าไม่เขียน ENABLE/FORCE RLS + REVOKE เอง
/// </summary>
public class ActionCatalogRlsTests
{
    // owner role — migration/seed รันด้วย role นี้
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    // role runtime จริงของ API — ตัวเดียวกับที่ Program.cs ใช้ต่อ
    private const string AppConnection =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

    // ── ตาข่ายจับ "ลืมเปิด RLS ให้ตารางใหม่" (DoD ของ Part นี้) ─────────────

    [Fact]
    public async Task Rls_is_enabled_and_forced_on_action_catalog_entries()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            SELECT relrowsecurity, relforcerowsecurity
            FROM pg_class
            WHERE oid = 'public.action_catalog_entries'::regclass
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "ไม่พบตาราง action_catalog_entries");
        Assert.True(reader.GetBoolean(0), "action_catalog_entries: ENABLE ROW LEVEL SECURITY ยังไม่ได้ตั้ง");
        Assert.True(reader.GetBoolean(1), "action_catalog_entries: FORCE ROW LEVEL SECURITY ยังไม่ได้ตั้ง");
    }

    [Fact]
    public async Task Table_has_at_least_one_policy()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_policies WHERE schemaname = 'public' AND tablename = 'action_catalog_entries'",
            conn);
        Assert.True((long)(await cmd.ExecuteScalarAsync())! > 0, "action_catalog_entries: ไม่มี policy เลย");
    }

    /// <summary>
    /// ชั้นที่สองที่ไม่ขึ้นกับ RLS เลย — สิทธิ์ระดับตาราง · ถ้า REVOKE ไม่ได้ผล (บทเรียน CVE-2018-1058:
    /// `REVOKE ... FROM dndez_app` เคยเป็น no-op เพราะสิทธิ์ถือโดย PUBLIC) เทสต์นี้จะจับได้ทันที
    /// </summary>
    [Fact]
    public async Task App_role_has_select_only_on_action_catalog_entries()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            SELECT privilege_type
            FROM information_schema.table_privileges
            WHERE table_schema = 'public'
              AND table_name = 'action_catalog_entries'
              AND grantee IN ('dndez_app', 'PUBLIC')
            ORDER BY privilege_type
            """, conn);

        var granted = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) granted.Add(reader.GetString(0));
        }

        Assert.Equal(new[] { "SELECT" }, granted.Distinct().ToArray());
    }

    // ── อ่านได้ (คลังกลไกเป็นข้อมูลอ้างอิงระดับระบบ ไม่ใช่ความลับของโต๊ะใด) ──

    [Fact]
    public async Task App_role_can_read_the_catalog()
    {
        await using var conn = new NpgsqlConnection(AppConnection);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM action_catalog_entries", conn);
        Assert.True((long)(await cmd.ExecuteScalarAsync())! > 0,
            "role แอปต้องอ่านคลังกลไกได้ (ไม่งั้น engine หยิบกฎมาใช้ไม่ได้)");
    }

    /// <summary>
    /// ต่างจากตารางผูกแคมเปญทุกตัวในโปรเจกต์นี้โดยตั้งใจ — คลังกลไกเป็นข้อมูลอ้างอิง SRD ที่เผยแพร่
    /// สาธารณะอยู่แล้ว ไม่มีอะไรให้ซ่อนรายแคมเปญ การบังคับให้ตั้ง `app.user_id` ก่อนอ่านจะทำให้
    /// เข้าใจผิดว่า RLS ตัวนี้เป็นด่าน auth ทั้งที่ด่าน auth จริงอยู่ชั้น API (Part 7/9)
    /// </summary>
    [Fact]
    public async Task Catalog_is_readable_without_app_user_id_set()
    {
        await using var conn = new NpgsqlConnection(AppConnection);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM action_catalog_entries", conn, tx);
        Assert.True((long)(await cmd.ExecuteScalarAsync())! > 0);

        await tx.RollbackAsync();
    }

    // ── เขียนไม่ได้เลย (หัวใจของ Part นี้) ───────────────────────────────────

    [Fact]
    public async Task App_role_cannot_insert_into_the_catalog()
    {
        await using var conn = new NpgsqlConnection(AppConnection);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO action_catalog_entries
                (id, rules_system, key, name, kind, action_cost, target_kind, roll_kind)
            VALUES (@id, 'srd2024', 'evil-injected-weapon', 'Evil', 'Weapon', 'Action', 'Creature', 'AttackRoll')
            """, conn, tx);
        insert.Parameters.AddWithValue("id", Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);

        await tx.RollbackAsync();
    }

    /// <summary>
    /// สถานการณ์จริงที่กลัวที่สุด — แก้ระยะของอาวุธให้ยาวขึ้นเงียบ ๆ แล้วไม่มีใครรู้
    /// ⚠️ ต้อง **throw** ไม่ใช่ได้ 0 rows: RLS อย่างเดียวจะซ่อนแถวแล้วคืน 0 rows เงียบ ๆ
    /// (บทเรียนจาก `CampaignMemberRoleRlsTests`) — ที่ทำให้ throw คือ REVOKE สิทธิ์ระดับตาราง
    /// การยืนยันว่า throw จึงเป็นการพิสูจน์ว่า REVOKE มีผลจริง ไม่ใช่แค่ RLS
    /// </summary>
    [Fact]
    public async Task App_role_cannot_update_the_catalog()
    {
        await using var conn = new NpgsqlConnection(AppConnection);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using var update = new NpgsqlCommand(
            "UPDATE action_catalog_entries SET range_normal_feet = 9999", conn, tx);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task App_role_cannot_delete_from_the_catalog()
    {
        await using var conn = new NpgsqlConnection(AppConnection);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using var delete = new NpgsqlCommand("DELETE FROM action_catalog_entries", conn, tx);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);

        await tx.RollbackAsync();
    }

    /// <summary>
    /// ยืนยันว่าไม่มีใครเผลอใส่ `campaign_id` ให้ตารางนี้ — ถ้ามีเมื่อไหร่ แปลว่าเจตนาของตาราง
    /// เปลี่ยนไปเป็นข้อมูลรายโต๊ะ แล้ว policy ชุดนี้ (อ่านได้ทุกคน) จะกลายเป็นช่องโหว่ทันที
    /// </summary>
    [Fact]
    public async Task Catalog_has_no_campaign_id_column()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'action_catalog_entries'
              AND column_name = 'campaign_id'
            """, conn);
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }
}
