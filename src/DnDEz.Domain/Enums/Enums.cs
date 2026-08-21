namespace DnDEz.Domain.Enums;

// ทุก enum ในไฟล์นี้เก็บลง DB เป็น text (ไม่ใช่ int) เพื่อให้อ่าน schema ตรง ๆ ได้
// และมี CHECK constraint กำกับใน migration

/// <summary>Guided = มือใหม่ (ซ่อน dial, AI ทำเกือบหมด) · Custom = มือโปร — PROJECT-CONTEXT §2</summary>
public enum CampaignMode { Guided, Custom }

/// <summary>4 โหมดการเล่นที่ทุกฟีเจอร์ต้องรองรับ — PROJECT-CONTEXT §1</summary>
public enum SessionMode { Online, InPerson, Companion }

public enum SessionStatus { Draft, Active, Paused, Ended }

/// <summary>บทบาทในแคมเปญ — DM ต้องมีบัญชีเต็มเสมอ</summary>
public enum CampaignRole { Dm, Player }

/// <summary>ระบบย่อยที่ตั้ง autonomy dial แยกกันได้ — dm-side-design §2.2</summary>
public enum AutonomySubsystem { Narration, NpcDialogue, RulesCombat, MapScene }

/// <summary>Suggest = tool call กลายเป็น pending proposal ไม่ execute ทันที</summary>
public enum AutonomyLevel { Manual, Suggest, Autonomous }

public enum ProposalStatus { Pending, Approved, Edited, Rejected }

/// <summary>record ที่ยังไม่ approved ห้ามส่งเข้า orchestrator context — PROJECT-CONTEXT §2</summary>
public enum ReviewStatus { Pending, Approved, Rejected }

public enum BeatProgress { Upcoming, Active, Passed }

public enum MessageAuthorType { Dm, Player, Ai, System }

/// <summary>
/// ที่มาของ action — ต้องแยกให้ออกว่า action มาจากผู้เล่นเองหรือ DM สั่งแทน
/// (DM override ได้ตลอดเวลา แต่ AI ห้ามเด็ดขาด) — player-side-design §6
/// </summary>
public enum ActionSource { Player, DmOverride, Ai }

public enum RestType { Short, Long }

public enum RestStatus { Open, Interrupted, Resolved }

/// <summary>SystemRoll = rules engine ทอย · DmEntered = ผู้เล่นทอยเต๋าจริง DM กรอกผล — player-side-design §5</summary>
public enum DiceMode { SystemRoll, DmEntered }

/// <summary>เพื่อนเห็นจำนวน death save หรือไม่ (ดีฟอลต์เห็น) — player-side-design §9</summary>
public enum DeathSaveVisibility { PartyVisible, DmOnly }

public enum DeathSaveStatus { Rolling, Stable, Dead }

/// <summary>resource รีเซ็ตเมื่อไหร่ — ใช้ตอน resolve rest</summary>
public enum ResourceResetOn { ShortRest, LongRest, Dawn, Never }

public enum OcrStatus { Pending, Processing, Completed, Failed }

/// <summary>Global = คลังกลางของแพลตฟอร์ม · Campaign = preset ส่วนตัวของ DM — player-side-design §18</summary>
public enum PresetScope { Global, Campaign }

/// <summary>Harvested = เก็บเกี่ยวจากตัวละครที่เล่นจริง (ต้องมี consent + SRD ล้วน) — player-side-design §19</summary>
public enum PresetSource { Authored, Harvested }

public enum ApiKeyStatus { Active, Invalid, Revoked }

public enum ConsultRole { Dm, Ai }
