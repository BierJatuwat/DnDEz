// Sprint A Part 1 — plumbing (RLS fail-closed diagnostics)
// Sprint A Part 2 — auth จริง: JWT bearer (HS256) + PasswordHasher<User> + middleware ที่เปิด
//   transaction เดียวคลุมทั้ง request แล้ว SET LOCAL app.user_id / app.campaign_id ให้ RLS ใช้
//   (ดู AuthTests.cs + CrossCampaignRlsMiddlewareTests.cs สำหรับ contract เต็ม)
//
// ⚠️ หลักที่ห้ามพัง: การกรองสิทธิ์เป็นหน้าที่ของ RLS policy ที่ชั้น DB ไม่ใช่ WHERE ในโค้ด C#
//   endpoint จึง query แบบตรงไปตรงมาแล้วปล่อยให้ policy กรองแถวออกเอง (แถวหาย = 404 ไม่ใช่ 403)
using System.Security.Claims;
using DnDEz.Api.Auth;
using DnDEz.Api.Endpoints;
using DnDEz.Api.Realtime;
using DnDEz.Api.Security;
using DnDEz.Domain.Entities;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Sprint C Part 1 — hub path ที่ query-string JWT (ด้านล่าง) กับ MapHub ต้องตรงกันเป๊ะ ประกาศ
// ค่าเดียวไว้ตรงนี้กันพิมพ์ผิดแยกกันสองที่
const string CampaignHubPath = "/hubs/campaign";

// role runtime ของ API ต้องเป็น dndez_app (ไม่ใช่ dndez ซึ่งเป็น owner ที่ bypass RLS)
// แยก env var จาก DNDEZ_DB ที่ DnDEzDbContextFactory ใช้ตอน design-time โดยตั้งใจ (คนละ concern)
var appDbConnectionString = Environment.GetEnvironmentVariable("DNDEZ_APP_DB")
    ?? "Host=localhost;Port=5432;Database=dndez;Username=dndez_app;Password=dndez_app";

builder.Services.AddDbContext<DnDEzDbContext>(options =>
    options.UseNpgsql(appDbConnectionString, o => o.UseVector())
        .UseSnakeCaseNamingConvention());

// ── Sprint C Part 1: SignalR + Redis backplane ─────────────────────────────
// ต่อ backplane ตั้งแต่ตอนนี้แม้รัน instance เดียว (ล็อกไว้ใน PROJECT-CONTEXT §4 "Sprint C" —
// ถูกตอนนี้ แพงถ้าย้อนแก้ทีหลังตอน scale-out หลาย instance)
var redisConnectionString = Environment.GetEnvironmentVariable("DNDEZ_REDIS")
    ?? "localhost:6379";
builder.Services.AddSignalR().AddStackExchangeRedis(redisConnectionString);

// ── Sprint D Part 1: BYOK — Data Protection key ring ────────────────────────
// key ring เก็บใน Redis ตัวเดียวกับ SignalR backplane (multiplexer แยกต่างหาก — SignalR ผูก
// connection ของตัวเองไว้ภายใน AddStackExchangeRedis ไม่ได้ expose ให้ share ใช้ซ้ำ) เหตุผลเดียวกับ
// Sprint C: ถูกตอนนี้ แพงถ้าย้อนแก้ทีหลัง (ต่างกันตรงนี้ยิ่งจริงกว่าเดิม — คีย์ที่เข้ารหัส ApiKey
// ไว้ก่อนหน้าจะ decrypt ไม่ได้อีกเลยถ้า key ring หายหรือย้ายที่เก็บแบบไม่ migrate ก่อน)
var dataProtectionRedis = ConnectionMultiplexer.Connect(redisConnectionString);
var dataProtectionBuilder = builder.Services
    .AddDataProtection()
    .SetApplicationName("DnDEz")
    .PersistKeysToStackExchangeRedis(dataProtectionRedis, "DnDEz-DataProtection-Keys");

// นอก Development ต้องมี cert ป้องกัน key ring เอง ไม่งั้น key-encryption-key จะไม่ถูกเข้ารหัสเลย
// บน Linux (ดูเหตุผลเต็มใน DataProtectionCertificateResolver) — fail-fast ตอน startup เหมือน
// JwtSigningKeyResolver ด้านล่าง
var dataProtectionCertificate = DataProtectionCertificateResolver.Resolve(builder.Environment);
if (dataProtectionCertificate is not null)
{
    dataProtectionBuilder.ProtectKeysWithCertificate(dataProtectionCertificate);
}

// ── Auth wiring ───────────────────────────────────────────────────────────
// key ถูก resolve ครั้งเดียวตอน startup: ถ้าไม่ใช่ Development แล้วไม่ได้ตั้ง DNDEZ_JWT_SIGNING_KEY
// จะ throw ที่นี่เลย (fail fast) ดีกว่าปล่อยให้แอปขึ้นแล้ว sign ด้วย key ที่อยู่ใน git
var jwtSigningKey = JwtSigningKeyResolver.Resolve(builder.Environment);

