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
/// 2026-09-23 — `messages.dm_private` (migration `AddMessageDmPrivateAndRecapDmWrite`)
///
/// ช่องรั่วที่ปิด: `/consult` (DM ปรึกษา AI ส่วนตัว) กับ `/actions` (เทิร์นผู้เล่น broadcast ทั้งโต๊ะ) เขียน
/// ลงตารางเดียวกัน แล้ว history ของเทิร์นผู้เล่นดึง log **ทั้งหมด** ให้ Claude — สิ่งที่ DM ปรึกษาลับ ๆ จึง
/// ไปถึง prompt ของเทิร์นที่ narration ถูก broadcast ได้ · เทสต์ leak ด้านล่างใช้ที่ `FakeAnthropicClient`
/// เก็บ history ที่ได้รับจริง แล้วตรวจว่า marker ลับไม่อยู่ในนั้น (บนโค้ดก่อนแก้ marker อยู่ใน history)
///
/// ชั้น RLS ทดสอบด้วย raw SQL ผ่าน role `dndez_app` แยกจากชั้น endpoint — แบบเดียวกับ
/// `ConsultMessageRlsTests`
/// </summary>
public class MessageDmPrivateTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "dm-private-secret-1";
    private const string DmValidApiKey = "sk-ant-api03-dm-valid-key-for-dm-private-tests-00";
    private const string SecretMarker = "SECRET-CONSULT-MARKER-the-innkeeper-is-a-doppelganger";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _heroId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private FakeAnthropicClient _fake = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;

    public async Task InitializeAsync()
    {
        _fake = new FakeAnthropicClient();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAnthropicClient>();
                services.AddSingleton<IAnthropicClient>(_fake);
            }));

        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn,
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@id, @owner, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """,
            ("id", _campaignId), ("owner", _dmUserId), ("name", $"DmPrivate {_suffix}"));
        await InsertMembershipAsync(conn, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _playerUserId, "Player");
        await ExecAsync(conn,
            """
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@id, @campaignId, 'Online', 'Active', NULL, now(), NULL)
            """,
            ("id", _sessionId), ("campaignId", _campaignId));
        await ExecAsync(conn,
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, damage_resistances, damage_vulnerabilities, damage_immunities, level,
                 controlled_by_dm, harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @campaignId, @owner, 'Hero', '{}', 20, 20, 0, 10, ARRAY[]::text[],
                 ARRAY[]::text[], ARRAY[]::text[], 1, false, false, false, now())
            """,
            ("id", _heroId), ("campaignId", _campaignId), ("owner", _playerUserId));

        (await AuthedClient(_dmToken).PutAsJsonAsync(
            "/users/me/api-keys/anthropic", new { ApiKey = DmValidApiKey })).EnsureSuccessStatusCode();
    }

    // ── การรั่วผ่าน history (ตัวที่แก้จริง) ─────────────────────────────────

    [Fact]
    public async Task Player_turn_history_never_contains_what_the_dm_asked_privately()
    {
        await ConsultAsync($"ช่วยวางแผนเรื่องนี้ {SecretMarker}");

        var action = await PlayerActionAsync("ฉันเดินเข้าไปในโรงเตี๊ยม");
        Assert.Equal(HttpStatusCode.OK, action.StatusCode);

        var history = _fake.LastHistoryReceived;
        Assert.NotNull(history);
        // ทั้งข้อความ DM และคำตอบ AI ของเทิร์นลับ (fake echo ข้อความกลับ จึงมี marker ทั้งสองฝั่ง)
        Assert.DoesNotContain(history!, h => h.Text.Contains(SecretMarker));
        Assert.Contains(history!, h => h.Text.Contains("ฉันเดินเข้าไปในโรงเตี๊ยม"));
    }

    /// <summary>
    /// เคสที่ RLS ช่วยไม่ได้: DM เล่นตัวละครของตัวเองผ่าน `/actions` (broadcast) — RLS ปล่อยให้ DM อ่าน
    /// แถวลับได้เพราะดูจากตัวตน · ตัวกันจริงคือ filter ใน `BuildHistoryAsync` ที่ผูกกับการ broadcast
    /// · รันจริง 2026-09-23: เปลี่ยน filter เป็น `includeDmPrivate: true` แล้วเทสต์นี้ล้มตัวเดียวในคลาส
    /// (เทสต์ฝั่งผู้เล่นยังผ่านเพราะ RLS กันให้) — จึงเป็นเทสต์เดียวที่คุม filter ชั้น C#
    /// </summary>
    [Fact]
    public async Task Dm_playing_own_character_via_actions_also_never_sends_private_history()
    {
        var dmCharacterId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await ExecAsync(conn,
                """
                INSERT INTO characters
                    (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                     armor_class, damage_resistances, damage_vulnerabilities, damage_immunities, level,
                     controlled_by_dm, harvest_consent, is_orphan, created_at)
                VALUES
                    (@id, @campaignId, @owner, 'DM Hero', '{}', 20, 20, 0, 10, ARRAY[]::text[],
                     ARRAY[]::text[], ARRAY[]::text[], 1, false, false, false, now())
                """,
                ("id", dmCharacterId), ("campaignId", _campaignId), ("owner", _dmUserId));
        }

        await ConsultAsync($"แผนลับ {SecretMarker}");

        var action = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/actions",
            new { CharacterId = dmCharacterId, Content = "DM Hero opens the door" });
        Assert.Equal(HttpStatusCode.OK, action.StatusCode);

        Assert.DoesNotContain(_fake.LastHistoryReceived!, h => h.Text.Contains(SecretMarker));
    }

    [Fact]
    public async Task Dm_consult_history_still_sees_both_private_and_public_messages()
    {
        await PlayerActionAsync("ฉันสำรวจห้องใต้ดิน");
        await ConsultAsync($"คำถามลับรอบแรก {SecretMarker}");
        await ConsultAsync("คำถามต่อเนื่อง");

        var history = _fake.LastHistoryReceived;
        Assert.NotNull(history);
        Assert.Contains(history!, h => h.Text.Contains("ฉันสำรวจห้องใต้ดิน"));
        Assert.Contains(history!, h => h.Text.Contains(SecretMarker));
    }

    [Fact]
    public async Task Consult_rows_are_written_private_and_action_rows_public()
    {
        await ConsultAsync("ข้อความ DM ลับ");
        await PlayerActionAsync("ข้อความผู้เล่น");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT author_type, dm_private FROM messages WHERE session_id = @s ORDER BY created_at", conn);
        cmd.Parameters.AddWithValue("s", _sessionId);
        var rows = new List<(string Author, bool Private)>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetBoolean(1)));
            }
        }

        Assert.Equal(
            [("Dm", true), ("Ai", true), ("Player", false), ("Ai", false)],
            rows);
    }

    [Fact]
    public async Task Approved_proposal_reply_keeps_the_privacy_of_the_turn_that_created_it()
    {
        // ทั้งสองเทิร์นมีคำว่า roll → Suggest (ค่าเริ่มต้น) สร้าง proposal · ผู้ approve เป็น DM เสมอ
        // แต่คำตอบ AI ต้องได้ dm_private ตามต้นทาง ไม่ใช่ตามคนกด
        var fromPlayer = await PlayerActionAsync("I roll to climb the wall");
        var playerProposalId = await ReadPendingProposalIdAsync(fromPlayer);
        var fromDm = await ConsultAsync($"roll a secret check {SecretMarker}");
        var dmProposalId = await ReadPendingProposalIdAsync(fromDm);

        var playerReplyId = await ApproveAsync(playerProposalId);
        var dmReplyId = await ApproveAsync(dmProposalId);

        Assert.False(await IsPrivateAsync(playerReplyId));
        Assert.True(await IsPrivateAsync(dmReplyId));
    }

    // ── ชั้น RLS (กันได้เองแม้ filter ใน C# หาย) ──────────────────────────────

    [Fact]
    public async Task Player_cannot_read_private_rows_at_the_db_layer_but_dm_can()
    {
        await InsertRawMessageAsync("Dm", dmPrivate: true, "private DM row");
        await InsertRawMessageAsync("Player", dmPrivate: false, "public row");

        Assert.Equal(1L, await CountVisibleMessagesAsAsync(_playerUserId));
        Assert.Equal(2L, await CountVisibleMessagesAsAsync(_dmUserId));
    }

    [Fact]
    public async Task Player_cannot_insert_a_row_claiming_to_be_dm_private()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertMessageInTxAsync(conn, tx, dmPrivate: true));
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task Player_can_still_insert_public_rows()
    {
        var (conn, tx) = await OpenAsAsync(_playerUserId);
        await using var _ = conn;
        await using var __ = tx;

        await InsertMessageInTxAsync(conn, tx, dmPrivate: false);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> ConsultAsync(string content) =>
        AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/consult", new { Content = content });

    private Task<HttpResponseMessage> PlayerActionAsync(string content) =>
        AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/actions",
            new { CharacterId = _heroId, Content = content });

    private static async Task<Guid> ReadPendingProposalIdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return body.GetProperty("pending").GetProperty("proposalId").GetGuid();
    }

    private async Task<Guid> ApproveAsync(Guid proposalId)
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/proposals/{proposalId}/resolve", new { Action = "approve" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return body.GetProperty("messageId").GetGuid();
    }

    private static async Task<bool> IsPrivateAsync(Guid messageId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT dm_private FROM messages WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", messageId);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task InsertRawMessageAsync(string authorType, bool dmPrivate, string content)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn,
            """
            INSERT INTO messages (id, session_id, author_type, content, source, dm_private, created_at)
            VALUES (@id, @s, @author, @content, 'Player', @private, now())
            """,
            ("id", Guid.NewGuid()), ("s", _sessionId), ("author", authorType),
            ("content", content), ("private", dmPrivate));
    }

    private async Task<long> CountVisibleMessagesAsAsync(Guid userId)
    {
        var (conn, tx) = await OpenAsAsync(userId);
        await using var _ = conn;
        await using var __ = tx;
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM messages WHERE session_id = @s", conn, tx);
        cmd.Parameters.AddWithValue("s", _sessionId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task InsertMessageInTxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, bool dmPrivate)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO messages (id, session_id, author_type, content, source, dm_private, created_at)
            VALUES (@id, @s, 'Player', 'from player', 'Player', @private, now())
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("s", _sessionId);
        cmd.Parameters.AddWithValue("private", dmPrivate);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> OpenAsAsync(Guid userId)
    {
        var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(
                         "SELECT set_config('app.user_id', @u, true), set_config('app.campaign_id', @c, true)", conn, tx))
        {
            cmd.Parameters.AddWithValue("u", userId.ToString());
            cmd.Parameters.AddWithValue("c", _campaignId.ToString());
            await cmd.ExecuteScalarAsync();
        }

        return (conn, tx);
    }

    private async Task InsertMembershipAsync(NpgsqlConnection conn, Guid userId, string role) =>
        await ExecAsync(conn,
            "INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at) VALUES (@id, @c, @u, @role, now())",
            ("id", Guid.NewGuid()), ("c", _campaignId), ("u", userId), ("role", role));

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/register", new
        {
            Email = $"dm-private-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"DmPrivate {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = @id", ("id", _campaignId));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)", ("ids", new[] { _dmUserId, _playerUserId }));
        await _factory.DisposeAsync();
    }

    private sealed record AuthResponse(string Token, Guid UserId);
}
