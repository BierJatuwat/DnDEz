using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// migration `AddCampaignCreationBootstrapFunction` — แก้ bootstrap gap ที่พบตอน Sprint A Part 1/2
/// (ดู docs/db-schema.md "ช่องโหว่ที่พบต่อเนื่อง"): สร้างแคมเปญใหม่ผ่าน role dndez_app ด้วย INSERT
/// ตรง ๆ ไม่ได้เลย เพราะ WITH CHECK ของทั้ง campaigns และ campaign_members ต้องการ app_is_member()
/// เป็นจริงก่อน แต่แถวที่พิสูจน์ความเป็นสมาชิกคือแถวที่กำลังจะสร้างพอดี (circular)
///
/// ทดสอบตรงกับ role dndez_app ผ่าน raw SQL (ยังไม่มี API endpoint ห่อฟังก์ชันนี้ — รอ Sprint A Part 3)
/// ไม่ผ่าน owner role เด็ดขาดในการเรียกฟังก์ชัน (นอกจาก seed/cleanup) เพื่อพิสูจน์ behavior ที่แอปจริงจะเจอ
/// </summary>
public class CampaignBootstrapTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

    private readonly Guid _ownerUserId = Guid.NewGuid();
    private readonly Guid _outsiderUserId = Guid.NewGuid();
    private readonly List<Guid> _campaignIdsToCleanUp = new();

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (id, label) in new[] { (_ownerUserId, "owner"), (_outsiderUserId, "outsider") })
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO users (id, email, display_name, password_hash, created_at)
                VALUES (@id, @email, @label, 'not-a-real-hash', now())
                """, conn);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("email", $"campaign-bootstrap-{label}-{id}@example.test");
            cmd.Parameters.AddWithValue("label", label);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        if (_campaignIdsToCleanUp.Count > 0)
        {
            await using var deleteMembers = new NpgsqlCommand(
                "DELETE FROM campaign_members WHERE campaign_id = ANY(@ids)", conn);
            deleteMembers.Parameters.AddWithValue("ids", _campaignIdsToCleanUp.ToArray());
            await deleteMembers.ExecuteNonQueryAsync();

            await using var deleteCampaigns = new NpgsqlCommand(
                "DELETE FROM campaigns WHERE id = ANY(@ids)", conn);
            deleteCampaigns.Parameters.AddWithValue("ids", _campaignIdsToCleanUp.ToArray());
            await deleteCampaigns.ExecuteNonQueryAsync();
        }

        await using var deleteUsers = new NpgsqlCommand(
            "DELETE FROM users WHERE id = ANY(@ids)", conn);
        deleteUsers.Parameters.AddWithValue("ids", new[] { _ownerUserId, _outsiderUserId });
        await deleteUsers.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task app_create_campaign_creates_campaign_and_dm_membership_atomically_via_dndez_app()
    {
        var campaignId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        _campaignIdsToCleanUp.Add(campaignId);

        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var setUser = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @userId, true)", conn, tx))
        {
            setUser.Parameters.AddWithValue("userId", _ownerUserId.ToString());
            await setUser.ExecuteScalarAsync();
        }

        await using (var create = new NpgsqlCommand(
            """
            SELECT app_create_campaign(
                @campaignId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', @membershipId)
            """, conn, tx))
        {
            create.Parameters.AddWithValue("campaignId", campaignId);
            create.Parameters.AddWithValue("name", "Bootstrap Test Campaign");
            create.Parameters.AddWithValue("membershipId", membershipId);
            var returned = (Guid)(await create.ExecuteScalarAsync())!;
            Assert.Equal(campaignId, returned);
        }

        // creator ต้องเห็นแคมเปญที่เพิ่งสร้างผ่าน normal SELECT ทันที (พิสูจน์ RLS ปกติทำงานต่อได้จริง
        // ไม่ใช่แค่ function สร้างแถวได้เฉย ๆ) — ตั้ง app.campaign_id แล้ว query ปกติ
        await using (var setCampaign = new NpgsqlCommand(
            "SELECT set_config('app.campaign_id', @campaignId, true)", conn, tx))
        {
            setCampaign.Parameters.AddWithValue("campaignId", campaignId.ToString());
            await setCampaign.ExecuteScalarAsync();
        }

        await using (var select = new NpgsqlCommand(
            "SELECT owner_user_id, name FROM campaigns WHERE id = @campaignId", conn, tx))
        {
            select.Parameters.AddWithValue("campaignId", campaignId);
            await using var reader = await select.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "creator ควรเห็นแคมเปญที่เพิ่งสร้างผ่าน RLS ปกติ");
            Assert.Equal(_ownerUserId, reader.GetGuid(0));
            Assert.Equal("Bootstrap Test Campaign", reader.GetString(1));
        }

        await using (var selectMember = new NpgsqlCommand(
            "SELECT user_id, role FROM campaign_members WHERE campaign_id = @campaignId", conn, tx))
        {
            selectMember.Parameters.AddWithValue("campaignId", campaignId);
            await using var reader = await selectMember.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "ต้องมีแถว campaign_members ของผู้สร้างเป็น DM");
            Assert.Equal(_ownerUserId, reader.GetGuid(0));
            Assert.Equal("Dm", reader.GetString(1));
        }

        await tx.CommitAsync();
    }

    [Fact]
    public async Task app_create_campaign_throws_when_app_user_id_is_not_set()
    {
        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // ไม่ set_config('app.user_id', ...) เลย โดยตั้งใจ
        await using var create = new NpgsqlCommand(
            """
            SELECT app_create_campaign(
                @campaignId, 'Should Not Exist', 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', @membershipId)
            """, conn, tx);
        create.Parameters.AddWithValue("campaignId", Guid.NewGuid());
        create.Parameters.AddWithValue("membershipId", Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<PostgresException>(() => create.ExecuteScalarAsync());
        Assert.Contains("app.user_id", ex.MessageText);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task outsider_cannot_see_campaign_created_by_someone_else_via_bootstrap_function()
    {
        var campaignId = Guid.NewGuid();
        _campaignIdsToCleanUp.Add(campaignId);

        // สร้างแคมเปญด้วย owner ตัวจริงก่อน (connection แยก, commit จริง ให้ connection ถัดไปเห็น)
        await using (var creatorConn = new NpgsqlConnection(AppConnectionString))
        {
            await creatorConn.OpenAsync();
            await using var tx = await creatorConn.BeginTransactionAsync();
            await using (var setUser = new NpgsqlCommand(
                "SELECT set_config('app.user_id', @userId, true)", creatorConn, tx))
            {
                setUser.Parameters.AddWithValue("userId", _ownerUserId.ToString());
                await setUser.ExecuteScalarAsync();
            }

            await using var create = new NpgsqlCommand(
                """
                SELECT app_create_campaign(
                    @campaignId, 'Private Campaign', 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', @membershipId)
                """, creatorConn, tx);
            create.Parameters.AddWithValue("campaignId", campaignId);
            create.Parameters.AddWithValue("membershipId", Guid.NewGuid());
            await create.ExecuteScalarAsync();
            await tx.CommitAsync();
        }

        // outsider เปิด connection ใหม่ ต้องมองไม่เห็นแคมเปญนี้เลย
        await using var outsiderConn = new NpgsqlConnection(AppConnectionString);
        await outsiderConn.OpenAsync();
        await using var outsiderTx = await outsiderConn.BeginTransactionAsync();

        await using (var setUser = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @userId, true)", outsiderConn, outsiderTx))
        {
            setUser.Parameters.AddWithValue("userId", _outsiderUserId.ToString());
            await setUser.ExecuteScalarAsync();
        }

        await using (var setCampaign = new NpgsqlCommand(
            "SELECT set_config('app.campaign_id', @campaignId, true)", outsiderConn, outsiderTx))
        {
            setCampaign.Parameters.AddWithValue("campaignId", campaignId.ToString());
            await setCampaign.ExecuteScalarAsync();
        }

        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM campaigns WHERE id = @campaignId", outsiderConn, outsiderTx);
        count.Parameters.AddWithValue("campaignId", campaignId);
        var visibleRows = (long)(await count.ExecuteScalarAsync())!;

        Assert.Equal(0, visibleRows);
        await outsiderTx.RollbackAsync();
    }

    [Fact]
    public async Task direct_insert_into_campaigns_bypassing_the_function_is_still_blocked_by_rls()
    {
        // ยืนยันว่า fix นี้เปิดแค่ "ทางผ่าน function ที่ควบคุมได้" ไม่ได้ทำให้ policy เดิมหลวมลง —
        // INSERT ตรง ๆ (ไม่ผ่าน app_create_campaign) ต้องยังถูก RLS บล็อกเหมือนก่อนแก้
        var campaignId = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var setUser = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @userId, true)", conn, tx))
        {
            setUser.Parameters.AddWithValue("userId", _ownerUserId.ToString());
            await setUser.ExecuteScalarAsync();
        }

        await using (var setCampaign = new NpgsqlCommand(
            "SELECT set_config('app.campaign_id', @campaignId, true)", conn, tx))
        {
            setCampaign.Parameters.AddWithValue("campaignId", campaignId.ToString());
            await setCampaign.ExecuteScalarAsync();
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO campaigns (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@id, @ownerId, 'Direct Insert Attempt', 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """, conn, tx);
        insert.Parameters.AddWithValue("id", campaignId);
        insert.Parameters.AddWithValue("ownerId", _ownerUserId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege / RLS policy violation

        await tx.RollbackAsync();
    }
}
