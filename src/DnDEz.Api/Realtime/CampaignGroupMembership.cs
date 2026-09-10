using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace DnDEz.Api.Realtime;

/// <summary>
/// Sprint F งานที่ 3 — ปิด finding ที่ค้างมาจาก security review ของ Sprint C Part 1:
/// <c>CampaignHub.JoinCampaign</c> เช็คสมาชิกภาพ **ครั้งเดียว** ตอน join แล้วใส่ connection เข้า
/// SignalR group **ถาวร** — ไม่มีการเช็คซ้ำ และไม่มีกลไกถอด connection ออกจาก group เมื่อสมาชิกภาพ
/// เปลี่ยน · ตราบใดที่ connection ยังเปิดอยู่ มันจะรับ broadcast ของแคมเปญนั้นต่อไปเรื่อย ๆ
///
/// ⚠️ **สถานะจริงตอนเขียน (2026-09-02): ยัง exploit ไม่ได้** — ตรวจแล้วว่าไม่มีเส้นทางไหนใน
/// ระบบที่ "ถอด/เปลี่ยน" แถว `campaign_members` ได้เลย: ไม่มีโค้ด C# เขียนตารางนี้สักที่ และสอง
/// ฟังก์ชันที่เขียนได้ (`app_create_campaign()` / `app_join_campaign()`) เป็น **additive ทั้งคู่**
/// (INSERT อย่างเดียว, hardcode role) · งานนี้จึงเป็น **"ทำให้ปลอดภัยไว้ก่อนที่เส้นทางถอดจะมีจริง"**
/// ไม่ใช่การอุดรูที่รั่วอยู่ — ตรงกับที่โปรเจกต์นี้ทำกับ `autonomy_settings` (ฮาร์เดน RLS ตั้งแต่ก่อน
/// วันที่มันจะเสี่ยงจริง)
///
/// ── ทำไมเลือกแบบนี้ (registry + สแกนซ้ำเป็นรอบ + hook ให้เตะทันที) ────────────────────────
///
/// ตัวเลือกที่ชั่งแล้วไม่พอ/ไม่เหมาะ:
///  • **เช็คซ้ำทุกครั้งที่เรียก hub method** — ไม่พอ เพราะ broadcast จริงของระบบนี้ยิงจาก REST
///    endpoint ผ่าน <c>IHubContext&lt;CampaignHub&gt;.Clients.Group(...)</c> **ไม่ได้ผ่าน hub method
///    เลย** (ดู `OrchestratorTurnRunner`, `DiceRollEndpoints`, `CombatEndpoints`) การเช็คตอนเรียก
///    hub method จึงไม่แตะเส้นทางที่ข้อมูลรั่วจริง
///  • **เช็คสิทธิ์ตอน broadcast ทุกครั้ง** — ถูกต้องที่สุดในทางทฤษฎี แต่ต้องยิง query ต่อ broadcast
///    ต่อผู้รับหนึ่งคน และต้องแก้ call site ทุกที่ที่ broadcast — แพงและกระจายจุดพลาด
///  • **เตะตอนสมาชิกภาพเปลี่ยน อย่างเดียว** — วันนี้ไม่มีจุดให้ hook เลย (ไม่มีเส้นทางถอด) จะกลาย
///    เป็นโค้ดที่ไม่มีใครเรียก และยังพลาดกรณีที่แถวถูกแก้นอกแอป (แก้ DB ตรง ๆ / job ในอนาคต)
///
/// ที่เลือก = **สองชั้นคู่กัน**:
///  1. <see cref="CampaignGroupRegistry"/> จำว่า connection ไหนอยู่ group ของแคมเปญไหนในนามของ user ไหน
///  2. <see cref="CampaignGroupRevalidator"/> สแกนซ้ำเป็นรอบ (backstop — จับได้ทุกกรณีรวมถึงการแก้
///     DB ตรง ๆ โดยไม่ต้องให้ใครมา hook) **บวก** <see cref="CampaignGroupRevalidator.EvictMemberAsync"/>
///     ให้ฟีเจอร์ในอนาคต (เช่น endpoint "เตะสมาชิก") เรียกเพื่อเตะ **ทันที** ปิดช่วงเวลาที่รอรอบสแกน
///
/// ข้อจำกัดที่ต้องรู้ตัว (1) — **หน้าต่างเวลา**: การสแกนเป็นรอบแปลว่ามีช่วง (ยาวสุด = ช่วงสแกน
/// หนึ่งรอบ) ที่ connection ที่เพิ่งหมดสิทธิ์ยังรับ broadcast ได้ · การเรียก `EvictMemberAsync`
/// ตรงจุดที่ถอดสมาชิกภาพคือสิ่งที่ปิดหน้าต่างนั้น — **ห้ามคิดว่าตัวสแกนอย่างเดียวพอ** เมื่อวันหนึ่ง
/// มี endpoint ถอดสมาชิกจริง
///
/// ⚠️ ข้อจำกัดที่ต้องรู้ตัว (2) — **หลายอินสแตนซ์ (Redis backplane)**: registry นี้เก็บใน memory
/// ของอินสแตนซ์ที่ connection นั้น handshake เข้ามาเท่านั้น ไม่ได้ share ผ่าน Redis ผลคือ:
///  • **การสแกนเป็นรอบครอบคลุมครบ** — ทุกอินสแตนซ์รัน <see cref="CampaignGroupRevalidationService"/>
///    ของตัวเอง และแต่ละ connection ถูกจำไว้ที่อินสแตนซ์ที่มันต่ออยู่ → รวมกันแล้วไม่มีใครหลุด
///  • **แต่ `EvictMemberAsync` ครอบคลุมเฉพาะ connection ที่อยู่บนอินสแตนซ์ที่รับ HTTP request นั้น**
///    ถ้า WebSocket ของผู้ใช้ homed อยู่คนละอินสแตนซ์กับ REST request ที่เพิ่งถอดสมาชิกภาพ
///    การเตะทันทีจะ **ไม่เกิด** และต้องรอรอบสแกนของอินสแตนซ์นั้นแทน (กลับไปเป็นหน้าต่างตามข้อ 1)
/// ถ้าวันหลังต้องการเตะทันทีจริงข้ามอินสแตนซ์ ต้องกระจายสัญญาณเตะผ่าน Redis pub/sub เพิ่ม —
/// ตอนนี้ยังไม่ทำเพราะยังไม่มีเส้นทางถอดสมาชิกใด ๆ ในระบบ (ดูย่อหน้าบนสุด)
///
/// หมายเหตุ: ตัว <c>Groups.RemoveFromGroupAsync</c> เองทำงานข้ามอินสแตนซ์ได้ผ่าน backplane อยู่แล้ว
/// — ข้อจำกัดข้างบนอยู่ที่ "อินสแตนซ์ไหน**รู้จัก** subscription นั้น" ไม่ใช่ที่การถอดออกจาก group
/// </summary>
public sealed class CampaignGroupRegistry
{
    private readonly ConcurrentDictionary<string, CampaignGroupSubscription> _subscriptions = new();

