using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint H Part 4 — affordance response ต้องมีสูตรทอยโจมตี/DC/ดาเมจ/การรักษา ครบ ผู้เล่นจึงเลิกกรอก
/// "1d20+5"/"1d8+3" เองได้ · แยกไฟล์จาก AffordanceEndpointsTests โดยตั้งใจ (dispatch ห้ามแก้เทสต์
/// เดิมของ affordance) — เทสต์เดิม 10 ตัวยังต้องเขียวโดยไม่แตะ
///
/// ฉาก: หมอกเปิดวง (0,0) รัศมี 10 ทุกตัวอยู่ในวง ผู้เล่นคนเดียวเป็นเจ้าของตัวละครสามตัว
///   • Hero (1,1) — Fighter STR 15 DEX 13 ถือดาบยาว + ธนูสั้น
///   • Healer (1,2) — Cleric WIS 15 ถือ Sacred Flame + Cure Wounds
///   • Mystery (2,2) — **sheet ไม่มี class** ถือ Fire Bolt → ต้องได้ตัวเลือกแต่สูตรว่าง (ไม่เดา)
///   • ก็อบลิน (2,1) — NPC ที่ DM คุม ประชิดทุกตัว
///
/// ตัวเลขคาดหวัง (SRD 5.2): modifier ตามตาราง Ability Modifiers · PB เลเวล 1 = +2
/// </summary>
public class AffordanceFormulaEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();

    private readonly Guid _heroId = Guid.NewGuid();
    private readonly Guid _healerId = Guid.NewGuid();
    private readonly Guid _mysteryId = Guid.NewGuid();
    private readonly Guid _goblinId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private Guid _dmUserId;
    private Guid _playerUserId;
    private string _playerToken = null!;

    private Guid _longswordId;
    private Guid _shortbowId;
    private Guid _sacredFlameId;
    private Guid _cureWoundsId;
    private Guid _fireBoltId;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        _longswordId = await LookupCatalogIdAsync(conn, "longsword");
        _shortbowId = await LookupCatalogIdAsync(conn, "shortbow");
        _sacredFlameId = await LookupCatalogIdAsync(conn, "sacred-flame");
        _cureWoundsId = await LookupCatalogIdAsync(conn, "cure-wounds");
        _fireBoltId = await LookupCatalogIdAsync(conn, "fire-bolt");

        await ExecAsync(conn,
            """
            INSERT INTO campaigns (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@c, @dm, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now());
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (gen_random_uuid(), @c, @dm, 'Dm', now()), (gen_random_uuid(), @c, @player, 'Player', now());
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@s, @c, 'Online', 'Active', NULL, now(), NULL);
            """,
            ("c", _campaignId), ("dm", _dmUserId), ("player", _playerUserId), ("s", _sessionId),
            ("name", $"Formula Campaign {_suffix}"));

        await InsertCharacterAsync(conn, _heroId, "Hero", _playerUserId, false,
            $$"""{"class":"Fighter","actionCatalogRefs":["{{_longswordId}}","{{_shortbowId}}"]}""",
            str: 15, dex: 13, con: 14, @int: 12, wis: 10, cha: 8);
        await InsertCharacterAsync(conn, _healerId, "Healer", _playerUserId, false,
            $$"""{"class":"Cleric","actionCatalogRefs":["{{_sacredFlameId}}","{{_cureWoundsId}}"]}""",
            str: 12, dex: 13, con: 14, @int: 8, wis: 15, cha: 10);
        await InsertCharacterAsync(conn, _mysteryId, "Mystery", _playerUserId, false,
            $$"""{"actionCatalogRefs":["{{_fireBoltId}}"]}""",
            str: 10, dex: 10, con: 10, @int: 16, wis: 10, cha: 10);
        await InsertCharacterAsync(conn, _goblinId, "ก็อบลิน", null, true, "{}",
            str: 8, dex: 14, con: 10, @int: 10, wis: 8, cha: 8);

        var tokens = $$"""
            [
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_heroId}}","x":1,"y":1},
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_healerId}}","x":1,"y":2},
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_mysteryId}}","x":2,"y":2},
                {"id":"{{Guid.NewGuid()}}","entityType":"Character","entityId":"{{_goblinId}}","x":2,"y":1}
            ]
            """;
        await ExecAsync(conn,
            """
            INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
            VALUES (gen_random_uuid(), @s, 'Formula Scene', '{}', @tokens::jsonb,
                '{"revealedAreas":[{"id":"33333333-3333-3333-3333-333333333333","x":0,"y":0,"radius":10}]}', true)
            """,
            ("s", _sessionId), ("tokens", tokens));
    }

    /// <summary>ดาบยาว: STR +2 + PB 2 → "1d20+4" · ดาเมจ 1d8 + STR → "1d8+2" Slashing</summary>
    [Fact]
    public async Task Longsword_option_carries_strength_attack_and_damage_formulas()
    {
        var item = await OptionAsync(_heroId, _longswordId, _goblinId);

        Assert.Equal("Strength", item.Ability);
        Assert.Equal("1d20+4", item.AttackExpression);
        Assert.Equal("1d8+2", item.DamageExpression);
        Assert.Equal("Slashing", item.DamageType);
        Assert.Null(item.SaveDc);
        Assert.Null(item.SaveAbility);
        Assert.Null(item.HealingExpression);
    }

    /// <summary>ธนูสั้น: DEX 13 (+1) + PB 2 → "1d20+3" · ดาเมจ "1d6+1" **Piercing** — ก่อน Part นี้
    /// frontend ส่ง damageType "Slashing" ตายตัวทุกท่า ธนูจึงถูกบันทึกเป็นดาเมจฟันผิดชนิด</summary>
    [Fact]
    public async Task Shortbow_option_uses_dexterity_and_piercing()
    {
        var item = await OptionAsync(_heroId, _shortbowId, _goblinId);

        Assert.Equal("Dexterity", item.Ability);
        Assert.Equal("1d20+3", item.AttackExpression);
        Assert.Equal("1d6+1", item.DamageExpression);
        Assert.Equal("Piercing", item.DamageType);
    }

    /// <summary>Sacred Flame: DC = 8 + WIS 2 + PB 2 = 12 · เป้า save ด้วย DEX · ไม่มีทอยโจมตี</summary>
    [Fact]
    public async Task Sacred_flame_option_carries_a_save_dc_and_save_ability()
    {
        var item = await OptionAsync(_healerId, _sacredFlameId, _goblinId);

        Assert.Equal("Wisdom", item.Ability);
        Assert.Null(item.AttackExpression);
        Assert.Equal(12, item.SaveDc);
        Assert.Equal("Dexterity", item.SaveAbility);
        Assert.Equal("1d8", item.DamageExpression);
        Assert.Equal("Radiant", item.DamageType);
    }

    /// <summary>Cure Wounds เล็งพวกเดียวกัน (Hero ห่าง 5 ฟุต) · รักษา "2d8+2" · ไม่มีดาเมจ</summary>
    [Fact]
    public async Task Cure_wounds_option_carries_a_healing_formula_and_no_damage()
    {
        var item = await OptionAsync(_healerId, _cureWoundsId, _heroId);

        Assert.Equal("Wisdom", item.Ability);
        Assert.Equal("2d8+2", item.HealingExpression);
        Assert.Null(item.DamageExpression);
        Assert.Null(item.DamageType);
        Assert.Null(item.AttackExpression);
        Assert.Null(item.SaveDc);
    }

    /// <summary>
    /// sheet ไม่มี class → ไม่รู้ว่าใช้ ability ไหนร่ายเวท → **ตัวเลือกยังอยู่** (ทำได้จริงตามระยะ/ราคา)
    /// แต่สูตรเป็น null ทั้งหมด ให้ frontend กลับไปให้กรอกเอง · ห้ามเดา (เช่นเอา INT 16 ที่สูงสุดมาใช้)
    /// </summary>
    [Fact]
    public async Task Spell_of_a_character_without_a_class_is_listed_without_formulas()
    {
        var item = await OptionAsync(_mysteryId, _fireBoltId, _goblinId);

        Assert.Null(item.Ability);
        Assert.Null(item.AttackExpression);
        Assert.Null(item.DamageExpression);
        Assert.Null(item.DamageType);
        Assert.Null(item.SaveDc);
        Assert.Null(item.HealingExpression);
    }

    // ── helpers ──

    private async Task<AffordanceItemResponse> OptionAsync(Guid actorId, Guid optionId, Guid targetId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _playerToken);
        var response = await client.GetAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/affordances/{actorId}");
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<AffordanceResponse>(JsonOptions))!;

        return Assert.Single(body.Options.Where(o => o.OptionId == optionId && o.TargetId == targetId));
    }

    private static async Task<Guid> LookupCatalogIdAsync(NpgsqlConnection conn, string key)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT id FROM action_catalog_entries WHERE key = @key AND rules_system = 'srd2024'", conn);
        cmd.Parameters.AddWithValue("key", key);
        var result = await cmd.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (Guid)result!;
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/register", new
        {
            Email = $"formula-{label}-{_suffix}@example.test",
            Password = "affordance-formula-secret-1",
            DisplayName = $"Formula {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthTokenResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private async Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, string name, Guid? ownerUserId, bool controlledByDm, string sheetJson,
        int str, int dex, int con, int @int, int wis, int cha)
    {
        await ExecAsync(conn,
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, npc_stats_visible_to_party,
                 harvest_consent, is_orphan, created_at,
                 strength, dexterity, constitution, intelligence, wisdom, charisma)
            VALUES
                (@id, @c, @owner, @name, @sheet::jsonb, 20, 20, 0, 12, 0,
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, @dmc, false, false, false, now(),
                 @str, @dex, @con, @int, @wis, @cha)
            """,
            ("id", id), ("c", _campaignId), ("owner", (object?)ownerUserId ?? DBNull.Value), ("name", name),
            ("sheet", sheetJson), ("dmc", controlledByDm),
            ("str", str), ("dex", dex), ("con", con), ("int", @int), ("wis", wis), ("cha", cha));
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps)
        {
            cmd.Parameters.AddWithValue(n, v);
        }
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = @c", ("c", _campaignId));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)", ("ids", new[] { _dmUserId, _playerUserId }));
        _factory.Dispose();
    }

    private record AuthTokenResponse(string Token, Guid UserId);

    private record AffordanceItemResponse(
        Guid OptionId,
        Guid? TargetId,
        string? Ability,
        string? AttackExpression,
        int? SaveDc,
        string? SaveAbility,
        string? DamageExpression,
        string? DamageType,
        string? HealingExpression);

    private record AffordanceResponse(Guid CharacterId, List<AffordanceItemResponse> Options);
}
