using System.Text.Json;
using DnDEz.Rules;
using StackExchange.Redis;

namespace DnDEz.Api.Encounters;

/// <summary>
/// ผลอ่าน turn state พร้อม <see cref="Version"/> — สตริง JSON ดิบที่อ่านมาได้จาก Redis (ไม่ใช่ค่าที่
/// re-serialize ใหม่) ใช้เป็น optimistic-concurrency token ส่งกลับเข้า
/// <see cref="IEncounterTurnStateStore.CompareAndSetAsync"/> เมื่อ key ยังไม่เคยมีค่า (Redis ล่ม/ไม่มี
/// key จริงๆ) <see cref="Version"/> จะเป็น <c>null</c> ซึ่งหมายถึง "คาดหวังว่า key ยังไม่มีอยู่"
/// </summary>
public readonly record struct TurnStateSnapshot(ActionEconomyState State, string? Version);

/// <summary>
/// ผลของการเขียนแบบ compare-and-set — <c>Written</c> เขียนสำเร็จ, <c>Conflict</c> มีคนอื่นเขียนแทรก
/// ระหว่างอ่าน-แก้-เขียนของ caller (version ไม่ตรง), <c>Unavailable</c> Redis ใช้งานไม่ได้
/// </summary>
public enum TurnStateWriteResult { Written, Conflict, Unavailable }