    private static string Key(string connectionId, Guid campaignId) => $"{connectionId}|{campaignId}";

    public void Add(string connectionId, Guid campaignId, Guid userId) =>
        _subscriptions[Key(connectionId, campaignId)] =
            new CampaignGroupSubscription(connectionId, campaignId, userId);

    public void Remove(string connectionId, Guid campaignId) =>
        _subscriptions.TryRemove(Key(connectionId, campaignId), out _);

    /// <summary>ล้างทุก subscription ของ connection นี้ — ใช้ตอน <c>OnDisconnectedAsync</c>
    /// (connection เดียวอาจ join หลายแคมเปญ)</summary>
    public void RemoveConnection(string connectionId)
    {
        foreach (var entry in _subscriptions.Where(e => e.Value.ConnectionId == connectionId).ToList())
        {
            _subscriptions.TryRemove(entry.Key, out _);
        }
    }

    /// <summary>เช็คเร็ว ๆ ว่า connection นี้อยู่ group ของแคมเปญนี้ไหม (in-memory ไม่แตะ DB) —
    /// Sprint G Part 5 ใช้กันสแปม hub method ที่ยิงถี่มาก (ghost token move ระหว่างลาก) ไม่ให้ต้อง
    /// query DB ทุกเฟรม (ผิดจุดประสงค์ของ ephemeral broadcast ทั้งหมดถ้าต้องพึ่ง DB round-trip)</summary>
    public bool Contains(string connectionId, Guid campaignId) =>
        _subscriptions.ContainsKey(Key(connectionId, campaignId));

