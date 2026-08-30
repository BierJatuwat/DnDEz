using DnDEz.Domain.Enums;

namespace DnDEz.Domain.Entities;

// ═══ แคมเปญ · story outline · ตัวละคร ═══ dm-side-design §3, player-side-design §23

public class Campaign
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public User OwnerUser { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>world state ที่ schema เปลี่ยนบ่อย — jsonb</summary>
    public string WorldState { get; set; } = "{}";

    public CampaignMode Mode { get; set; } = CampaignMode.Guided;
    public string RulesSystem { get; set; } = "srd2024";

    /// <summary>Sprint F — โค้ด join แคมเปญ ผู้เล่นใช้ code นี้ join ทั้งแคมเปญและทุก session ในนั้น
    /// (ไม่ใช่ per-session แบบ GameSession.JoinCode เดิมที่ยังไม่เคยต่อ endpoint จริง) —
    /// DM เป็นคนกำหนดเอง ไม่ใช่ระบบสุ่มให้ (ตัดสิน 2026-08-30) null = ยังไม่เปิดให้ join</summary>
    public string? JoinCode { get; set; }

    // ── Settings ต่อแคมเปญ (player-side-design §23) ────────────────────────
    /// <summary>ผู้เล่นกรอกผลเต๋าเองไม่ได้ — มีแค่ระบบทอย หรือ DM กรอก</summary>
    public DiceMode DiceMode { get; set; } = DiceMode.SystemRoll;

    /// <summary>DM ตั้งเองได้ ดีฟอลต์ 30 วินาที · หมดเวลา = ไม่ใช้ reaction</summary>
    public int ReactionTimeoutSeconds { get; set; } = 30;

    /// <summary>คิว review ตัวละครก่อนเข้าโต๊ะ — ปิดได้ถ้าเล่นกันเอง</summary>
    public bool CharacterReviewRequired { get; set; } = true;

    public DeathSaveVisibility DeathSaveVisibility { get; set; } = DeathSaveVisibility.PartyVisible;

    /// <summary>DM กรอกเป็นตัวเลขได้อิสระ — preset ต้อง auto-level ตามนี้</summary>
    public int StartingLevel { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<CampaignMember> Members { get; set; } = new List<CampaignMember>();
    public ICollection<AutonomySetting> AutonomySettings { get; set; } = new List<AutonomySetting>();
    public ICollection<StoryBeat> StoryBeats { get; set; } = new List<StoryBeat>();
    public ICollection<Character> Characters { get; set; } = new List<Character>();
    public ICollection<GameSession> Sessions { get; set; } = new List<GameSession>();
}

/// <summary>dial ต่อ subsystem ปรับได้ตลอดเกม — dm-side-design §2.2</summary>
public class AutonomySetting
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public AutonomySubsystem Subsystem { get; set; }
    public AutonomyLevel Level { get; set; } = AutonomyLevel.Suggest;
}

/// <summary>
/// beat หนึ่งช่วงของเนื้อเรื่อง แก้ไขได้แม้ระหว่างเล่น
/// Secrets เก็บเป็น jsonb array ของ {text, reveal_condition} — ใช้กฎเดียวกับ recap 2 เวอร์ชัน
/// </summary>
public class StoryBeat
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;

    public int Order { get; set; }
    public string Title { get; set; } = null!;
    public string? Summary { get; set; }
    public string? Objective { get; set; }
    public string? EntryTrigger { get; set; }
    public string[] ExitConditions { get; set; } = [];
    public Guid[] MonsterRefs { get; set; } = [];
    public Guid[] NpcRefs { get; set; } = [];

    /// <summary>jsonb: [{ "text": "...", "reveal_condition": "..." }] — ผู้เล่นไม่ควรรู้</summary>
    public string Secrets { get; set; } = "[]";

    public string? DmNotes { get; set; }
    public BeatProgress Progress { get; set; } = BeatProgress.Upcoming;

    /// <summary>อ่านเข้า scene prep panel ได้เฉพาะที่ approved</summary>
    public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Pending;
}

