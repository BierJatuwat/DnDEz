using System.Text.Json;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using DnDEz.Rules;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint B Part 1 — endpoint แรกที่ต่อ DnDEz.Rules เข้า API จริง (dice roll เท่านั้น)
///
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/DiceRollCrudTests.cs</c> — ห้ามแก้เทสต์เพื่อให้ผ่าน
///
/// จุดที่ห้ามพลาด:
///  • รองรับแค่ campaign ที่ DiceMode=SystemRoll เท่านั้น — DmEntered ต้อง 400 ชัดเจน (defer ไม่ทำ
///    silent fallback เป็น SystemRoll เพราะขัดกับ anti-cheat ที่ล็อกไว้ใน player-side-design §5)
///  • seed ต้อง generate ใหม่จาก CSPRNG ทุกครั้ง (`SeededDiceRoller.CreateWithSecureRandomSeed()`)
///    ตามกฎที่ล็อกไว้ใน PROJECT-CONTEXT §3 — ห้ามใช้ roller ตัวเดียวข้ามหลายการทอย
///  • seed เก็บลง DB จริง (ck_dice_rolls_provenance บังคับ) แต่ **ห้ามโผล่ใน response เด็ดขาด**
///    (ล็อกไว้ใน PROJECT-CONTEXT §3 เช่นกัน — seed มีไว้ให้ DM/audit เท่านั้น)
///  • validate + เช็ค membership/session ก่อนแตะ DB เสมอ (ทั้ง request อยู่ใน transaction เดียว
///    ของ middleware — ปล่อยให้ DiceExpressionException หรือ RLS เป็นคนดักจะทำให้ 400/404 กลายเป็น 500)
/// </summary>
public static class DiceRollEndpoints
{
    public static void MapDiceRollEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/dice-rolls", async (
            Guid campaignId,
            Guid sessionId,
            CreateDiceRollRequest request,
            DnDEzDbContext db,
            IHubContext<CampaignHub> hubContext) =>
        {
            if (string.IsNullOrWhiteSpace(request.Expression))
            {
                return Results.BadRequest();
            }

            if (!Enum.TryParse<AdvantageMode>(request.Advantage ?? nameof(AdvantageMode.None), out var advantage)
                || !Enum.IsDefined(advantage))
            {
                return Results.BadRequest();
            }

            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null)
            {
                return Results.NotFound();
            }

            var sessionExists = await db.Sessions
                .AnyAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
            if (!sessionExists)
            {
                return Results.NotFound();
            }

            if (campaign.DiceMode != DiceMode.SystemRoll)
            {
                // ขอบเขต Sprint B Part 1 — DmEntered ยังไม่รองรับ ต้องปฏิเสธชัดเจนไม่ใช่ทำแทนแบบเงียบ ๆ
                return Results.BadRequest();
            }

            RollResult result;
            var roller = SeededDiceRoller.CreateWithSecureRandomSeed();
            try
            {
                var engine = new DiceEngine();
                result = engine.RollSystem(request.Expression, roller, roller.Seed, advantage);
            }
            catch (DiceExpressionException)
            {
                return Results.BadRequest();
            }

            // dice_rolls ไม่มีปัญหา circular RLS แบบ campaigns (ผ่าน app_is_member() ปกติ ไม่ต้องผ่าน
            // SECURITY DEFINER function) จึง insert ผ่าน EF Core ตรง ๆ ได้เหมือน sessions/characters
            // — DiceSource (DnDEz.Rules) กับ DiceMode (DnDEz.Domain.Enums) เป็นคนละ type แต่มีชื่อค่า
            // ตรงกันเป๊ะ ("SystemRoll"/"DmEntered") แปลงผ่าน Enum.Parse ด้วยชื่อ ไม่ hardcode ทีละเคส
            var diceRoll = new DiceRoll
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                CharacterId = null,
                Expression = result.Expression,
                Rolls = JsonSerializer.Serialize(result.Rolls),
                Total = result.Total,
                Source = Enum.Parse<DiceMode>(result.Source.ToString()),
                Seed = roller.Seed,
                EnteredByUserId = null,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            db.DiceRolls.Add(diceRoll);
            await db.SaveChangesAsync();

            await hubContext.Clients.Group(CampaignHub.GroupName(campaignId)).SendAsync(
                "DiceRolled",
                new DiceRolledBroadcast(
                    diceRoll.Id,
                    sessionId,
                    diceRoll.CharacterId,
                    result.Expression,
                    result.Rolls.Select(r => new DieRollDetailResponse(r.Sides, r.Value, r.Dropped, r.Sign)).ToList(),
                    result.Total,
                    result.Source.ToString()));

            return Results.Created(
                $"/campaigns/{campaignId}/sessions/{sessionId}/dice-rolls/{diceRoll.Id}",
                new DiceRollCreatedResponse(
                    diceRoll.Id,
                    sessionId,
                    result.Expression,
                    result.Rolls.Select(r => new DieRollDetailResponse(r.Sides, r.Value, r.Dropped, r.Sign)).ToList(),
                    result.Total,
                    result.Source.ToString()));
        }).RequireAuthorization();
    }
}

internal record CreateDiceRollRequest(string? Expression, string? Advantage);

internal record DieRollDetailResponse(int Sides, int Value, bool Dropped, int Sign);

internal record DiceRollCreatedResponse(
    Guid Id,
    Guid SessionId,
    string Expression,
    IReadOnlyList<DieRollDetailResponse> Rolls,
    int Total,
    string Source);

/// <summary>
/// Sprint C Part 2 — payload ที่ broadcast เข้า SignalR group ของแคมเปญตอนมีการทอยลูกเต๋าใหม่
/// (ดู CampaignHub.GroupName) จงใจไม่มีฟิลด์ Seed เลย — กฎเดียวกับ DiceRollCreatedResponse
/// ที่ห้าม seed โผล่ให้ client เห็นเด็ดขาด (PROJECT-CONTEXT §3)
/// </summary>
internal record DiceRolledBroadcast(
    Guid Id,
    Guid SessionId,
    Guid? CharacterId,
    string Expression,
    IReadOnlyList<DieRollDetailResponse> Rolls,
    int Total,
    string Source);
