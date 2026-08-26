using System.Text.Json;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using DnDEz.Rules;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// Sprint B Part 3 — check/attack/damage resolution: จุดแรกที่เรียก CheckResolver/AttackResolver/
/// DamageResolver/HitPointResolver ต่อกันเป็น flow เดียว (Part 1 มีแค่ DiceEngine เดี่ยว ๆ)
///
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/CombatResolutionTests.cs</c> — ห้ามแก้เทสต์เพื่อให้ผ่าน
///
/// จุดที่ห้ามพลาด:
///  • รองรับแค่ DiceMode.SystemRoll เหมือน Part 1 — DmEntered ต้อง 400 ชัดเจน (เหตุผลเดียวกัน)
///  • seed generate ใหม่จาก CSPRNG ทุกครั้งต่อ "หนึ่งเหตุการณ์ทอย" (attack roll กับ damage roll คนละ
///    roller/seed กัน) แต่ **ไม่โผล่ใน response เด็ดขาด** เหมือน Part 1
///  • validate ทุกอย่างที่ตรวจได้แบบ pure (parse expression, damage type, DC/target ครบ) ก่อนแตะ DB
///    หรือทอยลูกเต๋าใด ๆ เสมอ — กัน "ทอย attack ไปแล้วแต่ damage component ผิด format" ทำให้ transaction
///    ต้อง rollback ทั้งก้อนหลังทอยไปแล้วโดยไม่จำเป็น
///  • damage component ทอย + หัก resistance เฉพาะตอน hit/critical เท่านั้น — miss/automatic-miss
///    ไม่แตะ DamageResolver/HitPointResolver เลย (ไม่มี damage roll เกิดขึ้นจริง ไม่ใช่แค่ = 0)
///  • target ที่ current_hp/current_temp_hp เปลี่ยน ต้องเป็นแถวเดียวกับที่ query มา (EF change tracking)
///    ไม่ใช่สร้าง Character ใหม่แล้ว Update() ทับ (จะเขียนทับคอลัมน์อื่นที่ไม่ได้ query มาด้วย)
///  • endpoint นี้เป็น action/RPC (resolve แล้วจบ) ไม่ใช่ resource creation ตรง ๆ — ไม่มี GET ให้ชี้
///    Location กลับไปได้ (ต่างจาก dice-rolls ที่มี pattern REST ปกติ) จึงตอบ 200 OK ไม่ใช่ 201 Created
/// </summary>
public static class CombatEndpoints
{
    private const int MinDc = 1;
    private const int MaxDc = 40;
    private const int MaxDamageComponents = 20;

