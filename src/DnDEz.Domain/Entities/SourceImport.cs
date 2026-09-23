using DnDEz.Domain.Enums;
using Pgvector;

namespace DnDEz.Domain.Entities;

// ═══ Source import · RulesCorpus · คลัง preset ═══ dm-side-design §5, player-side-design §18-§20

/// <summary>
/// ⚠️ PRIVATE ต่อแคมเปญเสมอ — เข้าถึงได้เฉพาะ DM role, filter ด้วย campaign_id ทุกครั้ง
/// ไฟล์ดิบเป็น signed URL อายุสั้น · delete เป็น hard delete · ปิดทั้งระบบด้วย ENABLE_SOURCE_IMPORT
/// </summary>
public class SourceDocument
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;

    /// <summary>DM เจ้าของเอกสาร — เนื้อหานี้ห้ามหลุดออกนอกแคมเปญไม่ว่าทางไหน</summary>
    public Guid OwnerUserId { get; set; }

    public string FileName { get; set; } = null!;
    public string StorageKey { get; set; } = null!;
    public OcrStatus OcrStatus { get; set; } = OcrStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>vector index แยก namespace ต่อแคมเปญ — CampaignId ต้องอยู่ในทุก query เสมอ</summary>
public class RawChunk
{
    public Guid Id { get; set; }
    public Guid SourceDocumentId { get; set; }
    public SourceDocument SourceDocument { get; set; } = null!;

    /// <summary>denormalize มาไว้ที่นี่ด้วยเพื่อให้ filter/RLS ทำได้โดยไม่ต้อง join</summary>
    public Guid CampaignId { get; set; }

    public string Content { get; set; } = null!;
    public int? PageNumber { get; set; }

    /// <summary>
    /// ⚠️ ยังไม่ได้ตัดสินใจว่าใช้ embedding provider เจ้าไหน (Anthropic ไม่มี embeddings API)
    /// มิติ 1536 เป็นค่าตั้งต้นชั่วคราว ต้องแก้เมื่อเลือก provider จริง
    /// </summary>
    public Vector? Embedding { get; set; }
}

/// <summary>ตัวเลขกลไกต้องตรงเป๊ะ — ไม่ commit อัตโนมัติ ต้องผ่านหน้า DM review เสมอ</summary>
public class MonsterStatBlock
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public Guid? SourceDocumentId { get; set; }

    public string Name { get; set; } = null!;
    public int ArmorClass { get; set; }
    public int HitPoints { get; set; }

    /// <summary>
    /// Sprint H Part 7 — ระยะเดินต่อเทิร์น (ฟุต) · ดีฟอลต์ 30 = ค่ามาตรฐานของสิ่งมีชีวิตขนาดกลางใน SRD
    /// ยังไม่มี endpoint ให้ DM แก้ค่านี้ (ตอนนี้ตั้งได้ตอน import/แก้ผ่าน DB) — ดูรายการที่เหลือใน
    /// doc comment ของ migration `AddMonsterSpeedAndReach`
    /// </summary>
    public int SpeedFeet { get; set; } = 30;

    /// <summary>
    /// Sprint H Part 7 — ระยะเอื้อม (ฟุต) ที่ใช้ตัดสิน opportunity attack · ดีฟอลต์ 5 ตาม SRD 5.2 "Reach"
    /// (*"A creature has a 5-foot reach"*) · ก่อน Part นี้ `MovementSceneContext` ใช้ 5 ตายตัวกับทุกตัว
    /// มอนสเตอร์เอื้อมยาวจึงถูกคิดผิดมาตลอด ทั้งที่ engine รองรับค่าอื่นอยู่แล้ว
    /// </summary>
    public int ReachFeet { get; set; } = 5;

    public string Actions { get; set; } = "[]";
    public string ConditionRefs { get; set; } = "[]";
    public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Pending;

    /// <summary>
    /// key ของ <see cref="SrdMonster"/> ที่แถวนี้ถูกคัดลอกมา (null = DM สร้างเอง/มาจาก source import) — ใช้ 2 อย่าง:
    /// กัน DM เพิ่ม "Goblin Warrior" ซ้ำเป็นสองแม่แบบ (unique ต่อแคมเปญ) และบอกที่มาเพื่อแสดง attribution ของ CC-BY-4.0
    /// </summary>
    public string? SrdKey { get; set; }
}

