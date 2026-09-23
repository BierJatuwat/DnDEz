using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Part E ของแผน "DM AI Assistant" (2026-09-22) — มอนสเตอร์มี HP รายตัวและเข้าวง initiative ได้
/// (เรื่องที่ผู้ใช้ค้างไว้ตั้งแต่ต้นเซสชัน: "Monster ไม่มี hp รายตัว")
///
/// contract:
///
/// POST /campaigns/{campaignId}/monsters/{monsterId}/spawn  [Authorize, DM-only]
///   body { Count?: 1..20, StatsVisibleToParty?: bool (default false) }
///   200 OK + [{ Id, Name, CurrentHp, MaxHp, ArmorClass }] — สร้างแถว `characters` ControlledByDm=true
///   400 invalid_count · 403 ไม่ใช่ DM · 404 แม่แบบไม่อยู่ในแคมเปญนี้
///
/// POST .../encounters/{id}/end (ของเดิม) — เพิ่มพฤติกรรม: สรุปผลลงบันทึกแคมเปญแล้ว**ลบ instance ทิ้ง**
///
/// สิ่งที่ต้องล็อกไว้เป็นพิเศษ:
///  • instance ต้องเข้า `POST .../encounters` (วง initiative) ได้จริง — เดิมมอนสเตอร์เข้าไม่ได้เลย
///  • ผู้เล่น **ไม่เห็น HP/AC** ของ instance (ผ่าน `CharacterStatsVisibility` ที่มีอยู่แล้ว)
///  • ตอนจบ encounter ต้องลบเฉพาะ instance ไม่แตะตัวละครผู้เล่นและ NPC ถาวรของ DM
/// </summary>
public class MonsterSpawnEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "monster-spawn-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");

    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _monsterId = Guid.NewGuid();
    private readonly Guid _otherCampaignMonsterId = Guid.NewGuid();
    private readonly Guid _playerCharacterId = Guid.NewGuid();
    private readonly Guid _permanentNpcId = Guid.NewGuid();

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

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Spawn Campaign {_suffix}");
        await InsertCampaignAsync(conn, _otherCampaignId, _dmUserId, $"Spawn Other Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _dmUserId, "Dm");
        await InsertSessionAsync(conn, _sessionId, _campaignId);

        await InsertMonsterAsync(conn, _monsterId, _campaignId, "Goblin Warrior", ac: 15, hp: 7);
        await InsertMonsterAsync(conn, _otherCampaignMonsterId, _otherCampaignId, "Other Table Ogre", ac: 11, hp: 59);

        // ตัวละครผู้เล่นจริง + NPC ถาวรของ DM — ทั้งคู่ต้องรอดจากการลบตอนจบ encounter
        await InsertCharacterAsync(conn, _playerCharacterId, _campaignId, _playerUserId, "Hero", controlledByDm: false);
        await InsertCharacterAsync(conn, _permanentNpcId, _campaignId, null, "Innkeeper", controlledByDm: true);
    }

    // ── spawn ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_can_spawn_monster_instances_that_copy_the_template_stats()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/monsters/{_monsterId}/spawn", new { Count = 3 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<List<SpawnedMonsterItem>>(JsonOptions);
        Assert.NotNull(items);
        Assert.Equal(3, items!.Count);

        // ชื่อไล่เลขให้ DM แยกตัวออกจากกันในวง initiative
        Assert.Equal("Goblin Warrior #1", items[0].Name);
        Assert.Equal("Goblin Warrior #3", items[2].Name);

        // HP/AC คัดลอกจากแม่แบบ และ **แต่ละตัวมี HP ของตัวเอง** (นี่คือหัวใจของ Part E)
        Assert.All(items, i =>
        {
            Assert.Equal(7, i.CurrentHp);
            Assert.Equal(7, i.MaxHp);
            Assert.Equal(15, i.ArmorClass);
        });
        Assert.Equal(3, items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public async Task Spawning_twice_continues_the_numbering_instead_of_restarting()
    {
        var client = AuthedClient(_dmToken);
        (await client.PostAsJsonAsync($"/campaigns/{_campaignId}/monsters/{_monsterId}/spawn", new { Count = 2 }))
            .EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync(
            $"/campaigns/{_campaignId}/monsters/{_monsterId}/spawn", new { Count = 2 });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var items = await second.Content.ReadFromJsonAsync<List<SpawnedMonsterItem>>(JsonOptions);

        // DM ทยอยเติมศัตรูกลางไฟต์ได้โดยชื่อไม่ชนกับตัวที่ยืนอยู่แล้ว
        Assert.Equal("Goblin Warrior #3", items![0].Name);
        Assert.Equal("Goblin Warrior #4", items[1].Name);
    }

    [Fact]
    public async Task Damage_applies_to_one_instance_only_not_all_copies_of_the_template()
    {
        // นี่คือสิ่งที่ทำไม่ได้ก่อน Part E — เดิมมอนสเตอร์มีแค่แม่แบบเดียว HP จึงเป็นของรวม
        var spawn = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/monsters/{_monsterId}/spawn", new { Count = 2 });
        var items = await spawn.Content.ReadFromJsonAsync<List<SpawnedMonsterItem>>(JsonOptions);
        var wounded = items![0].Id;
        var untouched = items[1].Id;

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, "UPDATE characters SET current_hp = 2 WHERE id = @id", ("id", wounded));

        Assert.Equal(2, await ReadCurrentHpAsync(conn, wounded));
        Assert.Equal(7, await ReadCurrentHpAsync(conn, untouched));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task Invalid_spawn_count_is_rejected(int count)
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/monsters/{_monsterId}/spawn", new { Count = count });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Spawning_a_monster_template_from_another_campaign_is_404()
    {
        // IDOR: แม่แบบมีอยู่จริงแต่เป็นของอีกโต๊ะ — ต้องปฏิเสธเหมือนไม่มีอยู่จริง
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/monsters/{_otherCampaignMonsterId}/spawn", new { Count = 1 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Player_member_cannot_spawn_monsters()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/monsters/{_monsterId}/spawn", new { Count = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── สิ่งที่ผู้เล่นเห็น ──────────────────────────────────────────────────

    [Fact]
    public async Task Player_sees_that_a_monster_exists_but_not_its_hp_or_ac()
    {
        (await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/monsters/{_monsterId}/spawn", new { Count = 1 }))
            .EnsureSuccessStatusCode();

        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/characters");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<List<CharacterListItem>>(JsonOptions);
        Assert.NotNull(items);

        var monster = items!.Single(i => i.Name == "Goblin Warrior #1");
        Assert.True(monster.ControlledByDm);
        // เห็นว่ามีก็อบลินอยู่ (วางบนแผนที่ได้) แต่ตัวเลขถูกปิด — ศัตรูเป็นของที่ต้องค้นเอาในเกม
        Assert.Null(monster.CurrentHp);
        Assert.Null(monster.MaxHp);

        // ตัวละครของผู้เล่นเองยังเห็นตัวเลขตามปกติ (ยืนยันว่าไม่ได้ปิดมั่วทั้งลิสต์)
        var hero = items.Single(i => i.Name == "Hero");
        Assert.NotNull(hero.CurrentHp);
    }

    [Fact]
    public async Task Dm_can_opt_in_to_showing_monster_stats_to_the_party()
    {
        (await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/monsters/{_monsterId}/spawn",
            new { Count = 1, StatsVisibleToParty = true }))
            .EnsureSuccessStatusCode();

        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/characters");
        var items = await response.Content.ReadFromJsonAsync<List<CharacterListItem>>(JsonOptions);
        Assert.NotNull(items);

        var monster = items!.Single(i => i.Name == "Goblin Warrior #1");
        Assert.Equal(7, monster.CurrentHp);
    }

    // ── วง initiative + การเก็บกวาดตอนจบ ────────────────────────────────────

    [Fact]
    public async Task Spawned_monster_can_join_the_initiative_order_and_is_removed_when_the_encounter_ends()
    {
        var client = AuthedClient(_dmToken);
        var spawn = await client.PostAsJsonAsync(
            $"/campaigns/{_campaignId}/monsters/{_monsterId}/spawn", new { Count = 2 });
        var monsters = await spawn.Content.ReadFromJsonAsync<List<SpawnedMonsterItem>>(JsonOptions);
        var monsterIds = monsters!.Select(m => m.Id).ToList();

        // เดิมทำไม่ได้เลย: EncounterParticipant รับแค่ CharacterId มอนสเตอร์จึงเข้าวงไม่ได้
        var createEncounter = await client.PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/encounters",
            new { CharacterIds = new[] { _playerCharacterId }.Concat(monsterIds).ToList() });
        Assert.Equal(HttpStatusCode.Created, createEncounter.StatusCode);

        var encounter = await createEncounter.Content.ReadFromJsonAsync<JsonElement>();
        var encounterId = encounter.GetProperty("id").GetGuid();

        // มอนสเตอร์อยู่ในวง initiative จริง (ไม่ใช่แค่สร้าง encounter ผ่าน)
        var participantCharacterIds = encounter.GetProperty("participants").EnumerateArray()
            .Select(p => p.GetProperty("characterId").GetGuid()).ToList();
        Assert.Contains(monsterIds[0], participantCharacterIds);
        Assert.Contains(monsterIds[1], participantCharacterIds);

        // ให้ตัวหนึ่งล้ม เพื่อดูว่าสรุปแยกตัวที่ล้มกับตัวที่ยังยืนได้
        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await ExecAsync(conn, "UPDATE characters SET current_hp = 0 WHERE id = @id", ("id", monsterIds[0]));
        }

        var end = await client.PostAsJsonAsync($"/campaigns/{_campaignId}/encounters/{encounterId}/end", new { });
        Assert.Equal(HttpStatusCode.OK, end.StatusCode);

        await using var verifyConn = new NpgsqlConnection(OwnerConnectionString);
        await verifyConn.OpenAsync();

        // instance ถูกลบทิ้งทั้งคู่ — HP รายตัวเป็นของชั่วคราวประจำไฟต์ตามที่ผู้ใช้สั่ง
        Assert.Equal(0L, await CountCharactersAsync(verifyConn, monsterIds));

        // แต่ตัวละครผู้เล่นและ NPC ถาวรของ DM ต้องไม่ถูกแตะ
        Assert.Equal(1L, await CountCharactersAsync(verifyConn, [_playerCharacterId]));
        Assert.Equal(1L, await CountCharactersAsync(verifyConn, [_permanentNpcId]));

        // และต้องเหลือ "สรุปภาพรวม" ไว้ในบันทึกแคมเปญ (ไม่ใช่ตัวเลข HP รายเทิร์น)
        await using var journalCmd = new NpgsqlCommand(
            """
            SELECT content, authored_by FROM campaign_journal_entries
            WHERE campaign_id = @c ORDER BY created_at DESC LIMIT 1
            """, verifyConn);
        journalCmd.Parameters.AddWithValue("c", _campaignId);
        await using var reader = await journalCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var content = reader.GetString(0);
        Assert.Contains("การต่อสู้จบลง", content);
        Assert.Contains("ล้มไป 1 ตัว", content);
        Assert.Contains("ยังยืนอยู่ตอนจบ 1 ตัว", content);
        Assert.Equal("System", reader.GetString(1)); // ระบบสรุปเอง ไม่ใช่ DM พิมพ์/AI แต่ง
    }

    /// <summary>
    /// 2026-09-23 — DM วางตัวที่ spawn ลงแผนที่เป็น token `Character` ได้ ถ้าจบ encounter แล้วลบแค่แถว
    /// character จะเหลือ token ผีชี้ไปหาตัวที่ไม่มีอยู่แล้วค้างบนแผนที่ · ต้องลบเฉพาะ token ของ instance
    /// ไม่แตะ token ของผู้เล่นหรือ NPC ถาวร
    /// </summary>
    [Fact]
    public async Task Ending_the_encounter_also_removes_spawned_monsters_map_tokens_only()
    {
        var client = AuthedClient(_dmToken);
        var spawn = await client.PostAsJsonAsync(
            $"/campaigns/{_campaignId}/monsters/{_monsterId}/spawn", new { Count = 1 });
        var monsterId = (await spawn.Content.ReadFromJsonAsync<List<SpawnedMonsterItem>>(JsonOptions))!.Single().Id;

        var sceneId = Guid.NewGuid();
        var monsterTokenId = Guid.NewGuid();
        var heroTokenId = Guid.NewGuid();
        var npcTokenId = Guid.NewGuid();
        var tokensJson = JsonSerializer.Serialize(new object[]
        {
            new { id = monsterTokenId, entityType = "Character", entityId = monsterId, x = 1.0, y = 1.0 },
            new { id = heroTokenId, entityType = "Character", entityId = _playerCharacterId, x = 2.0, y = 2.0 },
            new { id = npcTokenId, entityType = "Character", entityId = _permanentNpcId, x = 3.0, y = 3.0 },
        });
        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await ExecAsync(conn,
                """
                INSERT INTO scenes (id, session_id, name, grid, tokens, fog_layer, is_active)
                VALUES (@id, @sessionId, 'Spawn Token Scene', '{}', @tokens::jsonb, '{}', true)
                """,
                ("id", sceneId), ("sessionId", _sessionId), ("tokens", tokensJson));
        }

        var createEncounter = await client.PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/encounters",
            new { CharacterIds = new[] { _playerCharacterId, _permanentNpcId, monsterId } });
        Assert.Equal(HttpStatusCode.Created, createEncounter.StatusCode);
        var encounterId = (await createEncounter.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var end = await client.PostAsJsonAsync($"/campaigns/{_campaignId}/encounters/{encounterId}/end", new { });
        Assert.Equal(HttpStatusCode.OK, end.StatusCode);

        await using var verifyConn = new NpgsqlConnection(OwnerConnectionString);
        await verifyConn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT tokens::text FROM scenes WHERE id = @id", verifyConn);
        cmd.Parameters.AddWithValue("id", sceneId);
        var remaining = JsonSerializer.Deserialize<JsonElement>((string)(await cmd.ExecuteScalarAsync())!)
            .EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToList();

        Assert.DoesNotContain(monsterTokenId, remaining);
        Assert.Contains(heroTokenId, remaining);
        Assert.Contains(npcTokenId, remaining);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static async Task<int> ReadCurrentHpAsync(NpgsqlConnection conn, Guid characterId)
    {
        await using var cmd = new NpgsqlCommand("SELECT current_hp FROM characters WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", characterId);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountCharactersAsync(NpgsqlConnection conn, IReadOnlyList<Guid> ids)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM characters WHERE id = ANY(@ids)", conn);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task InsertCampaignAsync(NpgsqlConnection conn, Guid id, Guid ownerUserId, string name) =>
        await ExecAsync(conn,
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES
                (@id, @ownerId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """,
            ("id", id), ("ownerId", ownerUserId), ("name", name));

    private static async Task InsertMembershipAsync(NpgsqlConnection conn, Guid campaignId, Guid userId, string role) =>
        await ExecAsync(conn,
            "INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at) VALUES (@id, @c, @u, @role, now())",
            ("id", Guid.NewGuid()), ("c", campaignId), ("u", userId), ("role", role));

    private static async Task InsertSessionAsync(NpgsqlConnection conn, Guid id, Guid campaignId) =>
        await ExecAsync(conn,
            "INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at) " +
            "VALUES (@id, @c, 'Online', 'Active', NULL, now(), NULL)",
            ("id", id), ("c", campaignId));

    private static async Task InsertMonsterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, string name, int ac, int hp) =>
        await ExecAsync(conn,
            """
            INSERT INTO monster_stat_blocks
                (id, campaign_id, name, armor_class, hit_points, actions, condition_refs, review_status,
                 speed_feet, reach_feet)
            VALUES (@id, @c, @name, @ac, @hp, '[]'::jsonb, '[]'::jsonb, 'Approved', 30, 5)
            """,
            ("id", id), ("c", campaignId), ("name", name), ("ac", ac), ("hp", hp));

    private static async Task InsertCharacterAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, Guid? ownerUserId, string name, bool controlledByDm) =>
        await ExecAsync(conn,
            """
            INSERT INTO characters
                (id, campaign_id, owner_user_id, name, sheet, current_hp, max_hp, current_temp_hp,
                 armor_class, initiative_modifier, speed_feet, damage_resistances, damage_vulnerabilities,
                 damage_immunities, level, controlled_by_dm, npc_stats_visible_to_party,
                 harvest_consent, is_orphan, created_at)
            VALUES
                (@id, @c, @owner, @name, '{}'::jsonb, 20, 20, 0, 12, 0, 30,
                 ARRAY[]::text[], ARRAY[]::text[], ARRAY[]::text[], 1, @dm, true, false, false, now())
            """,
            ("id", id), ("c", campaignId), ("owner", (object?)ownerUserId ?? DBNull.Value),
            ("name", name), ("dm", controlledByDm));

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
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"monster-spawn-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Spawn {label}",
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
        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = ANY(@ids)",
            ("ids", new[] { _campaignId, _otherCampaignId }));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)",
            ("ids", new[] { _dmUserId, _playerUserId }));
        await _factory.DisposeAsync();
    }

    private sealed record AuthResponse(string Token, Guid UserId);

    private sealed record SpawnedMonsterItem(Guid Id, string Name, int CurrentHp, int MaxHp, int ArmorClass);

    private sealed record CharacterListItem(
        Guid Id, string Name, int Level, Guid? OwnerUserId, bool ControlledByDm,
        int? CurrentHp, int? MaxHp, int? CurrentTempHp);
}
