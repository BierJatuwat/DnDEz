using DnDEz.Domain.Enums;

namespace DnDEz.Domain.Entities;

// ═══ เซสชันการเล่น · ข้อความ · proposal · rest · เต๋า ═══ dm-side-design §3, player-side-design §23

/// <summary>ชื่อ GameSession ไม่ใช่ Session เพราะเลี่ยงชนกับ ASP.NET session</summary>
public class GameSession
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;

    public SessionMode Mode { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Draft;

    /// <summary>รหัสสั้น 6 หลักสำหรับ join (สำรองจาก QR) — player-side-design §17</summary>
    public string? JoinCode { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    public ICollection<Message> Messages { get; set; } = new List<Message>();
    public ICollection<Proposal> Proposals { get; set; } = new List<Proposal>();
    public ICollection<Scene> Scenes { get; set; } = new List<Scene>();
}

public class Message
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public GameSession Session { get; set; } = null!;

    public MessageAuthorType AuthorType { get; set; }
    public Guid? AuthorUserId { get; set; }
    public Guid? CharacterId { get; set; }

    public string Content { get; set; } = null!;

    /// <summary>jsonb — tool call ที่ AI ตัดสินใจเรียกในเทิร์นนี้</summary>
    public string? ToolCalls { get; set; }

    /// <summary>
    /// action นี้มาจากไหน — ต้องแยกให้ออกว่าผู้เล่นสั่งเองหรือ DM สั่งแทน
    /// เพื่อย้อนดูภายหลังได้ — player-side-design §6
    /// </summary>
    public ActionSource Source { get; set; } = ActionSource.Player;

    /// <summary>
    /// true = ข้อความจาก `/consult` ของ DM (ทั้งฝั่ง DM และคำตอบ AI) ซึ่ง**ไม่เคย broadcast** และอาจมี
    /// สปอยล์/แผนลับของ DM · false = ข้อความของเทิร์นที่ผู้เล่นเริ่ม (`/actions`) ที่ทุกคนในโต๊ะเห็นแล้ว
    ///
    /// ก่อนมีคอลัมน์นี้ ทั้งสองแบบปนกันแยกไม่ออก แล้ว `OrchestratorTurnRunner` ส่ง log ทั้ง session เป็น
    /// history ให้เทิร์นของผู้เล่นด้วย — AI เห็นสิ่งที่ DM ปรึกษาลับ ๆ แล้วเอามาเล่าใน narration ที่
    /// broadcast ได้ (คลาสเดียวกับ DmNotes/Secrets ใน Part A) · RLS ของ `messages` ซ่อนแถว true จาก
    /// คนที่ไม่ใช่ DM ด้วย (migration `AddMessageDmPrivate`)
    /// </summary>
    public bool DmPrivate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>ช่องทางลับ DM ↔ ผู้เล่นคนเดียว ทั้งสองทิศ — player-side-design §10</summary>
public class PrivateMessage
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public GameSession Session { get; set; } = null!;
    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }
    public string Content { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// แผนที่/ฉาก — DM เห็นเต็มไม่มี fog บัง
/// ⚠️ FogLayer เป็น state ฝั่ง server เท่านั้น ห้ามส่งทั้งก้อนไปให้ client ของผู้เล่น
/// ต้องคำนวณแล้วส่งเฉพาะส่วนที่มองเห็นจริง — PROJECT-CONTEXT §3 (ข้อบังคับสถาปัตยกรรม)
/// </summary>
public class Scene
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public GameSession Session { get; set; } = null!;

    public string Name { get; set; } = null!;
    public string Grid { get; set; } = "{}";
    public string Tokens { get; set; } = "[]";
    public string FogLayer { get; set; } = "{}";

    /// <summary>Sprint H Part 5 — พื้นจากแผนที่ (พื้นเดินยาก/กำแพง) ของ spatial-play-design §3.3 รูปร่าง
    /// <c>{"difficult":[[x,y],...],"blocked":[[x,y],...]}</c> · แยกจาก <see cref="Grid"/> เพราะ PATCH grid
    /// สร้าง grid ใหม่ทั้งก้อน — เหตุผลเต็มดู <c>SceneTerrainSerialization</c> (DnDEz.Api)
    /// ⚠️ เป็นข้อมูลที่กำหนดว่าใครเดินไปไหนได้ — เขียนผ่าน endpoint DM-only เท่านั้น</summary>
    public string Terrain { get; set; } = "{}";

    /// <summary>Sprint G Part 4 — เดิมตั้งใจเก็บ URL ตรง ๆ แต่ signed URL อายุสั้น (นาที) เก็บลง DB
    /// แล้วจะหมดอายุก่อนมีคนเปิดดูจริง (session เล่นเป็นชั่วโมง) จึง**ไม่ใช้คอลัมน์นี้ต่อ** เปลี่ยนไปเก็บ
    /// <see cref="MapAssetId"/> แทน แล้ว generate signed URL สดทุกครั้งที่ GET (ดู ScenePrepEndpoints
    /// TODO: ย้ายไป SceneEndpoints) — ปล่อยคอลัมน์นี้ไว้เฉย ๆ ไม่ลบ (ยังไม่คุ้มเสียเวลาทำ migration ลบ
    /// คอลัมน์สำหรับ Part นี้)</summary>
    public string? BackgroundImageUrl { get; set; }

    /// <summary>อ้างถึง map_assets ที่เป็นพื้นหลังของฉากนี้ — null = ยังไม่ตั้งแผนที่ (เช่น scene ที่สร้าง
    /// รอไว้ก่อนอัปโหลด) ตั้งใจ nullable + ON DELETE SET NULL: ลบ map asset ทิ้งไม่ควรพา scene ทั้งฉาก
    /// หายไปด้วย (ผู้เล่นอาจกำลังเล่นอยู่ตอนนั้น)</summary>
    public Guid? MapAssetId { get; set; }
    public MapAsset? MapAsset { get; set; }

    public bool IsActive { get; set; }
}

