// Sprint A Part 1 — plumbing (RLS fail-closed diagnostics)
// Sprint A Part 2 — auth จริง: JWT bearer (HS256) + PasswordHasher<User> + middleware ที่เปิด
//   transaction เดียวคลุมทั้ง request แล้ว SET LOCAL app.user_id / app.campaign_id ให้ RLS ใช้
//   (ดู AuthTests.cs + CrossCampaignRlsMiddlewareTests.cs สำหรับ contract เต็ม)
//
// ⚠️ หลักที่ห้ามพัง: การกรองสิทธิ์เป็นหน้าที่ของ RLS policy ที่ชั้น DB ไม่ใช่ WHERE ในโค้ด C#
//   endpoint จึง query แบบตรงไปตรงมาแล้วปล่อยให้ policy กรองแถวออกเอง (แถวหาย = 404 ไม่ใช่ 403)
using System.Security.Claims;
using Amazon.S3;
using DnDEz.Api.Auth;
using DnDEz.Api.Encounters;
using DnDEz.Api.Endpoints;
using DnDEz.Api.Orchestration;
using DnDEz.Api.Realtime;
using DnDEz.Api.Security;
using DnDEz.Domain.Entities;
using DnDEz.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Sprint G Part 4 — Kestrel default MaxRequestBodySize (~30MB) ปล่อยให้ request ใหญ่กว่า endpoint
// upload ของเราเองยอมรับ (15MB) เข้ามาเต็มก้อนก่อนจะถูกปฏิเสธ (endpoint เช็ค IFormFile.Length หลัง
// body ถูก parse เสร็จแล้ว ไม่ได้ short-circuit ที่ชั้น transport) จำกัดเพดานทั้ง API ให้ใกล้กับที่
// ต้องใช้จริงแทน — ไม่มี endpoint อื่นในโปรเจกต์นี้ต้องการ body ใหญ่กว่านี้เลย (ทุกอันเป็น JSON เล็ก ๆ)
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16 * 1024 * 1024);

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

// Sprint F งานที่ 3 — registry ของ group membership + revalidator ที่สแกนซ้ำเป็นรอบ (เหตุผลเต็ม:
// Realtime/CampaignGroupMembership.cs) ทั้งสามตัวพึ่ง dependency ที่เป็น singleton ได้เท่านั้น
// (IHubContext<CampaignHub> / IServiceScopeFactory) — ห้าม inject DbContext ตรง ๆ ที่นี่
builder.Services.AddSingleton<CampaignGroupRegistry>();
builder.Services.AddSingleton<CampaignGroupRevalidator>();
builder.Services.AddHostedService<CampaignGroupRevalidationService>();

// ── Sprint G Part 5: token position — write-coalescing ผ่าน Redis + flush เข้า Postgres จริง
// (design doc §14.3) ใช้ IConnectionMultiplexer ตัวเดียวกับ IEncounterTurnStateStore ด้านล่าง
// (AbortOnConnectFail = false ตัวเดียวกัน — "app data" Redis แยกจาก SignalR backplane) ────────────
builder.Services.AddSingleton<ISceneTokenStore, RedisSceneTokenStore>();
builder.Services.AddSingleton<SceneTokenFlusher>();
builder.Services.AddHostedService<SceneTokenFlushService>();

// ── Sprint G Part 10: การเดินที่ค้างรอ reaction + ตัวกวาดที่ปิดให้เองเมื่อหมดเวลา ──
// sweeper ต้องเป็น hosted service จริง ไม่ใช่เช็คตอนมี request เข้ามาเท่านั้น เพราะถ้าทุกคนปิดจอรอ
// ก็จะไม่มี request ให้เช็ค แล้วการเดินจะค้างถาวร (dispatch ข้อ 4: "เดินต่อได้เองโดยไม่ค้าง")
builder.Services.AddSingleton<IPendingReactionStore, RedisPendingReactionStore>();
builder.Services.AddSingleton<PendingReactionSweeper>();
builder.Services.AddHostedService<PendingReactionSweepService>();

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

