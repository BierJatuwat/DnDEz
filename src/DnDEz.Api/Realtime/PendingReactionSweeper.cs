using DnDEz.Api.Endpoints;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Realtime;

/// <summary>ผลของการเดินต่อหลังจังหวะ reaction หนึ่งจบลง</summary>
/// <param name="Completed">true = เดินจนจบเส้นทางแล้ว · false = ไปเจอจังหวะรอถัดไป</param>
public readonly record struct ResumeOutcome(bool Completed, PendingReaction? Next);

/// <summary>
/// เดินการเคลื่อนที่ที่ค้างอยู่ให้ไปต่อ — ใช้ร่วมกันระหว่าง endpoint ที่ผู้ถือสิทธิ์กดตอบ
/// กับ sweeper ที่ปิดให้เองเมื่อหมดเวลา
///
/// **ต้องเป็นโค้ดชุดเดียวกันทั้งสองทาง** ไม่ใช่เขียนแยก เพราะ "กดไม่สวน" กับ "หมดเวลา" ต้องให้ผล
/// เหมือนกันเป๊ะตาม player-side-design §4 — ถ้าแยกกันเขียนแล้ววันหนึ่งแก้ทางเดียว เกมจะเดินคนละแบบ
/// ระหว่างคนที่กดกับคนที่ปล่อยหมดเวลา ซึ่งเป็นบั๊กที่หาเจอยากมาก
///
/// ⚠️ เปิด scope + transaction + <c>SET LOCAL app.user_id/app.campaign_id</c> ของตัวเองเสมอ
/// เหมือน <see cref="SceneTokenFlusher"/> เพราะถูกเรียกจาก background timer ที่ไม่มี HTTP request
/// ให้ middleware ตั้ง RLS context ให้ — ใช้ <c>ActingUserId</c> ที่บันทึกไว้ตอนสั่งเดิน (ผ่านเช็ค
/// สิทธิ์มาแล้ว) เปิดทางให้ RLS มองเห็นแถว ไม่ใช่ตัดสินสิทธิ์ซ้ำ
/// </summary>
public sealed class PendingReactionSweeper(
    IServiceScopeFactory scopeFactory,
    IPendingReactionStore store,
    IHubContext<CampaignHub> hubContext,
    ILogger<PendingReactionSweeper> logger)
{
    /// <summary>
    /// ปิดทุกตัวที่พ้น <paramref name="now"/> ไปแล้วโดยถือว่า "ไม่ใช้ reaction" แล้วเดินต่อ
    ///
    /// รับเวลาเข้ามาเป็นพารามิเตอร์แทนการอ่านนาฬิกาเอง เพื่อให้เทสต์เร่งเวลาได้โดยไม่ต้องรอจริง
    /// และไม่ต้องมี API เฉพาะเทสต์รั่วเข้ามาใน interface ของ production
    /// </summary>
    /// <returns>จำนวนรายการที่ปิดไป</returns>
    public async Task<int> SweepExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var open = await store.GetOpenIdsAsync(cancellationToken);
        var swept = 0;

        foreach (var id in open)
        {
            var pending = await store.GetAsync(id, cancellationToken);
            if (pending is null)
            {
                // คีย์หมดอายุไปแล้ว — เก็บกวาด set ให้สะอาด ไม่มีอะไรต้องเดินต่อเพราะ
                // ตำแหน่งที่ค้างอยู่ถูกต้องตามกฎอยู่แล้ว (ช่องก่อนหลุดระยะ)
                await store.RemoveAsync(id, cancellationToken);
                continue;
            }

            if (pending.DeadlineUtc > now)
            {
                continue;
            }

            try
            {
                await ResumeAsync(pending, reactionUsed: false, cancellationToken);
                swept++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ปิด pending reaction {ReactionId} ที่หมดเวลาไม่สำเร็จ", id);
            }
        }

        return swept;
    }

    /// <summary>
    /// เดินต่อจากจังหวะปัจจุบัน — ถ้ายังมีจังหวะรอถัดไปก็ย้าย token ไปช่องนั้นแล้วรอใหม่
    /// ถ้าหมดแล้วก็เดินไปช่องปลายทางแล้วลบสถานะทิ้ง
    /// </summary>
    public async Task<ResumeOutcome> ResumeAsync(
        PendingReaction pending, bool reactionUsed, CancellationToken cancellationToken)
    {
        var nextIndex = pending.CurrentTriggerIndex + 1;
        var hasNext = nextIndex < pending.Triggers.Count;

        var destination = hasNext
            ? new PendingPathPoint(pending.Triggers[nextIndex].AttackFromX, pending.Triggers[nextIndex].AttackFromY)
            : pending.Path[^1];

        await MoveTokenAsync(pending, destination, cancellationToken);
        await store.RemoveAsync(pending.Id, cancellationToken);

        PendingReaction? next = null;
        if (hasNext)
        {
            // จังหวะถัดไปได้ **เวลานับถอยหลังใหม่เต็มจำนวน** ไม่ใช่เศษเวลาที่เหลือของจังหวะก่อน —
            // คนละคนถือสิทธิ์ คนใหม่ต้องมีเวลาตัดสินใจเท่ากับที่ DM ตั้งไว้ ไม่ใช่โดนหารกับคนก่อนหน้า
            next = pending with
            {
                Id = Guid.NewGuid(),
                CurrentTriggerIndex = nextIndex,
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(pending.TimeoutSeconds),
            };

            // เก็บไม่ได้ = ไม่มีใครมาปลุกให้เดินต่อ → เดินให้จบไปเลยดีกว่าค้างถาวร
            if (!await store.SaveAsync(next, cancellationToken))
            {
                logger.LogWarning(
                    "เก็บจังหวะ reaction ถัดไปไม่ได้ — เดินให้จบเส้นทางแทนการค้างรอ ({ReactionId})", pending.Id);
                await MoveTokenAsync(pending, pending.Path[^1], cancellationToken);
                await BroadcastAsync(pending, reactionUsed, completed: true, cancellationToken);
                return new ResumeOutcome(true, null);
            }

            await NotifyThreatHolderAsync(next, cancellationToken);
        }

        await BroadcastAsync(pending, reactionUsed, completed: !hasNext, cancellationToken);
        return new ResumeOutcome(!hasNext, next);
    }

    private Task MoveTokenAsync(
        PendingReaction pending, PendingPathPoint destination, CancellationToken cancellationToken) =>
        MoveTokenAsync(
            pending.CampaignId, pending.SceneId, pending.TokenId, pending.ActingUserId,
            destination.X, destination.Y, cancellationToken);

    /// <summary>
    /// เขียนตำแหน่ง token — **จุดเดียวในระบบที่การเคลื่อนที่แบบ committed ลง Postgres**
    /// ใช้ทั้งจาก endpoint (ตอนเริ่มเดิน) และจาก sweeper (ตอนเดินต่อ) · เปิด scope/transaction เอง
    /// เสมอแม้ถูกเรียกจากใน request เพราะ sweeper ไม่มี request ให้ยืม context — เขียนสองที่แยกกัน
    /// จะกลายเป็นสองวิธีเขียนที่วันหนึ่งเพี้ยนจากกัน
    /// </summary>
    public async Task MoveTokenAsync(
        Guid campaignId, Guid sceneId, Guid tokenId, Guid actingUserId,
        double x, double y, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DnDEzDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {actingUserId.ToString()}, true)", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.campaign_id', {campaignId.ToString()}, true)", cancellationToken);

        var scene = await db.Scenes.SingleOrDefaultAsync(s => s.Id == sceneId, cancellationToken);
        if (scene is null)
        {
            return;
        }

        var tokens = SceneTokenSerialization.Parse(scene.Tokens);
        var index = tokens.FindIndex(t => t.Id == tokenId);
        if (index < 0)
        {
            return;
        }

        tokens[index] = tokens[index] with { X = x, Y = y };
        scene.Tokens = SceneTokenSerialization.Serialize(tokens);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>ยิง prompt ไปหา **เจ้าของสิทธิ์คนเดียว** ไม่ใช่ทั้งกลุ่ม (ข้อ 3 ของ dispatch) —
    /// ถ้า broadcast ทั้งกลุ่ม ทุกคนจะรู้ว่ามีใครกำลังจะโดนสวนตรงไหน ซึ่งเป็นข้อมูลของ DM/เจ้าของ NPC</summary>
    public async Task NotifyThreatHolderAsync(PendingReaction pending, CancellationToken cancellationToken)
    {
        var trigger = pending.CurrentTrigger;
        try
        {
            await hubContext.Clients
                .Group(CampaignHub.UserGroupName(pending.CampaignId, trigger.ThreatUserId))
                .SendAsync(
                    "ReactionPrompt",
                    new
                    {
                        ReactionId = pending.Id,
                        pending.SessionId,
                        pending.MoverCharacterId,
                        trigger.ThreatCharacterId,
                        trigger.ThreatName,
                        pending.TimeoutSeconds,
                        pending.DeadlineUtc,
                    },
                    cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ส่ง ReactionPrompt ไม่สำเร็จ ({ReactionId})", pending.Id);
        }
    }

    private async Task BroadcastAsync(
        PendingReaction pending, bool reactionUsed, bool completed, CancellationToken cancellationToken)
    {
        try
        {
            await hubContext.Clients.Group(CampaignHub.GroupName(pending.CampaignId)).SendAsync(
                "MovementResumed",
                new
                {
                    pending.SessionId,
                    pending.MoverCharacterId,
                    ThreatCharacterId = pending.CurrentTrigger.ThreatCharacterId,
                    ReactionUsed = reactionUsed,
                    Completed = completed,
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "broadcast MovementResumed ไม่สำเร็จ ({ReactionId})", pending.Id);
        }
    }
}

/// <summary>
/// ตัวที่ทำให้ข้อกำหนด *"หมดเวลาแล้วต้องเดินต่อได้เองโดยไม่ค้าง"* เป็นจริง — ต้องมี background timer
/// จริง ๆ ไม่ใช่เช็คตอนมี request เข้ามาเท่านั้น เพราะถ้าทุกคนปิดจอรอ ก็จะไม่มี request ให้เช็คเลย
///
/// รอบละ 5 วินาที: ถี่พอให้ผู้เล่นไม่รู้สึกว่าค้าง (timeout ต่ำสุดที่ตั้งได้คือ 5 วินาที) และเบาพอ
/// เพราะแต่ละรอบอ่านแค่ SET เดียวที่ปกติว่างเปล่า · ลอกแพทเทิร์นจาก <c>SceneTokenFlushService</c>
/// </summary>
public sealed class PendingReactionSweepService(
    IServiceScopeFactory scopeFactory,
    ILogger<PendingReactionSweepService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sweeper = scope.ServiceProvider.GetRequiredService<PendingReactionSweeper>();
                await sweeper.SweepExpiredAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // ห้ามให้รอบเดียวที่พังฆ่า background service ทิ้ง — รอบหน้าลองใหม่
                logger.LogWarning(ex, "รอบกวาด pending reaction ล้มเหลว");
            }
        }
    }
}