/// <summary>
/// ที่เก็บ <see cref="ActionEconomyState"/> ของแต่ละเอนเคาน์เตอร์ระหว่างเทิร์น — เป็นสถานะชั่วคราว
/// ล้วนๆ (ไม่ใช่ source of truth) ต่างจาก HP/dice roll/proposal ที่บันทึกลง Postgres เสมอ
/// </summary>
public interface IEncounterTurnStateStore
{
    Task<TurnStateSnapshot> GetSnapshotAsync(Guid encounterId, int turnNumber, ActionEconomyState fallback, CancellationToken cancellationToken = default);
    Task<TurnStateWriteResult> CompareAndSetAsync(Guid encounterId, int turnNumber, string? expectedVersion, ActionEconomyState newState, CancellationToken cancellationToken = default);
    Task<bool> TrySetAsync(Guid encounterId, int turnNumber, ActionEconomyState state, CancellationToken cancellationToken = default);
    Task TryRemoveAsync(Guid encounterId, int turnNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// เก็บ action economy state ของเทิร์นปัจจุบันไว้ใน Redis คีย์ต่อเอนเคาน์เตอร์+เทิร์น
/// (<c>dndez:encounter:{encounterId}:turn:{turnNumber}:state</c>) อายุ 12 ชั่วโมง
///
/// นโยบาย read กับ write ตั้งใจให้ต่างกัน — นี่คือการตัดสินใจที่สำคัญที่สุดของไฟล์นี้:
///
/// - **อ่านไม่ได้ (ไม่ว่าเพราะไม่มี key จริงๆ หรือ Redis ล่ม) → fail closed เป็น "เทิร์นใหม่เต็ม
///   ทรัพยากร"** คืน <c>fallback</c> เหมือนกันทั้งสองกรณีโดยตั้งใจ ไม่ให้ caller แยกออก เพราะทั้งสอง
///   กรณีจบที่พฤติกรรมเดียวกันคือเริ่ม <c>StartTurn</c> ใหม่อยู่ดี — ตามที่ล็อกไว้ใน
///   docs/PROJECT-CONTEXT.md §4 "3 คำถามที่เคยบล็อกไว้" ข้อ 3: Redis หาย = รีเซ็ตเทิร์นปัจจุบัน
///   ไม่ทำ dual-write เพราะข้อมูลที่สำคัญจริง (HP/dice roll/proposal) อยู่ Postgres อยู่แล้ว
///
/// - **เขียนไม่ได้ → ต้องบอก caller ให้รู้ (คืน <c>false</c> หรือ <c>Unavailable</c>)** เพราะถ้าเงียบแล้ว
///   ตอบ 200 ผู้เล่นจะเชื่อว่า "ใช้ action ไปแล้ว" ทั้งที่ระบบไม่ได้บันทึกสถานะนั้นไว้เลย — นี่คือ fail
///   **open** ของ action economy ซึ่งอันตรายกว่า fail closed ตอนอ่าน caller (endpoint spend action)
///   มีหน้าที่แปลงผลเขียนที่ไม่สำเร็จเป็น 503 ให้ผู้เล่นรู้ว่าต้องลองใหม่ ไม่ใช่ปล่อยผ่านเงียบๆ
///
/// - TTL 12 ชั่วโมงปลอดภัยเพราะ key หมดอายุเองให้พฤติกรรมเดียวกับ Redis ล่มเป๊ะ (อ่านไม่เจอ →
///   รีเซ็ตเทิร์น) ไม่ใช่สถานะที่พังหรือค้าง
///
/// - **ทำไม key ต้องมี <c>turnNumber</c>**: วง initiative จะวนกลับมาที่ participant คนเดิม ถ้า key
///   ผูกแค่กับ encounter อย่างเดียว ค่าของเทิร์นก่อนหน้าจะถูกอ่านซ้ำเป็นของเทิร์นใหม่ได้ตอนที่ Redis
///   เขียนไม่ลง (เช่น next-turn เขียนล้มเหลวแต่ Postgres เลื่อนตัวชี้เทิร์นไปแล้ว) การผูก key กับเทิร์น
///   ทำให้ค่าเก่า**ไม่มีทางถูกอ่านโดยเทิร์นใหม่ตั้งแต่ต้น** ไม่ว่า Redis จะพังแบบไหน ทางแก้อื่นที่เคย
///   พิจารณาแล้วไม่ได้ผล: ตอบ 503 ตรงๆ ไม่ช่วยเพราะ <c>Results.StatusCode(503)</c> ไม่ใช่ exception
///   ทำให้ transaction ของ middleware ยัง commit ตัวชี้เทิร์นที่เลื่อนไปแล้วอยู่ดี · ลบ key เก่าก่อน
///   เขียนก็ไม่ช่วยเพราะตอน Redis ล่มการลบก็ล้มเหลวเหมือนกัน
///
/// - **ทำไม spend ต้อง CAS แต่ start/next-turn ไม่ต้อง**: start/next-turn เขียนค่า "เทิร์นใหม่เต็ม
///   ทรัพยากร" ลง key ที่ยังไม่มีใครถือ ไม่มีอะไรให้ชน (ใช้ <see cref="TrySetAsync"/> เขียนทับตรงๆ ได้)
///   ส่วน spend เป็น read-modify-write ที่ชนกันได้จริง — สอง request ในเทิร์นเดียวกัน (เช่น ยิง Action
///   กับ Movement พร้อมกัน) อ่าน state ตั้งต้นเดียวกันแล้วเขียนทับกัน ถ้าไม่ CAS ตัวที่เขียนทีหลังจะ
///   ชนะเสมอโดยไม่รู้ตัว (lost update) จึงต้องใช้ <see cref="CompareAndSetAsync"/> ผูกกับ
///   <see cref="TurnStateSnapshot.Version"/> ที่อ่านมาตอนต้น request
/// </summary>
public sealed class RedisEncounterTurnStateStore(
    IConnectionMultiplexer multiplexer,
    ILogger<RedisEncounterTurnStateStore> logger) : IEncounterTurnStateStore
{
    private static readonly TimeSpan TurnStateTtl = TimeSpan.FromHours(12);

    private static string BuildKey(Guid encounterId, int turnNumber) => $"dndez:encounter:{encounterId}:turn:{turnNumber}:state";

    public async Task<TurnStateSnapshot> GetSnapshotAsync(Guid encounterId, int turnNumber, ActionEconomyState fallback, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = multiplexer.GetDatabase();
            RedisValue value = await db.StringGetAsync(BuildKey(encounterId, turnNumber)).WaitAsync(cancellationToken);
            if (value.IsNullOrEmpty)
            {
                return new TurnStateSnapshot(fallback, null);
            }

            var rawJson = value.ToString();
            var state = JsonSerializer.Deserialize<ActionEconomyState>(rawJson);
            if (state is null)
            {
                return new TurnStateSnapshot(fallback, null);
            }

            return new TurnStateSnapshot(state, rawJson);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            logger.LogWarning(ex, "อ่าน turn state ของเอนเคาน์เตอร์ {EncounterId} เทิร์น {TurnNumber} ไม่สำเร็จ — fail closed เป็นเทิร์นใหม่", encounterId, turnNumber);
            return new TurnStateSnapshot(fallback, null);
        }
    }

    public async Task<TurnStateWriteResult> CompareAndSetAsync(Guid encounterId, int turnNumber, string? expectedVersion, ActionEconomyState newState, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = multiplexer.GetDatabase();
            var key = BuildKey(encounterId, turnNumber);
            var json = JsonSerializer.Serialize(newState);

            var tran = db.CreateTransaction();
            tran.AddCondition(expectedVersion is null ? Condition.KeyNotExists(key) : Condition.StringEqual(key, expectedVersion));
            _ = tran.StringSetAsync(key, json, TurnStateTtl);
            var ok = await tran.ExecuteAsync().WaitAsync(cancellationToken);

            if (!ok)
            {
                logger.LogWarning("เขียน turn state ของเอนเคาน์เตอร์ {EncounterId} เทิร์น {TurnNumber} ชนกับการเขียนอื่น — ปฏิเสธเพื่อกัน lost update", encounterId, turnNumber);
                return TurnStateWriteResult.Conflict;
            }

            return TurnStateWriteResult.Written;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            logger.LogWarning(ex, "เขียน turn state (CAS) ของเอนเคาน์เตอร์ {EncounterId} เทิร์น {TurnNumber} ไม่สำเร็จ — caller ต้องแจ้งผู้เล่นว่าลองใหม่", encounterId, turnNumber);
            return TurnStateWriteResult.Unavailable;
        }
    }

    public async Task<bool> TrySetAsync(Guid encounterId, int turnNumber, ActionEconomyState state, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = multiplexer.GetDatabase();
            var json = JsonSerializer.Serialize(state);
            return await db.StringSetAsync(BuildKey(encounterId, turnNumber), json, TurnStateTtl).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            logger.LogWarning(ex, "เขียน turn state ของเอนเคาน์เตอร์ {EncounterId} เทิร์น {TurnNumber} ไม่สำเร็จ — caller ต้องแจ้งผู้เล่นว่าลองใหม่", encounterId, turnNumber);
            return false;
        }
    }

    public async Task TryRemoveAsync(Guid encounterId, int turnNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = multiplexer.GetDatabase();
            await db.KeyDeleteAsync(BuildKey(encounterId, turnNumber)).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            logger.LogWarning(ex, "ลบ turn state ของเอนเคาน์เตอร์ {EncounterId} เทิร์น {TurnNumber} ไม่สำเร็จ — ปล่อยให้ TTL หมดอายุเอง", encounterId, turnNumber);
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
