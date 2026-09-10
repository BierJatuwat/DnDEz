using System.Text.Json;
using StackExchange.Redis;

namespace DnDEz.Api.Realtime;

/// <summary>หนึ่งจังหวะที่การเคลื่อนที่ต้องหยุดรอ — ใครได้สิทธิ์สวน และสวนจากช่องไหน</summary>
/// <param name="ThreatUserId">ผู้ใช้ที่มีสิทธิ์ตอบแทนตัวนี้ — เจ้าของตัวละคร หรือ DM ถ้าเป็น NPC
/// ที่ DM คุม (คำนวณตอนสร้าง ไม่ใช่ตอนตอบ เพื่อให้ SignalR ยิงหาคนเดียวได้ทันที)</param>
public sealed record PendingReactionTrigger(
    Guid ThreatCharacterId,
    string ThreatName,
    Guid ThreatUserId,
    int LeavesAtStepIndex,
    int AttackFromX,
    int AttackFromY);

public readonly record struct PendingPathPoint(double X, double Y);

/// <summary>
/// การเคลื่อนที่ที่ "ค้างกลางทาง" อยู่ระหว่างรอคำตอบ reaction
///
/// เก็บ **เส้นทางทั้งเส้น** ไว้ ไม่ใช่แค่ช่องถัดไป เพราะการเดินหนึ่งครั้งอาจหลุดระยะของศัตรูหลายตัว
/// ต้องหยุดหลายจังหวะ — <c>CurrentTriggerIndex</c> บอกว่าตอนนี้รออยู่ที่จังหวะไหน
/// </summary>
/// <param name="ActingUserId">คนที่สั่งเดิน (ผ่านเช็คสิทธิ์มาแล้ว) — เก็บไว้ให้ sweeper เปิด RLS
/// context ได้ตอนไม่มี HTTP request ให้ middleware ตั้งค่าให้ (บทเรียนเดียวกับ Part 5)</param>
public sealed record PendingReaction(
    Guid Id,
    Guid CampaignId,
    Guid SessionId,
    Guid SceneId,
    Guid TokenId,
    Guid MoverCharacterId,
    Guid ActingUserId,
    IReadOnlyList<PendingPathPoint> Path,
    int CostFeet,
    IReadOnlyList<PendingReactionTrigger> Triggers,
    int CurrentTriggerIndex,
    int TimeoutSeconds,
    DateTimeOffset DeadlineUtc)
{
    public PendingReactionTrigger CurrentTrigger => Triggers[CurrentTriggerIndex];
}

/// <summary>
/// ที่เก็บการเคลื่อนที่ที่ค้างรอ reaction อยู่
///
/// **นโยบายความล้มเหลวต่างจาก <see cref="ISceneTokenStore"/> โดยสิ้นเชิง และนี่คือประเด็นสำคัญที่สุด
/// ของไฟล์นี้**: ตำแหน่ง token สร้างใหม่ไม่ได้ จึงห้ามหาย — แต่ "การรอ reaction" ที่หายไป **มีความหมาย
/// ที่ถูกต้องอยู่แล้ว** คือ *"ไม่มีใครใช้ reaction แล้วเกมเดินต่อ"* ซึ่งตรงกับที่ player-side-design §4
/// ล็อกไว้เป๊ะว่า *"หมดเวลา = ถือว่าไม่ใช้ reaction แล้วเกมเดินต่อ (กัน pacing สะดุด)"*
///
/// ผลคือ **สามทางจบเหมือนกันหมด**: ผู้ถือสิทธิ์กดไม่สวน · หมดเวลา · Redis ล่ม/คีย์หมดอายุ
/// จึงไม่ต้องออกแบบ failure path ใหม่เลย — TTL ที่หมดเองให้พฤติกรรมเดียวกับ Redis ล่มเป๊ะ
/// (หลักการเดียวกับที่ <c>EncounterTurnStateStore</c> เขียนไว้ว่า TTL ต้องให้ผลเท่ากับตอนล่ม)
///
/// ⚠️ ข้อแลกเปลี่ยนที่รู้ตัว: ถ้า Redis ล่มตอนกำลังรออยู่ **การเคลื่อนที่จะค้างที่ช่องกลางทาง**
/// จนกว่าจะมีอะไรมาปลุก เพราะทั้ง sweeper และ endpoint respond ต่างก็อ่านสถานะจาก Redis ไม่ได้
/// ทั้งคู่ — ตำแหน่งที่ค้างเป็นตำแหน่งที่ถูกต้องตามกฎ (ช่องก่อนหลุดระยะ) ไม่ใช่ตำแหน่งพัง
/// และผู้เล่นสั่งเดินใหม่ได้ทันที
/// </summary>
public interface IPendingReactionStore
{
    /// <summary>คืน false เมื่อ Redis ใช้ไม่ได้ — caller ต้องรู้เพื่อจะได้ไม่หยุดการเดินค้างไว้
    /// โดยไม่มีใครปลุก (ถ้าเก็บไม่ได้ ให้เดินให้จบไปเลยดีกว่าค้างถาวร)</summary>
    Task<bool> SaveAsync(PendingReaction reaction, CancellationToken cancellationToken = default);