    public static void MapCombatEndpoints(this WebApplication app)
    {
        app.MapPost("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/characters/{characterId:guid}/checks", async (
            Guid campaignId,
            Guid sessionId,
            Guid characterId,
            CreateCheckRequest request,
            DnDEzDbContext db) =>
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

            if (request.Dc is null || request.Dc < MinDc || request.Dc > MaxDc)
            {
                return Results.BadRequest();
            }

            var dc = request.Dc.Value;

            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null)
            {
                return Results.NotFound();
            }

            var sessionExists = await db.Sessions.AnyAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
            if (!sessionExists)
            {
                return Results.NotFound();
            }

            var characterExists = await db.Characters
                .AnyAsync(c => c.Id == characterId && c.CampaignId == campaignId);
            if (!characterExists)
            {
                return Results.NotFound();
            }

            if (campaign.DiceMode != DiceMode.SystemRoll)
            {
                return Results.BadRequest();
            }

            RollResult roll;
            var roller = SeededDiceRoller.CreateWithSecureRandomSeed();
            try
            {
                var engine = new DiceEngine();
                roll = engine.RollSystem(request.Expression, roller, roller.Seed, advantage);
            }
            catch (DiceExpressionException)
            {
                return Results.BadRequest();
            }

            var result = CheckResolver.Resolve(roll, dc);

            var diceRoll = new DiceRoll
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                CharacterId = characterId,
                Expression = roll.Expression,
                Rolls = JsonSerializer.Serialize(roll.Rolls),
                Total = roll.Total,
                Source = DiceMode.SystemRoll,
                Seed = roller.Seed,
                EnteredByUserId = null,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            db.DiceRolls.Add(diceRoll);
            await db.SaveChangesAsync();

            return Results.Ok(new CheckResolutionResponse(
                diceRoll.Id,
                characterId,
                roll.Expression,
                roll.Rolls.Select(r => new RollDetailResponse(r.Sides, r.Value, r.Dropped, r.Sign)).ToList(),
                roll.Total,
                dc,
                result.Success));
        }).RequireAuthorization();

        app.MapPost("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/characters/{characterId:guid}/attacks", async (
            Guid campaignId,
            Guid sessionId,
            Guid characterId,
            CreateAttackRequest request,
            DnDEzDbContext db) =>
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

            if (request.TargetCharacterId is null || request.TargetCharacterId == Guid.Empty)
            {
                return Results.BadRequest();
            }

            var targetCharacterId = request.TargetCharacterId.Value;

            if (request.DamageComponents is null
                || request.DamageComponents.Count == 0
                || request.DamageComponents.Count > MaxDamageComponents)
            {
                return Results.BadRequest();
            }

            // validate ทั้ง attack expression และทุก damage component แบบ pure (parse อย่างเดียว
            // ไม่ทอย) ก่อนแตะ DB หรือ CSPRNG ใด ๆ เลย — ตามหลักที่ล็อกไว้ด้านบน
            try
            {
                DiceExpressionParser.Parse(request.Expression);
            }
            catch (DiceExpressionException)
            {
                return Results.BadRequest();
            }

            var damageComponents = new List<DamageComponent>(request.DamageComponents.Count);
            foreach (var component in request.DamageComponents)
            {
                if (string.IsNullOrWhiteSpace(component.Expression)
                    || !Enum.TryParse<DamageType>(component.Type, out var damageType))
                {
                    return Results.BadRequest();
                }

                try
                {
                    DiceExpressionParser.Parse(component.Expression);
                }
                catch (DiceExpressionException)
                {
                    return Results.BadRequest();
                }

                damageComponents.Add(new DamageComponent(component.Expression, damageType));
            }

            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null)
            {
                return Results.NotFound();
            }

            var sessionExists = await db.Sessions.AnyAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
            if (!sessionExists)
            {
                return Results.NotFound();
            }

            var attackerExists = await db.Characters
                .AnyAsync(c => c.Id == characterId && c.CampaignId == campaignId);
            if (!attackerExists)
            {
                return Results.NotFound();
            }

            var target = await db.Characters
                .SingleOrDefaultAsync(c => c.Id == targetCharacterId && c.CampaignId == campaignId);
            if (target is null)
            {
                return Results.NotFound();
            }

            if (campaign.DiceMode != DiceMode.SystemRoll)
            {
                return Results.BadRequest();
            }

            var engine = new DiceEngine();
            RollResult attackRoll;
            var attackRoller = SeededDiceRoller.CreateWithSecureRandomSeed();
            try
            {
                attackRoll = engine.RollSystem(request.Expression, attackRoller, attackRoller.Seed, advantage);
            }
            catch (DiceExpressionException)
            {
                return Results.BadRequest();
            }

            AttackResult attackResult;
            try
            {
                attackResult = AttackResolver.Resolve(attackRoll, target.ArmorClass);
            }
            catch (InvalidOperationException)
            {
                // expression ไม่มี d20 เดี่ยวที่ไม่ถูก drop — attack roll ที่ไม่ถูกรูปแบบ
                return Results.BadRequest();
            }

            var attackDiceRoll = new DiceRoll
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                CharacterId = characterId,
                Expression = attackRoll.Expression,
                Rolls = JsonSerializer.Serialize(attackRoll.Rolls),
                Total = attackRoll.Total,
                Source = DiceMode.SystemRoll,
                Seed = attackRoller.Seed,
                EnteredByUserId = null,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.DiceRolls.Add(attackDiceRoll);

            IReadOnlyList<DamageComponentResponse>? damageComponentResponses = null;
            int? totalDamage = null;
            int? targetPreviousHp = null;
            int? targetNewHp = null;
            int? targetPreviousTempHp = null;
            int? targetNewTempHp = null;
            bool? targetDroppedToZero = null;
            bool? targetIsInstantDeath = null;

            if (attackResult.IsHit)
            {
                var isCritical = attackResult.Outcome == AttackOutcome.CriticalHit;
                var resistances = target.DamageResistances.Select(Enum.Parse<DamageType>).ToHashSet();
                var vulnerabilities = target.DamageVulnerabilities.Select(Enum.Parse<DamageType>).ToHashSet();
                var immunities = target.DamageImmunities.Select(Enum.Parse<DamageType>).ToHashSet();

                var damageRoller = SeededDiceRoller.CreateWithSecureRandomSeed();
                var damageResult = DamageResolver.RollAndResolve(
                    engine, damageComponents, damageRoller, isCritical,
                    resistances, vulnerabilities, immunities, damageRoller.Seed);

                var applyResult = HitPointResolver.ApplyDamage(
                    target.CurrentHp, target.MaxHp, target.CurrentTempHp, damageResult.TotalAdjustedAmount);

                target.CurrentHp = applyResult.NewHp;
                target.CurrentTempHp = applyResult.NewTempHp;

                foreach (var componentResult in damageResult.Components)
                {
                    db.DiceRolls.Add(new DiceRoll
                    {
                        Id = Guid.NewGuid(),
                        SessionId = sessionId,
                        CharacterId = characterId,
                        Expression = componentResult.Roll.Expression,
                        Rolls = JsonSerializer.Serialize(componentResult.Roll.Rolls),
                        Total = componentResult.Roll.Total,
                        Source = DiceMode.SystemRoll,
                        Seed = damageRoller.Seed,
                        EnteredByUserId = null,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                }

                damageComponentResponses = damageResult.Components.Select(c => new DamageComponentResponse(
                    c.Type.ToString(),
                    c.RawAmount,
                    c.AdjustedAmount,
                    c.Roll.Rolls.Select(r => new RollDetailResponse(r.Sides, r.Value, r.Dropped, r.Sign)).ToList()))
                    .ToList();
                totalDamage = damageResult.TotalAdjustedAmount;
                targetPreviousHp = applyResult.PreviousHp;
                targetNewHp = applyResult.NewHp;
                targetPreviousTempHp = applyResult.PreviousTempHp;
                targetNewTempHp = applyResult.NewTempHp;
                targetDroppedToZero = applyResult.DroppedToZero;
                targetIsInstantDeath = applyResult.IsInstantDeath;
            }

            await db.SaveChangesAsync();

            return Results.Ok(new AttackResolutionResponse(
                attackDiceRoll.Id,
                characterId,
                targetCharacterId,
                attackRoll.Expression,
                attackRoll.Rolls.Select(r => new RollDetailResponse(r.Sides, r.Value, r.Dropped, r.Sign)).ToList(),
                attackRoll.Total,
                target.ArmorClass,
                attackResult.Outcome.ToString(),
                attackResult.IsHit,
                damageComponentResponses,
                totalDamage,
                targetPreviousHp,
                targetNewHp,
                targetPreviousTempHp,
                targetNewTempHp,
                targetDroppedToZero,
                targetIsInstantDeath));
        }).RequireAuthorization();
    }
}