builder.Services.AddSingleton(new JwtTokenIssuer(jwtSigningKey));

// instance เดียวใช้ทั้งแอป — สร้างเองแทน AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>()
// เพราะต้องใช้ตัวเดียวกันนี้คำนวณ dummy hash ด้านล่าง (ค่า work factor ต้องตรงกันเป๊ะ ไม่งั้น
// การ verify หลอกกัน timing attack จะใช้เวลาไม่เท่าของจริง = ยังรั่วอยู่ดี)
var passwordHasher = new PasswordHasher<User>();
builder.Services.AddSingleton<IPasswordHasher<User>>(passwordHasher);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // ปิด inbound claim mapping — อยากให้ "sub" ยังเป็น "sub" ไม่ถูกแปลงเงียบ ๆ
        // (ClaimsPrincipalExtensions.GetUserId รองรับทั้งสองรูปแบบอยู่แล้ว)
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = JwtTokenIssuer.Issuer,
            ValidateAudience = true,
            ValidAudience = JwtTokenIssuer.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = jwtSigningKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),

            // ⚠️ ล็อก algorithm ไว้ตัวเดียว — กัน algorithm-confusion (เช่นโทเคนที่อ้าง alg=none
            // หรือสลับไปใช้ RS256 แล้วเอา public key มาเป็น HMAC secret)
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
        };

        // SignalR/WebSocket: browser ตั้ง header กำหนดเองตอน handshake ไม่ได้ (WebSocket API ไม่ให้
        // custom header) จึงต้องรับ token จาก query string แทนสำหรับ hub path นี้เท่านั้น — ไม่ใช่
        // relax ทั้งระบบ (endpoint REST อื่นทุกตัวยังคงต้องใช้ Authorization header ปกติเท่านั้น)
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken)
                    && context.HttpContext.Request.Path.StartsWithSegments(CampaignHubPath))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// hash หลอกสำหรับเคส "ไม่พบ email" — ทำให้ /auth/login ใช้เวลาใกล้เคียงกันทั้งเคสเจอและไม่เจอ user
// (ถ้าไม่ทำ เคสไม่เจอจะคืนทันทีโดยไม่ verify = วัดเวลาแล้วเดาได้ว่า email ไหนมีอยู่จริง)
var dummyUserForTiming = new User
{
    Id = Guid.Empty,
    Email = string.Empty,
    DisplayName = string.Empty,
    PasswordHash = string.Empty,
};
var dummyPasswordHashForTiming =
    passwordHasher.HashPassword(dummyUserForTiming, "not-a-real-password-only-for-timing");

// body เดียวกันเป๊ะทุกเคสที่ credential ไม่ผ่าน (email ไม่มีจริง / password ผิด) — กัน user enumeration
// ผ่านความต่างของข้อความ error ซึ่งเป็นช่องที่คนมองข้ามบ่อยที่สุด
static IResult InvalidCredentials() =>
    Results.Json(new AuthErrorResponse("invalid_credentials"), statusCode: StatusCodes.Status401Unauthorized);

app.UseAuthentication();
app.UseAuthorization();

// ── RLS session-context middleware ────────────────────────────────────────
// วางหลัง UseAuthentication/UseAuthorization โดยเจตนา 2 เหตุผล:
//   1. ต้องเห็น context.User ที่ authenticate เสร็จแล้วถึงจะรู้ว่า SET LOCAL app.user_id เป็นใคร
//   2. request ที่ไม่ผ่าน authorization จะถูกตัดตั้งแต่ชั้นบน ไม่กินคอนเนกชัน DB เปล่า ๆ
//
// ⚠️ ทำไมต้อง "transaction เดียวคลุมทั้ง request": set_config(..., is_local => true) มีอายุผูกกับ
// transaction ไม่ใช่ connection ถ้าปล่อยให้ EF Core เปิด implicit transaction ต่อ query ค่าที่ตั้งไว้
// จะหายไปทันทีที่ query แรกจบ แล้ว query ถัดไปในรีเควสต์เดียวกันจะรันแบบ "ไม่มี context" = RLS
// กรองทุกแถวทิ้ง (fail closed) — และในทางกลับกัน การผูกกับ transaction คือสิ่งที่รับประกันว่าค่าจะถูก
// ล้างตอน commit/rollback เสมอ จึงไม่หลุดข้าม request แม้ connection ถูก reuse จาก pool
app.Use(async (context, next) =>
{
    // ไม่มี endpoint = 404 จาก routing ยังไม่ต้องแตะ DB (กันคนสแกน path มั่ว ๆ ดูดคอนเนกชันจนหมด pool)
    if (context.GetEndpoint() is null)
    {
        await next(context);
        return;
    }

    var db = context.RequestServices.GetRequiredService<DnDEzDbContext>();
    await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);

    // ค่าทั้งสองส่งเป็น parameter เสมอ (ExecuteSqlInterpolated* = parameterized ไม่ใช่ string concat)
    // ต่อให้เป็น Guid ที่ parse แล้วก็ไม่ต่อสตริงเข้า SQL — กันนิสัยเสียที่จะลามไปที่อื่น
    var userId = context.User.GetUserId();
    if (userId is not null)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {userId.Value.ToString()}, true)", context.RequestAborted);
    }

    // campaignId มาจาก route ที่ constraint :guid กรองมาแล้ว แต่ยัง TryParse ซ้ำ เพราะ middleware นี้
    // ต้องทนกับ route อื่นในอนาคตที่อาจตั้งชื่อ campaignId โดยไม่ผูก constraint
    if (context.Request.RouteValues.TryGetValue("campaignId", out var rawCampaignId)
        && Guid.TryParse(rawCampaignId?.ToString(), out var campaignId))
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.campaign_id', {campaignId.ToString()}, true)", context.RequestAborted);
    }

    try
    {
        await next(context);
        await transaction.CommitAsync(CancellationToken.None);
    }
    catch
    {
        await transaction.RollbackAsync(CancellationToken.None);
        throw;
    }
});