    Task<PendingReaction?> GetAsync(Guid reactionId, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid reactionId, CancellationToken cancellationToken = default);

    /// <summary>รายการที่ยังค้างอยู่ ให้ sweeper สแกนหาตัวที่หมดเวลา</summary>
    Task<IReadOnlyCollection<Guid>> GetOpenIdsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// เก็บใน Redis สองโครงสร้าง:
///  - <c>dndez:reaction:{id}</c> (STRING json) — ตัวสถานะ พร้อม TTL = เวลาที่เหลือ + grace
///  - <c>dndez:reactions:open</c> (SET ของ id) — ให้ sweeper สแกน
///
/// grace เผื่อไว้เพื่อให้ sweeper ได้เห็นตัวที่ "เพิ่งหมดเวลา" ก่อนที่คีย์จะหายไปเอง — ถ้า TTL
/// เท่ากับ deadline พอดี คีย์อาจหายก่อน sweeper ตื่น แล้วการเดินจะค้างโดยไม่มีใครปิดให้
/// </summary>
public sealed class RedisPendingReactionStore(
    IConnectionMultiplexer multiplexer, ILogger<RedisPendingReactionStore> logger) : IPendingReactionStore
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private const string OpenSetKey = "dndez:reactions:open";
    private static string ReactionKey(Guid id) => $"dndez:reaction:{id}";

    public async Task<bool> SaveAsync(PendingReaction reaction, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = multiplexer.GetDatabase();
            var ttl = reaction.DeadlineUtc - DateTimeOffset.UtcNow + Grace;
            if (ttl <= TimeSpan.Zero)
            {
                ttl = Grace;
            }

            await db.StringSetAsync(
                ReactionKey(reaction.Id), JsonSerializer.Serialize(reaction, Options), ttl);
            await db.SetAddAsync(OpenSetKey, reaction.Id.ToString());
            return true;
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "เก็บ pending reaction {ReactionId} ลง Redis ไม่สำเร็จ", reaction.Id);
            return false;
        }
    }

    public async Task<PendingReaction?> GetAsync(Guid reactionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await multiplexer.GetDatabase().StringGetAsync(ReactionKey(reactionId));
            return value.IsNullOrEmpty
                ? null
                : JsonSerializer.Deserialize<PendingReaction>(value!, Options);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "อ่าน pending reaction {ReactionId} ไม่สำเร็จ", reactionId);
            return null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "pending reaction {ReactionId} อ่านเป็น JSON ไม่ได้", reactionId);
            return null;
        }
    }

    public async Task RemoveAsync(Guid reactionId, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = multiplexer.GetDatabase();
            await db.KeyDeleteAsync(ReactionKey(reactionId));
            await db.SetRemoveAsync(OpenSetKey, reactionId.ToString());
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "ลบ pending reaction {ReactionId} ไม่สำเร็จ", reactionId);
        }
    }

    public async Task<IReadOnlyCollection<Guid>> GetOpenIdsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var members = await multiplexer.GetDatabase().SetMembersAsync(OpenSetKey);
            return members
                .Select(m => Guid.TryParse(m, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToList();
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "อ่านรายการ pending reaction ที่ค้างอยู่ไม่สำเร็จ");
            return [];
        }
    }
}