// ── Sprint F งานที่ 4: Redis สำหรับ "ข้อมูลแอป" (turn state ของ encounter) ──
// multiplexer ตัวที่สาม (แยกจาก SignalR backplane และจาก dataProtectionRedis ด้านบน) ตั้งใจ ไม่ reuse:
//   1. connection ของ SignalR ถูกสร้าง/ถือไว้ภายใน AddStackExchangeRedis เอง ไม่ expose ออกมาให้ใช้ซ้ำ
//      (เหตุผลเดียวกับคอมเมนต์ด้านบนตอนตั้ง Data Protection)
//   2. dataProtectionRedis ใช้ AbortOnConnectFail ค่าเริ่มต้น (true) และหน้าที่ของมันคือ key ring ที่
//      ถอดรหัส ApiKey — ห้ามเอามาปนกับ policy ความทนทานของข้อมูลเกม (turn state) ที่ต้องการพฤติกรรม
//      คนละแบบกันโดยสิ้นเชิง
//   3. AbortOnConnectFail = false ที่นี่จำเป็น: ถ้าเป็น true แล้ว Redis ล่มตอน startup Connect() จะ
//      throw ทำให้ทั้ง API พังทั้งที่กระทบแค่ turn state ตั้ง false แล้วความล้มเหลวจะไปโผล่ตอนเรียกใช้
//      จริงแทน ซึ่ง RedisEncounterTurnStateStore จับไว้หมดแล้ว (ดูคอมเมนต์ในไฟล์นั้น) ผลคือ degrade
//      เป็น "รีเซ็ตเทิร์น" ตามที่ล็อกไว้ใน docs/PROJECT-CONTEXT.md §4 ข้อ 3 — ⚠️ ข้อนี้คือสิ่งที่ทำให้
//      เทสต์ Active_encounter_falls_back_to_full_turn_resources_when_redis_is_unavailable เป็นไปได้
//      (เทสต์สลับ IConnectionMultiplexer ใน DI เป็นตัวที่ชี้พอร์ตตาย) ดังนั้นต้องลงทะเบียนเป็น
//      IConnectionMultiplexer (interface) เท่านั้น ห้ามลงทะเบียนเป็น concrete type
var appDataRedisConfiguration = ConfigurationOptions.Parse(redisConnectionString);
appDataRedisConfiguration.AbortOnConnectFail = false;
builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(appDataRedisConfiguration));
builder.Services.AddSingleton<IEncounterTurnStateStore, RedisEncounterTurnStateStore>();

// ── Sprint G Part 4: MinIO (S3-compatible) สำหรับ map asset ที่ DM อัปโหลด ─────────────────────
// ⚠️ ใช้ AWSSDK.S3 กับ endpoint ของ MinIO เอง (ไม่ใช่ AWS จริง) — ต้องตั้ง ForcePathStyle = true
// เพราะ MinIO ไม่รองรับ virtual-hosted-style URL (bucket.endpoint/...) แบบ AWS จริง และปิด
// RegionEndpoint (MinIO ไม่สนใจ region แต่ SDK ต้องการค่าอะไรสักอย่างที่ไม่ throw ตอน validate)
var minioEndpoint = Environment.GetEnvironmentVariable("DNDEZ_MINIO_ENDPOINT") ?? "http://localhost:9000";
var minioAccessKey = Environment.GetEnvironmentVariable("DNDEZ_MINIO_ACCESS_KEY") ?? "dndez";
var minioSecretKey = Environment.GetEnvironmentVariable("DNDEZ_MINIO_SECRET_KEY") ?? "dndez12345";
var minioBucket = Environment.GetEnvironmentVariable("DNDEZ_MINIO_BUCKET") ?? "dndez-map-assets";
var minioUseHttp = minioEndpoint.StartsWith("http://", StringComparison.Ordinal);
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
    minioAccessKey,
    minioSecretKey,
    new AmazonS3Config
    {
        ServiceURL = minioEndpoint,
        ForcePathStyle = true,
        UseHttp = minioUseHttp,
    }));
// AWSSDK.S3 คืน presigned URL เป็น https:// เสมอไม่สนใจ UseHttp (ดู doc comment ของ
// SceneEndpoints.CreateSignedUrlAsync สำหรับหลักฐาน) — rewrite เป็น http เฉพาะตอน endpoint
// ตั้งค่าเป็น http จริง (dev/MinIO) ไม่ทำกับ endpoint https จริง (production ต่อ AWS จริง)
builder.Services.AddSingleton(new MapAssetStorageOptions(minioBucket, RewriteSignedUrlToHttp: minioUseHttp));

