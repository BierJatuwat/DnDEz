using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint A Part 1 — พิสูจน์ว่า RLS เป็น fail-closed ผ่าน HTTP จริง (ไม่ใช่แค่ query DB ตรง ๆ)
///
/// รูปแบบ: seed แถวจริงเข้า `campaigns` ผ่าน owner role (`dndez`, bypass RLS) → ยิง HTTP
/// เข้า `DnDEz.Api` (ต่อ DB ด้วย role `dndez_app` เท่านั้น) แบบไม่ตั้ง `SET LOCAL app.user_id`/
/// `app.campaign_id` เลย → ต้องได้ 0 แถวเสมอ แม้ DB มีข้อมูลจริงอยู่ (fail-closed = policy ปฏิเสธ
/// ไม่ใช่ error, ไม่ใช่ DB ว่างเปล่า) — ห้าม mock DbContext/repository ใด ๆ ต่อ Postgres จริงที่
/// docker compose รันอยู่ (`docker compose up -d postgres`)
///
/// contract ที่ implementation (green) ต้องทำตามให้ตรง:
///   GET /diagnostics/rls-fail-closed-check → 200 + JSON มี field ชื่อ "CampaignRowCount" (int)
///     ต้องเป็นผลจาก DnDEzDbContext.Campaigns query จริงผ่าน role dndez_app โดยไม่ SET LOCAL ใด ๆ
///   GET /diagnostics/db-connectivity → 200 + JSON มี field ชื่อ "Connected" (bool)
///     ต้องเป็นผลจาก db.Database.CanConnectAsync() (หรือเทียบเท่า) ผ่าน role dndez_app จริง
///     (พิสูจน์ว่า role ต่อ DB ได้จริง ไม่ใช่ error สิทธิ์พื้นฐาน)
/// (การเทียบ JSON property name เป็น case-insensitive ในเทสต์นี้ — สะกดตรงตัวพอ ไม่ต้องตรง case เป๊ะ)
/// </summary>
public class RlsFailClosedTests : IAsyncLifetime
{
    // owner role — ใช้ seed/cleanup ข้อมูลทดสอบเท่านั้น (bypass RLS ตามธรรมชาติของ owner)
    // ตรงกับ fallback connection string เดียวกับ DnDEzDbContextFactory (docs/db-schema.md)
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly WebApplicationFactory<Program> _factory = new();

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var insertUser = new NpgsqlCommand(
            """
            INSERT INTO users (id, email, display_name, password_hash, created_at)
            VALUES (@id, @email, 'RLS Test Owner', 'not-a-real-hash', now())
            """, conn))
        {
            insertUser.Parameters.AddWithValue("id", _userId);
            insertUser.Parameters.AddWithValue("email", $"rls-test-{_userId}@example.test");
            await insertUser.ExecuteNonQueryAsync();
        }

        await using (var insertCampaign = new NpgsqlCommand(
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES
                (@id, @ownerId, 'RLS fail-closed fixture campaign', 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """, conn))
        {
            insertCampaign.Parameters.AddWithValue("id", _campaignId);
            insertCampaign.Parameters.AddWithValue("ownerId", _userId);
            await insertCampaign.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var deleteCampaign = new NpgsqlCommand("DELETE FROM campaigns WHERE id = @id", conn))
        {
            deleteCampaign.Parameters.AddWithValue("id", _campaignId);
            await deleteCampaign.ExecuteNonQueryAsync();
        }

        await using (var deleteUser = new NpgsqlCommand("DELETE FROM users WHERE id = @id", conn))
        {
            deleteUser.Parameters.AddWithValue("id", _userId);
            await deleteUser.ExecuteNonQueryAsync();
        }

        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Seed_via_owner_role_actually_landed_in_db_before_trusting_the_rls_assertion()
    {
        // ถ้า assert นี้ fail แปลว่า test setup เสีย (owner insert ไม่ติด) — ไม่เกี่ยวกับ RLS เลย
        // ต้องแยกให้ชัดจากเทสต์ถัดไปซึ่งพิสูจน์พฤติกรรมของ RLS จริง ๆ
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM campaigns WHERE id = @id", conn);
        count.Parameters.AddWithValue("id", _campaignId);
        var result = (long)(await count.ExecuteScalarAsync())!;

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Endpoint_returns_zero_campaign_rows_via_app_role_without_session_vars_even_though_data_exists()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/diagnostics/rls-fail-closed-check");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RlsFailClosedResponse>(JsonOptions);

        Assert.NotNull(body);
        Assert.Equal(0, body!.CampaignRowCount);
    }

    [Fact]
    public async Task App_role_connection_actually_works_zero_rows_is_not_just_a_broken_connection()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/diagnostics/db-connectivity");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<DbConnectivityResponse>(JsonOptions);

        Assert.NotNull(body);
        Assert.True(body!.Connected);
    }

    private record RlsFailClosedResponse(int CampaignRowCount);
    private record DbConnectivityResponse(bool Connected);
}