// ── Auth endpoints ────────────────────────────────────────────────────────
app.MapPost("/auth/register", async (
    RegisterRequest request,
    DnDEzDbContext db,
    IPasswordHasher<User> hasher,
    JwtTokenIssuer issuer) =>
{
    var email = request.Email?.Trim().ToLowerInvariant();
    var password = request.Password;

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
    {
        return Results.BadRequest(new AuthErrorResponse("email_and_password_required"));
    }

    if (password.Length < 8)
    {
        return Results.BadRequest(new AuthErrorResponse("password_too_short"));
    }

    var user = new User
    {
        Id = Guid.NewGuid(),
        Email = email,
        DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email : request.DisplayName.Trim(),
        CreatedAt = DateTimeOffset.UtcNow,
    };
    // hash ก่อนแตะ DB เสมอ — plaintext ต้องไม่มีทางไปโผล่ในคอลัมน์ password_hash ได้เลย
    user.PasswordHash = hasher.HashPassword(user, password);

    db.Users.Add(user);

    // savepoint เพราะ insert นี้อยู่ในทรานแซกชันของ middleware ที่คลุมทั้ง request อยู่แล้ว:
    // ถ้าชน unique index ของ email แล้วปล่อยให้ error ลอยขึ้นไป Postgres จะ abort ทั้งทรานแซกชัน
    // ทำให้ commit ตอนท้ายพังตามไปด้วย (500 ทั้งที่จริง ๆ แค่ email ซ้ำ)
    var currentTransaction = db.Database.CurrentTransaction;
    const string savepoint = "register_user";
    if (currentTransaction is not null)
    {
        await currentTransaction.CreateSavepointAsync(savepoint);
    }

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex)
    {
        if (currentTransaction is not null)
        {
            await currentTransaction.RollbackToSavepointAsync(savepoint);
        }

        db.ChangeTracker.Clear();

        if (ex.InnerException is not Npgsql.PostgresException { SqlState: "23505" })
        {
            // ไม่ใช่ unique-violation ของ email — อย่าจัดผิดประเภทเป็น "email ซ้ำ"
            // ปล่อยให้ ASP.NET Core จัดการเป็น unhandled exception (500) ตามปกติ
            throw;
        }

        // ยอมรับตรง ๆ ว่า endpoint สมัครสมาชิกบอกได้ว่า email นี้ถูกใช้แล้ว (enumeration surface ที่
        // เลี่ยงไม่ได้ถ้าไม่มี flow ยืนยันอีเมล) — ต่างจาก /auth/login ที่ปิดสนิทเพราะปิดได้จริง
        return Results.Conflict(new AuthErrorResponse("email_already_registered"));
    }

    return Results.Ok(new AuthResponse(issuer.Issue(user.Id), user.Id));
});