    public IReadOnlyCollection<CampaignGroupSubscription> Snapshot() => _subscriptions.Values.ToList();

    public IReadOnlyCollection<CampaignGroupSubscription> SnapshotFor(Guid campaignId, Guid userId) =>
        _subscriptions.Values.Where(s => s.CampaignId == campaignId && s.UserId == userId).ToList();
}

public sealed record CampaignGroupSubscription(string ConnectionId, Guid CampaignId, Guid UserId);

/// <summary>
/// ตรวจสมาชิกภาพซ้ำแล้วถอด connection ที่หมดสิทธิ์ออกจาก SignalR group
///
/// ⚠️ ต้องเปิด scope + transaction ของตัวเองต่อการเช็คหนึ่งครั้ง เหมือน
/// <c>CampaignHub.IsMemberAsync</c> — เหตุผลเต็มอยู่ที่ doc comment ของ <see cref="CampaignHub"/>:
/// `SET LOCAL app.user_id` ที่ middleware ตั้งไว้ผูกกับ transaction ของ HTTP request ตอน handshake
/// เท่านั้น ไม่รอดมาถึงตรงนี้ · ถ้าลืมตั้งค่า RLS จะกรองทุกแถวทิ้ง (fail closed) ซึ่งที่นี่แปลว่า
/// **เตะทุกคนออก** — ผิดพลาดไปทางปลอดภัย แต่ยังผิดอยู่ดี
/// </summary>
public sealed class CampaignGroupRevalidator(
    IServiceScopeFactory scopeFactory,
    IHubContext<CampaignHub> hubContext,
    CampaignGroupRegistry registry,
    ILogger<CampaignGroupRevalidator> logger)
{
    /// <summary>
    /// เตะทุก connection ของ (แคมเปญ, user) คู่นี้ออกจาก group ทันทีโดยไม่ต้องรอรอบสแกน —
    /// จุดเรียกที่ตั้งใจไว้คือ "ตรงที่ถอดสมาชิกภาพ" ของฟีเจอร์ในอนาคต (endpoint เตะสมาชิก)
    ///
    /// ตั้งใจ **ไม่เช็ค DB ซ้ำ** ที่นี่ — ผู้เรียกคือคนที่เพิ่งถอดสมาชิกภาพเอง จึงรู้ดีกว่า และการ
    /// เช็คซ้ำจะแพ้ race กับ transaction ของผู้เรียกที่อาจยังไม่ commit
    /// </summary>
    public async Task EvictMemberAsync(Guid campaignId, Guid userId)
    {
        foreach (var subscription in registry.SnapshotFor(campaignId, userId))
        {
            await RemoveAsync(subscription);
        }
    }

    /// <summary>
    /// สแกนทุก subscription ที่จำไว้ เช็คสมาชิกภาพใหม่ แล้วถอดตัวที่หมดสิทธิ์ออก
    ///
    /// public เพื่อให้เทสต์เรียกตรงได้ ไม่ต้องรอ timer (กันเทสต์ flaky จากการนอนรอ) —
    /// production เรียกผ่าน <see cref="CampaignGroupRevalidationService"/>
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var evicted = 0;

        foreach (var subscription in registry.Snapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                if (!await IsStillMemberAsync(subscription, cancellationToken))
                {
                    await RemoveAsync(subscription);
                    evicted++;
                }
            }
            catch (Exception ex)
            {
                // DB ล่มชั่วคราวต้องไม่กลายเป็น "เตะทุกคนออก" — ข้ามรอบนี้ไป รอบหน้าค่อยเช็คใหม่
                // (fail open โดยตั้งใจเฉพาะกรณี error เท่านั้น ไม่ใช่กรณีตอบว่า "ไม่ใช่สมาชิก"
                // ซึ่งเป็น false ธรรมดาและตกไปเข้า RemoveAsync ตามปกติ)
                //
                // ⚠️ try ครอบ RemoveAsync ด้วย (security review รอบ 1) — เดิมครอบแค่การเช็คสมาชิกภาพ
                // ทำให้ Redis สะดุดตอนถอด group โยน exception ทะลุออกไปทั้ง SweepAsync แล้ว
                // subscription ที่เหลือในรอบนั้นถูกข้ามทั้งหมด · ลำดับใน RemoveAsync (ถอด group ก่อน
                // แล้วค่อยลบออกจาก registry) ทำให้แถวยังค้างอยู่เมื่อถอดไม่สำเร็จ → รอบหน้าลองใหม่เอง
                logger.LogWarning(ex,
                    "ตรวจ/ถอดสมาชิกภาพไม่สำเร็จสำหรับ connection {ConnectionId} แคมเปญ {CampaignId} — ข้ามรอบนี้",
                    subscription.ConnectionId, subscription.CampaignId);
                continue;
            }
        }

        return evicted;
    }

    private async Task RemoveAsync(CampaignGroupSubscription subscription)
    {
        await hubContext.Groups.RemoveFromGroupAsync(
            subscription.ConnectionId, CampaignHub.GroupName(subscription.CampaignId));
        registry.Remove(subscription.ConnectionId, subscription.CampaignId);

        logger.LogInformation(
            "ถอด connection {ConnectionId} ออกจากกลุ่มแคมเปญ {CampaignId} — สมาชิกภาพหมดแล้ว",
            subscription.ConnectionId, subscription.CampaignId);
    }

    private async Task<bool> IsStillMemberAsync(
        CampaignGroupSubscription subscription, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DnDEzDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {subscription.UserId.ToString()}, true)", cancellationToken);

        // เหมือน CampaignHub.IsMemberAsync เป๊ะ — RLS policy `campaign_member_read` กรอง campaigns
        // ให้เหลือเฉพาะแคมเปญที่ user นี้เป็นสมาชิก แถวหาย = ไม่ใช่สมาชิกแล้ว (หรือแคมเปญถูกลบ)
        var exists = await db.Campaigns.AnyAsync(c => c.Id == subscription.CampaignId, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return exists;
    }
}

