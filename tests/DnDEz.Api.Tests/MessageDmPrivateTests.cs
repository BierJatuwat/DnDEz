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
/// **2026-09-24 (Phase 4 — ผู้ช่วย DM ช่องเดียว) — เขียนใหม่ทั้งไฟล์** เดิมเทสต์ชุดนี้พิสูจน์ว่า `/consult`
/// (DM ปรึกษา AI ส่วนตัว) กับ `/actions` (เทิร์นผู้เล่น broadcast) เขียนลง `messages` ตารางเดียวกันแล้วกรอง
/// ด้วย `dm_private` — ตอนนี้ `/consult` ถูกลบไปแล้ว แทนที่ด้วย `/assistant` (`AssistantRunner`) ซึ่ง**ไม่แตะ
/// ตาราง `messages` เลย** บทสนทนาของ DM ทั้งหมดอยู่ใน `consult_messages` (ตาราง `dm_only` แยกต่างหาก อยู่แล้ว
/// ดู `ConsultMessageRlsTests`) ส่วน `messages` ตอนนี้มีแต่แถวที่มาจากเทิร์นผู้เล่น (`/actions`, เสมอ
/// `DmPrivate = false`) กับ tool ที่ AI แต่งข้อความส่งถึงผู้เล่น (`narrate_to_players`/`npc_say`, เสมอ
/// `DmPrivate = false` เช่นกัน — ดู `AssistantTableToolsTests`) ช่องรั่วเดิมจึงเป็นไปไม่ได้แล้วโดยโครงสร้าง
/// ตาราง ไม่ใช่แค่โดย filter ชั้น C# — เทสต์ด้านล่างยืนยันโครงสร้างนั้นตรง ๆ (แทนที่จะพิสูจน์ filter ที่ไม่มี
/// อยู่แล้ว)
///
/// ชั้น RLS ยังทดสอบด้วย raw SQL ผ่าน role `dndez_app` แยกจากชั้น endpoint เหมือนเดิม — คอลัมน์
/// `dm_private` ยังมีอยู่ในตาราง (defense-in-depth) แม้ path ปกติจะไม่มีทางเขียน `true` ได้อีกแล้วก็ตาม —
/// แบบเดียวกับ `ConsultMessageRlsTests`
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

    // ── การแยกตารางจริง (ตัวที่แทนที่การกันรั่วผ่าน filter เดิม) ────────────────

    [Fact]
    public async Task Player_turn_history_never_contains_what_the_dm_asked_privately()
    {
        await AssistantAsync($"ช่วยวางแผนเรื่องนี้ {SecretMarker}");

        var action = await PlayerActionAsync("ฉันเดินเข้าไปในโรงเตี๊ยม");
        Assert.Equal(HttpStatusCode.OK, action.StatusCode);

        var history = _fake.LastHistoryReceived;
        Assert.NotNull(history);
        Assert.DoesNotContain(history!, h => h.Text.Contains(SecretMarker));
        Assert.Contains(history!, h => h.Text.Contains("ฉันเดินเข้าไปในโรงเตี๊ยม"));
    }

    /// <summary>เคสที่ RLS ช่วยไม่ได้: DM เล่นตัวละครของตัวเองผ่าน `/actions` (broadcast) — RLS ปล่อยให้ DM
    /// อ่านแถวของแคมเปญตัวเองได้ตามปกติ แต่ history ของเทิร์นผู้เล่นมาจาก `messages` เท่านั้น (`BuildHistoryAsync`
    /// ใน `OrchestratorTurnRunner`) ซึ่งไม่มีทางมีแถวของ `/assistant` ปนอยู่เลยเพราะคนละตาราง</summary>
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

        await AssistantAsync($"แผนลับ {SecretMarker}");

        var action = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/actions",
            new { CharacterId = dmCharacterId, Content = "DM Hero opens the door" });
        Assert.Equal(HttpStatusCode.OK, action.StatusCode);

        Assert.DoesNotContain(_fake.LastHistoryReceived!, h => h.Text.Contains(SecretMarker));
    }

    [Fact]
    public async Task Assistant_chat_is_stored_in_consult_messages_never_in_the_messages_table()
    {
        await AssistantAsync($"ข้อความ DM ลับ {SecretMarker}");
        await PlayerActionAsync("ข้อความผู้เล่น");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        // `messages` ต้องมีแต่แถวของเทิร์นผู้เล่น (public เสมอ) — ไม่มีแถวของ DM เลยแม้แต่แถวเดียว
        await using (var cmd = new NpgsqlCommand(
            "SELECT author_type, dm_private FROM messages WHERE session_id = @s ORDER BY created_at", conn))
        {
            cmd.Parameters.AddWithValue("s", _sessionId);
            var rows = new List<(string Author, bool Private)>();
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    rows.Add((reader.GetString(0), reader.GetBoolean(1)));
                }
            }

            Assert.Equal([("Player", false), ("Ai", false)], rows);
        }

        // บทสนทนาลับของ DM ต้องไปอยู่ใน `consult_messages` จริง ไม่ใช่หายไปเฉย ๆ
        await using (var cmd = new NpgsqlCommand(
            "SELECT content FROM consult_messages WHERE campaign_id = @c AND dm_user_id = @d", conn))
        {
            cmd.Parameters.AddWithValue("c", _campaignId);
            cmd.Parameters.AddWithValue("d", _dmUserId);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Contains(SecretMarker, reader.GetString(0));
        }
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

    private Task<HttpResponseMessage> AssistantAsync(string content) =>
        AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/assistant", new { Content = content, SessionId = _sessionId });

    private Task<HttpResponseMessage> PlayerActionAsync(string content) =>
        AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/actions",
            new { CharacterId = _heroId, Content = content });

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
