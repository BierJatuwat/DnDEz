using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DnDEz.Api.Orchestration;
using DnDEz.Api.Tests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Part B ของแผน "DM AI Assistant — session-context grounding" (2026-09-22) — tool อ่านอย่างเดียว
/// `lookup_rule`/`lookup_monster` ที่ `OrchestratorTurnRunner` เสนอให้ Claude เรียกผ่าน `/consult`
/// (path เดียวกับ `roll_dice` แต่**ไม่ผ่าน `AutonomySetting`/`Proposal` gate เลย** — อ่านอย่างเดียว
/// ไม่มี state ให้พัง ดู doc comment ของ `OrchestratorTurnRunner` ส่วน Part B)
///
/// อาศัยข้อมูล SRD จริงที่ migration `ImportSrd521`/`AddSrdThaiTranslations` seed ไว้แล้วในฐานนี้
/// (330 มอนสเตอร์ + 380 รายการกฎ/เวท/สถานะ) — ไม่ต้อง seed เอง เหมือนที่ `SrdTranslationTests.cs`
/// ทำ · seed เฉพาะที่เป็น per-campaign override (`monster_stat_blocks`) เอง
///
/// **ห้ามยิง HTTP จริงไปหา Anthropic** — สลับ `IAnthropicClient` เป็น `FakeAnthropicClient` เหมือน
/// `ConsultEndpointsTests` ทั้งไฟล์ (ดู doc comment ของ `FakeAnthropicClient` สำหรับ trigger keyword
/// `lookup-rule:<key>` / `lookup-monster:<key>`)
/// </summary>
public class LookupToolsEndpointTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "lookup-tools-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-lookup-tools-000";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    // แคมเปญหลัก — ไม่ seed AutonomySetting เลย = default Suggest (เหมือน ConsultEndpointsTests)
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();

    // RulesCombat=Manual — ทดสอบว่า lookup tool ยังถูกเสนออยู่แม้ roll_dice ถูกตัดออก
    private readonly Guid _campaignManualId = Guid.NewGuid();
    private readonly Guid _sessionManualId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private FakeAnthropicClient _fakeAnthropicClient = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;

    public async Task InitializeAsync()
    {
        _fakeAnthropicClient = new FakeAnthropicClient();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAnthropicClient>();
                services.AddSingleton<IAnthropicClient>(_fakeAnthropicClient);
            });
        });

        (_dmUserId, _dmToken) = await RegisterAsync("dm");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Lookup Campaign {_suffix}");
        await InsertCampaignAsync(conn, _campaignManualId, _dmUserId, $"Lookup Manual Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId);
        await InsertMembershipAsync(conn, _campaignManualId, _dmUserId);
        await InsertSessionAsync(conn, _sessionId, _campaignId);
        await InsertSessionAsync(conn, _sessionManualId, _campaignManualId);
        await InsertAutonomySettingAsync(conn, _campaignManualId, "RulesCombat", "Manual");

        var putKeyResponse = await AuthedClient().PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = DmValidApiKey });
        putKeyResponse.EnsureSuccessStatusCode();
    }

    // ── lookup_rule — ต้นฉบับ SRD จริงในฐาน ────────────────────────────────

    [Fact]
    public async Task Consult_lookup_rule_tool_returns_real_srd_content_for_known_condition_key()
    {
        var response = await AuthedClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = "lookup-rule:blinded please" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        Assert.NotNull(body!.ToolCall);
        Assert.Equal("lookup_rule", body.ToolCall!.Name);
        Assert.Contains("[condition]", body.ToolCall.Result);
        Assert.Contains("Blinded", body.ToolCall.Result);
        Assert.Contains("Can", body.ToolCall.Result); // "Can't See." อยู่ในเนื้อหาจริงของ SRD
        Assert.Null(body.Pending); // ไม่ผ่าน Proposal gate เลย แม้แคมเปญนี้ default เป็น Suggest
        Assert.NotNull(body.MessageId); // execute ทันที ไม่รอ approve
    }

    [Fact]
    public async Task Consult_lookup_rule_tool_with_unknown_key_returns_error_string_not_500()
    {
        var response = await AuthedClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = "lookup-rule:not-a-real-srd-key-at-all please" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        Assert.NotNull(body!.ToolCall);
        Assert.Contains("Error", body.ToolCall!.Result, StringComparison.OrdinalIgnoreCase);
    }

    // ── lookup_monster — SRD default vs campaign override ──────────────────

    [Fact]
    public async Task Consult_lookup_monster_tool_returns_srd_default_when_campaign_has_no_override()
    {
        var response = await AuthedClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = "lookup-monster:goblin-warrior please" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        Assert.NotNull(body!.ToolCall);
        Assert.Contains("Goblin Warrior", body.ToolCall!.Result);
        Assert.Contains("SRD default", body.ToolCall.Result);
    }

    [Fact]
    public async Task Consult_lookup_monster_tool_prefers_campaign_override_over_srd_default()
    {
        var monsterId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        // DM ปรับ HP ของก็อบลินในแคมเปญนี้ไว้ที่ 999 (ค่าที่ไม่มีทางเป็นค่า SRD ดั้งเดิมโดยบังเอิญ)
        await ExecAsync(conn,
            """
            INSERT INTO monster_stat_blocks
                (id, campaign_id, name, armor_class, hit_points, actions, condition_refs, review_status,
                 speed_feet, reach_feet, srd_key)
            VALUES (@id, @c, 'Goblin Warrior', 15, 999, '[]'::jsonb, '[]'::jsonb, 'Approved', 30, 5, @srdKey)
            """,
            ("id", monsterId), ("c", _campaignId), ("srdKey", "goblin-warrior"));

        var response = await AuthedClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = "lookup-monster:goblin-warrior please" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        Assert.NotNull(body!.ToolCall);
        Assert.Contains("999", body.ToolCall!.Result);
        Assert.Contains("adjusted by this campaign's DM", body.ToolCall.Result);
        Assert.DoesNotContain("SRD default", body.ToolCall.Result);
    }

    [Fact]
    public async Task Consult_lookup_monster_tool_with_unknown_key_returns_error_string_not_500()
    {
        var response = await AuthedClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult",
            new { Content = "lookup-monster:not-a-real-monster-key please" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        Assert.NotNull(body!.ToolCall);
        Assert.Contains("Error", body.ToolCall!.Result, StringComparison.OrdinalIgnoreCase);
    }

    // ── gate/offer semantics — จุดที่ต่างจาก roll_dice โดยตั้งใจ ─────────────

    [Fact]
    public async Task Consult_lookup_tools_are_offered_and_executed_even_under_manual_autonomy()
    {
        // Manual ตัด roll_dice ออกจากที่เสนอ แต่ lookup tool อ่านอย่างเดียว ไม่เกี่ยวกับ dial นี้เลย
        var response = await AuthedClient().PostAsJsonAsync(
            $"/campaigns/{_campaignManualId}/sessions/{_sessionManualId}/consult",
            new { Content = "lookup-rule:blinded please" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        Assert.NotNull(body!.ToolCall);
        Assert.Equal("lookup_rule", body.ToolCall!.Name);
        Assert.Contains("Blinded", body.ToolCall.Result);
    }

    [Fact]
    public async Task Consult_roll_dice_is_not_offered_under_manual_autonomy_even_though_lookup_tools_are()
    {
        var response = await AuthedClient().PostAsJsonAsync(
            $"/campaigns/{_campaignManualId}/sessions/{_sessionManualId}/consult",
            new { Content = "please roll for it" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ConsultResponse>(JsonOptions);
        Assert.Null(body!.ToolCall); // ไม่มี roll_dice ให้เรียก — narration เฉย ๆ

        Assert.NotNull(_fakeAnthropicClient.LastToolNamesOffered);
        Assert.Contains("lookup_rule", _fakeAnthropicClient.LastToolNamesOffered!);
        Assert.Contains("lookup_monster", _fakeAnthropicClient.LastToolNamesOffered!);
        Assert.DoesNotContain("roll_dice", _fakeAnthropicClient.LastToolNamesOffered!);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task InsertCampaignAsync(NpgsqlConnection conn, Guid id, Guid ownerUserId, string name) =>
        await ExecAsync(conn,
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES
                (@id, @ownerUserId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """,
            ("id", id), ("ownerUserId", ownerUserId), ("name", name));

    private static async Task InsertMembershipAsync(NpgsqlConnection conn, Guid campaignId, Guid userId) =>
        await ExecAsync(conn,
            "INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at) VALUES (@id, @campaignId, @userId, 'Dm', now())",
            ("id", Guid.NewGuid()), ("campaignId", campaignId), ("userId", userId));

    private static async Task InsertSessionAsync(NpgsqlConnection conn, Guid id, Guid campaignId) =>
        await ExecAsync(conn,
            "INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at) " +
            "VALUES (@id, @campaignId, 'Online', 'Active', NULL, now(), NULL)",
            ("id", id), ("campaignId", campaignId));

    private static async Task InsertAutonomySettingAsync(
        NpgsqlConnection conn, Guid campaignId, string subsystem, string level) =>
        await ExecAsync(conn,
            "INSERT INTO autonomy_settings (id, campaign_id, subsystem, level) VALUES (@id, @campaignId, @subsystem, @level)",
            ("id", Guid.NewGuid()), ("campaignId", campaignId), ("subsystem", subsystem), ("level", level));

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (paramName, value) in parameters)
        {
            cmd.Parameters.AddWithValue(paramName, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"lookup-tools-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Lookup {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private sealed record AuthResponse(string Token, Guid UserId);

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _dmToken);
        return client;
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        // ต้องลบ campaigns ก่อน users เสมอ — _dmUserId เป็นเจ้าของทั้งสองแคมเปญ ลบ user ก่อนจะชน FK
        await using (var deleteCampaigns = new NpgsqlCommand("DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("ids", new[] { _campaignId, _campaignManualId });
            await deleteCampaigns.ExecuteNonQueryAsync();
        }

        await using (var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = @id", conn))
        {
            deleteUsers.Parameters.AddWithValue("id", _dmUserId);
            await deleteUsers.ExecuteNonQueryAsync();
        }

        await _factory.DisposeAsync();
    }
}

internal sealed record ConsultResponse(Guid? MessageId, string Content, ConsultToolCallResponse? ToolCall, ConsultPendingResponse? Pending);

internal sealed record ConsultToolCallResponse(string Name, JsonElement Input, string Result);

internal sealed record ConsultPendingResponse(Guid ProposalId, string ToolName, JsonElement ToolInput);
