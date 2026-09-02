using DnDEz.Api.Auth;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Realtime;

/// <summary>
/// Sprint C Part 1 — hub เดียวสำหรับ broadcast dice-roll/HP-update (ยังไม่ broadcast อะไรจริงในรอบนี้ —
/// นั่นเป็นของ Part 2/3 ที่ wire เข้า DiceRollEndpoints/CombatEndpoints) รอบนี้วางแค่โครง connection +
/// group + authorization ให้ถูกต้องก่อน เพราะเป็นจุดเสี่ยงสุดของ Sprint C
///
/// ⚠️ ทำไมต้องมี IsMemberAsync ของตัวเอง แยกจาก CampaignVisibility ที่ endpoint อื่นใช้:
/// middleware ใน Program.cs ที่เปิด transaction + `SET LOCAL app.user_id/app.campaign_id` ผูกกับ
/// **หนึ่ง HTTP request** เท่านั้น (`app.Use(...)` วิ่งตอน negotiate/handshake ครั้งเดียว) — หลังจาก
/// WebSocket upgrade แล้ว การเรียก Hub method ถัดไปทุกครั้ง**ไม่ผ่าน middleware pipeline นั้นซ้ำอีก**
/// (SignalR dispatch ตรงไปที่ HubDispatcher ไม่ใช่ request ใหม่) ถ้าใช้ DbContext ที่ inject ผ่าน
/// constructor ปกติ (scoped ผูกกับ connection ทั้งชีวิต) แล้วพึ่ง SET LOCAL ที่ตั้งไว้ตอน handshake
/// เดียว ค่านั้นจะหายไปทันทีที่ query แรกของ handshake request จบ (SET LOCAL ผูกกับ transaction
/// ไม่ใช่ connection — บทเรียนเดียวกับที่ล็อกไว้ใน Program.cs) ทำให้ทุก Hub method หลังจากนั้นรัน
/// "ไม่มี context" = RLS กรองทุกแถวทิ้ง (fail closed แต่ผิดพลาดเงียบ ๆ ไม่ใช่ error ชัดเจน)
///
/// ทางแก้: เปิด scope + transaction ของตัวเองสั้น ๆ ต่อการเช็คสิทธิ์หนึ่งครั้ง (เหมือนที่ middleware
/// ทำต่อ HTTP request) ผ่าน IServiceScopeFactory แทนการพึ่ง DbContext ที่ inject มาจาก connection scope
/// </summary>
[Authorize]
public sealed class CampaignHub(IServiceScopeFactory scopeFactory, CampaignGroupRegistry registry) : Hub
{
    // จำไว้ว่า connection นี้อยู่ group ของแคมเปญ/user ไหน — การเช็คสิทธิ์ตอน join ครั้งเดียวไม่พอ
    // เพราะ broadcast จริงยิงผ่าน IHubContext จาก REST endpoint ไม่ผ่าน hub method เลย จึงต้องมี
    // registry ให้ CampaignGroupRevalidator สแกนซ้ำ/เตะทันทีได้ (เหตุผลเต็ม: CampaignGroupMembership.cs)

    /// <summary>ชื่อ SignalR group ต่อแคมเปญ — endpoint ฝั่ง REST (Part 2/3) ต้อง broadcast เข้า group
    /// ชื่อนี้เป๊ะ ๆ ถึงจะถึงผู้เล่นที่ join ไว้</summary>
    public static string GroupName(Guid campaignId) => $"campaign:{campaignId}";

    /// <summary>
    /// join group ได้เฉพาะสมาชิกแคมเปญจริงเท่านั้น — ไม่ใช่สมาชิก/แคมเปญไม่มีจริง โยน HubException
    /// ข้อความเดียวกันทั้งคู่ (เหมือน REST 404 เดียวกันสองเคส) กัน enumeration ผ่านข้อความ error
    /// </summary>
    public async Task JoinCampaign(Guid campaignId)
    {
        var userId = Context.User?.GetUserId();
        if (userId is null)
        {
            throw new HubException("unauthorized");
        }

        if (!await IsMemberAsync(campaignId, userId.Value))
        {
            throw new HubException("campaign_not_found");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(campaignId));
        registry.Add(Context.ConnectionId, campaignId, userId.Value);
    }

    public async Task LeaveCampaign(Guid campaignId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(campaignId));
        registry.Remove(Context.ConnectionId, campaignId);
    }

    /// <summary>
    /// ล้าง registry ตอน connection หลุด (ปิดแท็บ/เน็ตขาด ฯลฯ) — ถ้าไม่ทำ registry จะมีแถวค้างของ
    /// connection ที่ตายแล้วไปเรื่อย ๆ (memory leak) และ <see cref="CampaignGroupRevalidator"/> จะเสีย
    /// เวลาสแกน connection ที่ไม่มีอยู่จริงทุกรอบ
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        registry.RemoveConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private async Task<bool> IsMemberAsync(Guid campaignId, Guid userId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DnDEzDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {userId.ToString()}, true)");

        // แถวหาย (ไม่ว่าเพราะไม่มีอยู่จริงหรือไม่ใช่สมาชิก) = RLS กรองออกเหมือนกัน — ไม่ต้องแยกเคส
        // เหมือน CampaignVisibility.IsVisibleAsync ที่ REST endpoint ใช้
        var exists = await db.Campaigns.AnyAsync(c => c.Id == campaignId);

        await transaction.CommitAsync();
        return exists;
    }
}