public class Character
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;

    /// <summary>null = quick play (ไม่มีบัญชี) หรือ orphan ที่รอ claim</summary>
    public Guid? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    public string Name { get; set; } = null!;

    /// <summary>
    /// stats ที่เหลือ/inventory/spell slots/feature ฯลฯ — jsonb เพราะ schema เปลี่ยนบ่อยจริง ๆ
    /// (ต่างคลาส/subclass มี field ไม่เหมือนกัน, homebrew เพิ่ม field เองได้)
    /// </summary>
    public string Sheet { get; set; } = "{}";

    // ── ตัวเลขที่ rules engine (DnDEz.Rules) ต้องใช้โดยตรง — promote ออกจาก Sheet jsonb (Sprint B
    // Part 2, 2026-08-26) เป็น typed column เพราะ "ทุกตัวละครมีแน่นอน ไม่ผันแปรตามคลาส" ต่างจากของ
    // ที่เหลือใน Sheet ที่ผันแปรจริง — ทำให้ DB บังคับ invariant ได้ (HP ไม่ติดลบ/ไม่เกิน max) และ
    // API อ่าน-เขียนตรง ๆ ไม่ต้อง parse jsonb ทุกครั้งที่คำนวณ combat — รายละเอียดเหตุผลเต็มดู
    // migration `AddCharacterCombatStats` และ PROJECT-CONTEXT §4 "Sprint B Part 2"
    public int CurrentHp { get; set; } = 10;
    public int MaxHp { get; set; } = 10;
    public int CurrentTempHp { get; set; }
    public int ArmorClass { get; set; } = 10;

    /// <summary>ชื่อค่าต้องตรงกับ DnDEz.Rules.DamageType enum เป๊ะ (Acid, Bludgeoning, ... 13 ค่า) —
    /// validate ที่ชั้น API ตอนสร้าง/แก้ตัวละคร ไม่ validate ในนี้ (Domain ไม่ reference Rules)</summary>
    public string[] DamageResistances { get; set; } = [];
    public string[] DamageVulnerabilities { get; set; } = [];
    public string[] DamageImmunities { get; set; } = [];

    public int Level { get; set; } = 1;

    /// <summary>DM เข้าควบคุมตัวละครนี้อยู่หรือไม่ (ได้ตลอดเวลา ไม่ใช่แค่ตอนหลุด) — player-side-design §6</summary>
    public bool ControlledByDm { get; set; }

    // ── Quick play lifecycle (dm-side-design §8 + player-side-design §19, §22) ──
    /// <summary>ผู้เล่นอนุญาตให้เก็บ "โครง" ไปเป็น preset หรือไม่ · ไม่ติ๊ก = hard delete ตามเดิม</summary>
    public bool HarvestConsent { get; set; }
    public bool IsOrphan { get; set; }
    public string? ClaimToken { get; set; }
    public DateTimeOffset? ClaimExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<CharacterResource> Resources { get; set; } = new List<CharacterResource>();
    public DeathSaveState? DeathSaveState { get; set; }
    public CharacterReview? Review { get; set; }
}

public class CharacterResource
{
    public Guid Id { get; set; }
    public Guid CharacterId { get; set; }
    public Character Character { get; set; } = null!;
    public string Name { get; set; } = null!;
    public int Current { get; set; }
    public int Max { get; set; }
    public ResourceResetOn ResetOn { get; set; }
}

/// <summary>สถานะ death save — player-side-design §9</summary>
public class DeathSaveState
{
    public Guid CharacterId { get; set; }
    public Character Character { get; set; } = null!;
    public int Successes { get; set; }
    public int Failures { get; set; }
    public DeathSaveStatus Status { get; set; } = DeathSaveStatus.Rolling;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>คิว review ตัวละครก่อนเข้าโต๊ะ · validation ที่ไม่ผ่านติด flag ให้ DM เห็น — player-side-design §21</summary>
public class CharacterReview
{
    public Guid CharacterId { get; set; }
    public Character Character { get; set; } = null!;
    public ReviewStatus Status { get; set; } = ReviewStatus.Pending;

    /// <summary>เหตุผลที่ rules engine flag ไว้ (point buy ผิด, spell เกิน ฯลฯ)</summary>
    public string[] FlaggedReasons { get; set; } = [];

    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
}
