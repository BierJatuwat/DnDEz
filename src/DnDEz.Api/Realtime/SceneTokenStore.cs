using System.Text.Json;
using StackExchange.Redis;

namespace DnDEz.Api.Realtime;

public readonly record struct TokenPosition(double X, double Y);

/// <summary>metadata ที่ต้องรู้ตอน flush เพื่อเปิด RLS context ให้ถูก (ดู doc comment ของ
/// <see cref="RedisSceneTokenStore"/>) — <c>ActingUserId</c> คือผู้เรียก PATCH ล่าสุดที่ผ่านเช็คสิทธิ์
/// มาแล้ว (เป็นสมาชิกแคมเปญแน่นอน ไม่จำเป็นต้องเป็นเจ้าของ token ทุกตัวที่ค้าง flush อยู่ — ใช้แค่
/// เปิดทางให้ RLS มองเห็นแถว ไม่ใช่ใช้ตัดสินสิทธิ์ซ้ำ เพราะสิทธิ์เช็คแล้วตอน endpoint ตอนที่ค่านี้เกิดขึ้น)</summary>
public readonly record struct SceneFlushMetadata(Guid CampaignId, Guid ActingUserId);

/// <summary>
/// เก็บตำแหน่ง token ที่ยังไม่ flush ลง Postgres — write-coalescing layer ตาม design doc §14.3
///
/// ต่างจาก <c>IEncounterTurnStateStore</c> (Sprint F งานที่ 5) ตรงนโยบาย "อ่านไม่ได้ตกไปทางไหน":
/// turn state อ่านไม่ได้ = fallback เป็นเทิร์นใหม่ได้เพราะสร้างใหม่ได้ แต่ตำแหน่ง token สร้างใหม่
/// ไม่ได้ (§14.3) ดังนั้น <see cref="SetPendingPositionAsync"/> **คืน false เมื่อ Redis ใช้ไม่ได้**
/// (ไม่ fallback เงียบ ๆ ในชั้นนี้) — caller (endpoint) มีหน้าที่ fallback ไปเขียน Postgres ตรง ๆ เอง
/// เมื่อได้ false กลับมา ตรงกับที่ design doc บอกว่า "ให้ถอยไปอ่าน-เขียน Postgres ตรง ๆ พร้อม log
/// warning ห้ามยอมให้ข้อมูลหาย" — ชั้นนี้เป็นแค่ cache ไม่ตัดสินใจแทน caller ว่าจะ fallback ยังไง
/// </summary>
public interface ISceneTokenStore
{
    Task<bool> SetPendingPositionAsync(
        Guid campaignId, Guid sceneId, Guid tokenId, double x, double y, Guid actingUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, TokenPosition>> GetPendingPositionsAsync(
        Guid sceneId, CancellationToken cancellationToken = default);

    Task<SceneFlushMetadata?> GetFlushMetadataAsync(Guid sceneId, CancellationToken cancellationToken = default);

    /// <summary>ลบ pending ทั้งหมดของ scene นี้ทิ้ง — เรียกหลัง flush สำเร็จ หรือหลัง scene/token
    /// ถูกลบ (กัน pending เก่าฟื้นคืนชีพตอน flush รอบหน้า)</summary>
    Task ClearAllPendingAsync(Guid sceneId, CancellationToken cancellationToken = default);

    /// <summary>ลบ token เดียวออกจาก pending — ใช้ตอน DELETE token (token นั้นไม่ควรมี pending
    /// ค้างอยู่อีกต่อไปแม้ scene อื่นยังมี pending ของ token อื่นอยู่)</summary>
    Task ClearTokenAsync(Guid sceneId, Guid tokenId, CancellationToken cancellationToken = default);

    /// <summary>รายชื่อ scene ที่มี pending ค้างอยู่ — ให้ periodic flush service สแกน</summary>
    Task<IReadOnlyCollection<Guid>> GetDirtySceneIdsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// เก็บใน Redis 3 โครงสร้างต่อ scene:
///  - <c>dndez:scene:{sceneId}:tokens:pending</c> (HASH tokenId→json {x,y}) — ตำแหน่งล่าสุดที่ยังไม่ flush
///  - <c>dndez:scene:{sceneId}:tokens:meta</c> (STRING json {campaignId,userId}) — สำหรับเปิด RLS
///    context ตอน flush (ดู doc comment ของ <see cref="SceneFlushMetadata"/>)
///  - <c>dndez:scenes:dirty</c> (SET ของ sceneId) — ให้ periodic flush scan ว่ามี scene ไหนค้างบ้าง
///
/// ไม่มี TTL โดยตั้งใจ (ต่างจาก turn state ที่ TTL 12 ชม.) — pending ต้องอยู่จนกว่าจะ flush สำเร็จจริง
/// เท่านั้น ปล่อยให้หมดอายุเองจะเสี่ยงข้อมูลตำแหน่งหายเงียบ ๆ ซึ่งขัดกับ §14.3 โดยตรง
/// </summary>
public sealed class RedisSceneTokenStore(
    IConnectionMultiplexer multiplexer, ILogger<RedisSceneTokenStore> logger) : ISceneTokenStore
{
    private static string PendingKey(Guid sceneId) => $"dndez:scene:{sceneId}:tokens:pending";
    private static string MetaKey(Guid sceneId) => $"dndez:scene:{sceneId}:tokens:meta";
    private const string DirtySetKey = "dndez:scenes:dirty";

    public async Task<bool> SetPendingPositionAsync(
        Guid campaignId, Guid sceneId, Guid tokenId, double x, double y, Guid actingUserId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var db = multiplexer.GetDatabase();
            var json = JsonSerializer.Serialize(new TokenPosition(x, y));
            await db.HashSetAsync(PendingKey(sceneId), tokenId.ToString(), json).WaitAsync(cancellationToken);
            await db.StringSetAsync(MetaKey(sceneId), JsonSerializer.Serialize(new SceneFlushMetadata(campaignId, actingUserId)))
                .WaitAsync(cancellationToken);
            await db.SetAddAsync(DirtySetKey, sceneId.ToString()).WaitAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            logger.LogWarning(ex,
                "เขียน pending token position ของ scene {SceneId} token {TokenId} ไม่สำเร็จ — caller ต้อง fallback ไป Postgres ตรง ๆ",
                sceneId, tokenId);
            return false;
        }
    }

    public async Task<IReadOnlyDictionary<Guid, TokenPosition>> GetPendingPositionsAsync(
        Guid sceneId, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = multiplexer.GetDatabase();
            var entries = await db.HashGetAllAsync(PendingKey(sceneId)).WaitAsync(cancellationToken);
            var result = new Dictionary<Guid, TokenPosition>();
            foreach (var entry in entries)
            {
                if (Guid.TryParse(entry.Name.ToString(), out var tokenId))
                {
                    var pos = JsonSerializer.Deserialize<TokenPosition>(entry.Value.ToString()!);
                    result[tokenId] = pos;
                }
            }
            return result;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            // อ่าน pending ไม่ได้ = ถือว่าไม่มี pending (GET scene จะคืนค่าจาก Postgres เฉย ๆ ซึ่งอาจ
            // เก่ากว่าความจริงเล็กน้อย — ยอมรับได้เพราะ endpoint PATCH เองก็ fallback ไป Postgres
            // ตรง ๆ ตอน Redis ล่มอยู่แล้ว คนละเคสกับการทำข้อมูลหาย (ไม่มีอะไรถูกเขียนทับ แค่อ่านไม่ครบ)
            logger.LogWarning(ex, "อ่าน pending token positions ของ scene {SceneId} ไม่สำเร็จ — คืนค่าว่าง", sceneId);
            return new Dictionary<Guid, TokenPosition>();
        }
    }