public class NpcProfile
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public Guid? SourceDocumentId { get; set; }

    public string Name { get; set; } = null!;
    public string? Motive { get; set; }
    public string? VoiceHint { get; set; }
    public string KnowsSecrets { get; set; } = "[]";
    public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Pending;
}

public class MapAsset
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public Guid? SourceDocumentId { get; set; }

    public string StorageKey { get; set; } = null!;
    public string? CropSource { get; set; }
    public string? GridAlignment { get; set; }
    public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Pending;
}

/// <summary>
/// Companion mode — ถ่ายรูป sheet กระดาษ แล้วผ่าน vision extraction pipeline เดียวกับ source import
/// รอ DM review ก่อน commit เป็น Character จริง — player-side-design §20
/// </summary>
public class ExtractedCharacterSheet
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public Guid? SourceDocumentId { get; set; }

    public string RawData { get; set; } = "{}";

    /// <summary>รายการที่ AI อ่านไม่ชัด — ต้องรายงานแทนการเดา</summary>
    public string[] Unresolved { get; set; } = [];

    public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Pending;
    public Guid? CommittedCharacterId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// SRD 5.2 (CC-BY-4.0) ฝังมากับระบบตั้งแต่ deploy — ใช้เป็นฐานของ rules engine
/// และ condition tooltip ฝั่งผู้เล่นโดย "ไม่ต้องเรียก AI" (ไม่เปลือง token) — player-side-design §13
/// ไม่ผูกกับแคมเปญ = เป็น global read-only
/// </summary>
public class RulesCorpusEntry
{
    public Guid Id { get; set; }

    /// <summary>condition / spell / feat / item / rule</summary>
    public string Category { get; set; } = null!;

    /// <summary>slug สำหรับ lookup เช่น "poisoned"</summary>
    public string Key { get; set; } = null!;

    public string Title { get; set; } = null!;
    public string Content { get; set; } = null!;
    public string License { get; set; } = "CC-BY-4.0";
    public string Source { get; set; } = "SRD 5.2";

    /// <summary>
    /// ข้อมูลเชิงโครงสร้างของรายการนี้ (jsonb) — เวท: level/school/classes/castingTime/range/components/duration ·
    /// กฎที่มีป้าย: `{"tag":"Action"}` ฯลฯ · ส่วน <see cref="Content"/> ยังเป็นข้อความอ่านตรง ๆ สำหรับ tooltip
    /// เพิ่มใน migration `ImportSrd521` (ก่อนหน้านั้นตารางนี้ไม่มีแถวเลย)
    /// </summary>
    public string Data { get; set; } = "{}";
}

/// <summary>
/// คลังมอนสเตอร์ SRD 5.2.1 (CC-BY-4.0) ระดับ **global อ่านอย่างเดียว** — ไม่ผูกแคมเปญ (ต่างจาก
/// <see cref="MonsterStatBlock"/> ที่เป็นแม่แบบระดับแคมเปญและ DM แก้ได้)
///
/// **ทำไมแยกตาราง ไม่ยัดใส่ monster_stat_blocks**: แถวใน monster_stat_blocks ต้องมี campaign_id + RLS ต่อแคมเปญ
/// และต้องผ่านด่าน review ของ DM · ข้อมูล SRD เป็นของกลางที่ตรวจแล้วตอน import จึงใช้เส้นทาง "DM เลือกจากคลัง →
/// คัดลอกเข้าแคมเปญ" (`POST /campaigns/{id}/monsters/from-srd`) แทน — DM แก้ค่าของสำเนาได้โดยไม่กระทบคลังกลาง
/// </summary>
public class SrdMonster
{
    public Guid Id { get; set; }

    /// <summary>slug ของชื่อ เช่น "goblin-warrior" — unique · ใช้อ้างอิงจาก API</summary>
    public string Key { get; set; } = null!;
    public string Name { get; set; } = null!;