// ── Sprint D Part 2: Anthropic client (BYOK tool-calling loop) ─────────────
// IAnthropicClient แยก interface ออกจาก implementation จริงตั้งใจ — ทดสอบด้วย fake ในเทสต์
// (ไม่มีทางเรียก Anthropic จริงในเทสต์อัตโนมัติได้อยู่แล้ว ไม่มี key จริง/ไม่ deterministic/มีต้นทุนจริง)
// transaction ต่อ request + RLS context (ใช้โดย middleware ด้านล่าง และ endpoint ที่ต้อง checkpoint)
builder.Services.AddScoped<RlsRequestTransaction>();

builder.Services.AddHttpClient<IAnthropicClient, AnthropicClient>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
    // เพดานนอกสุดเท่านั้น — timeout จริงต่อคำขอตั้งใน AnthropicClient (30 วินาทีปกติ / 5 นาทีตอนอ่านเอกสาร)
    client.Timeout = AnthropicClient.ExtractionTimeout + TimeSpan.FromSeconds(30);
});

// ── Rate limiting ────────────────────────────────────────────────────────
// พบระหว่าง retroactive review (Sprint D Part 2) — PUT /users/me/api-keys/{provider} เรียก
// IAnthropicClient.ValidateKeyAsync จริงทุกครั้ง (live call ไปหา Anthropic) โดยไม่มี rate limit
// เลย ทั้งที่ security review ของ Part 1 ล็อกไว้แล้วว่าต้องมีก่อน Part 2 เพิ่ม live validation —
// ไม่งั้น endpoint นี้กลายเป็น oracle ให้ผู้ใช้ที่ authenticate แล้วเช็คได้ว่า string ใด ๆ ที่ตัวเอง
// พิมพ์เข้ามาเป็น Anthropic API key ของคนอื่นที่ขโมยมาใช้ได้จริงไหม — partition ตาม user id (ไม่ใช่
// IP) เพราะ endpoint นี้ต้อง [Authorize] อยู่แล้ว ทุก request มี user id เสมอ
builder.Services.AddRateLimiter(options =>
{
    // default ของ ASP.NET Core คือ 503 Service Unavailable ตอนโดน reject — เปลี่ยนเป็น 429 Too Many
    // Requests ตาม HTTP semantic ที่ถูกต้องจริง (503 สื่อว่า server ล่ม ไม่ใช่ client ยิงถี่เกิน)
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(ApiKeyEndpoints.RateLimitPolicyName, context =>
    {
        var userId = context.User.GetUserId()?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });

    // Sprint F งานที่ 1 — POST /campaigns/join เป็น enumeration surface ตรง ๆ (พิมพ์โค้ดสุ่มไปเรื่อย ๆ
    // เพื่อหาแคมเปญที่มีอยู่จริง) partition ตาม user id เหมือนกัน (endpoint นี้ [Authorize] เสมอ)
    options.AddPolicy(CampaignMembershipEndpoints.JoinRateLimitPolicyName, context =>
    {
        var userId = context.User.GetUserId()?.ToString() ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
});

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

// ── Sprint E Part 1: CORS สำหรับ Next.js frontend ──────────────────────────
// backend ไม่เคยมี CORS มาก่อนเลยเพราะไม่มี client ข้าม origin จนถึงตอนนี้ (เทสต์ทั้งหมดยิงตรงผ่าน
// WebApplicationFactory ไม่ผ่าน browser) frontend แยก origin จริง (Next.js dev server) จึงต้องเปิด —
// ไม่ใช้ AllowCredentials เพราะ auth ทั้งระบบใช้ Bearer token (Authorization header / query string
// เฉพาะ SignalR) ไม่ใช่ cookie เลย ไม่มีเหตุผลต้องเปิด credentialed CORS ที่ซับซ้อนกว่าโดยไม่จำเป็น
var frontendOrigin = Environment.GetEnvironmentVariable("DNDEZ_FRONTEND_ORIGIN")
    ?? "http://localhost:3001";
const string FrontendCorsPolicy = "Frontend";
builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCorsPolicy, policy =>
        policy.WithOrigins(frontendOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod());
});

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

