using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// ปุ่มสลับภาษา (`?lang=th`) ของคลัง SRD — migration `AddSrdThaiTranslations`
///
/// ตรวจ 3 ชั้นเหมือน `SrdImportTests`: (1) ข้อมูลจริงในฐาน (2) สิทธิ์อ่านอย่างเดียว (3) พฤติกรรม endpoint
/// รวมถึง **backward-compat**: ไม่ส่ง `lang` มาเลยต้องได้อังกฤษเหมือนเดิมทุกประการ (ตารางนี้เพิ่มเข้ามาทีหลัง
/// `SrdEndpoints` ที่มีอยู่แล้ว ต้องไม่ทำให้ผู้เรียกเดิมที่ไม่รู้จัก `lang` ได้ผลต่างไปจากเดิม)
///
/// ห้ามแก้เทสต์เพื่อให้ผ่าน
/// </summary>
public class SrdTranslationTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private const string AppConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "srd-translation-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private WebApplicationFactory<Program> _factory = null!;
    private Guid _userId;
    private string _token = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_userId, _token) = await RegisterAsync();
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM users WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", _userId);
        await cmd.ExecuteNonQueryAsync();
        await _factory.DisposeAsync();
    }

    // ═══ 1) ข้อมูลในฐานจริง ═══════════════════════════════════════════════════

    [Fact]
    public async Task Every_monster_and_spell_has_a_translation_row_and_all_condition_and_rule_content_is_full()
    {
        // ทุกมอนสเตอร์/เวทต้องมีแถวคำแปล 330 + 339 (แม้ name/title ในแถวนั้นเป็นค่าอังกฤษ — ดู
        // Monster_and_spell_names_and_condition_rule_titles_stay_english_not_translated) —
        // เนื้อหาเต็มของสถานะ/กฎ (41 แถวมีป้ายกำกับ) ก็ต้องครบ 100%
        Assert.Equal(330L, await ScalarAsync("SELECT count(*) FROM srd_monster_translations WHERE locale = 'th'"));
        Assert.Equal(339L, await ScalarAsync(
            "SELECT count(*) FROM rules_corpus_translations WHERE locale = 'th' AND category = 'spell'"));
        Assert.Equal(41L, await ScalarAsync(
            "SELECT count(*) FROM rules_corpus_translations WHERE locale = 'th' AND category IN ('condition', 'rule') AND content IS NOT NULL"));
    }

    [Fact]
    public async Task Starter_set_has_full_content_translated_and_no_other_monster_does()
    {
        // "ชุดที่ใช้จริงก่อน" ตามที่ผู้ใช้เลือก — 8 มอนสเตอร์ระบุไว้ชัดเจน ไม่ใช่ตัวเลขคลุมเครือ
        Assert.Equal(8L, await ScalarAsync("SELECT count(*) FROM srd_monster_translations WHERE data IS NOT NULL"));
        foreach (var key in new[]
                 {
                     "goblin-warrior", "goblin-boss", "wolf", "skeleton", "zombie", "giant-rat", "giant-spider", "stirge",
                 })
        {
            Assert.Equal(1L, await ScalarAsync(
                $"SELECT count(*) FROM srd_monster_translations WHERE monster_key = '{key}' AND data IS NOT NULL"));
        }
    }

    [Fact]
    public async Task All_27_cantrips_have_full_content_translated()
    {
        // 2026-09-22: ผู้ใช้เลือก "เวทระดับต่ำก่อน" — คาถริป (ระดับ 0) ต้องครบ 27/27
        // (Fire Bolt/Sacred Flame เป็นคาถริปอยู่แล้วจึงอยู่ในกลุ่ม 27 ตัวนี้ ไม่แยกนับซ้ำ)
        var cantripKeys = new[]
        {
            "acid-splash", "chill-touch", "dancing-lights", "druidcraft", "eldritch-blast", "elementalism",
            "fire-bolt", "guidance", "light", "mage-hand", "mending", "message", "minor-illusion",
            "poison-spray", "prestidigitation", "produce-flame", "ray-of-frost", "resistance", "sacred-flame",
            "shillelagh", "shocking-grasp", "sorcerous-burst", "spare-the-dying", "starry-wisp", "thaumaturgy",
            "true-strike", "vicious-mockery",
        };
        Assert.Equal(27, cantripKeys.Length);
        foreach (var key in cantripKeys)
        {
            Assert.Equal(1L, await ScalarAsync(
                $"SELECT count(*) FROM rules_corpus_translations WHERE category = 'spell' AND key = '{key}' AND content IS NOT NULL"));
        }
    }

    [Fact]
    public async Task All_level_1_spells_have_full_content_translated_and_total_translated_spell_count_is_84()
    {
        // 2026-09-22: ผู้ใช้สั่ง "แปลต่อ" หลังคาถริป — เวทระดับ 1 ทั้งหมดต้องครบ 57/57 (รวม Magic Missile/
        // Cure Wounds ที่แปลไปก่อนหน้าตั้งแต่รอบแรก) · รวมทั้งหมดตอนนี้ = คาถริป 27 + ระดับ 1 อีก 57 = 84
        Assert.Equal(84L, await ScalarAsync(
            "SELECT count(*) FROM rules_corpus_translations WHERE category = 'spell' AND content IS NOT NULL"));

        var level1Keys = new[]
        {
            "alarm", "animal-friendship", "bane", "bless", "burning-hands", "charm-person", "chromatic-orb",
            "color-spray", "command", "comprehend-languages", "create-or-destroy-water", "cure-wounds",
            "detect-evil-and-good", "detect-magic", "detect-poison-and-disease", "disguise-self",
            "dissonant-whispers", "divine-favor", "divine-smite", "ensnaring-strike", "entangle",
            "expeditious-retreat", "faerie-fire", "false-life", "feather-fall", "find-familiar",
            "floating-disk", "fog-cloud", "goodberry", "grease", "guiding-bolt", "healing-word",
            "hellish-rebuke", "heroism", "hex", "hideous-laughter", "hunters-mark", "ice-knife", "identify",
            "illusory-script", "inflict-wounds", "jump", "longstrider", "mage-armor", "magic-missile",
            "protection-from-evil-and-good", "purify-food-and-drink", "ray-of-sickness", "sanctuary",
            "searing-smite", "shield", "shield-of-faith", "silent-image", "sleep", "speak-with-animals",
            "thunderwave", "unseen-servant",
        };
        Assert.Equal(57, level1Keys.Length);
        foreach (var key in level1Keys)
        {
            Assert.Equal(1L, await ScalarAsync(
                $"SELECT count(*) FROM rules_corpus_translations WHERE category = 'spell' AND key = '{key}' AND content IS NOT NULL"));
        }
    }

    [Fact]
    public async Task Condition_names_referenced_inline_inside_spell_content_stay_english()
    {
        // 2026-09-22 (รอบสอง): ผู้ใช้ยืนยันว่าชื่อสถานะ (Condition) ที่ถูกอ้างถึงกลางประโยค — ไม่ใช่แค่
        // title ของ condition เอง — ก็ต้องคงอังกฤษด้วย เช่น charm-person พูดถึงสถานะ Charmed กลางเนื้อความ
        var charmPerson = await TextAsync(
            "SELECT content FROM rules_corpus_translations WHERE category = 'spell' AND key = 'charm-person'");
        Assert.Contains("Charmed", charmPerson);
        Assert.DoesNotContain("สถานะถูกสะกดจิต", charmPerson);

        var entangle = await TextAsync(
            "SELECT content FROM rules_corpus_translations WHERE category = 'spell' AND key = 'entangle'");
        Assert.Contains("Restrained", entangle);
    }

    [Fact]
    public async Task Weapon_item_names_inside_translated_spell_content_stay_english()
    {
        // shillelagh อ้างถึงอาวุธ (Club/Quarterstaff) ในเนื้อความ — ชื่อไอเทมไม่แปลแม้อยู่กลางประโยคไทย
        var content = await TextAsync(
            "SELECT content FROM rules_corpus_translations WHERE category = 'spell' AND key = 'shillelagh'");
        Assert.Contains("Club", content);
        Assert.Contains("Quarterstaff", content);
    }

    [Fact]
    public async Task Monster_and_spell_names_and_condition_rule_titles_stay_english_not_translated()
    {
        // 2026-09-22: ผู้ใช้ระบุว่าชื่อมอนสเตอร์/ชื่อเวท/ชื่อ condition-rule เป็น "ชื่อเฉพาะ" ไม่ต้องแปล —
        // แถวคำแปลต้องเก็บค่าอังกฤษเดียวกับต้นฉบับเป๊ะ ไม่ใช่คำแปลไทย (แปลเฉพาะบริบทโดยรอบ เช่น ขนาด/ชนิด)
        Assert.Equal(0L, await ScalarAsync(@"
            SELECT count(*) FROM srd_monster_translations t
            JOIN srd_monsters m ON m.key = t.monster_key
            WHERE t.locale = 'th' AND t.name <> m.name"));
        Assert.Equal(0L, await ScalarAsync(@"
            SELECT count(*) FROM rules_corpus_translations t
            JOIN rules_corpus_entries e ON e.category = t.category AND e.key = t.key
            WHERE t.locale = 'th' AND t.title <> e.title"));

        // ชื่อมังกรเป็นตัวแทนที่ดี (สูตรผสมสี×วัยเดิมเคยแปลเป็นไทย — ตอนนี้ต้องเหลือ "Adult Red Dragon" เฉย ๆ)
        Assert.Equal("Adult Red Dragon", await TextAsync(
            "SELECT name FROM srd_monster_translations WHERE monster_key = 'adult-red-dragon'"));
    }

    // ═══ 2) สิทธิ์: อ่านอย่างเดียวสำหรับ role แอป (เหมือนตารางต้นฉบับ) ═════════════

    [Theory]
    [InlineData("srd_monster_translations")]
    [InlineData("rules_corpus_translations")]
    public async Task Rls_is_enabled_and_forced_and_app_role_has_select_only(string table)
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

        await using (var cmd = new NpgsqlCommand(
                         "SELECT privilege_type FROM information_schema.table_privileges " +
                         $"WHERE table_schema = 'public' AND table_name = '{table}' AND grantee IN ('dndez_app', 'PUBLIC') " +
                         "ORDER BY privilege_type", conn))
        {
            var granted = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) granted.Add(r.GetString(0));
            Assert.Equal(new[] { "SELECT" }, granted.Distinct().ToArray());
        }
    }

    [Theory]
    [InlineData("UPDATE srd_monster_translations SET name = 'x' WHERE monster_key = 'goblin-warrior'")]
    [InlineData("DELETE FROM srd_monster_translations WHERE monster_key = 'goblin-warrior'")]
    [InlineData("UPDATE rules_corpus_translations SET title = 'x' WHERE key = 'blinded'")]
    public async Task App_role_cannot_write_the_translation_tables(string sql)
    {
        await using var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    // ═══ 3) endpoint ═════════════════════════════════════════════════════════

    [Fact]
    public async Task No_lang_param_behaves_exactly_like_before_this_feature_existed()
    {
        // backward-compat — ผู้เรียกเดิมที่ไม่รู้จัก lang ต้องได้อังกฤษเหมือนเดิมทุกประการ
        var monsters = await GetJsonAsync("/srd/monsters?query=goblin&limit=100");
        var names = monsters.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();
        Assert.Contains("Goblin Warrior", names);
        Assert.DoesNotContain("ก็อบลินนักรบ", names);

        var detail = await GetJsonAsync("/srd/monsters/goblin-warrior");
        Assert.Equal("Goblin Warrior", detail.GetProperty("name").GetString());
        Assert.Equal("Small", detail.GetProperty("size").GetString());

        var corpus = await GetJsonAsync("/srd/corpus/condition/blinded");
        Assert.Equal("Blinded", corpus.GetProperty("title").GetString());
        Assert.Contains("Can", corpus.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Lang_en_explicit_is_identical_to_omitting_it()
    {
        var omitted = await GetJsonAsync("/srd/monsters/goblin-warrior");
        var explicitEn = await GetJsonAsync("/srd/monsters/goblin-warrior?lang=en");
        Assert.Equal(omitted.GetProperty("name").GetString(), explicitEn.GetProperty("name").GetString());
        Assert.Equal(omitted.GetProperty("size").GetString(), explicitEn.GetProperty("size").GetString());
    }

    [Theory]
    [InlineData("/srd/monsters?lang=TH")]
    [InlineData("/srd/monsters?lang=thai")]
    [InlineData("/srd/monsters/goblin-warrior?lang=fr")]
    [InlineData("/srd/corpus?lang=xx")]
    [InlineData("/srd/corpus/condition/blinded?lang=english")]
    public async Task Unknown_lang_values_are_400_not_silently_treated_as_english(string url)
    {
        var response = await AuthedClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Lang_th_translates_monster_list_size_type_and_alignment_but_keeps_english_name()
    {
        // ค้นด้วยชื่ออังกฤษ (ชื่อไม่แปล — "ชื่อเฉพาะ" ตามคำขอผู้ใช้ 2026-09-22)
        var body = await GetJsonAsync("/srd/monsters?query=Goblin&lang=th&limit=100");
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.Contains(items, i => i.GetProperty("name").GetString() == "Goblin Warrior");
        var goblin = items.Single(i => i.GetProperty("name").GetString() == "Goblin Warrior");
        Assert.Equal("เล็ก", goblin.GetProperty("size").GetString());
        Assert.Equal("พรายไพร", goblin.GetProperty("type").GetString());
        Assert.Equal("อลักษณ์กลาง", goblin.GetProperty("alignment").GetString());
        // ตัวเลขไม่แปล — AC/HP/CR ต้องเหมือนเดิมทุกภาษา
        Assert.Equal(15, goblin.GetProperty("armorClass").GetInt32());
    }

    [Fact]
    public async Task Lang_th_monster_detail_translates_full_content_for_the_starter_set_but_keeps_english_name_and_weapon_names()
    {
        var body = await GetJsonAsync("/srd/monsters/goblin-warrior?lang=th");
        // ชื่อมอนสเตอร์ไม่แปล
        Assert.Equal("Goblin Warrior", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("translated").GetBoolean());
        Assert.True(body.GetProperty("contentTranslated").GetBoolean());

        var actions = body.GetProperty("data").GetProperty("actions").EnumerateArray().ToList();
        // ชื่ออาวุธ (Scimitar) เป็น "ชื่อไอเทม" — ไม่แปลเช่นกัน แต่เนื้อความทอยโจมตีต้องเป็นไทย
        var scimitar = actions.Single(a => a.GetProperty("name").GetString() == "Scimitar");
        Assert.Contains("ทอยโจมตีระยะประชิด", scimitar.GetProperty("text").GetString());
        // ตัวเลขในเนื้อหายังเป็นตัวเลขเดิม ไม่ถูกแปล/ปัดเศษ
        Assert.Contains(actions, a => (a.GetProperty("text").GetString() ?? "").Contains("1d6 + 2"));
    }

    [Fact]
    public async Task Lang_th_monster_detail_falls_back_to_english_content_when_not_yet_translated()
    {
        // มอนสเตอร์นอกชุดที่ใช้จริงก่อน — ขนาด/ชนิด/แนวคิดแปลแล้ว (ครบ 330) แต่ traits/actions ยังเป็นอังกฤษ
        // (ชื่อมอนสเตอร์เองไม่แปลอยู่แล้วไม่ว่ากรณีใด)
        var body = await GetJsonAsync("/srd/monsters/ogre?lang=th");
        Assert.Equal("Ogre", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("translated").GetBoolean()); // ขนาด/ชนิด/แนวคิดแปลแล้ว
        Assert.False(body.GetProperty("contentTranslated").GetBoolean());

        var actions = body.GetProperty("data").GetProperty("actions").EnumerateArray().ToList();
        // ท่าของ Ogre ในต้นฉบับคือ "Greatclub" — ต้องเป็นอังกฤษเดิม ไม่ใช่ค่าว่างหรือพัง
        Assert.Contains(actions, a => (a.GetProperty("name").GetString() ?? "").Length > 0);
    }

    [Fact]
    public async Task Lang_th_corpus_list_and_detail_translate_condition_content_but_keep_english_title()
    {
        var list = await GetJsonAsync("/srd/corpus?category=condition&lang=th&limit=100");
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(15, items.Count);
        var blinded = items.Single(i => i.GetProperty("key").GetString() == "blinded");
        // ชื่อ condition เป็น "ชื่อเฉพาะ" — ไม่แปล (ผู้ใช้ยืนยัน 2026-09-22)
        Assert.Equal("Blinded", blinded.GetProperty("title").GetString());
        Assert.True(blinded.GetProperty("contentTranslated").GetBoolean());

        var detail = await GetJsonAsync("/srd/corpus/condition/blinded?lang=th");
        Assert.Equal("Blinded", detail.GetProperty("title").GetString());
        Assert.Contains("มองไม่เห็น", detail.GetProperty("content").GetString());
        Assert.True(detail.GetProperty("contentTranslated").GetBoolean());
    }

    [Fact]
    public async Task Lang_th_spell_detail_uses_translated_content_for_the_starter_set_but_keeps_english_title()
    {
        var body = await GetJsonAsync("/srd/corpus/spell/magic-missile?lang=th");
        // ชื่อเวทเป็น "ชื่อเฉพาะ" — ไม่แปล (ผู้ใช้ยืนยัน 2026-09-22)
        Assert.Equal("Magic Missile", body.GetProperty("title").GetString());
        Assert.True(body.GetProperty("contentTranslated").GetBoolean());
        Assert.Contains("ลูกดอกเวท", body.GetProperty("content").GetString());
        // สำนักเวท/คลาสแปลแล้วอยู่ใน data (ทุกเวท ไม่ใช่แค่ชุดที่ใช้จริง) — เป็นบริบท ไม่ใช่ชื่อเฉพาะ
        Assert.Equal("กระตุ้นพลัง", body.GetProperty("data").GetProperty("school").GetString());
        var classes = body.GetProperty("data").GetProperty("classes").EnumerateArray()
            .Select(c => c.GetString()).ToList();
        Assert.Contains("จอมเวทย์", classes);
        // ตัวเลข (level) ไม่มีในคำแปล ต้องยังเหลืออยู่จากอังกฤษเดิม
        Assert.Equal(1, body.GetProperty("data").GetProperty("level").GetInt32());
    }

    [Fact]
    public async Task Lang_th_spell_detail_falls_back_to_english_body_when_not_yet_translated_and_title_stays_english()
    {
        var body = await GetJsonAsync("/srd/corpus/spell/fireball?lang=th");
        Assert.Equal("Fireball", body.GetProperty("title").GetString());
        Assert.False(body.GetProperty("contentTranslated").GetBoolean());
        // เนื้อหายังเป็นอังกฤษของเดิม (ไม่ใช่ null/ว่างเปล่า)
        Assert.Contains("fire", body.GetProperty("content").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Spell_class_filter_matches_translated_class_name_when_lang_is_th()
    {
        // คลาส ("จอมเวทย์") เป็นบริบทที่แปล — ไม่ใช่ชื่อเฉพาะ ยังกรองด้วยคำแปลไทยได้ตามเดิม
        var body = await GetJsonAsync("/srd/corpus?category=spell&lang=th&class=" + Uri.EscapeDataString("จอมเวทย์") + "&level=1&limit=100");
        var keys = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.Contains("magic-missile", keys);
        Assert.DoesNotContain("cure-wounds", keys); // ไม่มีคลาสจอมเวทย์
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await AuthedClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    private async Task<(Guid Id, string Token)> RegisterAsync()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/auth/register", new
        {
            Email = $"srd-th-{_suffix}@example.test",
            Password,
            DisplayName = "Srd Th",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private record AuthResponse(string Token, Guid UserId);

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
}
