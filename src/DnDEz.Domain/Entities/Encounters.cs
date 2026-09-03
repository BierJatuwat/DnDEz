namespace DnDEz.Domain.Entities;

// ═══ Encounter · initiative · turn order ═══ Sprint F งานที่ 4

/// <summary>
/// การต่อสู้หนึ่งครั้ง — **แยกเป็น entity ของตัวเอง ไม่ใช่ field ใน `GameSession`**
/// (ตัดสินไว้แล้วใน PROJECT-CONTEXT §4 "3 คำถามที่เคยบล็อกไว้" ข้อ 2, 2026-08-30)
/// เพราะ **1 session มีได้หลาย encounter** — เปิด initiative order ใหม่ทั้งชุดได้ทุกครั้งที่เจอศัตรู
/// ใหม่ โดยไม่ต้องปิด session · ผูกทั้ง `SessionId` (encounter เกิดในเซสชันไหน) และ `CampaignId`
/// (ซ้ำกับ `Session.CampaignId` โดยตั้งใจ — RLS policy ของตารางนี้ผูกกับ `campaign_id` ตรง ๆ
/// ตามแบบแผนเดิมของระบบ ไม่ต้อง join ข้ามตารางทุก query)
///
/// `EndedAt IS NULL` = encounter ที่ยัง active อยู่ (index `(session_id, ended_at)` รองรับ query
/// "encounter ที่ยังเปิดอยู่ของ session นี้" ซึ่งเป็น query หลักของ turn loop)
/// </summary>
public class Encounter
{
    public Guid Id { get; set; }

    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;

    public Guid SessionId { get; set; }
    public GameSession Session { get; set; } = null!;

    /// <summary>
    /// เทิร์นของใครอยู่ตอนนี้ — **เก็บใน Postgres ไม่ใช่ Redis** ทั้งที่ §3 ล็อกไว้ว่า turn state
    /// อยู่ Redis เพราะสองอย่างนี้คนละชั้นกัน: Redis เก็บแค่ `ActionEconomyState` ของเทิร์นปัจจุบัน
    /// (ใช้ action/bonus/reaction/movement ไปหรือยัง) ส่วน "ตาของใคร" เป็นลำดับของเกมที่ต้องรอดจาก
    /// Redis ล่ม — กลยุทธ์กู้คืนที่ตอบไว้ในคำถามข้อ 3 คือ "Redis หาย = รีเซ็ตเทิร์นปัจจุบัน
    /// (คืนทรัพยากรเต็ม) ไม่ throw" ซึ่ง **make sense ได้ก็ต่อเมื่อ Postgres ยังรู้ว่ากำลังเป็นตา
    /// ของใครอยู่** ถ้าเก็บไว้ที่ Redis ที่เดียว Redis ล่ม = ลำดับเทิร์นทั้งวงหายไปเลย
    ///
    /// null = ยังไม่เริ่มเดินเทิร์น (เพิ่งสร้าง encounter / ยังทอย initiative ไม่ครบ)
    /// </summary>
    public Guid? CurrentParticipantId { get; set; }
    public EncounterParticipant? CurrentParticipant { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>null = encounter นี้ยัง active — มีได้หลายอันต่อ session แต่ตามลำดับเวลา</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    /// ตัวนับเทิร์นสะสมทั้ง encounter (ไม่ใช่เลขรอบ/round) — เพิ่มทีละ 1 ทุกครั้งที่ next-turn
    /// มีไว้ทำให้ Redis key ไม่ซ้ำข้ามเทิร์น จำเป็นเพราะวงจะวนกลับมาที่ participant คนเดิมในรอบถัดไป
    /// ถ้า key ผูกแค่ `participantId` ค่าของรอบก่อนจะถูกอ่านซ้ำ
    /// </summary>
    public int TurnNumber { get; set; }

    public ICollection<EncounterParticipant> Participants { get; set; } = new List<EncounterParticipant>();
}

/// <summary>
/// ตัวละครหนึ่งตัวในลำดับ initiative ของ encounter หนึ่งครั้ง
/// </summary>
public class EncounterParticipant
{
    public Guid Id { get; set; }

    public Guid EncounterId { get; set; }
    public Encounter Encounter { get; set; } = null!;

    public Guid CharacterId { get; set; }
    public Character Character { get; set; } = null!;

    /// <summary>ผลเต๋า d20 ดิบ (1-20) ที่ทอยตอนเปิด initiative</summary>
    public int InitiativeRoll { get; set; }

    /// <summary>
    /// **snapshot** ของ `Character.InitiativeModifier` ณ ตอนเข้า encounter — จงใจไม่อ่านสดจาก
    /// `Character` ทุกครั้งที่เรียงลำดับ เพราะค่าที่เปลี่ยนกลางเกม (สวม/ถอดไอเทม, buff/debuff,
    /// DM แก้ชีต) ต้อง **ไม่** ทำให้ลำดับเทิร์นที่เดินไปแล้วสลับใหม่ย้อนหลัง — ลำดับที่ตกลงกันตอน
    /// เปิดวงต้องคงที่ตลอด encounter นั้น ไม่งั้นผู้เล่นที่เพิ่งเล่นไปแล้วอาจถูกดันกลับมาเล่นซ้ำ
    /// (หรือถูกข้าม) กลางวง
    /// </summary>
    public int InitiativeModifier { get; set; }

    /// <summary>`InitiativeRoll + InitiativeModifier` — เก็บซ้ำเพื่อ sort/index ตรง ๆ
    /// และมี CHECK constraint บังคับให้ตรงกับสองคอลัมน์ต้นทางเสมอ</summary>
    public int InitiativeTotal { get; set; }

    /// <summary>ลำดับเทิร์นในวง **นับจาก 0** (ไม่ใช่ 1) · unique ต่อ encounter</summary>
    public int TurnOrder { get; set; }
}