app.MapPost("/auth/login", async (
    LoginRequest request,
    DnDEzDbContext db,
    IPasswordHasher<User> hasher,
    JwtTokenIssuer issuer) =>
{
    var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
    var password = request.Password ?? string.Empty;

    var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);

    if (user is null)
    {
        // เผาเวลาเท่าของจริงก่อนคืน 401 (ดู dummyPasswordHashForTiming ด้านบน)
        hasher.VerifyHashedPassword(dummyUserForTiming, dummyPasswordHashForTiming, password);
        return InvalidCredentials();
    }

    PasswordVerificationResult verification;
    try
    {
        verification = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
    }
    catch (FormatException)
    {
        // แถวที่ password_hash ไม่ใช่รูปแบบของ PasswordHasher (เช่น fixture ที่ seed ตรงเข้า DB)
        // ต้องเป็น "login ไม่ผ่าน" ไม่ใช่ 500 ที่บอกใบ้ว่าแถวนี้มีอยู่จริง
        return InvalidCredentials();
    }

    if (verification == PasswordVerificationResult.Failed)
    {
        return InvalidCredentials();
    }

    if (verification == PasswordVerificationResult.SuccessRehashNeeded)
    {
        user.PasswordHash = hasher.HashPassword(user, password);
        await db.SaveChangesAsync();
    }

    return Results.Ok(new AuthResponse(issuer.Issue(user.Id), user.Id));
});

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

    // พิสูจน์ว่า middleware ตั้ง app.user_id/app.campaign_id ให้ RLS ได้จริงผ่าน HTTP จริง
    // ⚠️ ห้ามเติม WHERE เช็คสมาชิกภาพลงใน query นี้เด็ดขาด — จุดประสงค์ทั้งหมดของมันคือพิสูจน์ว่า
    // policy ที่ชั้น DB กรองแถวออกเองได้ ถ้าเติม WHERE เอง เทสต์จะผ่านทั้งที่ RLS อาจพังอยู่
    app.MapGet("/campaigns/{campaignId:guid}/diagnostics/whoami", async (
        Guid campaignId,
        ClaimsPrincipal principal,
        DnDEzDbContext db) =>
    {
        // โทเคน valid แต่ไม่มี claim user id ที่ parse เป็น Guid ได้ = ปฏิเสธ ห้ามเดาต่อ
        var userId = principal.GetUserId();
        if (userId is null)
        {
            return Results.Unauthorized();
        }

        var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);

        // แถวหายเพราะ RLS = 404 เปล่า ๆ ไม่ใช่ 403 — 403 จะยืนยันกลาย ๆ ว่าแคมเปญนี้มีอยู่จริง
        return campaign is null
            ? Results.NotFound()
            : Results.Ok(new WhoAmIResponse(campaign.Id, campaign.Name, userId.Value));
    }).RequireAuthorization();
}

// ── Sprint A Part 3: thin CRUD (campaign / session / character) ───────────
// แยกออกเป็นไฟล์ละ entity ใน DnDEz.Api/Endpoints เพื่อไม่ให้ Program.cs (auth + middleware ที่ห้ามแตะ)
// บวมจนอ่านไม่ออก — ทุกตัวถูกลงทะเบียนหลัง middleware ที่ตั้ง app.user_id/app.campaign_id แล้ว
app.MapCampaignEndpoints();
app.MapSessionEndpoints();
app.MapCharacterEndpoints();

// ── Sprint B Part 1: ต่อ DnDEz.Rules (rules engine) เข้า API จริงเป็นครั้งแรก ─
app.MapDiceRollEndpoints();

// ── Sprint B Part 3: check/attack/damage resolution ────────────────────────
app.MapCombatEndpoints();

// ── Sprint C Part 1: realtime hub (ยังไม่ broadcast อะไรจริง — Part 2/3 wire เข้า endpoint เดิม) ──
app.MapHub<CampaignHub>(CampaignHubPath).RequireAuthorization();

// ── Sprint D Part 1: BYOK — เก็บ/ดึง/ลบ API key เข้ารหัสของผู้ใช้ (ยังไม่ validate ผ่าน live call
// ไปหา provider จริง — เก็บไว้ Part 2 ที่ต้องต่อ Anthropic API client อยู่แล้ว) ────────────────
app.MapApiKeyEndpoints();

app.Run();

// ให้ WebApplicationFactory<Program> ใน integration test มองเห็นคลาสนี้ได้
public partial class Program { }

internal record RlsFailClosedCheckResponse(int CampaignRowCount);

internal record DbConnectivityResponse(bool Connected);

// ── Auth contracts ────────────────────────────────────────────────────────
// nullable ทุกช่องโดยเจตนา: body ที่ client ส่งมาเป็น input จากภายนอก จะขาดฟิลด์ก็ได้
// การ validate เป็นหน้าที่ของ handler ไม่ใช่ปล่อยให้ binder โยน 400 แบบที่คุมข้อความไม่ได้
internal record RegisterRequest(string? Email, string? Password, string? DisplayName);

internal record LoginRequest(string? Email, string? Password);

internal record AuthResponse(string Token, Guid UserId);

/// <summary>ห้ามใส่รายละเอียดที่แยกแยะเคสได้ลงใน Error (ดู InvalidCredentials ใน Program.cs)</summary>
internal record AuthErrorResponse(string Error);

internal record WhoAmIResponse(Guid CampaignId, string CampaignName, Guid UserId);