internal record CreateCheckRequest(string? Expression, string? Advantage, int? Dc);

internal record RollDetailResponse(int Sides, int Value, bool Dropped, int Sign);

internal record CheckResolutionResponse(
    Guid Id,
    Guid CharacterId,
    string Expression,
    IReadOnlyList<RollDetailResponse> Rolls,
    int Total,
    int Dc,
    bool Success);

internal record DamageComponentRequest(string? Expression, string? Type);

internal record CreateAttackRequest(
    string? Expression,
    string? Advantage,
    Guid? TargetCharacterId,
    List<DamageComponentRequest>? DamageComponents);

internal record DamageComponentResponse(
    string Type,
    int RawAmount,
    int AdjustedAmount,
    IReadOnlyList<RollDetailResponse> Rolls);

internal record AttackResolutionResponse(
    Guid Id,
    Guid AttackerCharacterId,
    Guid TargetCharacterId,
    string Expression,
    IReadOnlyList<RollDetailResponse> Rolls,
    int Total,
    int TargetArmorClass,
    string Outcome,
    bool Hit,
    IReadOnlyList<DamageComponentResponse>? DamageComponents,
    int? TotalDamage,
    int? TargetPreviousHp,
    int? TargetNewHp,
    int? TargetPreviousTempHp,
    int? TargetNewTempHp,
    bool? TargetDroppedToZero,
    bool? TargetIsInstantDeath);