/// <summary>
/// เรียก <see cref="CampaignGroupRevalidator.SweepAsync"/> เป็นรอบ ๆ
///
/// ช่วงเวลา 60 วินาทีเป็นการแลกระหว่าง "หน้าต่างที่ยังรับ broadcast ได้หลังหมดสิทธิ์" กับภาระ query
/// (หนึ่ง query ต่อ connection ต่อรอบ) · ถี่กว่านี้ไม่ได้ช่วยมากเพราะเส้นทางที่ควรใช้จริงเมื่อมี
/// ฟีเจอร์ถอดสมาชิกคือ `EvictMemberAsync` ที่เตะทันทีอยู่แล้ว — ตัวนี้เป็น backstop ไม่ใช่ด่านหลัก
/// </summary>
public sealed class CampaignGroupRevalidationService(
    CampaignGroupRevalidator revalidator,
    ILogger<CampaignGroupRevalidationService> logger) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await revalidator.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // ปิดแอปตามปกติ
            }
            catch (Exception ex)
            {
                // background service ที่โยน exception ออกไปจะตายถาวรและเงียบ — ต้องกลืนแล้วไปต่อ
                logger.LogError(ex, "รอบตรวจสมาชิกภาพซ้ำล้มเหลว — จะลองใหม่รอบหน้า");
            }
        }
    }
}