app.UseCors(FrontendCorsPolicy);
// สร้าง bucket ของ MinIO ถ้ายังไม่มี (docker-compose ไม่ pre-create ให้) — idempotent (check แล้ว
// ค่อยสร้าง) กัน exception ไม่ให้ crash ทั้ง process ตอน MinIO ยังไม่พร้อมตอน cold boot (เหมือน
// AbortOnConnectFail = false ของ Redis ด้านบน) endpoint upload เองจะเจอ error ตอนใช้จริงถ้า MinIO
// ยังไม่พร้อมจริง ๆ ซึ่งดีกว่า API ทั้งตัวขึ้นไม่ได้เพราะ dependency ที่ไม่ critical กับทุก request
try
{
    var s3 = app.Services.GetRequiredService<IAmazonS3>();
    var storageOptions = app.Services.GetRequiredService<MapAssetStorageOptions>();
    if (!await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(s3, storageOptions.BucketName))
    {
        await s3.PutBucketAsync(storageOptions.BucketName);
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "ตรวจ/สร้าง MinIO bucket ไม่สำเร็จตอน startup — endpoint upload จะ error เองถ้าเรียกใช้จริง");
}

app.UseAuthentication();
app.UseAuthorization();

// วางหลัง UseAuthentication เพราะ policy ข้างบน partition ตาม context.User (ต้อง authenticate
// เสร็จก่อนถึงจะมี user id ให้ partition ได้) — วางก่อน RLS middleware เพื่อปฏิเสธ request ที่ชน
// rate limit ตั้งแต่ก่อนเปิด transaction/แตะ DB เลย ไม่เปลืองคอนเนกชันเปล่า ๆ
app.UseRateLimiter();

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

    // ตัว transaction + การตั้ง set_config อยู่ใน RlsRequestTransaction (scoped) — endpoint ที่ทำงานหลายขั้น
    // เรียก CheckpointAsync ได้ (ดู doc comment ของคลาส) ส่วน endpoint อื่นได้พฤติกรรมเดิมทุกประการ
    var requestTransaction = context.RequestServices.GetRequiredService<RlsRequestTransaction>();

    // campaignId มาจาก route ที่ constraint :guid กรองมาแล้ว แต่ยัง TryParse ซ้ำ เพราะ middleware นี้
    // ต้องทนกับ route อื่นในอนาคตที่อาจตั้งชื่อ campaignId โดยไม่ผูก constraint
    Guid? campaignId = context.Request.RouteValues.TryGetValue("campaignId", out var rawCampaignId)
        && Guid.TryParse(rawCampaignId?.ToString(), out var parsedCampaignId)
            ? parsedCampaignId
            : null;

    await requestTransaction.BeginAsync(context.User.GetUserId(), campaignId, context.RequestAborted);

    try
    {
        await next(context);
        await requestTransaction.CommitAsync();
    }
    catch
    {
        await requestTransaction.RollbackAsync();
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

// ── Sprint G Part 7: คลัง preset ตัวละคร (design doc §5) — สมาชิกทุกคนใช้ได้ ไม่ใช่ DM-only ──
app.MapCharacterPresetEndpoints();

// ── Sprint F งานที่ 1: join campaign flow ───────────────────────────────────
app.MapCampaignMembershipEndpoints();

// ── Sprint F งานที่ 2a: dial autonomy ต่อ subsystem (DM ตั้ง / สมาชิกอ่าน) ────
app.MapAutonomySettingEndpoints();

// ── Sprint B Part 1: ต่อ DnDEz.Rules (rules engine) เข้า API จริงเป็นครั้งแรก ─
app.MapDiceRollEndpoints();

// ── Sprint B Part 3: check/attack/damage resolution ────────────────────────
app.MapCombatEndpoints();

// ── Sprint C Part 1: realtime hub (ยังไม่ broadcast อะไรจริง — Part 2/3 wire เข้า endpoint เดิม) ──
app.MapHub<CampaignHub>(CampaignHubPath).RequireAuthorization();

// ── Sprint D Part 1: BYOK — เก็บ/ดึง/ลบ API key เข้ารหัสของผู้ใช้ ──────────────
app.MapApiKeyEndpoints();

// ── คิว proposal (Sprint D Part 3) — เหลือแค่ resolve/list หลัง 2026-09-23: การคุยกับ AI ทั้งหมดย้าย
// ไปที่ผู้ช่วย DM ช่องเดียวด้านล่างแล้ว (เดิมไฟล์นี้มี POST /consult ด้วย ตอนนี้ลบออก) ──
app.MapProposalEndpoints();

// ── Sprint E Part 3: player action input — ไหลเข้า OrchestratorTurnRunner เดียวกับที่สร้าง
// proposal ข้างบน แต่ auth เป็น "เจ้าของตัวละคร" ไม่ใช่ DM-only ──────────────────────
app.MapPlayerActionEndpoints();

// ── DM AI Assistant Part D: บันทึกแคมเปญ (ชั้นความจำระหว่าง log ดิบกับ recap) — DM-only,
// AI เข้าถึงผ่าน tool `search_campaign_journal` ไม่ใช่ยัดเข้า system prompt ──
app.MapCampaignJournalEndpoints();

// ── DM AI Assistant Part E: spawn มอนสเตอร์เป็นตัวละคร DM-controlled เพื่อให้มี HP รายตัวและ
// เข้าวง initiative ได้ (ลบทิ้งพร้อมสรุปผลตอน encounter จบ ดู MonsterSpawnEndpoints) ──
app.MapMonsterSpawnEndpoints();

// ── สรุปเซสชัน (recap_snapshots) — ฉบับ DM (ความจำของ AI, รวมความลับ) กับฉบับผู้เล่น ("ตอนที่แล้ว…")
// สร้างจาก input คนละชุด ไม่ใช่ให้ AI แยกเอง ดู doc comment ของ SessionRecapEndpoints ──
app.MapSessionRecapEndpoints();

// ── ผู้ช่วย DM ช่องเดียว (2026-09-23) — ถามข้อมูล + สั่งงานหลายขั้น, tool ขออนุญาตตาม autonomy dial,
// ข้อความถึงผู้เล่นต้องให้ DM ยืนยันเสมอ ดู AssistantEndpoints / AssistantTools ──
app.MapAssistantEndpoints();

// ── Sprint F งานที่ 4: encounter / initiative / turn order ──
app.MapEncounterEndpoints();

// ── Sprint F งานที่ 5: death save state machine (confirm-dying / roll / GET state) ──
app.MapDeathSaveEndpoints();

// ── Sprint G Part 2: story_beats CRUD + active-beat (design doc §11.1, §12) — DM-only ──
app.MapStoryBeatEndpoints();
app.MapStoryImportEndpoints();

// ── Sprint G Part 3: scene-prep — monster/npc ของ beat ที่ระบุ (design doc §13.2) — DM-only ──
app.MapScenePrepEndpoints();
app.MapSrdEndpoints();
app.MapMonsterEndpoints();

// ── Sprint G Part 4: อัปโหลดแผนที่ + scene + จัดกริด (design doc §11.1, §22) — DM-only ──
app.MapSceneEndpoints();

// ── Sprint G Part 5: token บนแผนที่ — DM ทุกตัว, ผู้เล่นเฉพาะตัวละครตัวเอง (design doc §14) ──
app.MapSceneTokenEndpoints();

// ── Sprint G Part 6: fog reveal/hide (DM-only) — projection ตาม role อยู่ใน GET scene (design doc §13) ──
app.MapSceneFogEndpoints();

// ── Sprint H Part 5: DM ระบายพื้นเดินยาก/กำแพง (DM-only) — ข้อมูลที่กำหนดว่าใครเดินไปไหนได้ (§3.3) ──
app.MapSceneTerrainEndpoints();

// ── Sprint G Part 9: "ทำอะไรได้บ้าง" คำนวณต่อผู้ดูแต่ละคน — deterministic ล้วน ไม่แตะ AI (§1.3) ──
app.MapAffordanceEndpoints();

// ── Sprint G Part 10: บอกราคา+การโดนสวนของเส้นทาง **ก่อน** ผู้เล่นกด (§3.4) — ไม่เปลี่ยน state ──
app.MapMovementPreviewEndpoints();
app.MapMovementCommitEndpoints();

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