    public async Task<SceneFlushMetadata?> GetFlushMetadataAsync(Guid sceneId, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = multiplexer.GetDatabase();
            RedisValue value = await db.StringGetAsync(MetaKey(sceneId)).WaitAsync(cancellationToken);
            if (value.IsNullOrEmpty)
            {
                return null;
            }
            return JsonSerializer.Deserialize<SceneFlushMetadata>(value.ToString());
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            logger.LogWarning(ex, "อ่าน flush metadata ของ scene {SceneId} ไม่สำเร็จ", sceneId);
            return null;
        }
    }

    public async Task ClearAllPendingAsync(Guid sceneId, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = multiplexer.GetDatabase();
            await db.KeyDeleteAsync(PendingKey(sceneId)).WaitAsync(cancellationToken);
            await db.KeyDeleteAsync(MetaKey(sceneId)).WaitAsync(cancellationToken);
            await db.SetRemoveAsync(DirtySetKey, sceneId.ToString()).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            logger.LogWarning(ex, "ล้าง pending ของ scene {SceneId} ไม่สำเร็จ — รอบ flush ถัดไปจะเจอ key เดิมอีกครั้ง (idempotent ไม่เสียหาย)", sceneId);
        }
    }

    public async Task ClearTokenAsync(Guid sceneId, Guid tokenId, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = multiplexer.GetDatabase();
            await db.HashDeleteAsync(PendingKey(sceneId), tokenId.ToString()).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            logger.LogWarning(ex, "ลบ pending token {TokenId} ของ scene {SceneId} ไม่สำเร็จ", tokenId, sceneId);
        }
    }

    public async Task<IReadOnlyCollection<Guid>> GetDirtySceneIdsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var db = multiplexer.GetDatabase();
            var members = await db.SetMembersAsync(DirtySetKey).WaitAsync(cancellationToken);
            return members
                .Select(m => Guid.TryParse(m.ToString(), out var id) ? id : (Guid?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToList();
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            logger.LogWarning(ex, "อ่านรายชื่อ scene ที่ค้าง flush ไม่สำเร็จ — ข้ามรอบนี้");
            return [];
        }
    }

    private static bool IsExpectedFailure(Exception ex) => ex is
        RedisConnectionException or
        RedisTimeoutException or
        RedisException or
        TimeoutException or
        IOException or
        ObjectDisposedException or
        JsonException;
}
