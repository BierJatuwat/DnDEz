using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// นำเข้า SRD 5.2.1 (migration `ImportSrd521`) — ตรวจ 3 ชั้น:
///  1) ข้อมูลในฐานจริง: จำนวน + ค่าตัวอย่างที่อ่านตรงจาก `docs/SRD_CC_v5.2.1.pdf` (ไม่ใช่จาก JSON ที่ parser สร้าง —
///     ค่าคาดหวังด้านล่างทุกตัวเปิดดูจากข้อความในไฟล์ PDF โดยตรงก่อนเขียนเทสต์)
///  2) สิทธิ์: role แอปอ่านได้อย่างเดียวทั้ง `srd_monsters` และ `rules_corpus_entries` (ทั้งระดับสิทธิ์ตารางและ RLS)
///  3) endpoint: อ่าน/ค้นหา/กรอง + DM คัดลอกมอนสเตอร์เข้าแคมเปญ (DM-only, idempotent, ไม่ข้ามแคมเปญ)
///
/// ห้ามแก้เทสต์เพื่อให้ผ่าน — ถ้าค่าไม่ตรง ให้ตรวจ PDF ก่อนว่าฝั่งไหนผิด
/// </summary>
public class SrdImportTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "srd-import-secret-1";

    // ข้อความจากหน้า "Legal Information" ของ PDF — ต้องตรงทุกตัวอักษร (CC-BY-4.0 บังคับให้ใช้ถ้อยคำนี้)
    private const string ExpectedAttribution =
        "This work includes material from the System Reference Document 5.2.1 (\"SRD 5.2.1\") by Wizards of the Coast LLC, "
        + "available at https://www.dndbeyond.com/srd. The SRD 5.2.1 is licensed under the Creative Commons Attribution 4.0 "
        + "International License, available at https://creativecommons.org/licenses/by/4.0/legalcode.";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;
    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"SRD Campaign {_suffix}");
        await InsertCampaignAsync(conn, _otherCampaignId, _outsiderUserId, $"SRD Other {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _outsiderUserId, "Dm");
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await ExecAsync(conn, "DELETE FROM campaigns WHERE id = ANY(@ids)", ("ids", new[] { _campaignId, _otherCampaignId }));
        await ExecAsync(conn, "DELETE FROM users WHERE id = ANY(@ids)", ("ids", new[] { _dmUserId, _playerUserId, _outsiderUserId }));
        await _factory.DisposeAsync();
    }

    // ═══ 1) ข้อมูลในฐานจริง ═══════════════════════════════════════════════════

    [Fact]
    public async Task Imported_row_counts_match_the_source_document()
    {
        // 330 = จำนวนรายการใน "Index of Stat Blocks" · 339 = เวทใน "Spell Descriptions" ตรงกับรายชื่อเวทของทุกคลาส
        // 15 = รายการใต้ "This glossary defines these conditions" · 26 = กฎที่มีป้าย Action/Area of Effect/Hazard/Attitude
        Assert.Equal(330L, await ScalarAsync("SELECT count(*) FROM srd_monsters"));
        Assert.Equal(339L, await ScalarAsync("SELECT count(*) FROM rules_corpus_entries WHERE category = 'spell'"));
        Assert.Equal(15L, await ScalarAsync("SELECT count(*) FROM rules_corpus_entries WHERE category = 'condition'"));
        Assert.Equal(26L, await ScalarAsync("SELECT count(*) FROM rules_corpus_entries WHERE category = 'rule'"));
    }

    [Theory]
    // key, AC, HP, speed(เดิน), reach, CR, XP — ตัวเลขจาก stat block ในต้นฉบับ
    [InlineData("goblin-warrior", 15, 10, 30, 5, "1/4", 50)]
    [InlineData("goblin-boss", 17, 21, 30, 5, "1", 200)]
    [InlineData("roper", 20, 93, 10, 60, "5", 1800)]   // Tentacle "reach 60 ft." — reach ยาวสุดในเล่ม
    [InlineData("tarrasque", 25, 697, 60, 30, "30", 155000)]
    public async Task Monster_stat_block_values_match_the_source(
        string key, int ac, int hp, int speed, int reach, string cr, int xp)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT armor_class, hit_points, speed_feet, reach_feet, challenge_rating, xp FROM srd_monsters WHERE key = @k", conn);
        cmd.Parameters.AddWithValue("k", key);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), $"ไม่พบ {key}");
        Assert.Equal((ac, hp, speed, reach, cr, xp),
            (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4), r.GetInt32(5)));
    }

    [Fact]
    public async Task Source_typos_are_repaired_only_where_the_formula_proves_them()
    {
        // ต้นฉบับพิมพ์ "Int 6 −2 2" (ตกเครื่องหมายลบหน้า save) — mod = floor((6-10)/2) = -2 และ save = mod เมื่อไม่ได้ฝึกเซฟ
        Assert.Equal("-2", await TextAsync(
            "SELECT data->'abilities'->'int'->>'save' FROM srd_monsters WHERE key = 'young-white-dragon'"));
        Assert.Contains("เครื่องหมายลบ", await TextAsync(
            "SELECT data->'abilities'->'int'->>'sourceFixup' FROM srd_monsters WHERE key = 'young-white-dragon'"));
        // Tarrasque "Int 3 −4 +5" — save เป็นบวกจริง (ฝึกเซฟ) ต้องไม่ถูก "แก้" ตามกฎข้างบน
        Assert.Equal("5", await TextAsync(
            "SELECT data->'abilities'->'int'->>'save' FROM srd_monsters WHERE key = 'tarrasque'"));
        // "Con22" ไม่มีช่องว่างในต้นฉบับ แต่ต้องอ่านได้เป็น 22
        Assert.Equal("22", await TextAsync(
            "SELECT data->'abilities'->'con'->>'score' FROM srd_monsters WHERE key = 'adult-white-dragon'"));
    }

    [Fact]
    public async Task Spell_values_match_the_source()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT data->>'level', data->>'school', data->>'castingTime', data->>'range', data->>'components', " +
            "data->>'duration', content FROM rules_corpus_entries WHERE category = 'spell' AND key = 'magic-missile'", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal("1", r.GetString(0));
        Assert.Equal("Evocation", r.GetString(1));
        Assert.Equal("Action", r.GetString(2));
        Assert.Equal("120 feet", r.GetString(3));
        Assert.Equal("V, S", r.GetString(4));
        Assert.Equal("Instantaneous", r.GetString(5));
        Assert.Contains("A dart deals 1d4 + 1 Force damage to its target.", r.GetString(6));
        Assert.Contains("Using a Higher-Level Spell Slot. The spell creates one more dart", r.GetString(6));
    }

    [Fact]
    public async Task Spell_header_with_a_wrapped_class_list_is_not_lost()
    {
        // "Level 1 Abjuration (Bard, Cleric, Druid, Paladin,\nRanger)" — รายชื่อคลาสยาวถูกตัดขึ้นบรรทัดใหม่ในต้นฉบับ
        // เวอร์ชันแรกของ parser ทำเวทกลุ่มนี้หายเงียบ ๆ 37 ตัว (รวม Cure Wounds ที่ preset นักบวชใช้)
        Assert.Equal("""["Bard", "Cleric", "Druid", "Paladin", "Ranger"]""", await TextAsync(
            "SELECT (data->'classes')::text FROM rules_corpus_entries WHERE category = 'spell' AND key = 'cure-wounds'"));
    }

    [Fact]
    public async Task Cantrips_have_level_zero_and_the_cantrip_upgrade_text()
    {
        Assert.Equal("0", await TextAsync(
            "SELECT data->>'level' FROM rules_corpus_entries WHERE category = 'spell' AND key = 'fire-bolt'"));
        Assert.Contains("Cantrip Upgrade.", await TextAsync(
            "SELECT content FROM rules_corpus_entries WHERE category = 'spell' AND key = 'fire-bolt'"));
    }

    [Fact]
    public async Task Condition_text_is_split_into_its_rules()
    {
        var blinded = await TextAsync(
            "SELECT content FROM rules_corpus_entries WHERE category = 'condition' AND key = 'blinded'");
        Assert.Contains("Can’t See. You can’t see and automatically fail any ability check that requires sight.", blinded);
        Assert.Contains("Attacks Affected. Attack rolls against you have Advantage, and your attack rolls have Disadvantage.", blinded);

        // ตัวที่เคยถูกตัดย่อหน้าผิด: "Rest removes 1 of your Exhaustion levels" เป็นประโยคธรรมดา ไม่ใช่หัวข้อย่อย
        var exhaustion = await TextAsync(
            "SELECT content FROM rules_corpus_entries WHERE category = 'condition' AND key = 'exhaustion'");
        Assert.Contains("Removing Exhaustion Levels. Finishing a Long Rest removes 1 of your Exhaustion levels.", exhaustion);
    }

    [Fact]
    public async Task Imported_text_has_no_broken_glyphs_or_page_furniture()
    {
        // ต้นทางเคยเสีย U+FFFD ~700 จุดเพราะ encoding ของ pdftotext และหัวกระดาษ "System Reference Document 5.2.1" แทรกกลางข้อความ
        Assert.Equal(0L, await ScalarAsync(
            "SELECT count(*) FROM rules_corpus_entries WHERE content LIKE '%' || chr(65533) || '%' OR title LIKE '%' || chr(65533) || '%'"));
        Assert.Equal(0L, await ScalarAsync(
            "SELECT count(*) FROM srd_monsters WHERE data::text LIKE '%' || chr(65533) || '%'"));
        Assert.Equal(0L, await ScalarAsync(
            "SELECT count(*) FROM rules_corpus_entries WHERE content LIKE '%System Reference Document 5.2.1%'"));
        Assert.Equal(0L, await ScalarAsync(
            "SELECT count(*) FROM srd_monsters WHERE data::text LIKE '%System Reference Document 5.2.1%'"));
    }

    [Fact]
    public async Task Every_monster_has_the_numbers_the_engine_needs()
    {
        // อยู่ในตารางแล้วต้องผ่าน CHECK เสมอ — เทสต์นี้กันการแก้ CHECK ทีหลังจนหลวม
        Assert.Equal(0L, await ScalarAsync(
            "SELECT count(*) FROM srd_monsters WHERE armor_class <= 0 OR hit_points <= 0 OR speed_feet < 0 OR reach_feet < 5"));
        Assert.Equal(330L, await ScalarAsync(
            "SELECT count(*) FROM srd_monsters WHERE jsonb_typeof(data->'abilities') = 'object' AND (SELECT count(*) FROM jsonb_object_keys(data->'abilities')) = 6"));
    }

    // ═══ 2) สิทธิ์: อ่านอย่างเดียวสำหรับ role แอป ═════════════════════════════

    [Theory]
    [InlineData("srd_monsters")]
    [InlineData("rules_corpus_entries")]
    public async Task Rls_is_enabled_and_forced_and_a_policy_exists(string table)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(
                         $"SELECT relrowsecurity, relforcerowsecurity FROM pg_class WHERE oid = 'public.{table}'::regclass", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await r.ReadAsync());
            Assert.True(r.GetBoolean(0), $"{table}: ENABLE ROW LEVEL SECURITY ยังไม่ได้ตั้ง");
            Assert.True(r.GetBoolean(1), $"{table}: FORCE ROW LEVEL SECURITY ยังไม่ได้ตั้ง");
        }

        Assert.True(await ScalarAsync(
            $"SELECT count(*) FROM pg_policies WHERE schemaname = 'public' AND tablename = '{table}'") > 0);
    }

    [Theory]
    [InlineData("srd_monsters")]
    [InlineData("rules_corpus_entries")]
    public async Task App_role_has_select_only(string table)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT privilege_type FROM information_schema.table_privileges " +
            $"WHERE table_schema = 'public' AND table_name = '{table}' AND grantee IN ('dndez_app', 'PUBLIC') " +
            "ORDER BY privilege_type", conn);
        var granted = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) granted.Add(r.GetString(0));
        }

        Assert.Equal(new[] { "SELECT" }, granted.Distinct().ToArray());
    }

    [Theory]
    [InlineData("INSERT INTO srd_monsters (id, key, name, size, type, alignment, armor_class, hit_points, speed_feet, reach_feet, challenge_rating, xp, source_version) VALUES (gen_random_uuid(), 'x', 'X', 'Small', 'Beast', 'Unaligned', 10, 5, 30, 5, '0', 0, '5.2.1')")]
    [InlineData("UPDATE srd_monsters SET hit_points = 1 WHERE key = 'goblin-warrior'")]
    [InlineData("DELETE FROM srd_monsters WHERE key = 'goblin-warrior'")]
    [InlineData("UPDATE rules_corpus_entries SET content = 'tampered' WHERE key = 'blinded'")]
    [InlineData("DELETE FROM rules_corpus_entries WHERE key = 'blinded'")]
    public async Task App_role_cannot_write_the_srd_tables(string sql)
    {
        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState); // insufficient_privilege
    }

    // ═══ 3) endpoint ═════════════════════════════════════════════════════════

    [Fact]
    public async Task Attribution_is_public_and_matches_the_source_text_exactly()
    {
        var response = await _factory.CreateClient().GetAsync("/srd/attribution");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ExpectedAttribution, body.GetProperty("text").GetString());
        Assert.Equal("CC-BY-4.0", body.GetProperty("license").GetString());
    }

    [Theory]
    [InlineData("/srd/monsters")]
    [InlineData("/srd/monsters/goblin-warrior")]
    [InlineData("/srd/corpus")]
    [InlineData("/srd/corpus/spell/magic-missile")]
    public async Task Reading_the_srd_requires_login(string url)
    {
        var response = await _factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Monster_list_defaults_to_25_sorted_by_cr_and_reports_the_total()
    {
        var body = await GetJsonAsync(_playerToken, "/srd/monsters");
        Assert.Equal(330, body.GetProperty("total").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(25, items.Count);
        var crs = items.Select(i => Cr(i.GetProperty("challengeRating").GetString()!)).ToList();
        Assert.Equal(crs.OrderBy(c => c).ToList(), crs);
        Assert.Equal(ExpectedAttribution, body.GetProperty("attribution").GetString());
    }

    [Fact]
    public async Task Monster_list_filters_by_name_case_insensitively()
    {
        var body = await GetJsonAsync(_playerToken, "/srd/monsters?query=GOBLIN&limit=100");
        var names = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToList();
        Assert.Contains("Goblin Warrior", names);
        Assert.Contains("Goblin Boss", names);
        Assert.All(names, n => Assert.Contains("goblin", n, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    public async Task Like_wildcards_in_the_query_are_literal_not_patterns(string query)
    {
        // ถ้าไม่ escape "%" จะจับทุกแถว (330) · "_" จะจับทุกชื่อที่มีอย่างน้อย 1 ตัวอักษร
        var body = await GetJsonAsync(_playerToken, $"/srd/monsters?query={Uri.EscapeDataString(query)}");
        Assert.Equal(0, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Monster_list_filters_by_max_challenge_rating_using_real_fractions()
    {
        var body = await GetJsonAsync(_playerToken, "/srd/monsters?crMax=1/4&limit=100");
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.True(Cr(i.GetProperty("challengeRating").GetString()!) <= 0.25m));
        var names = items.Select(i => i.GetProperty("name").GetString()).ToList();
        Assert.Contains("Goblin Warrior", names);      // CR 1/4 พอดี
        Assert.DoesNotContain("Goblin Boss", names);   // CR 1
    }

    [Fact]
    public async Task Type_filter_is_an_exact_match()
    {
        var body = await GetJsonAsync(_playerToken, "/srd/monsters?type=beast&limit=100");
        var types = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("type").GetString()).Distinct().ToList();
        Assert.Equal(new[] { "Beast" }, types); // ไม่ปน "Swarm of Tiny Beasts"
    }

    [Theory]
    [InlineData("/srd/monsters?crMax=abc")]
    [InlineData("/srd/monsters?crMax=1/0")]
    [InlineData("/srd/corpus?category=potion")]
    public async Task Bad_filters_are_400(string url)
    {
        var response = await AuthedClient(_playerToken).GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_query_is_400_and_limit_is_clamped()
    {
        var tooLong = new string('a', 101);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await AuthedClient(_playerToken).GetAsync($"/srd/monsters?query={tooLong}")).StatusCode);

        var body = await GetJsonAsync(_playerToken, "/srd/monsters?limit=100000&offset=-5");
        Assert.Equal(100, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Monster_detail_returns_the_full_stat_block()
    {
        var body = await GetJsonAsync(_playerToken, "/srd/monsters/goblin-warrior");
        Assert.Equal("Goblin Warrior", body.GetProperty("name").GetString());
        var actions = body.GetProperty("data").GetProperty("actions").EnumerateArray().ToList();
        var scimitar = actions.Single(a => a.GetProperty("name").GetString() == "Scimitar");
        Assert.Contains("Melee Attack Roll: +4, reach 5 ft.", scimitar.GetProperty("text").GetString());
        Assert.Contains("5 (1d6 + 2) Slashing damage", scimitar.GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await AuthedClient(_playerToken).GetAsync("/srd/monsters/no-such-monster")).StatusCode);
    }

    [Fact]
    public async Task Corpus_lists_the_fifteen_conditions()
    {
        var body = await GetJsonAsync(_playerToken, "/srd/corpus?category=condition&limit=100");
        Assert.Equal(15, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Spell_filter_by_level_and_class()
    {
        var body = await GetJsonAsync(_playerToken, "/srd/corpus?category=spell&level=1&class=wizard&limit=100");
        var keys = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.Contains("magic-missile", keys);
        Assert.DoesNotContain("cure-wounds", keys);  // เวทคลาส Bard/Cleric/Druid/Paladin/Ranger ไม่มี Wizard
        Assert.DoesNotContain("fire-bolt", keys);    // cantrip = ระดับ 0
        Assert.All(body.GetProperty("items").EnumerateArray(),
            i => Assert.Equal(1, i.GetProperty("data").GetProperty("level").GetInt32()));
    }

    [Fact]
    public async Task Corpus_detail_returns_the_full_text_and_license()
    {
        var body = await GetJsonAsync(_playerToken, "/srd/corpus/spell/magic-missile");
        Assert.Equal("Magic Missile", body.GetProperty("title").GetString());
        Assert.Equal("CC-BY-4.0", body.GetProperty("license").GetString());
        Assert.Equal("SRD 5.2.1", body.GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await AuthedClient(_playerToken).GetAsync("/srd/corpus/spell/nope")).StatusCode);
    }

    // ── DM คัดลอกมอนสเตอร์เข้าแคมเปญ ─────────────────────────────────────

    [Fact]
    public async Task Dm_can_copy_an_srd_monster_into_the_campaign()
    {
        var response = await PostCopyAsync(_dmToken, _campaignId, "goblin-warrior");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CopyResponse>(JsonOptions);
        Assert.True(body!.Created);

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT name, armor_class, hit_points, speed_feet, reach_feet, review_status, srd_key, actions::text " +
            "FROM monster_stat_blocks WHERE id = @id AND campaign_id = @c", conn);
        cmd.Parameters.AddWithValue("id", body.MonsterId);
        cmd.Parameters.AddWithValue("c", _campaignId);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(("Goblin Warrior", 15, 10, 30, 5, "Approved", "goblin-warrior"),
            (r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5), r.GetString(6)));
        var actions = JsonDocument.Parse(r.GetString(7)).RootElement.EnumerateArray().ToList();
        Assert.Contains(actions, a => a.GetProperty("kind").GetString() == "Action" && a.GetProperty("name").GetString() == "Scimitar");
        Assert.Contains(actions, a => a.GetProperty("kind").GetString() == "BonusAction" && a.GetProperty("name").GetString() == "Nimble Escape");
    }

    [Fact]
    public async Task Copying_carries_the_long_reach_that_opportunity_attacks_use()
    {
        // Sprint H Part 7 ใช้ reach_feet ของ monster_stat_blocks ตัดสิน OA — Roper เอื้อม 60 ฟุต ต้องไม่ถูกลดเหลือ 5
        var body = await (await PostCopyAsync(_dmToken, _campaignId, "roper")).Content.ReadFromJsonAsync<CopyResponse>(JsonOptions);
        Assert.Equal(60L, await ScalarAsync($"SELECT reach_feet FROM monster_stat_blocks WHERE id = '{body!.MonsterId}'"));
    }

    [Fact]
    public async Task Copying_twice_returns_the_same_template_and_creates_no_duplicate()
    {
        var first = await (await PostCopyAsync(_dmToken, _campaignId, "goblin-boss")).Content.ReadFromJsonAsync<CopyResponse>(JsonOptions);
        var secondResponse = await PostCopyAsync(_dmToken, _campaignId, "goblin-boss");
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await secondResponse.Content.ReadFromJsonAsync<CopyResponse>(JsonOptions);
        Assert.False(second!.Created);
        Assert.Equal(first!.MonsterId, second.MonsterId);
        Assert.Equal(1L, await ScalarAsync(
            $"SELECT count(*) FROM monster_stat_blocks WHERE campaign_id = '{_campaignId}' AND srd_key = 'goblin-boss'"));
    }

    [Fact]
    public async Task The_same_srd_monster_can_be_copied_into_two_different_campaigns()
    {
        Assert.Equal(HttpStatusCode.Created, (await PostCopyAsync(_dmToken, _campaignId, "goblin-warrior")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostCopyAsync(_outsiderToken, _otherCampaignId, "goblin-warrior")).StatusCode);
    }

    [Fact]
    public async Task A_player_cannot_copy_a_monster()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await PostCopyAsync(_playerToken, _campaignId, "goblin-warrior")).StatusCode);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM monster_stat_blocks WHERE campaign_id = '{_campaignId}'"));
    }

    [Fact]
    public async Task A_dm_of_another_campaign_cannot_write_into_this_one()
    {
        var response = await PostCopyAsync(_outsiderToken, _campaignId, "goblin-warrior");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM monster_stat_blocks WHERE campaign_id = '{_campaignId}'"));
    }

    [Fact]
    public async Task Copy_requires_login()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            $"/campaigns/{_campaignId}/monsters/from-srd", new { key = "goblin-warrior" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Copy_rejects_unknown_missing_and_oversized_keys()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await PostCopyAsync(_dmToken, _campaignId, "no-such-monster")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostCopyAsync(_dmToken, _campaignId, "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostCopyAsync(_dmToken, _campaignId, new string('a', 101))).StatusCode);
        var noBody = await AuthedClient(_dmToken).PostAsync($"/campaigns/{_campaignId}/monsters/from-srd", null);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private record CopyResponse(Guid MonsterId, string SrdKey, string Name, bool Created);

    private record AuthResponse(string Token, Guid UserId);

    private static decimal Cr(string s)
    {
        var parts = s.Split('/');
        return parts.Length == 2
            ? decimal.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)
              / decimal.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)
            : decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<HttpResponseMessage> PostCopyAsync(string token, Guid campaignId, string key) =>
        await AuthedClient(token).PostAsJsonAsync($"/campaigns/{campaignId}/monsters/from-srd", new { key });

    private async Task<JsonElement> GetJsonAsync(string token, string url)
    {
        var response = await AuthedClient(token).GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/register", new
        {
            Email = $"srd-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Srd {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private static async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string> TextAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertCampaignAsync(NpgsqlConnection conn, Guid id, Guid ownerUserId, string name) =>
        await ExecAsync(conn,
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@id, @ownerId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """, ("id", id), ("ownerId", ownerUserId), ("name", name));

    private static async Task InsertMembershipAsync(NpgsqlConnection conn, Guid campaignId, Guid userId, string role) =>
        await ExecAsync(conn,
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, @role, now())
            """, ("id", Guid.NewGuid()), ("campaignId", campaignId), ("userId", userId), ("role", role));
}
