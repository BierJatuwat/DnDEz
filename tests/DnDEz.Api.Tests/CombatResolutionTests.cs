using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint B Part 3 — check/attack/damage resolution เรียก CheckResolver/AttackResolver/
/// DamageResolver/HitPointResolver ต่อกันเป็น flow เดียว (DnDEz.Rules 221 เทสต์ผ่านแล้ว) ผ่าน API จริง
///
/// ขอบเขต (ดู PROJECT-CONTEXT §4 "Sprint B" Part 3):
///  • รองรับแค่ DiceMode.SystemRoll เหมือน Part 1 — DmEntered reject 400
///  • damage roll เกิดเฉพาะตอน hit/critical เท่านั้น — miss/automatic-miss ไม่แตะ HP เลย
///  • seed ไม่โผล่ใน response เด็ดขาด (เหมือน Part 1)
///
/// contract:
///
/// POST /campaigns/{campaignId}/sessions/{sessionId}/characters/{characterId}/checks  [Authorize]
///   body { Expression, Advantage?, Dc (required, 1..40) }
///   200 OK + { Id, CharacterId, Expression, Rolls, Total, Dc, Success }
///   400 เมื่อ input ไม่ถูก / campaign เป็น DmEntered · 404 เมื่อไม่ใช่สมาชิก/session หรือ character ไม่มี
///
/// POST /campaigns/{campaignId}/sessions/{sessionId}/characters/{characterId}/attacks  [Authorize]
///   body { Expression, Advantage?, TargetCharacterId (required), DamageComponents (required, 1..20
///     ของ { Expression, Type }) }
///   200 OK + { Id, AttackerCharacterId, TargetCharacterId, Expression, Rolls, Total, TargetArmorClass,
///     Outcome, Hit, DamageComponents?, TotalDamage?, TargetPreviousHp?, TargetNewHp?,
///     TargetPreviousTempHp?, TargetNewTempHp?, TargetDroppedToZero?, TargetIsInstantDeath? }
///   damage fields ทั้งหมดเป็น null เมื่อ Hit=false (miss/automatic-miss ไม่มี damage roll เกิดขึ้นจริง)
///
/// ห้าม mock การทอยเต๋า (CSPRNG จริงตามธรรมเนียมทั้งโปรเจกต์) — เทสต์ที่ต้องการ outcome เฉพาะ
/// (hit-guaranteed / miss-only) ใช้ AC สุดขั้ว (1 หรือ 50) + retry loop ที่ bound จำนวนรอบไว้ชัดเจน
/// เพื่อข้าม 5% ของ AutomaticMiss/CriticalHit ที่ override AC เสมอไม่ว่า modifier จะเป็นเท่าไหร่
/// </summary>
public class CombatResolutionTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "combat-resolution-secret-1";
    private const int MaxRetries = 30;

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _campaignDmEnteredId = Guid.NewGuid();

    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _sessionInDmEnteredCampaignId = Guid.NewGuid();

    private readonly Guid _attackerId = Guid.NewGuid();
    private readonly Guid _attackerInDmEnteredCampaignId = Guid.NewGuid();
    private readonly Guid _targetInDmEnteredCampaignId = Guid.NewGuid();
    private readonly Guid _lowAcTargetId = Guid.NewGuid();   // AC=1  — ไม่มีทาง Miss ได้เลยนอกจาก AutomaticMiss
    private readonly Guid _resistantTargetId = Guid.NewGuid(); // AC=1, resist Fire

    // Sprint F งานที่ 3.1 — fixtures สำหรับเทสต์ RequireCharacterActorAsync (character ที่ "ลงมือ"
    // ต้องเป็นเจ้าของเองหรือ DM คุม ControlledByDm)
    private readonly Guid _dmControlledMonsterId = Guid.NewGuid(); // ControlledByDm=true, owner=null
    private readonly Guid _otherPlayersCharacterId = Guid.NewGuid(); // owner=outsider, ControlledByDm=false

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _memberUserId;
    private string _memberToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_memberUserId, _memberToken) = await RegisterAsync("member");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (campaignId, name, diceMode) in new[]
                 {
                     (_campaignId, $"Combat SystemRoll Campaign {_suffix}", "SystemRoll"),
                     (_campaignDmEnteredId, $"Combat DmEntered Campaign {_suffix}", "DmEntered"),
                 })
        {
            await using var insertCampaign = new NpgsqlCommand(
                """
                INSERT INTO campaigns
                    (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
                VALUES
                    (@id, @ownerId, @name, 'Guided', 'srd2024', @diceMode, 'PartyVisible', now())
                """, conn);
            insertCampaign.Parameters.AddWithValue("id", campaignId);
            insertCampaign.Parameters.AddWithValue("ownerId", _dmUserId);
            insertCampaign.Parameters.AddWithValue("name", name);
            insertCampaign.Parameters.AddWithValue("diceMode", diceMode);
            await insertCampaign.ExecuteNonQueryAsync();
        }

        // DM ต้องมีแถว campaign_members ของตัวเองด้วย (role 'Dm') ไม่งั้น RLS `campaign_member_read`
        // (app_is_member_of) กรองแคมเปญทิ้งไปเลยตอน DM เรียก endpoint ด้วยโทเคนของตัวเอง — ปกติ
        // `app_create_campaign()` insert แถวนี้ให้อัตโนมัติ แต่ fixture นี้ insert ตรงเข้า `campaigns`
        // เพื่อควบคุม id/dice_mode เอง จึงต้องเพิ่มแถวนี้เองด้วย
        await using (var insertDmMembership = new NpgsqlCommand(
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, 'Dm', now())
            """, conn))
        {
            insertDmMembership.Parameters.AddWithValue("id", Guid.NewGuid());
            insertDmMembership.Parameters.AddWithValue("campaignId", _campaignId);
            insertDmMembership.Parameters.AddWithValue("userId", _dmUserId);
            await insertDmMembership.ExecuteNonQueryAsync();
        }

        await using (var insertMembership = new NpgsqlCommand(
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, 'Player', now())
            """, conn))
        {
            insertMembership.Parameters.AddWithValue("id", Guid.NewGuid());
            insertMembership.Parameters.AddWithValue("campaignId", _campaignId);
            insertMembership.Parameters.AddWithValue("userId", _memberUserId);
            await insertMembership.ExecuteNonQueryAsync();
        }

        await using (var insertMembership2 = new NpgsqlCommand(
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, 'Player', now())
            """, conn))
        {
            insertMembership2.Parameters.AddWithValue("id", Guid.NewGuid());
            insertMembership2.Parameters.AddWithValue("campaignId", _campaignDmEnteredId);
            insertMembership2.Parameters.AddWithValue("userId", _memberUserId);
            await insertMembership2.ExecuteNonQueryAsync();
        }

        await InsertSessionAsync(conn, _sessionId, _campaignId);
        await InsertSessionAsync(conn, _sessionInDmEnteredCampaignId, _campaignDmEnteredId);

        // _attackerId ต้องมีเจ้าของเป็น _memberUserId (ไม่ใช่ NULL เหมือนเดิม) — ตั้งแต่ Sprint F
        // งานที่ 3.1 endpoint บังคับ RequireCharacterActorAsync (เจ้าของเอง/DM คุม ControlledByDm)
        // แทนเช็คแค่ "มีอยู่จริง" เทสต์ happy-path เดิมทั้งหมดที่ใช้ _memberToken เล่นแทน _attackerId
        // จึงต้องให้ _memberUserId เป็นเจ้าของจริงถึงจะยังผ่าน
        await InsertCharacterAsync(conn, _attackerId, _campaignId, "Attacker", armorClass: 10, ownerUserId: _memberUserId);
        // ต้องมี character ในแคมเปญ DmEntered ด้วย ไม่งั้นเทสต์ dice-mode-rejection จะ 404 ที่
        // character-existence check ก่อนถึง DiceMode check เลย (คนละเงื่อนไขกับที่ตั้งใจทดสอบ)
        await InsertCharacterAsync(
            conn, _attackerInDmEnteredCampaignId, _campaignDmEnteredId, "Attacker (DmEntered)", armorClass: 10,
            ownerUserId: _memberUserId);
        await InsertCharacterAsync(conn, _targetInDmEnteredCampaignId, _campaignDmEnteredId, "Target (DmEntered)", armorClass: 10);
        await InsertCharacterAsync(conn, _lowAcTargetId, _campaignId, "Low AC Target", armorClass: 1);
        await InsertCharacterAsync(
            conn, _resistantTargetId, _campaignId, "Fire Resistant Target", armorClass: 1,
            damageResistances: new[] { "Fire" });

        await InsertCharacterAsync(
            conn, _dmControlledMonsterId, _campaignId, "DM Monster", armorClass: 10, controlledByDm: true);
        await InsertCharacterAsync(
            conn, _otherPlayersCharacterId, _campaignId, "Other Player's Character", armorClass: 10,
            ownerUserId: _outsiderUserId);
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

    private static async Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, string name, int armorClass,
        string[]? damageResistances = null, Guid? ownerUserId = null, bool controlledByDm = false)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, damage_resistances, damage_vulnerabilities, damage_immunities, level,
                 controlled_by_dm, harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @campaignId, @ownerUserId, @name, '{}', 20, 20, 0, @armorClass, @damageResistances,
                 ARRAY[]::text[], ARRAY[]::text[], 1, @controlledByDm, false, false, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("ownerUserId", (object?)ownerUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("armorClass", armorClass);
        cmd.Parameters.AddWithValue("damageResistances", damageResistances ?? Array.Empty<string>());
        cmd.Parameters.AddWithValue("controlledByDm", controlledByDm);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var deleteCampaigns = new NpgsqlCommand(
            "DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("ids", new[] { _campaignId, _campaignDmEnteredId });
            await deleteCampaigns.ExecuteNonQueryAsync();
        }

        var userIds = new[] { _dmUserId, _memberUserId, _outsiderUserId }
            .Where(id => id != Guid.Empty).ToArray();
        await using (var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn))
        {
            deleteUsers.Parameters.AddWithValue("ids", userIds);
            await deleteUsers.ExecuteNonQueryAsync();
        }

        await _factory.DisposeAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"combat-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Combat {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ── SignalR helper (Sprint C Part 3 — copied/adapted from CampaignHubTests/DiceRollCrudTests'
    //    BuildConnection so HpUpdated broadcast tests below can join the campaign group) ──
    private HubConnection BuildConnection(string? token)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/campaign"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = _factory.Server.CreateWebSocketClient();
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .Build();
    }

    private async Task<(int CurrentHp, int CurrentTempHp)> ReadTargetHpAsync(Guid characterId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT current_hp, current_temp_hp FROM characters WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", characterId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    // ── checks — Dc=1 (ทอย 1d20 ต่ำสุด=1 >= 1 เสมอ) / Dc=21 (สูงสุด 1d20=20 < 21 เสมอ) จึง
    //    deterministic ได้โดยไม่ต้อง retry ────────────────────────────────────────────────

    [Fact]
    public async Task Post_checks_with_dc_1_always_succeeds_and_persists_row()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/checks",
            new { Expression = "1d20", Dc = 1 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CheckResolutionResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(_attackerId, body!.CharacterId);
        Assert.Equal(1, body.Dc);
        Assert.True(body.Success);
        Assert.InRange(body.Total, 1, 20);

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var select = new NpgsqlCommand(
            "SELECT character_id, seed FROM dice_rolls WHERE id = @id", conn);
        select.Parameters.AddWithValue("id", body.Id);
        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(_attackerId, reader.GetGuid(0));
        Assert.False(await reader.IsDBNullAsync(1));
    }

    [Fact]
    public async Task Post_checks_with_dc_21_always_fails()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/checks",
            new { Expression = "1d20", Dc = 21 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CheckResolutionResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.False(body!.Success);
    }

    [Fact]
    public async Task Post_checks_response_never_exposes_seed_field()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/checks",
            new { Expression = "1d20", Dc = 10 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawJson = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("seed", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Post_checks_without_dc_returns_400()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/checks",
            new { Expression = "1d20" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_checks_with_dc_out_of_range_returns_400()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/checks",
            new { Expression = "1d20", Dc = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_checks_with_malformed_expression_returns_400()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/checks",
            new { Expression = "not-a-dice-expression", Dc = 10 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_checks_for_unknown_character_returns_404()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{Guid.NewGuid()}/checks",
            new { Expression = "1d20", Dc = 10 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_checks_into_campaign_with_dm_entered_dice_mode_returns_400()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignDmEnteredId}/sessions/{_sessionInDmEnteredCampaignId}/characters/{_attackerInDmEnteredCampaignId}/checks",
            new { Expression = "1d20", Dc = 10 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_checks_by_non_member_returns_404()
    {
        var response = await AuthedClient(_outsiderToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/checks",
            new { Expression = "1d20", Dc = 10 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_checks_without_jwt_returns_401()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/checks",
            new { Expression = "1d20", Dc = 10 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── checks — authorization (Sprint F งานที่ 3.1) ─────────────────────────
    // เจ้าของตัวละครเล่นแทนตัวเอง (Post_checks_with_dc_1/21_*, Post_checks_response_never_exposes_seed
    // ฯลฯ ด้านบนใช้ _memberToken เล่นแทน _attackerId ที่เป็นเจ้าของอยู่แล้ว = คุมกรณี "เจ้าของสำเร็จ"
    // ครบอยู่แล้ว ไม่ต้องเพิ่มซ้ำ) — ที่เหลือคือ DM override และ non-owner/non-DM

    [Fact]
    public async Task Post_checks_by_dm_as_dm_controlled_character_succeeds()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_dmControlledMonsterId}/checks",
            new { Expression = "1d20", Dc = 1 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CheckResolutionResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(_dmControlledMonsterId, body!.CharacterId);
    }

    [Fact]
    public async Task Post_checks_by_non_owner_non_dm_returns_403_and_creates_no_row()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var countBefore = new NpgsqlCommand(
            "SELECT count(*) FROM dice_rolls WHERE character_id = @characterId", conn);
        countBefore.Parameters.AddWithValue("characterId", _otherPlayersCharacterId);
        var before = (long)(await countBefore.ExecuteScalarAsync())!;

        // _memberUserId เป็นสมาชิกแคมเปญ (เห็น campaign/session ได้ผ่าน RLS) แต่ไม่ใช่เจ้าของ
        // _otherPlayersCharacterId (owner = outsider) และไม่ใช่ DM — ต้องได้ 403 ไม่ใช่ 404
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_otherPlayersCharacterId}/checks",
            new { Expression = "1d20", Dc = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var countAfter = new NpgsqlCommand(
            "SELECT count(*) FROM dice_rolls WHERE character_id = @characterId", conn);
        countAfter.Parameters.AddWithValue("characterId", _otherPlayersCharacterId);
        var after = (long)(await countAfter.ExecuteScalarAsync())!;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Post_checks_by_dm_as_non_dm_controlled_character_returns_403()
    {
        // DM override สงวนไว้เฉพาะตัวละครที่ ControlledByDm=true เท่านั้น — _attackerId เป็นของจริง
        // ของผู้เล่น (ControlledByDm=false) DM เล่นแทนไม่ได้แม้จะเป็น DM ของแคมเปญนี้ก็ตาม
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/checks",
            new { Expression = "1d20", Dc = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── attacks — happy path (hit) ─────────────────────────────────────────
    // AC=1 + modifier มหาศาล: Miss (Total<AC) เป็นไปไม่ได้เลย มีแค่ Hit/CriticalHit (รวมกัน 95%)
    // กับ AutomaticMiss (นัด1, 5%) ที่ override AC เสมอ retry จนกว่าจะ Hit เพื่อตัด flake ทิ้ง
    // (retry ปลอดภัยเพราะ AutomaticMiss ไม่แตะ DB เลย ไม่มี state ค้างระหว่างรอบ)

    [Fact]
    public async Task Post_attacks_that_hits_rolls_damage_and_updates_target_hp()
    {
        AttackResolutionResponse? body = null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var response = await AuthedClient(_memberToken).PostAsJsonAsync(
                $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
                new
                {
                    Expression = "1d20+50",
                    TargetCharacterId = _lowAcTargetId,
                    DamageComponents = new[] { new { Expression = "2d6+3", Type = "Slashing" } },
                });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            body = await response.Content.ReadFromJsonAsync<AttackResolutionResponse>(JsonOptions);
            Assert.NotNull(body);
            if (body!.Hit)
            {
                break;
            }
        }

        Assert.NotNull(body);
        Assert.True(body!.Hit, $"AutomaticMiss เกิดขึ้น {MaxRetries} รอบติดกัน (โอกาสจริง ~{Math.Pow(0.05, MaxRetries):E2}) — น่าสงสัยว่า RNG ผิดปกติ");
        Assert.Equal(_attackerId, body.AttackerCharacterId);
        Assert.Equal(_lowAcTargetId, body.TargetCharacterId);
        Assert.Equal(1, body.TargetArmorClass);
        Assert.NotNull(body.DamageComponents);
        var component = Assert.Single(body.DamageComponents!);
        Assert.Equal("Slashing", component.Type);
        Assert.InRange(component.RawAmount, 5, body.Outcome == "CriticalHit" ? 30 : 15); // 2d6+3 ปกติ 5..15, crit ดับเบิลลูกเต๋า 4d6+3 = 7..27 (เผื่อขอบ 30)
        Assert.Equal(component.RawAmount, component.AdjustedAmount); // ไม่มี resistance ที่ target นี้
        Assert.NotNull(body.TotalDamage);
        Assert.Equal(component.AdjustedAmount, body.TotalDamage);

        Assert.NotNull(body.TargetPreviousHp);
        Assert.NotNull(body.TargetNewHp);
        Assert.Equal(20, body.TargetPreviousHp);
        // HitPointResolver.ApplyDamage clamp NewHp ที่ 0 เสมอ (ห้ามติดลบ) — 20-TotalDamage เพียว ๆ
        // ติดลบได้จริงถ้า critical hit ทอยแรงพอ (4d6+3 max=27 > 20) ต้อง Math.Max(0, ...) เหมือน
        // resolver เอง ไม่งั้น flaky ~5%*บางส่วนของรอบที่ crit ทอยสูง (พบจริงระหว่าง Sprint C Part 3)
        Assert.Equal(Math.Max(0, 20 - body.TotalDamage!.Value), body.TargetNewHp);

        var (currentHp, _) = await ReadTargetHpAsync(_lowAcTargetId);
        Assert.Equal(body.TargetNewHp, currentHp);
    }

    [Fact]
    public async Task Post_attacks_that_hits_persists_attack_and_damage_dice_rolls_with_attacker_character_id()
    {
        AttackResolutionResponse? body = null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var response = await AuthedClient(_memberToken).PostAsJsonAsync(
                $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
                new
                {
                    Expression = "1d20+50",
                    TargetCharacterId = _lowAcTargetId,
                    DamageComponents = new[] { new { Expression = "1d6", Type = "Bludgeoning" } },
                });
            body = await response.Content.ReadFromJsonAsync<AttackResolutionResponse>(JsonOptions);
            if (body!.Hit)
            {
                break;
            }
        }

        Assert.NotNull(body);
        Assert.True(body!.Hit);

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var select = new NpgsqlCommand(
            """
            SELECT count(*) FROM dice_rolls
            WHERE session_id = @sessionId AND character_id = @attackerId
              AND created_at > now() - interval '1 minute'
            """, conn);
        select.Parameters.AddWithValue("sessionId", _sessionId);
        select.Parameters.AddWithValue("attackerId", _attackerId);
        var count = (long)(await select.ExecuteScalarAsync())!;
        // อย่างน้อย 2 แถวจากการโจมตีที่ hit รอบสุดท้าย (attack roll + damage roll อย่างน้อย 1) —
        // อาจมีมากกว่านี้ถ้า retry เจอ hit ช้า (แต่ AutomaticMiss ไม่สร้างแถวเลย นับได้แน่นอนว่า >= 2)
        Assert.True(count >= 2, $"คาดว่ามี dice_rolls อย่างน้อย 2 แถว (attack+damage) แต่ได้ {count}");
    }

    [Fact]
    public async Task Post_attacks_applies_resistance_to_damage_amount()
    {
        (int CurrentHp, int CurrentTempHp) before = await ReadTargetHpAsync(_resistantTargetId);

        AttackResolutionResponse? body = null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var response = await AuthedClient(_memberToken).PostAsJsonAsync(
                $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
                new
                {
                    Expression = "1d20+50",
                    TargetCharacterId = _resistantTargetId,
                    DamageComponents = new[] { new { Expression = "2d6", Type = "Fire" } },
                });
            body = await response.Content.ReadFromJsonAsync<AttackResolutionResponse>(JsonOptions);
            if (body!.Hit)
            {
                break;
            }
        }

        Assert.NotNull(body);
        Assert.True(body!.Hit);
        var component = Assert.Single(body.DamageComponents!);
        Assert.Equal("Fire", component.Type);
        // resistance หารครึ่งปัดลง — adjusted ต้องน้อยกว่าหรือเท่ากับ raw/2 เสมอ (floor)
        Assert.Equal(component.RawAmount / 2, component.AdjustedAmount);

        var after = await ReadTargetHpAsync(_resistantTargetId);
        Assert.Equal(before.CurrentHp - component.AdjustedAmount, after.CurrentHp);
    }

    // ── attacks — SignalR broadcast (Sprint C Part 3 — wires HpUpdated into CampaignHub group,
    //    same retry-until-hit pattern as Post_attacks_that_hits_rolls_damage_and_updates_target_hp) ──

    [Fact]
    public async Task Post_attacks_that_hits_broadcasts_HpUpdated_to_joined_campaign_group()
    {
        await using var connection = BuildConnection(_memberToken);

        var received = new TaskCompletionSource<HpUpdatedBroadcastDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<HpUpdatedBroadcastDto>("HpUpdated", broadcast => received.TrySetResult(broadcast));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinCampaign", _campaignId);

        AttackResolutionResponse? body = null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var response = await AuthedClient(_memberToken).PostAsJsonAsync(
                $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
                new
                {
                    Expression = "1d20+50",
                    TargetCharacterId = _lowAcTargetId,
                    DamageComponents = new[] { new { Expression = "2d6+3", Type = "Slashing" } },
                });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            body = await response.Content.ReadFromJsonAsync<AttackResolutionResponse>(JsonOptions);
            Assert.NotNull(body);
            if (body!.Hit)
            {
                break;
            }
        }

        Assert.NotNull(body);
        Assert.True(body!.Hit, $"AutomaticMiss เกิดขึ้น {MaxRetries} รอบติดกัน (โอกาสจริง ~{Math.Pow(0.05, MaxRetries):E2}) — น่าสงสัยว่า RNG ผิดปกติ");

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(received.Task, completed);
        var broadcastPayload = await received.Task;

        Assert.Equal(_lowAcTargetId, broadcastPayload.CharacterId);
        Assert.Equal(body.TargetPreviousHp, broadcastPayload.PreviousHp);
        Assert.Equal(body.TargetNewHp, broadcastPayload.NewHp);
        Assert.Equal(body.TargetPreviousTempHp, broadcastPayload.PreviousTempHp);
        Assert.Equal(body.TargetNewTempHp, broadcastPayload.NewTempHp);
        Assert.Equal(body.TargetDroppedToZero, broadcastPayload.DroppedToZero);
        Assert.Equal(body.TargetIsInstantDeath, broadcastPayload.IsInstantDeath);

        await connection.StopAsync();
    }

    /// <summary>
    /// Sprint F งานที่ 7 follow-up (Critical จาก security review commit 4434650) — NPC ที่ DM ปิดการ
    /// มองเห็น stat ไว้ (ControlledByDm=true, NpcStatsVisibleToParty=false) ต้องไม่ broadcast HpUpdated
    /// เข้ากลุ่มแคมเปญเลย (ผู้เล่นทุกคนอยู่ในกลุ่มนั้น) — ต้องส่งตรงถึง DM คนเดียวแทน (Clients.User)
    /// เหมือนแพทเทิร์น DeathSaveVisibility.DmOnly ที่มีอยู่แล้ว ไม่งั้นฟีเจอร์ซ่อน stat ที่ list/detail
    /// endpoint ทำไว้ไม่มีความหมายทันทีที่เข้า combat จริง
    /// </summary>
    [Fact]
    public async Task Post_attacks_on_hidden_npc_does_not_broadcast_HpUpdated_to_party_group_but_reaches_dm()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        var hiddenNpcId = Guid.NewGuid();
        await InsertCharacterAsync(conn, hiddenNpcId, _campaignId, "Hidden Ambusher", armorClass: 1, controlledByDm: true);
        await using (var hide = new NpgsqlCommand(
            "UPDATE characters SET npc_stats_visible_to_party = false WHERE id = @id", conn))
        {
            hide.Parameters.AddWithValue("id", hiddenNpcId);
            await hide.ExecuteNonQueryAsync();
        }

        await using var partyConnection = BuildConnection(_memberToken);
        var partyReceived = new TaskCompletionSource<HpUpdatedBroadcastDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        partyConnection.On<HpUpdatedBroadcastDto>("HpUpdated", b => partyReceived.TrySetResult(b));
        await partyConnection.StartAsync();
        await partyConnection.InvokeAsync("JoinCampaign", _campaignId);

        await using var dmConnection = BuildConnection(_dmToken);
        var dmReceived = new TaskCompletionSource<HpUpdatedBroadcastDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dmConnection.On<HpUpdatedBroadcastDto>("HpUpdated", b => dmReceived.TrySetResult(b));
        await dmConnection.StartAsync();
        await dmConnection.InvokeAsync("JoinCampaign", _campaignId);

        AttackResolutionResponse? body = null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var response = await AuthedClient(_memberToken).PostAsJsonAsync(
                $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
                new
                {
                    Expression = "1d20+50",
                    TargetCharacterId = hiddenNpcId,
                    DamageComponents = new[] { new { Expression = "2d6+3", Type = "Slashing" } },
                });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            body = await response.Content.ReadFromJsonAsync<AttackResolutionResponse>(JsonOptions);
            Assert.NotNull(body);
            if (body!.Hit)
            {
                break;
            }
        }

        Assert.NotNull(body);
        Assert.True(body!.Hit, $"AutomaticMiss เกิดขึ้น {MaxRetries} รอบติดกัน — น่าสงสัยว่า RNG ผิดปกติ");

        // caller ที่ไม่ใช่ DM ต้องไม่เห็น AC จริงใน response แม้เพิ่งโจมตีไปแล้วก็ตาม
        Assert.Null(body.TargetArmorClass);

        var dmCompleted = await Task.WhenAny(dmReceived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(dmReceived.Task, dmCompleted);
        var dmPayload = await dmReceived.Task;
        Assert.Equal(hiddenNpcId, dmPayload.CharacterId);
        Assert.Equal(body.TargetNewHp, dmPayload.NewHp);

        // ฝั่งผู้เล่นต้องไม่ได้รับ broadcast เลยภายในเวลาที่พอสมควร (bounded wait — mirror
        // Post_attacks_that_misses_does_not_broadcast_HpUpdated ด้านล่าง)
        var partyCompleted = await Task.WhenAny(partyReceived.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(partyReceived.Task, partyCompleted);

        await partyConnection.StopAsync();
        await dmConnection.StopAsync();
    }

    // ── attacks — miss path (ไม่แตะ HP/damage เลย) ─────────────────────────
    // AC=50 + "1d20+0": Hit ธรรมดาเป็นไปไม่ได้ (max 20 < 50) มีแค่ CriticalHit (นัด20, 5%) หรือ
    // Miss/AutomaticMiss (95%) — retry จนกว่าจะไม่ hit เพื่อยืนยัน miss-path ไม่ flaky
    //
    // ⚠️ ห้าม reuse target ตัวเดิมข้ามรอบ retry: ถ้ารอบแรก ๆ ดันเกิด CriticalHit (5% ต่อรอบ) ดาเมจจะ
    // ถูก apply เข้า DB จริงถาวร ทำให้ HP ก่อน/หลังของ "รอบที่ miss ในที่สุด" เทียบกับค่าที่จับไว้ตอน
    // ต้น test ไม่ตรงกันอีกต่อไป (พังจริงมาแล้วรอบแรกที่รัน — เจอผ่าน CI ไม่ใช่ทฤษฎี) — สร้าง target
    // ใหม่ทุกรอบแทน ผลจาก crit รอบก่อนจึงถูกทิ้งไปกับ target แบบใช้แล้วทิ้งนั้น ไม่รั่วมาปนรอบถัดไป
    [Fact]
    public async Task Post_attacks_that_misses_does_not_roll_damage_or_change_target_hp()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        AttackResolutionResponse? body = null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var disposableTargetId = Guid.NewGuid();
            await InsertCharacterAsync(conn, disposableTargetId, _campaignId, $"Miss Target {attempt}", armorClass: 50);

            var response = await AuthedClient(_memberToken).PostAsJsonAsync(
                $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
                new
                {
                    Expression = "1d20",
                    TargetCharacterId = disposableTargetId,
                    DamageComponents = new[] { new { Expression = "1d6", Type = "Piercing" } },
                });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            body = await response.Content.ReadFromJsonAsync<AttackResolutionResponse>(JsonOptions);
            Assert.NotNull(body);
            if (!body!.Hit)
            {
                var (afterHp, afterTempHp) = await ReadTargetHpAsync(disposableTargetId);
                Assert.Equal(20, afterHp);
                Assert.Equal(0, afterTempHp);
                break;
            }
        }

        Assert.NotNull(body);
        Assert.False(body!.Hit, $"CriticalHit เกิดขึ้น {MaxRetries} รอบติดกัน (โอกาสจริง ~{Math.Pow(0.05, MaxRetries):E2}) — น่าสงสัยว่า RNG ผิดปกติ");
        Assert.Null(body.DamageComponents);
        Assert.Null(body.TotalDamage);
        Assert.Null(body.TargetNewHp);
    }

    /// <summary>
    /// ยืนยันกฎเดียวกับ Post_attacks_that_misses_does_not_roll_damage_or_change_target_hp แต่ฝั่ง
    /// broadcast — miss/automatic-miss ไม่มี HP เปลี่ยนแปลงจริง จึงต้องไม่มี HpUpdated ยิงออกไปเลย
    /// (bounded wait 2 วินาที มิเรอร์ pattern เดียวกับ
    /// Outsider_connection_that_never_joined_group_does_not_receive_broadcast ใน DiceRollCrudTests)
    /// </summary>
    [Fact]
    public async Task Post_attacks_that_misses_does_not_broadcast_HpUpdated()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using var connection = BuildConnection(_memberToken);

        var received = new TaskCompletionSource<HpUpdatedBroadcastDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<HpUpdatedBroadcastDto>("HpUpdated", broadcast => received.TrySetResult(broadcast));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinCampaign", _campaignId);

        AttackResolutionResponse? body = null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var disposableTargetId = Guid.NewGuid();
            await InsertCharacterAsync(conn, disposableTargetId, _campaignId, $"Miss Broadcast Target {attempt}", armorClass: 50);

            var response = await AuthedClient(_memberToken).PostAsJsonAsync(
                $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
                new
                {
                    Expression = "1d20",
                    TargetCharacterId = disposableTargetId,
                    DamageComponents = new[] { new { Expression = "1d6", Type = "Piercing" } },
                });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            body = await response.Content.ReadFromJsonAsync<AttackResolutionResponse>(JsonOptions);
            Assert.NotNull(body);
            if (!body!.Hit)
            {
                break;
            }
        }

        Assert.NotNull(body);
        Assert.False(body!.Hit, $"CriticalHit เกิดขึ้น {MaxRetries} รอบติดกัน (โอกาสจริง ~{Math.Pow(0.05, MaxRetries):E2}) — น่าสงสัยว่า RNG ผิดปกติ");

        await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.False(received.Task.IsCompleted, "attack ที่ miss ต้องไม่ broadcast HpUpdated เลย");

        await connection.StopAsync();
    }

    // ── attacks — validation (400 ก่อนแตะ DB/CSPRNG เลย) ────────────────────

    [Fact]
    public async Task Post_attacks_without_target_returns_400()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
            new
            {
                Expression = "1d20+5",
                DamageComponents = new[] { new { Expression = "1d6", Type = "Slashing" } },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_attacks_with_empty_damage_components_returns_400()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
            new
            {
                Expression = "1d20+5",
                TargetCharacterId = _lowAcTargetId,
                DamageComponents = Array.Empty<object>(),
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_attacks_with_unknown_damage_type_returns_400_and_creates_no_row()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var countBefore = new NpgsqlCommand(
            "SELECT count(*) FROM dice_rolls WHERE session_id = @sessionId", conn);
        countBefore.Parameters.AddWithValue("sessionId", _sessionId);
        var before = (long)(await countBefore.ExecuteScalarAsync())!;

        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
            new
            {
                Expression = "1d20+5",
                TargetCharacterId = _lowAcTargetId,
                DamageComponents = new[] { new { Expression = "1d6", Type = "NotARealDamageType" } },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var countAfter = new NpgsqlCommand(
            "SELECT count(*) FROM dice_rolls WHERE session_id = @sessionId", conn);
        countAfter.Parameters.AddWithValue("sessionId", _sessionId);
        var after = (long)(await countAfter.ExecuteScalarAsync())!;
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Post_attacks_with_malformed_attack_expression_returns_400_before_rolling_anything()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
            new
            {
                Expression = "not-a-dice-expression",
                TargetCharacterId = _lowAcTargetId,
                DamageComponents = new[] { new { Expression = "1d6", Type = "Slashing" } },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_attacks_with_expression_without_d20_returns_400()
    {
        // parse ผ่าน (2d6 เป็น dice expression ที่ถูกต้อง) แต่ AttackResolver ต้องการ d20 เดี่ยวเสมอ
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
            new
            {
                Expression = "2d6",
                TargetCharacterId = _lowAcTargetId,
                DamageComponents = new[] { new { Expression = "1d6", Type = "Slashing" } },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_attacks_for_unknown_target_returns_404()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
            new
            {
                Expression = "1d20+5",
                TargetCharacterId = Guid.NewGuid(),
                DamageComponents = new[] { new { Expression = "1d6", Type = "Slashing" } },
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_attacks_for_unknown_attacker_returns_404()
    {
        // ต่างจาก Post_attacks_for_unknown_target_returns_404 — เคสนี้ตัวละคร "ที่ลงมือ"
        // (characterId ใน route) ไม่มีอยู่จริง ไม่ใช่ target — ยังไม่เคยมีเทสต์ครอบเคสนี้มาก่อน
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{Guid.NewGuid()}/attacks",
            new
            {
                Expression = "1d20+5",
                TargetCharacterId = _lowAcTargetId,
                DamageComponents = new[] { new { Expression = "1d6", Type = "Slashing" } },
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_attacks_into_campaign_with_dm_entered_dice_mode_returns_400()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignDmEnteredId}/sessions/{_sessionInDmEnteredCampaignId}/characters/{_attackerInDmEnteredCampaignId}/attacks",
            new
            {
                Expression = "1d20+5",
                TargetCharacterId = _targetInDmEnteredCampaignId,
                DamageComponents = new[] { new { Expression = "1d6", Type = "Slashing" } },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_attacks_by_non_member_returns_404()
    {
        var response = await AuthedClient(_outsiderToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
            new
            {
                Expression = "1d20+5",
                TargetCharacterId = _lowAcTargetId,
                DamageComponents = new[] { new { Expression = "1d6", Type = "Slashing" } },
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_attacks_without_jwt_returns_401()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
            new
            {
                Expression = "1d20+5",
                TargetCharacterId = _lowAcTargetId,
                DamageComponents = new[] { new { Expression = "1d6", Type = "Slashing" } },
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── attacks — authorization (Sprint F งานที่ 3.1) ────────────────────────
    // เจ้าของตัวละครเล่นแทนตัวเองสำเร็จ คุมอยู่แล้วโดย happy-path tests ด้านบนที่ใช้ _memberToken
    // เล่นแทน _attackerId (เจ้าของจริง) — ที่เหลือคือ DM override, non-owner/non-DM, และ side effects

    [Fact]
    public async Task Post_attacks_by_dm_as_dm_controlled_character_succeeds_and_updates_target_hp()
    {
        AttackResolutionResponse? body = null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var response = await AuthedClient(_dmToken).PostAsJsonAsync(
                $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_dmControlledMonsterId}/attacks",
                new
                {
                    Expression = "1d20+50",
                    TargetCharacterId = _lowAcTargetId,
                    DamageComponents = new[] { new { Expression = "2d6+3", Type = "Slashing" } },
                });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            body = await response.Content.ReadFromJsonAsync<AttackResolutionResponse>(JsonOptions);
            Assert.NotNull(body);
            if (body!.Hit)
            {
                break;
            }
        }

        Assert.NotNull(body);
        Assert.True(body!.Hit, $"AutomaticMiss เกิดขึ้น {MaxRetries} รอบติดกัน (โอกาสจริง ~{Math.Pow(0.05, MaxRetries):E2}) — น่าสงสัยว่า RNG ผิดปกติ");
        Assert.Equal(_dmControlledMonsterId, body.AttackerCharacterId);
        Assert.NotNull(body.TargetNewHp);
    }

    [Fact]
    public async Task Post_attacks_by_non_owner_non_dm_returns_403_and_does_not_roll_or_change_hp()
    {
        var (hpBefore, tempHpBefore) = await ReadTargetHpAsync(_lowAcTargetId);

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var countBefore = new NpgsqlCommand(
            "SELECT count(*) FROM dice_rolls WHERE character_id = @characterId", conn);
        countBefore.Parameters.AddWithValue("characterId", _otherPlayersCharacterId);
        var before = (long)(await countBefore.ExecuteScalarAsync())!;

        // _memberUserId เป็นสมาชิกแคมเปญแต่ไม่ใช่เจ้าของ _otherPlayersCharacterId (owner = outsider)
        // และไม่ใช่ DM — ต้องได้ 403 โดยไม่ทอยลูกเต๋าหรือแตะ HP ของ target เลย (auth check อยู่ก่อน
        // การทอย attack roll ใด ๆ ในโค้ด)
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_otherPlayersCharacterId}/attacks",
            new
            {
                Expression = "1d20+50",
                TargetCharacterId = _lowAcTargetId,
                DamageComponents = new[] { new { Expression = "2d6+3", Type = "Slashing" } },
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var countAfter = new NpgsqlCommand(
            "SELECT count(*) FROM dice_rolls WHERE character_id = @characterId", conn);
        countAfter.Parameters.AddWithValue("characterId", _otherPlayersCharacterId);
        var after = (long)(await countAfter.ExecuteScalarAsync())!;
        Assert.Equal(before, after);

        var (hpAfter, tempHpAfter) = await ReadTargetHpAsync(_lowAcTargetId);
        Assert.Equal(hpBefore, hpAfter);
        Assert.Equal(tempHpBefore, tempHpAfter);
    }

    [Fact]
    public async Task Post_attacks_by_dm_as_non_dm_controlled_character_returns_403()
    {
        // DM override สงวนไว้เฉพาะ ControlledByDm=true — _attackerId เป็นของผู้เล่นจริง DM เล่นแทนไม่ได้
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
            new
            {
                Expression = "1d20+5",
                TargetCharacterId = _lowAcTargetId,
                DamageComponents = new[] { new { Expression = "1d6", Type = "Slashing" } },
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_attacks_response_never_exposes_seed_field()
    {
        var response = await AuthedClient(_memberToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/characters/{_attackerId}/attacks",
            new
            {
                Expression = "1d20+5",
                TargetCharacterId = _lowAcTargetId,
                DamageComponents = new[] { new { Expression = "1d6", Type = "Slashing" } },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawJson = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("seed", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    private record AuthResponse(string Token, Guid UserId);

    private record RollDetailDto(int Sides, int Value, bool Dropped, int Sign);

    private record CheckResolutionResponse(
        Guid Id, Guid CharacterId, string Expression, IReadOnlyList<RollDetailDto> Rolls,
        int Total, int Dc, bool Success);

    private record DamageComponentDto(string Type, int RawAmount, int AdjustedAmount, IReadOnlyList<RollDetailDto> Rolls);

    private record AttackResolutionResponse(
        Guid Id,
        Guid AttackerCharacterId,
        Guid TargetCharacterId,
        string Expression,
        IReadOnlyList<RollDetailDto> Rolls,
        int Total,
        int? TargetArmorClass,
        string Outcome,
        bool Hit,
        IReadOnlyList<DamageComponentDto>? DamageComponents,
        int? TotalDamage,
        int? TargetPreviousHp,
        int? TargetNewHp,
        int? TargetPreviousTempHp,
        int? TargetNewTempHp,
        bool? TargetDroppedToZero,
        bool? TargetIsInstantDeath);

    private record HpUpdatedBroadcastDto(
        Guid CharacterId,
        int PreviousHp,
        int NewHp,
        int PreviousTempHp,
        int NewTempHp,
        bool DroppedToZero,
        bool IsInstantDeath);
}