    /// <summary>"Small" / "Medium or Small" ฯลฯ ตามต้นฉบับ (บางตัวมีสองขนาด จึงเป็น text ไม่ใช่ enum)</summary>
    public string Size { get; set; } = null!;
    public string Type { get; set; } = null!;
    public string Alignment { get; set; } = null!;

    public int ArmorClass { get; set; }
    public int HitPoints { get; set; }

    /// <summary>ความเร็วเดิน (ฟุต) — ตัวเลขแรกของบรรทัด Speed · บินอย่างเดียว = 0 (ความเร็วอื่นอยู่ใน <see cref="Data"/>)</summary>
    public int SpeedFeet { get; set; }

    /// <summary>ระยะเอื้อมสูงสุดของท่าระยะประชิด (ฟุต) · ไม่มีท่าระยะประชิดเลย = 5 ตาม SRD "Reach"</summary>
    public int ReachFeet { get; set; }

    /// <summary>เก็บเป็นข้อความเพราะมี "1/8" "1/4" "1/2" — ห้ามแปลงเป็นตัวเลขแล้วเรียงลำดับตรง ๆ</summary>
    public string ChallengeRating { get; set; } = null!;
    public int Xp { get; set; }

    /// <summary>เวอร์ชัน SRD ที่นำเข้า เช่น "5.2.1"</summary>
    public string SourceVersion { get; set; } = null!;

    /// <summary>stat block เต็มที่แยกจาก PDF (jsonb): abilities, skills, senses, traits, actions ฯลฯ</summary>
    public string Data { get; set; } = "{}";
}

/// <summary>
/// คำแปลภาษาไทยของ <see cref="SrdMonster"/> หนึ่งตัว — **global อ่านอย่างเดียวเหมือนต้นทาง**
///
/// **ทำไมแยกตาราง ไม่เติมคอลัมน์ `NameTh`/`DataTh` ใน SrdMonster เอง**: `srd_monsters` ถูก REVOKE
/// สิทธิ์เขียนจาก role แอปไว้ตั้งแต่ migration `ImportSrd521` (มีแค่ role เจ้าของที่รันผ่าน migration
/// เขียนได้) ตารางคำแปลเป็นข้อมูลที่ทีมพัฒนาเป็นคนแปล ไม่ใช่ผู้ใช้พิมพ์เอง จึงเข้าเส้นทางเดียวกับ
/// ต้นฉบับ (ฝังมากับ migration) ไม่ใช่ endpoint ที่ผู้ใช้เขียนได้ · แยกตารางยังทำให้ **แปลไม่ครบได้โดย
/// ไม่พัง** — มอนสเตอร์ที่ยังไม่มีแถวในตารางนี้แปลว่ายังไม่แปล (fallback ไปอังกฤษที่ชั้น endpoint)
/// ต่างจากคอลัมน์ nullable ที่ปนอยู่กับข้อมูลจริงจนแยกยากว่า "ว่างเพราะยังไม่แปล" กับ "ว่างเพราะไม่มีค่า"
///
/// <see cref="Name"/>/<see cref="Size"/>/<see cref="Type"/>/<see cref="TypeTag"/>/<see cref="Alignment"/>
/// แปลครบทุกแถวเสมอ (ศัพท์ปิด 46 คำ ใช้ซ้ำได้กับมอนสเตอร์ทุกตัว) ส่วน <see cref="Data"/> (traits/actions
/// เต็ม) เป็น null ได้ — ดู `tools/srd-translate/build_th_data.py` สำหรับรายชื่อที่แปลเต็มแล้ว
/// </summary>
public class SrdMonsterTranslation
{
    public Guid Id { get; set; }

    /// <summary>ชี้ไป <see cref="SrdMonster.Key"/> — ไม่ทำ FK object navigation เพราะฝั่งอ่านทำ join
    /// เองด้วย LINQ ตรง ๆ (ตารางเล็กทั้งคู่) การมี navigation property จะกวนเฉย ๆ</summary>
    public string MonsterKey { get; set; } = null!;

    public string Locale { get; set; } = "th";

