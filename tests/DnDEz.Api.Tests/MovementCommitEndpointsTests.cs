using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DnDEz.Api.Realtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 10 (ส่วน ค) — เดินตามเส้นทางจริง + กลไก interrupt ของ opportunity attack
///
/// นี่คือส่วนที่ dispatch เรียกว่า "จุดที่ยากที่สุด" เพราะ turn state เดิมเดินหน้าอย่างเดียว
/// (DM กด "เทิร์นถัดไป") ไม่เคยมีอะไรหยุดค้างกลางทางแล้วรอ input จากคนอื่นมาก่อน
///
/// **สัญญาของ endpoint**
///   POST .../movement/commit            → เดินจริง หักระยะ ขยับ token · ถ้ามี OA จะ "หยุดค้าง"
///   POST .../movement/reactions/{id}/respond → คนถือสิทธิ์ตอบ สวน/ไม่สวน แล้วการเดินไปต่อ
///
/// **สามทางที่จบเหมือนกันหมด** (นี่คือหัวใจของการออกแบบ): ผู้ถือสิทธิ์กดไม่สวน · หมดเวลา ·
/// Redis ล่ม/คีย์หาย — ทั้งสามให้ผลเดียวกันคือ "ไม่มี OA แล้วการเดินจบตามเส้นทางเดิม" ตรงกับที่
/// player-side-design §4 ล็อกไว้ว่า *"หมดเวลา = ถือว่าไม่ใช้ reaction แล้วเกมเดินต่อ"*
/// จึงไม่มี failure path ใหม่ที่ต้องออกแบบเพิ่มเลย
/// </summary>
public class MovementCommitEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "movement-commit-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sceneId = Guid.NewGuid();
    private readonly Guid _heroTokenId = Guid.NewGuid();

    private readonly Guid _heroId = Guid.NewGuid();
    private readonly Guid _goblinId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Commit Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertSessionAsync(conn, _sessionId, _campaignId);

        await InsertCharacterAsync(conn, _heroId, "อัลดริค", _playerUserId, controlledByDm: false);
        await InsertCharacterAsync(conn, _goblinId, "ก็อบลินยาม", null, controlledByDm: true);

        // ฮีโร่ (0,0) · ก็อบลิน (1,1) เอื้อม 5 ฟุต → เดินไป (3,0) จะหลุดระยะตอนถึง (3,0)
        var tokens = $$"""
            [
                {"id":"{{_heroTokenId}}","entityType":"Character","entityId":"{{_heroId}}","x":0,"y":0},
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_goblinId}}","x":1,"y":1}
            ]
            """;
        await InsertSceneAsync(conn, _sceneId, _sessionId, tokens);
    }

    // ── เดินแบบไม่มีใครสวน ───────────────────────────────────────────────────

    /// <summary>เส้นทางที่ไม่หลุดระยะใคร = จบทันที ไม่มีการหยุดค้าง และ token ไปอยู่ปลายทางจริง</summary>
    [Fact]
    public async Task Move_without_triggers_completes_immediately()
    {
        var body = await CommitAsync(_playerToken, [(0, 0), (1, 0), (2, 1)]);

        Assert.Equal("Completed", body.Status);
        Assert.Null(body.PendingReactionId);
        Assert.Equal(2, await TokenXAsync());
    }

    /// <summary>Disengage = ไม่มีใครได้สวน จึงเดินรวดเดียวจบแม้เส้นทางจะหลุดระยะ
    /// (§3.4: ผู้เล่นต้องเลือกได้ระหว่าง "ทางที่ถูกที่สุด" กับ "ทางที่ปลอดภัยที่สุด")</summary>
    [Fact]
    public async Task Disengaging_completes_without_pausing()
    {
        var body = await CommitAsync(_playerToken, [(0, 0), (1, 0), (2, 0), (3, 0)], disengage: true);

        Assert.Equal("Completed", body.Status);
        Assert.Equal(3, await TokenXAsync());
    }

    /// <summary>เส้นทางยาวเกิน speed = 400 และ **token ต้องไม่ขยับเลย** (ไม่เดินไปครึ่งทางแล้วค้าง)</summary>
    [Fact]
    public async Task Unaffordable_path_is_rejected_and_the_token_does_not_move()
    {
        var path = Enumerable.Range(0, 9).Select(i => (i, 0)).ToArray();

        var response = await RawCommitAsync(_playerToken, path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await TokenXAsync());
    }

    // ── หยุดค้างรอ reaction ──────────────────────────────────────────────────

    /// <summary>
    /// **หัวใจของส่วน (ค)** — เดินหลุดระยะก็อบลิน → การเดินหยุดค้างที่ **ช่องสุดท้ายที่ยังอยู่ในระยะ**
    /// ไม่ใช่ปลายทาง ตามตัวบท SRD *"The attack occurs right before it leaves your reach"*
    ///
    /// ถ้าหยุดผิดช่อง ระยะที่ก็อบลินสวนจะผิดตามไปด้วย (สวนจากระยะที่เอื้อมไม่ถึงจริง)
    /// </summary>
    [Fact]
    public async Task Move_leaving_reach_pauses_at_the_square_the_attack_comes_from()
    {
        var body = await CommitAsync(_playerToken, [(0, 0), (1, 0), (2, 0), (3, 0)]);

        Assert.Equal("AwaitingReaction", body.Status);
        Assert.NotNull(body.PendingReactionId);
        Assert.Equal(_goblinId, body.AwaitingThreatId);
        Assert.Equal("ก็อบลินยาม", body.AwaitingThreatName);
        // token ค้างอยู่ที่ (2,0) — ยังไม่ถึง (3,0)
        Assert.Equal(2, await TokenXAsync());
        // ต้องมีเวลานับถอยหลังส่งไปให้ UI (ดีฟอลต์แคมเปญ 30 วินาที)
        Assert.Equal(30, body.ReactionTimeoutSeconds);
    }

    /// <summary>ผู้ถือสิทธิ์กด "ไม่สวน" → การเดินไปต่อจนจบเส้นทาง</summary>
    [Fact]
    public async Task Declining_the_reaction_resumes_the_move()
    {
        var paused = await CommitAsync(_playerToken, [(0, 0), (1, 0), (2, 0), (3, 0)]);

        var body = await RespondAsync(_dmToken, paused.PendingReactionId!.Value, useReaction: false);

        Assert.Equal("Completed", body.Status);
        Assert.False(body.ReactionUsed);
        Assert.Equal(3, await TokenXAsync());
    }

    /// <summary>ผู้ถือสิทธิ์กด "สวน" → การเดินก็ไปต่อจนจบเหมือนกัน (OA ไม่หยุดการเคลื่อนที่
    /// ตามกฎ — มันเกิด "ก่อน" ออกจากระยะ แล้วเป้าก็เดินต่อ) แต่ผลลัพธ์ต้องบอกว่ามีการสวนเกิดขึ้น</summary>
    [Fact]
    public async Task Accepting_the_reaction_resumes_the_move_and_reports_it_was_used()
    {
        var paused = await CommitAsync(_playerToken, [(0, 0), (1, 0), (2, 0), (3, 0)]);

        var body = await RespondAsync(_dmToken, paused.PendingReactionId!.Value, useReaction: true);

        Assert.Equal("Completed", body.Status);
        Assert.True(body.ReactionUsed);
        Assert.Equal(3, await TokenXAsync());
    }

    /// <summary>คนที่ไม่ได้ถือสิทธิ์ตอบแทนไม่ได้ — ผู้เล่นที่กำลังถูกสวนจะกด "ไม่สวน" แทนก็อบลินเองไม่ได้</summary>
    [Fact]
    public async Task Only_the_rights_holder_may_respond()
    {
        var paused = await CommitAsync(_playerToken, [(0, 0), (1, 0), (2, 0), (3, 0)]);

        var response = await RawRespondAsync(_playerToken, paused.PendingReactionId!.Value, useReaction: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // และการเดินต้องยังค้างอยู่ที่เดิม ไม่ถูกดันไปต่อโดยคนที่ไม่มีสิทธิ์
        Assert.Equal(2, await TokenXAsync());
    }

    /// <summary>ตอบซ้ำหลังจบไปแล้ว = 404 (คีย์ถูกลบทิ้งแล้ว) ไม่ใช่ 500 และต้องไม่ทำให้ token ขยับซ้ำ</summary>
    [Fact]
    public async Task Responding_twice_returns_404()
    {
        var paused = await CommitAsync(_playerToken, [(0, 0), (1, 0), (2, 0), (3, 0)]);
        await RespondAsync(_dmToken, paused.PendingReactionId!.Value, useReaction: false);

        var second = await RawRespondAsync(_dmToken, paused.PendingReactionId.Value, useReaction: false);

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(3, await TokenXAsync());
    }

    // ── หมดเวลาแล้วต้องเดินต่อเองโดยไม่ค้าง (ข้อ 4 ของ dispatch) ─────────────

    /// <summary>
    /// **ข้อกำหนดที่ว่า "หมดเวลาแล้วต้องเดินต่อได้เองโดยไม่ค้าง"** — ไม่มีใครกดอะไรเลย ไม่มี request
    /// จาก client ใด ๆ เข้ามา แต่การเดินต้องจบเองเมื่อพ้น deadline
    ///
    /// เทสต์นี้เร่งเวลาโดยเขียน deadline ที่ผ่านไปแล้วลง Redis ตรง ๆ แล้วเรียก sweeper เอง
    /// (แทนการรอ 30 วินาทีจริงในเทสต์) — ตัว sweeper เป็นตัวเดียวกับที่ BackgroundService เรียกตามเวลา
    /// </summary>
    [Fact]
    public async Task Expired_pending_reaction_is_swept_and_the_move_completes_on_its_own()
    {
        var paused = await CommitAsync(_playerToken, [(0, 0), (1, 0), (2, 0), (3, 0)]);
        Assert.Equal(2, await TokenXAsync());
        Assert.NotNull(paused.PendingReactionId);

        // เร่งเวลาโดยบอก sweeper ว่า "ตอนนี้" คืออีกหนึ่งนาทีข้างหน้า แทนการรอ 30 วินาทีจริง —
        // sweeper รับเวลาเป็นพารามิเตอร์โดยตั้งใจ จะได้ทดสอบได้โดยไม่ต้องมี API เฉพาะเทสต์
        using var scope = _factory.Services.CreateScope();
        var sweeper = scope.ServiceProvider.GetRequiredService<PendingReactionSweeper>();
        var swept = await sweeper.SweepExpiredAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        // sweeper กวาดทั้งระบบตามการออกแบบ (ต้องเจอทุกแคมเปญ) จำนวนที่คืนมาจึงรวมของเทสต์อื่นที่
        // รันคู่กันด้วย — สิ่งที่เทสต์นี้ยืนยันคือ **การเดินของเราจบเอง** ไม่ใช่ตัวเลขรวมทั้งระบบ
        Assert.True(swept >= 1, $"ต้องกวาดอย่างน้อยรายการของเทสต์นี้ แต่ได้ {swept}");
        Assert.Equal(3, await TokenXAsync());
    }

    /// <summary>ยังไม่หมดเวลา sweeper ต้องไม่ไปยุ่ง — ไม่งั้นผู้ถือสิทธิ์จะถูกตัดสิทธิ์ก่อนเวลา</summary>
    [Fact]
    public async Task Sweeper_leaves_a_pending_reaction_that_has_not_expired_alone()
    {
        var paused = await CommitAsync(_playerToken, [(0, 0), (1, 0), (2, 0), (3, 0)]);

        using var scope = _factory.Services.CreateScope();
        var sweeper = scope.ServiceProvider.GetRequiredService<PendingReactionSweeper>();
        await sweeper.SweepExpiredAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // ยังไม่ถึง deadline → token ต้องยังค้างอยู่ที่เดิม และยังตอบได้อยู่ (สิทธิ์ไม่ถูกตัดก่อนเวลา)
        Assert.Equal(2, await TokenXAsync());
        var stillAnswerable = await RawRespondAsync(
            _dmToken, paused.PendingReactionId!.Value, useReaction: false);
        Assert.Equal(HttpStatusCode.OK, stillAnswerable.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string CommitUrl() => $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/commit";

    private string RespondUrl(Guid reactionId) =>
        $"/campaigns/{_campaignId}/sessions/{_sessionId}/movement/reactions/{reactionId}/respond";

    private async Task<HttpResponseMessage> RawCommitAsync(
        string token, (int X, int Y)[] path, bool disengage = false) =>
        await AuthedClient(token).PostAsJsonAsync(CommitUrl(), new
        {
            CharacterId = _heroId,
            Path = path.Select(p => new { X = (double)p.X, Y = (double)p.Y }).ToArray(),
            Disengage = disengage,
        });

    private async Task<CommitResponse> CommitAsync(
        string token, (int X, int Y)[] path, bool disengage = false)
    {
        var response = await RawCommitAsync(token, path, disengage);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CommitResponse>(JsonOptions))!;
    }

    private async Task<HttpResponseMessage> RawRespondAsync(string token, Guid reactionId, bool useReaction) =>
        await AuthedClient(token).PostAsJsonAsync(RespondUrl(reactionId), new { UseReaction = useReaction });

    private async Task<CommitResponse> RespondAsync(string token, Guid reactionId, bool useReaction)
    {
        var response = await RawRespondAsync(token, reactionId, useReaction);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CommitResponse>(JsonOptions))!;
    }

    /// <summary>อ่านพิกัด X ของ token ฮีโร่จาก Postgres ตรง ๆ — ไม่เชื่อ response ของ endpoint
    /// ว่าเขียนจริง ต้องเห็นในฐานข้อมูลเอง</summary>
    private async Task<int> TokenXAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT (t->>'x')::float
            FROM scenes s, jsonb_array_elements(s.tokens) t
            WHERE s.id = @sceneId AND (t->>'id')::uuid = @tokenId
            """, conn);
        cmd.Parameters.AddWithValue("sceneId", _sceneId);
        cmd.Parameters.AddWithValue("tokenId", _heroTokenId);
        var result = await cmd.ExecuteScalarAsync();
        return (int)Math.Round((double)result!);
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"commit-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Commit {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthTokenResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task InsertCampaignAsync(
        NpgsqlConnection conn, Guid id, Guid ownerUserId, string name)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES
                (@id, @ownerId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("ownerId", ownerUserId);
        cmd.Parameters.AddWithValue("name", name);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertMembershipAsync(
        NpgsqlConnection conn, Guid campaignId, Guid userId, string role)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, @role, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("role", role);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertSessionAsync(NpgsqlConnection conn, Guid id, Guid campaignId)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@id, @campaignId, 'Online', 'Active', NULL, now(), NULL)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, string name, Guid? ownerUserId, bool controlledByDm)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, speed_feet, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, npc_stats_visible_to_party,
                 harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @campaignId, @ownerUserId, @name, '{}'::jsonb, 20, 20, 0, 12, 0, 30,
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, @controlledByDm, false,
                 false, false, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", _campaignId);
        cmd.Parameters.AddWithValue("ownerUserId", (object?)ownerUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("controlledByDm", controlledByDm);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertSceneAsync(
        NpgsqlConnection conn, Guid id, Guid sessionId, string tokensJson)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
            VALUES (@id, @sessionId, 'Commit Scene', '{}', @tokens::jsonb,
                '{"revealedAreas":[{"id":"44444444-4444-4444-4444-444444444444","x":0,"y":0,"radius":12}]}',
                true)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("sessionId", sessionId);
        cmd.Parameters.AddWithValue("tokens", tokensJson);
        await cmd.ExecuteNonQueryAsync();
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

        await using (var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn))
        {
            deleteUsers.Parameters.AddWithValue("ids", new[] { _dmUserId, _playerUserId });
            await deleteUsers.ExecuteNonQueryAsync();
        }

        _factory.Dispose();
    }

    private record AuthTokenResponse(string Token, Guid UserId);

    private record CommitResponse(
        Guid CharacterId,
        string Status,
        int CostFeet,
        Guid? PendingReactionId,
        Guid? AwaitingThreatId,
        string? AwaitingThreatName,
        int? ReactionTimeoutSeconds,
        bool ReactionUsed);
}
