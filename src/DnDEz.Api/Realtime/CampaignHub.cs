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
    /// ชื่อนี้เป๊ะ ๆ ถึงจะถึงผู้เล่นที่ join ไว้ · ยังคงไว้เป็น "กลุ่มรวม" สำหรับ event ที่ไม่มีความลับ
    /// ต่างกันตาม role (เช่น TurnChanged, ActiveBeatChanged) — event ที่มีข้อมูลต่างกันตาม role
    /// (Sprint G Part 6 เป็นต้นมา) ต้องใช้ <see cref="RoleGroupName"/> แทน</summary>
    public static string GroupName(Guid campaignId) => $"campaign:{campaignId}";

    /// <summary>
    /// Sprint G Part 6 (design doc §13.3 — "SignalR ต้องแยก group ต่อ role") — group ย่อยตาม role
    /// ภายในแคมเปญเดียวกัน ใช้ตอน broadcast ข้อมูลที่ DM เห็นได้แต่ผู้เล่นเห็นไม่ได้ (หรือกลับกัน)
    /// เช่น FogChanged ที่ยิงแยกทั้งสอง group ด้วย payload ที่อาจต่างกันในอนาคต (ตอนนี้ payload
    /// เดียวกันเพราะ fog state เองไม่ใช่ความลับ แต่โครงสร้าง group ต้องแยกไว้ตั้งแต่ตอนนี้ตามที่ design
    /// doc บังคับ ไม่ใช่รอจนกว่าจะมี event ที่ payload ต่างจริงถึงค่อยแยก)
    /// </summary>
    public static string RoleGroupName(Guid campaignId, bool isDm) => $"campaign:{campaignId}:{(isDm ? "dm" : "player")}";

    /// <summary>
    /// Sprint G Part 10 — group ของผู้ใช้คนเดียวภายในแคมเปญ ใช้ยิง prompt ที่เป็นของคนคนนั้นจริง ๆ
    /// เช่น <c>ReactionPrompt</c> ตอนมีสิทธิ์สวน opportunity attack
    ///
    /// **ทำไมไม่ใช้ <c>Clients.User(...)</c>**: ตัวนั้นพึ่ง <c>IUserIdProvider</c> ที่ผูกกับ claim
    /// ของ JWT ซึ่งยังไม่ได้ตั้งไว้ในโปรเจกต์นี้ · การทำเป็น group เพิ่มอีกชั้นใช้กลไกเดียวกับ
    /// role-group ที่มีอยู่แล้วเป๊ะ (join ตอน JoinCampaign ถอดตอน LeaveCampaign) จึงไม่มีเส้นทาง
    /// สิทธิ์ใหม่ให้พลาด — และ **ผูกกับแคมเปญด้วย** ไม่ใช่ user ล้วน ๆ เพื่อไม่ให้ event ของแคมเปญ
    /// หนึ่งรั่วไปหาคนเดียวกันที่กำลังเปิดอีกแคมเปญอยู่
    /// </summary>
    public static string UserGroupName(Guid campaignId, Guid userId) => $"campaign:{campaignId}:user:{userId}";

    /// <summary>
    /// join group ได้เฉพาะสมาชิกแคมเปญจริงเท่านั้น — ไม่ใช่สมาชิก/แคมเปญไม่มีจริง โยน HubException
    /// ข้อความเดียวกันทั้งคู่ (เหมือน REST 404 เดียวกันสองเคส) กัน enumeration ผ่านข้อความ error
    ///
    /// เข้า role-group ด้วยตอน join เลย (ไม่ใช่ join แยกอีกครั้ง) — นิยาม "DM" ใช้
    /// <c>campaign.OwnerUserId == callerUserId</c> ชั้น C# ตัวเดียวกับที่ REST endpoint ทั้งหมดใช้
    /// (ดู doc comment ของ <see cref="DnDEz.Api.Auth.CampaignAuthorization"/> — สองชั้นนิยาม DM ที่
    /// ต้องตรงกันโดยไม่อ้างอิงกันตรง ๆ)
    /// </summary>
    public async Task JoinCampaign(Guid campaignId)
    {
        var userId = Context.User?.GetUserId();
        if (userId is null)
        {
            throw new HubException("unauthorized");
        }

        var membership = await GetMembershipAsync(campaignId, userId.Value);
        if (membership is null)
        {
            throw new HubException("campaign_not_found");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(campaignId));
        await Groups.AddToGroupAsync(Context.ConnectionId, RoleGroupName(campaignId, membership.Value));
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(campaignId, userId.Value));
        registry.Add(Context.ConnectionId, campaignId, userId.Value);
    }

    public async Task LeaveCampaign(Guid campaignId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(campaignId));
        // ไม่รู้ role ตอน leave โดยไม่ query DB ซ้ำ — ถอดออกจากทั้งสอง role-group ไปเลย (ถอดกลุ่มที่
        // ไม่ได้อยู่เป็น no-op ปลอดภัย ไม่ error)
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoleGroupName(campaignId, isDm: true));
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoleGroupName(campaignId, isDm: false));
        registry.Remove(Context.ConnectionId, campaignId);
    }

    /// <summary>
    /// Sprint G Part 5 (design doc §14.1/§14.2) — broadcast ตำแหน่ง "ghost" ระหว่างลาก token เท่านั้น
    /// **ไม่แตะ DB/Redis เลย ไม่มีอำนาจ ไม่ persist** — ใครเห็นก็แค่ preview ที่ยังไม่ใช่ความจริง
    /// ตำแหน่งจริงเกิดจาก <c>PATCH .../tokens/{tokenId}</c> ตอนปล่อยเมาส์เท่านั้น (คนละ endpoint)
    ///
    /// เช็คสิทธิ์ผ่าน <see cref="CampaignGroupRegistry.Contains"/> (in-memory) ไม่ query DB — เพราะ
    /// เมธอดนี้ถูกเรียกถี่มากระหว่างลาก (หลายเฟรมต่อวินาที) การเช็ค DB ทุกครั้งจะขัดจุดประสงค์ของ
    /// ephemeral broadcast ทั้งหมด (ต้องเร็ว ไม่ต้องแม่นเป๊ะ — ผิดพลาดชั่วขณะไม่มีผลเพราะไม่ persist)
    /// ไม่เช็คด้วยว่าเป็นเจ้าของ token ตัวนั้นจริงไหม (ตั้งใจ — ผลของ broadcast ผิดคือคนอื่นเห็น ghost
    /// เคลื่อนผิดที่ชั่วขณะ ไม่ใช่ข้อมูลรั่ว/เขียนทับอะไรจริง ต่างจาก PATCH ที่เช็คสิทธิ์เต็มเสมอ)
    /// </summary>
    public Task MoveTokenGhost(Guid campaignId, Guid sceneId, Guid tokenId, double x, double y)
    {
        if (!registry.Contains(Context.ConnectionId, campaignId))
        {
            throw new HubException("not_in_campaign_group");
        }

        return Clients.OthersInGroup(GroupName(campaignId))
            .SendAsync("TokenGhostMoved", new { SceneId = sceneId, TokenId = tokenId, X = x, Y = y });
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

    private async Task<bool?> GetMembershipAsync(Guid campaignId, Guid userId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DnDEzDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {userId.ToString()}, true)");

        // แถวหาย (ไม่ว่าเพราะไม่มีอยู่จริงหรือไม่ใช่สมาชิก) = RLS กรองออกเหมือนกัน — ไม่ต้องแยกเคส
        // เหมือน CampaignVisibility.IsVisibleAsync ที่ REST endpoint ใช้
        var ownerUserId = await db.Campaigns
            .Where(c => c.Id == campaignId)
            .Select(c => (Guid?)c.OwnerUserId)
            .SingleOrDefaultAsync();

        await transaction.CommitAsync();
        return ownerUserId is null ? null : (ownerUserId == userId);
    }
}
