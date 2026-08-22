// Sprint A Part 1 — plumbing เท่านั้น ยังไม่มี auth ใด ๆ (มาใน Part 2)
// ต่อ DnDEzDbContext ผ่าน role dndez_app โดยตั้งใจไม่ SET LOCAL app.user_id/app.campaign_id ใด ๆ
// เพื่อพิสูจน์ว่า RLS เป็น fail-closed (ดู RlsFailClosedTests.cs สำหรับ contract เต็ม)
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// role runtime ของ API ต้องเป็น dndez_app (ไม่ใช่ dndez ซึ่งเป็น owner ที่ bypass RLS)
// แยก env var จาก DNDEZ_DB ที่ DnDEzDbContextFactory ใช้ตอน design-time โดยตั้งใจ (คนละ concern)
var appDbConnectionString = Environment.GetEnvironmentVariable("DNDEZ_APP_DB")
    ?? "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

builder.Services.AddDbContext<DnDEzDbContext>(options =>
    options.UseNpgsql(appDbConnectionString, o => o.UseVector())
        .UseSnakeCaseNamingConvention());

var app = builder.Build();

// security review Sprint A Part 1 (M1): diagnostic endpoint รั่วจำนวนแถวโดยไม่มี auth ใด ๆ —
// ยอมรับได้ใน dev เพราะยังไม่มี auth ทั้งระบบตามแผน แต่ต้องไม่หลุดไป production/staging จริง
// ถ้าจะเปิดใน environment อื่นต้องมี auth ผ่านมาก่อนแล้ว (Part 2) — ไม่ใช่แค่ลบ guard นี้ทิ้ง
if (app.Environment.IsDevelopment())
{
    app.MapGet("/diagnostics/rls-fail-closed-check", async (DnDEzDbContext db) =>
    {
        var campaignRowCount = await db.Campaigns.CountAsync();
        return Results.Ok(new RlsFailClosedCheckResponse(campaignRowCount));
    });

    app.MapGet("/diagnostics/db-connectivity", async (DnDEzDbContext db) =>
    {
        var connected = await db.Database.CanConnectAsync();
        return Results.Ok(new DbConnectivityResponse(connected));
    });
}

app.Run();

// ให้ WebApplicationFactory<Program> ใน integration test มองเห็นคลาสนี้ได้
public partial class Program { }

internal record RlsFailClosedCheckResponse(int CampaignRowCount);

internal record DbConnectivityResponse(bool Connected);