/// <summary>
/// tool call ที่อยู่ใน subsystem ระดับ "suggest" — ไม่ execute ทันที
/// ส่งเข้า DM console เท่านั้น ผู้เล่นไม่เห็นแม้แต่สถานะ — player-side-design §15
/// </summary>
public class Proposal
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public GameSession Session { get; set; } = null!;

    public AutonomySubsystem Subsystem { get; set; }
    public string ToolCallJson { get; set; } = null!;

    /// <summary>เหตุผลกำกับทุกอัน — DM ต้องเห็นว่าทำไม AI เสนอสิ่งนี้</summary>
    public string? Reason { get; set; }

    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;
    public Guid? ResolvedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// ช่วงพัก — DM กดเปิด ผู้เล่นเลือก hit dice/สกิลบนเครื่องตัวเอง แล้ว resolve รวดเดียว
/// ถ้าโดน encounter แทรก: long rest คิดเป็น % จากเวลาที่พักไป · short rest ตัดสินล่วงหน้าตั้งแต่เริ่ม
/// player-side-design §8
/// </summary>
public class RestPhase
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public GameSession Session { get; set; } = null!;

    public RestType Type { get; set; }

    /// <summary>จำนวนชั่วโมงที่ตั้งใจพัก (long rest)</summary>
    public int? Hours { get; set; }

    /// <summary>ลำดับเฝ้าเวร — ใช้เป็น narrative context ให้ AI ตัดสิน random encounter</summary>
    public Guid[] WatchOrder { get; set; } = [];

    public RestStatus Status { get; set; } = RestStatus.Open;

    /// <summary>โดนแทรกตอนนาทีที่เท่าไหร่ (ใช้คำนวณ % ที่ได้คืน)</summary>
    public int? InterruptedAtMinutes { get; set; }

    /// <summary>สัดส่วนที่ resolve ได้จริง 0–100 (long rest ที่โดนแทรกกลางคัน)</summary>
    public int? ResolvedPercent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<PlayerRestAction> Actions { get; set; } = new List<PlayerRestAction>();
}

