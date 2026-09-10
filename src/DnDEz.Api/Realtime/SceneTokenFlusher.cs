using DnDEz.Api.Endpoints;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Realtime;

/// <summary>
/// รวม pending token position จาก Redis เข้า Postgres จริง (design doc §14.3: "persist ทันทีที่
/// เปลี่ยนเทิร์น/เปลี่ยน fog/จบ encounter/ปิด session บวก flush ตามเวลา")
///
/// ⚠️ ต้องเปิด scope + transaction ของตัวเอง + `SET LOCAL app.user_id`/`app.campaign_id` เหมือน
/// <c>CampaignHub.IsMemberAsync</c> เป๊ะ — เหตุผลเดียวกัน: เรียกจาก background timer (ไม่มี HTTP
/// request/route ให้ middleware ตั้งค่าให้) หรือจาก endpoint อื่น (next-turn/end-encounter) ที่
/// context ผูกกับแคมเปญของ **encounter** ไม่ใช่ของ scene ที่กำลัง flush เสมอไป (คนละ scope กัน)
///
/// ที่มาของ (campaignId, userId) ที่ใช้เปิด context: <see cref="SceneFlushMetadata"/> ที่บันทึกไว้
/// ตอน endpoint PATCH เขียน pending — เป็น userId ของคนที่**ผ่านเช็คสิทธิ์มาแล้ว**ตอนสั่งย้าย token
/// นั้น (เป็นสมาชิกแคมเปญแน่ ๆ) ใช้แค่เปิดทางให้ RLS มองเห็นแถว ไม่ใช่ตัดสินสิทธิ์ซ้ำ (สิทธิ์เช็ค
/// แล้วตอน endpoint ตอนเขียน pending — flush แค่ทำให้สิ่งที่อนุญาตแล้วไปถึง Postgres จริง)
/// </summary>
public sealed class SceneTokenFlusher(
    ISceneTokenStore store, IServiceScopeFactory scopeFactory, ILogger<SceneTokenFlusher> logger)
{
    public async Task FlushAsync(Guid sceneId, CancellationToken cancellationToken = default)
    {
        var metadata = await store.GetFlushMetadataAsync(sceneId, cancellationToken);
        if (metadata is null)
        {
            return; // ไม่มี pending จริง (หรือ Redis อ่านไม่ได้ — เข้าเงื่อนไข no-op เดียวกัน)
        }

        var pending = await store.GetPendingPositionsAsync(sceneId, cancellationToken);
        if (pending.Count == 0)
        {
            await store.ClearAllPendingAsync(sceneId, cancellationToken);
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DnDEzDbContext>();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.user_id', {metadata.Value.ActingUserId.ToString()}, true)", cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.campaign_id', {metadata.Value.CampaignId.ToString()}, true)", cancellationToken);

            var scene = await db.Scenes.SingleOrDefaultAsync(s => s.Id == sceneId, cancellationToken);
            if (scene is null)
            {
                // scene ถูกลบไปแล้วระหว่างที่ pending ค้างอยู่ — ไม่มีอะไรให้ flush เข้า ทิ้ง pending ทิ้ง
                await transaction.CommitAsync(cancellationToken);
                await store.ClearAllPendingAsync(sceneId, cancellationToken);
                return;
            }

            var tokens = SceneTokenSerialization.Parse(scene.Tokens);
            var changed = false;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (pending.TryGetValue(tokens[i].Id, out var pos))
                {
                    tokens[i] = tokens[i] with { X = pos.X, Y = pos.Y };
                    changed = true;
                }
            }

            if (changed)
            {
                scene.Tokens = SceneTokenSerialization.Serialize(tokens);
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            await store.ClearAllPendingAsync(sceneId, cancellationToken);
        }
        catch (Exception ex)
        {
            // ห้ามล้าง pending ถ้าเขียน Postgres ไม่สำเร็จ — ปล่อยให้รอบ flush ถัดไป (trigger ถัดไป
            // หรือ periodic) ลองใหม่ ดีกว่าทำข้อมูลตำแหน่งหายเงียบ ๆ ตาม §14.3
            logger.LogWarning(ex, "flush pending token positions ของ scene {SceneId} ไม่สำเร็จ — จะลองใหม่รอบถัดไป", sceneId);
        }
    }
}

/// <summary>flush ทุก scene ที่ค้างอยู่เป็นรอบ ๆ — ตาข่ายรองกรณี trigger point (next-turn/end
/// encounter) ไม่ยิง เช่น DM ลาก token ตอนไม่มี encounter ที่ active เลย</summary>
public sealed class SceneTokenFlushService(
    ISceneTokenStore store, SceneTokenFlusher flusher, ILogger<SceneTokenFlushService> logger) : BackgroundService
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                var dirtySceneIds = await store.GetDirtySceneIdsAsync(stoppingToken);
                foreach (var sceneId in dirtySceneIds)
                {
                    await flusher.FlushAsync(sceneId, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break; // ปิดแอปตามปกติ
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "รอบ flush token position ตามเวลาล้มเหลว — จะลองใหม่รอบหน้า");
            }
        }
    }
}
