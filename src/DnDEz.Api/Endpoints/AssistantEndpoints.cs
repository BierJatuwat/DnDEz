using System.Security.Claims;
using System.Text.Json;
using DnDEz.Api.Assistant;
using DnDEz.Api.Auth;
using DnDEz.Api.Orchestration;
using DnDEz.Api.Security;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>
/// ผู้ช่วย DM ช่องเดียว (2026-09-23) — แทน `/advisor` (ที่ปรึกษา) และ `/consult` (ผู้ช่วยกลางเซสชัน) ด้วยช่องเดียว
/// ที่ถามข้อมูลได้และสั่งงานได้หลายขั้น (ฟีลแบบ Claude Cowork ตามที่ผู้ใช้ขอ)
///
/// DM-only ทุก endpoint · ไม่ broadcast อะไรเองเลย — สิ่งที่ผู้เล่นเห็นมาจากผลของ tool ที่ DM อนุญาตแล้วเท่านั้น
/// (กฎการขออนุญาตอยู่ที่ `AssistantTools`/`ToolGate`) · ตัว loop อยู่ใน <see cref="AssistantRunner"/>
/// </summary>
public static class AssistantEndpoints
{
    internal const int MaxContentLength = 4000;

    public static void MapAssistantEndpoints(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AssistantEndpoints));

        app.MapPost("/campaigns/{campaignId:guid}/assistant", async (
            Guid campaignId,
            AssistantRequest request,
            ClaimsPrincipal principal,
            HttpContext httpContext,
            DnDEzDbContext db,
            IDataProtectionProvider dataProtectionProvider,
            IAnthropicClient anthropicClient,
            RlsRequestTransaction requestTransaction) =>
        {
            var content = request.Content?.Trim();
            if (string.IsNullOrEmpty(content) || content.Length > MaxContentLength)
            {
                return Results.BadRequest();
            }

            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var (campaign, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            // ⚠️ IDOR: sessionId มาจาก client — ต้องอยู่ในแคมเปญนี้ ไม่งั้น tool ของเซสชันจะไปแตะโต๊ะอื่น
            if (request.SessionId is not null
                && !await db.Sessions.AnyAsync(s => s.Id == request.SessionId.Value && s.CampaignId == campaignId))
            {
                return Results.BadRequest(new AssistantErrorResponse("session_not_in_campaign"));
            }

            var rawApiKey = await DmApiKeyResolver.ResolveAsync(db, dataProtectionProvider, callerUserId.Value);
            if (rawApiKey is null)
            {
                return Results.BadRequest(new AssistantErrorResponse("dm_api_key_not_set"));
            }

            var runner = new AssistantRunner(db, anthropicClient, requestTransaction, httpContext.RequestServices, logger);
            var response = await runner.StartAsync(campaign!, callerUserId.Value, request.SessionId, content, rawApiKey);
            return Results.Ok(response);
        }).RequireAuthorization();

        app.MapPost("/campaigns/{campaignId:guid}/assistant/runs/{runId:guid}/decisions", async (
            Guid campaignId,
            Guid runId,
            AssistantDecisionRequest request,
            ClaimsPrincipal principal,
            HttpContext httpContext,
            DnDEzDbContext db,
            IDataProtectionProvider dataProtectionProvider,
            IAnthropicClient anthropicClient,
            RlsRequestTransaction requestTransaction) =>
        {
            if (string.IsNullOrEmpty(request.ToolUseId) || request.Approve is null)
            {
                return Results.BadRequest();
            }

            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var (campaign, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            var rawApiKey = await DmApiKeyResolver.ResolveAsync(db, dataProtectionProvider, callerUserId.Value);
            if (rawApiKey is null)
            {
                return Results.BadRequest(new AssistantErrorResponse("dm_api_key_not_set"));
            }

            var runner = new AssistantRunner(db, anthropicClient, requestTransaction, httpContext.RequestServices, logger);
            var (response, error) = await runner.DecideAsync(
                campaign!, callerUserId.Value, runId, request.ToolUseId, request.Approve.Value, request.EditedText, rawApiKey);

            return error switch
            {
                null => Results.Ok(response),
                "not_found" => Results.NotFound(),
                "not_awaiting_approval" => Results.Conflict(),
                _ => Results.BadRequest(new AssistantErrorResponse(error)),
            };
        }).RequireAuthorization();

        app.MapGet("/campaigns/{campaignId:guid}/assistant", async (
            Guid campaignId,
            ClaimsPrincipal principal,
            DnDEzDbContext db) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            var rows = await db.ConsultMessages.AsNoTracking()
                .Where(m => m.CampaignId == campaignId && m.DmUserId == callerUserId.Value)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new { m.Id, m.Role, m.Content, m.SessionId, m.Activity, m.CreatedAt })
                .ToListAsync();
            var messages = rows
                .Select(m => new AssistantMessageItem(
                    m.Id, m.Role.ToString(), m.Content, m.SessionId, AssistantActivityJson.Parse(m.Activity), m.CreatedAt))
                .ToList();

            var pendingRun = await db.AssistantRuns.AsNoTracking()
                .Where(r => r.CampaignId == campaignId
                    && r.DmUserId == callerUserId.Value
                    && r.Status == AssistantRunStatus.AwaitingApproval)
                .OrderByDescending(r => r.UpdatedAt)
                .FirstOrDefaultAsync();
            AssistantTurnResponse? pending = null;
            if (pendingRun is not null)
            {
                var state = JsonSerializer.Deserialize<AssistantRunState>(pendingRun.State, AssistantRunner.StateJson)!;
                pending = AssistantRunner.BuildResponse(pendingRun.Id, state, AssistantTurnStatus.AwaitingApproval, messageId: null);
            }

            return Results.Ok(new AssistantHistoryResponse(messages, pending));
        }).RequireAuthorization();
    }
}

internal record AssistantRequest(string? Content, Guid? SessionId);

/// <summary><c>EditedText</c> ใช้ได้เฉพาะการ์ดที่มีข้อความให้แก้ (tool ที่ส่งข้อความถึงผู้เล่น)</summary>
internal record AssistantDecisionRequest(string? ToolUseId, bool? Approve, string? EditedText);

internal record AssistantErrorResponse(string Error);

internal record AssistantMessageItem(
    Guid Id, string Role, string Content, Guid? SessionId, IReadOnlyList<AssistantActivity> Activity, DateTimeOffset CreatedAt);

internal record AssistantHistoryResponse(IReadOnlyList<AssistantMessageItem> Messages, AssistantTurnResponse? PendingRun);