public class PlayerRestAction
{
    public Guid Id { get; set; }
    public Guid RestPhaseId { get; set; }
    public RestPhase RestPhase { get; set; } = null!;
    public Guid CharacterId { get; set; }
    public int HitDiceSpent { get; set; }
    public string AbilitiesUsed { get; set; } = "[]";
    public bool Confirmed { get; set; }
}

/// <summary>สองเวอร์ชันเสมอ: DM version รวม secret / Player version เฉพาะที่ตัวละครรู้จริง</summary>
public class RecapSnapshot
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public GameSession Session { get; set; } = null!;
    public string DmVersionText { get; set; } = null!;
    public string PlayerVersionText { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>แชทปรึกษาส่วนตัวของ DM — แยกขาดจาก proposal queue ไม่กระทบ game state</summary>
public class ConsultMessage
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public Guid DmUserId { get; set; }
    public ConsultRole Role { get; set; }
    public string Content { get; set; } = null!;
    public Guid? LinkedBeatId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// บันทึกแคมเปญ (campaign journal) — DM พิมพ์เอง หรือ AI สรุปให้จากบทสนทนา (Part D)
///
/// ใช้ policy `dm_only FOR ALL` ตัวเดียว ไม่แยก `member_read` + `dm_write` เหมือน `autonomy_settings`
/// เพราะเส้นทางที่ AI เขียนบันทึกนี้ลงได้คือเส้นทางเดียวกับ `consult_messages` ใน Part C — AI เห็น
/// `StoryBeat.DmNotes`/`Secrets` ของ DM ตรง ๆ ตอนสรุป เนื้อหาที่ลงในบันทึกจึงอาจมีความลับของแคมเปญ
/// ปนอยู่ได้เสมอ (ไม่ต่างจากเหตุผลที่ `consult_messages` ต้อง fail closed ใน
/// `AddConsultMessageDmOnlyRls`) ผู้เล่นจึงอ่านไม่ได้แม้แต่แถวเดียวจนกว่าจะมีคอลัมน์
/// `visibility` แยกระดับการมองเห็นในอนาคต — ตอนนั้นค่อยแยกเป็น 2 policy แบบ
/// `AddAutonomySettingDmOnlyRls` ห้ามแค่ผ่อน policy ตัวเดียวนี้เฉย ๆ
///
/// ห้ามมี navigation property ไปหา `GameSession` — เลี่ยง shadow FK ซ้อน ผูก FK ที่ชั้น
/// `DnDEzDbContext.OnModelCreating` แทน (ดู config ของ entity นี้)
/// </summary>
public class CampaignJournalEntry
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;

    /// <summary>nullable — บันทึกอาจไม่ผูกเซสชันไหนเลย เช่น DM จดตอนเตรียมเกม</summary>
    public Guid? SessionId { get; set; }

    public string Title { get; set; } = null!;
    public string Content { get; set; } = null!;
    public JournalAuthor AuthoredBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// บันทึกการทอยเต๋าทุกครั้ง — RNG อยู่ฝั่ง server + เก็บ seed เพื่อ replay ตรวจย้อนหลังได้
/// เวลามีข้อพิพาท — PROJECT-CONTEXT §3 (ข้อบังคับสถาปัตยกรรม)
/// </summary>
public class DiceRoll
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public GameSession Session { get; set; } = null!;
    public Guid? CharacterId { get; set; }

    /// <summary>เช่น "1d20+5", "4d6kh3"</summary>
    public string Expression { get; set; } = null!;

    /// <summary>jsonb — ผลดิบแต่ละลูก ก่อนบวก modifier</summary>
    public string Rolls { get; set; } = "[]";

    public int Total { get; set; }

    /// <summary>ทอยโดยระบบ หรือ DM กรอกผลเต๋าจริงบนโต๊ะ</summary>
    public DiceMode Source { get; set; }

    /// <summary>seed ของ RNG (เฉพาะ SystemRoll) — ใช้ replay ยืนยันผล</summary>
    public long? Seed { get; set; }

    /// <summary>ใครกรอก (เฉพาะ DmEntered)</summary>
    public Guid? EnteredByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
