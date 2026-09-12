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
