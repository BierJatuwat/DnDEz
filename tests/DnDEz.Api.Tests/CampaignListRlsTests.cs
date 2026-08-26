using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// migration `FixCampaignListRlsPolicy` — policy เดิมของ campaigns ผูกกับ app.campaign_id ตัวเดียว
/// เสมอ (app_current_campaign() คืนได้แค่ค่าเดียวต่อ request) ทำให้ "list แคมเปญของตัวเอง" เป็นไปไม่ได้
/// เลยในทางโครงสร้าง ไม่ว่าจะเป็นสมาชิกกี่แคมเปญก็ตาม — แก้ด้วย app_is_member_of(uuid) ที่เช็คทีละแถว
///
/// ทดสอบตรงกับ role dndez_app ผ่าน raw SQL (ยังไม่มี API endpoint ห่อ — รอ Sprint A Part 3)
/// </summary>
public class CampaignListRlsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

    private readonly Guid _userAId = Guid.NewGuid();
    private readonly Guid _userBId = Guid.NewGuid();
    private readonly List<Guid> _campaignIdsToCleanUp = new();

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (id, label) in new[] { (_userAId, "a"), (_userBId, "b") })
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO users (id, email, display_name, password_hash, created_at)
                VALUES (@id, @email, @label, 'not-a-real-hash', now())
                """, conn);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("email", $"campaign-list-{label}-{id}@example.test");
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
        deleteUsers.Parameters.AddWithValue("ids", new[] { _userAId, _userBId });
        await deleteUsers.ExecuteNonQueryAsync();
    }

    private async Task<Guid> CreateCampaignAsync(Guid ownerUserId, string name)
    {
        var campaignId = Guid.NewGuid();
        _campaignIdsToCleanUp.Add(campaignId);

        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var setUser = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @userId, true)", conn, tx))
        {
            setUser.Parameters.AddWithValue("userId", ownerUserId.ToString());
            await setUser.ExecuteScalarAsync();
        }

        await using (var create = new NpgsqlCommand(
            """
            SELECT app_create_campaign(
                @campaignId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', @membershipId)
            """, conn, tx))
        {
            create.Parameters.AddWithValue("campaignId", campaignId);
            create.Parameters.AddWithValue("name", name);
            create.Parameters.AddWithValue("membershipId", Guid.NewGuid());
            await create.ExecuteScalarAsync();
        }

        await tx.CommitAsync();
        return campaignId;
    }

    // --- security review finding (W1): CreateCampaignAsync ทำให้ผู้เรียกเป็น role 'Dm' เสมอ
    // (ผ่าน app_create_campaign) — ไม่มีเทสต์ไหนครอบเคสสมาชิกที่เป็น 'Player' (ไม่ใช่คนสร้าง) เลย
    // ทั้งที่ app_is_member_of ตั้งใจให้ role-agnostic (ดู doc comment ใน migration) — เพิ่มเทสต์นี้
    // เพื่อล็อกพฤติกรรมที่ยืนยันแล้วว่าถูกต้อง (security review ทดสอบเชิงประจักษ์ผ่าน psql ตรงแล้ว)
    // ไม่ให้กลายเป็น regression เงียบ ๆ ถ้ามีคนแก้ policy ทีหลังแล้วเผลอใส่ role check เข้าไป

    [Fact]
    public async Task Non_owner_player_member_sees_the_campaign_in_their_list_too()
    {
        var campaignId = await CreateCampaignAsync(_userAId, "Shared Campaign");

        // เพิ่ม user B เป็นสมาชิก role Player ตรง ๆ ผ่าน owner connection (จำลอง invite flow
        // ที่ยังไม่มีใน Part 3 นี้ — ตรงตามที่ security reviewer ทดสอบเชิงประจักษ์ไว้)
        await using (var ownerConn = new NpgsqlConnection(OwnerConnectionString))
        {
            await ownerConn.OpenAsync();
            await using var insertMember = new NpgsqlCommand(
                """
                INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
                VALUES (@id, @campaignId, @userId, 'Player', now())
                """, ownerConn);
            insertMember.Parameters.AddWithValue("id", Guid.NewGuid());
            insertMember.Parameters.AddWithValue("campaignId", campaignId);
            insertMember.Parameters.AddWithValue("userId", _userBId);
            await insertMember.ExecuteNonQueryAsync();
        }

        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var setUser = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @userId, true)", conn, tx))
        {
            setUser.Parameters.AddWithValue("userId", _userBId.ToString());
            await setUser.ExecuteScalarAsync();
        }

        await using var select = new NpgsqlCommand(
            "SELECT count(*) FROM campaigns WHERE id = @campaignId", conn, tx);
        select.Parameters.AddWithValue("campaignId", campaignId);
        var visibleCount = (long)(await select.ExecuteScalarAsync())!;

        Assert.Equal(1, visibleCount);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task User_lists_exactly_their_own_campaigns_without_setting_app_campaign_id()
    {
        var campaignA1 = await CreateCampaignAsync(_userAId, "A Campaign 1");
        var campaignA2 = await CreateCampaignAsync(_userAId, "A Campaign 2");
        var campaignB1 = await CreateCampaignAsync(_userBId, "B Campaign 1");

        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // ไม่ตั้ง app.campaign_id เลยโดยตั้งใจ — จุดประสงค์ทั้งหมดของเทสต์นี้คือพิสูจน์ว่า list
        // ทำได้โดยไม่ต้องรู้ campaign id ล่วงหน้า (ต่างจาก whoami/single-campaign query ใน Part 2)
        await using (var setUser = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @userId, true)", conn, tx))
        {
            setUser.Parameters.AddWithValue("userId", _userAId.ToString());
            await setUser.ExecuteScalarAsync();
        }

        var visibleIds = new List<Guid>();
        await using (var select = new NpgsqlCommand("SELECT id FROM campaigns ORDER BY name", conn, tx))
        await using (var reader = await select.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                visibleIds.Add(reader.GetGuid(0));
            }
        }

        Assert.Equal(new[] { campaignA1, campaignA2 }, visibleIds);
        Assert.DoesNotContain(campaignB1, visibleIds);

        await tx.RollbackAsync();
    }

    [Fact]
    public async Task User_with_no_campaigns_sees_empty_list_not_an_error()
    {
        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var setUser = new NpgsqlCommand(
            "SELECT set_config('app.user_id', @userId, true)", conn, tx))
        {
            setUser.Parameters.AddWithValue("userId", _userBId.ToString());
            await setUser.ExecuteScalarAsync();
        }

        await using var select = new NpgsqlCommand("SELECT count(*) FROM campaigns", conn, tx);
        var count = (long)(await select.ExecuteScalarAsync())!;

        Assert.Equal(0, count);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Query_without_app_user_id_set_sees_zero_campaigns_fail_closed()
    {
        await CreateCampaignAsync(_userAId, "Should Stay Hidden");

        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // ไม่ set_config('app.user_id', ...) เลย โดยตั้งใจ
        await using var select = new NpgsqlCommand("SELECT count(*) FROM campaigns", conn, tx);
        var count = (long)(await select.ExecuteScalarAsync())!;

        Assert.Equal(0, count);
        await tx.RollbackAsync();
    }
}