    public string Name { get; set; } = null!;
    public string Size { get; set; } = null!;
    public string Type { get; set; } = null!;
    public string? TypeTag { get; set; }
    public string Alignment { get; set; } = null!;

    /// <summary>null = ยังไม่แปลรายละเอียดเต็ม (traits/actions/bonusActions/reactions/legendaryActions) ·
    /// มีค่า = jsonb รูปทรงเดียวกับ `SrdMonster.Data` ทุกประการแต่แปลแล้ว</summary>
    public string? Data { get; set; }
}

/// <summary>
/// คำแปลภาษาไทยของ <see cref="RulesCorpusEntry"/> หนึ่งแถว (เวท/สถานะ/กฎ) — เหตุผลที่แยกตารางเดียวกับ
/// <see cref="SrdMonsterTranslation"/> ด้านบนทุกประการ
///
/// <see cref="Title"/> แปลครบทุกแถว (669 ชื่อ) ส่วน <see cref="Content"/> (เนื้อหาเต็ม) แปลครบ 100%
/// เฉพาะ category "condition"/"rule" (41 แถว, มีป้ายกำกับ) เพราะเป็นเนื้อหาสั้นและใช้เป็น tooltip บ่อย ·
/// เวท (category "spell") มี Content ให้เฉพาะ 4 ตัวที่อยู่ใน action_catalog_entries ส่วนที่เหลือเป็น null
/// </summary>
public class RulesCorpusTranslation
{
    public Guid Id { get; set; }

    public string Category { get; set; } = null!;
    public string Key { get; set; } = null!;
    public string Locale { get; set; } = "th";

    public string Title { get; set; } = null!;

    /// <summary>null = ยังไม่แปลเนื้อหาเต็ม (มีแต่ชื่อ) · มีค่า = ข้อความเต็มแปลแล้ว รูปแบบย่อหน้าเดียวกับต้นฉบับ</summary>
    public string? Content { get; set; }

    /// <summary>สำนักเวท/คลาสที่แปลแล้ว เก็บเป็น jsonb `{"school":"...","classes":["...",...]}` — null สำหรับ
    /// category ที่ไม่ใช่ "spell"</summary>
    public string? Data { get; set; }
}

/// <summary>
/// คลัง preset — ระบบหลักของแพลตฟอร์ม ไม่ใช่แค่ของ quick play
/// Harvested ต้องผ่านกติกา 4 ข้อ: มี consent, เก็บแค่โครง, SrdOnly เท่านั้นถึงเข้าคลังกลางได้,
/// dedupe ด้วย fingerprint — player-side-design §18, §19
/// </summary>
public class CharacterPreset
{
    public Guid Id { get; set; }

    public PresetScope Scope { get; set; } = PresetScope.Global;

    /// <summary>ต้องมีค่าเมื่อ Scope = Campaign (preset ส่วนตัวของ DM คนนั้น)</summary>
    public Guid? CampaignId { get; set; }
    public Campaign? Campaign { get; set; }

    public PresetSource Source { get; set; } = PresetSource.Authored;
    public string Name { get; set; } = null!;

    /// <summary>เฉพาะ "โครง": race/class/subclass/ability/feat/spell/equipment — ไม่มีชื่อ/backstory/รูป</summary>
    public string Build { get; set; } = "{}";

    /// <summary>เส้นทาง auto-level แบบ deterministic: level ไหนได้ feat/ASI/spell อะไร</summary>
    public string LevelPath { get; set; } = "{}";

    public int Level { get; set; } = 1;

    /// <summary>hash ของ build ใช้ dedupe — ตัวซ้ำนับ popularity แทนการเก็บซ้ำ</summary>
    public string BuildFingerprint { get; set; } = null!;

    public int PopularityCount { get; set; } = 1;

    /// <summary>
    /// ⚠️ false = มีเนื้อหาจาก SourceDocument ปนอยู่ → ห้ามเข้าคลัง Global เด็ดขาด
    /// (เนื้อหามีลิขสิทธิ์ของ DM คนนั้น) — player-side-design §19 ข้อ 3
    /// </summary>
    public bool SrdOnly { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}
