using System.Security.Claims;
using Amazon.S3;
using Amazon.S3.Model;
using DnDEz.Api.Auth;
using DnDEz.Api.Realtime;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DnDEz.Api.Endpoints;

/// <summary>Bucket ของ MinIO ที่เก็บ map asset — DI singleton เดียวกับที่ Program.cs ลงทะเบียน
/// <paramref name="RewriteSignedUrlToHttp"/>: ดู doc comment ของ
/// <see cref="SceneEndpoints.CreateSignedUrlAsync"/> ว่าทำไมต้องมีฟิลด์นี้</summary>
public record MapAssetStorageOptions(string BucketName, bool RewriteSignedUrlToHttp);

/// <summary>
/// Sprint G Part 4 — อัปโหลดแผนที่ (map asset) + สร้าง/อ่าน scene + จัดกริด (design doc §11.1, §22)
/// contract เต็มอยู่ใน <c>tests/DnDEz.Api.Tests/SceneEndpointsTests.cs</c> — ห้ามแก้เทสต์เพื่อให้ผ่าน
///
/// ⚠️ **Part นี้เป็น security surface ที่เสี่ยงสุดใน Sprint G** (external file upload) — ทุกจุดที่
/// ทำเพื่อความปลอดภัยมีคอมเมนต์กำกับไว้ตรงจุดเสมอ ห้ามลบ/ผ่อนโดยไม่เข้าใจเหตุผลก่อน
/// </summary>
public static class SceneEndpoints
{
    /// <summary>เพดานขนาดไฟล์แผนที่ — 15MB เพียงพอสำหรับภาพแผนที่ความละเอียดสูงทั่วไป ค่านี้เป็น
    /// ดุลยพินิจของ owner ไม่ใช่ตัวเลขที่ล็อกจาก spec ปรับได้ถ้าจำเป็นในอนาคต</summary>
    private const long MaxUploadBytes = 15 * 1024 * 1024;

    /// <summary>quota เริ่มต้นสำหรับผู้ใช้ที่ยังไม่มีแถว storage_quotas เลย (lazy-provision ตอน
    /// อัปโหลดครั้งแรก — ไม่มี pipeline ไหนสร้างแถวนี้ล่วงหน้าให้ ตรวจแล้วทั้ง repo) เป็นค่าดีฟอลต์
    /// ชั่วคราว ไม่ใช่ระบบ tier/billing จริง (ยังไม่มีในโปรเจกต์นี้)</summary>
    internal const long DefaultQuotaLimitBytes = 200 * 1024 * 1024;

    /// <summary>อายุของ signed URL ที่ปล่อยให้ client ใช้โหลดรูป — สั้นพอที่แม้หลุดออกไปก็ใช้ต่อไม่ได้นาน
    /// ยาวพอที่ session หนึ่งจะโหลดรูปซ้ำได้ไม่ถี่เกินไป (client ควร re-fetch scene ใหม่ถ้าหมดอายุ)</summary>
    private static readonly TimeSpan SignedUrlLifetime = TimeSpan.FromMinutes(15);

    public static void MapSceneEndpoints(this WebApplication app)
    {
        // ── อัปโหลดแผนที่ ────────────────────────────────────────────────────
        app.MapPost("/campaigns/{campaignId:guid}/map-assets", async (
            Guid campaignId,
            IFormFile? file,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IAmazonS3 s3,
            MapAssetStorageOptions storage) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new UploadErrorResponse("file_required"));
            }

            // IFormFile.Length มาจาก multipart body ที่ Kestrel parse เองแล้ว ไม่ใช่ header ที่ client
            // ประกาศเอง (ต่างจาก Content-Length ที่เชื่อไม่ได้) เช็คได้ก่อน copy สตรีมทั้งก้อนเข้าหน่วยความจำ
            if (file.Length > MaxUploadBytes)
            {
                return Results.BadRequest(new UploadErrorResponse("file_too_large"));
            }

            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            using var buffer = new MemoryStream();
            await file.OpenReadStream().CopyToAsync(buffer);
            var bytes = buffer.ToArray();

            // ⚠️ ตรวจชนิดไฟล์จริงจาก magic bytes เท่านั้น — ห้ามเชื่อ file.ContentType (client ส่งอะไร
            // มาก็ได้) หรือนามสกุลไฟล์ (client ตั้งชื่อเองได้ทั้งหมด) ทั้งสองอย่างปลอมได้ง่าย ๆ
            var extension = DetectImageExtension(bytes);
            if (extension is null)
            {
                return Results.BadRequest(new UploadErrorResponse("invalid_file_type"));
            }

            // quota ต่อบัญชี — lazy-provision แถวถ้ายังไม่มี (ไม่มี pipeline ไหนสร้างให้ล่วงหน้า)
            var quota = await db.StorageQuotas.SingleOrDefaultAsync(q => q.UserId == callerUserId.Value);
            if (quota is null)
            {
                quota = new StorageQuota
                {
                    UserId = callerUserId.Value,
                    UsedBytes = 0,
                    LimitBytes = DefaultQuotaLimitBytes,
                };
                db.StorageQuotas.Add(quota);
            }

            if (quota.UsedBytes + bytes.Length > quota.LimitBytes)
            {
                return Results.BadRequest(new UploadErrorResponse("quota_exceeded"));
            }

            // ⚠️ ห้ามใช้ชื่อไฟล์ที่ client ส่งมาประกอบ storage key เด็ดขาด (path traversal) — สร้าง
            // key เองทั้งเส้นทางจาก campaignId + guid ใหม่ + นามสกุลที่ "เรา" ตรวจแล้วว่าใช่จริง
            // (ไม่ใช่นามสกุลจากชื่อไฟล์ client) ชื่อไฟล์เดิมของ client ไม่ถูกเก็บไว้ที่ไหนเลย
            var storageKey = $"map-assets/{campaignId}/{Guid.NewGuid()}.{extension}";

            // อัปโหลดไป MinIO ก่อน แล้วค่อยเขียน DB — ถ้า DB เขียนไม่สำเร็จหลังอัปโหลดสำเร็จ จะเหลือ
            // object กำพร้าใน MinIO (เสีย storage เปล่า ๆ แต่ไม่ใช่ช่องโหว่ความปลอดภัย) กลับด้านกัน
            // (เขียน DB ก่อน) จะแย่กว่า: แถวชี้ไปยัง object ที่ไม่มีจริง = ภาพเสียถาวรที่ client เจอ
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = storage.BucketName,
                Key = storageKey,
                InputStream = new MemoryStream(bytes),
                ContentType = extension == "png" ? "image/png" : "image/jpeg",
            });

            var mapAsset = new MapAsset
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                StorageKey = storageKey,
                // อัปโหลดตรงจาก DM เอง ไม่ได้ผ่าน extraction/scan pipeline — ไม่มีอะไรให้ "รีวิวว่าอ่านถูกไหม"
                // ต่างจาก monster/npc ที่มาจากสแกนหนังสือ (design doc §4.1) จึง approve ทันที
                ReviewStatus = ReviewStatus.Approved,
            };
            db.MapAssets.Add(mapAsset);

            quota.UsedBytes += bytes.Length;

            await db.SaveChangesAsync();

            return Results.Created(
                $"/campaigns/{campaignId}/map-assets/{mapAsset.Id}",
                new MapAssetCreatedResponse(mapAsset.Id, campaignId, mapAsset.ReviewStatus.ToString()));
        }).RequireAuthorization().DisableAntiforgery();

        // ── สร้าง scene จาก map asset ────────────────────────────────────────
        app.MapPost("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/scene", async (
            Guid campaignId,
            Guid sessionId,
            CreateSceneRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IAmazonS3 s3,
            MapAssetStorageOptions storage) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest();
            }

            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
            if (session is null)
            {
                return Results.NotFound();
            }

            var mapAsset = await db.MapAssets
                .SingleOrDefaultAsync(m => m.Id == request.MapAssetId && m.CampaignId == campaignId);
            if (mapAsset is null)
            {
                // อ้างอิงมาจาก body ไม่ใช่ route — ผิดเป็น input error (400) ไม่ใช่ 404 ของ resource หลัก
                return Results.BadRequest(new UploadErrorResponse("map_asset_not_found"));
            }

            // มี active scene ได้ทีละอันต่อ session (ix_scenes_one_active_per_session) — ต้อง demote
            // ตัวเก่าก่อนแล้วค่อย save แยกจากการสร้างตัวใหม่ที่ active เลย (บทเรียนจาก Sprint G Part 2:
            // ถ้ารวมเป็น SaveChangesAsync เดียว EF อาจส่ง INSERT ตัวใหม่ก่อน UPDATE ตัวเก่า ทำให้มีสอง
            // แถว active พร้อมกันชั่วขณะ ชน unique index จริง — ไม่ใช่แค่ทฤษฎี ดู routing.md)
            var previousActive = await db.Scenes.SingleOrDefaultAsync(s => s.SessionId == sessionId && s.IsActive);
            if (previousActive is not null)
            {
                previousActive.IsActive = false;
                await db.SaveChangesAsync();
            }

            var scene = new Scene
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Name = request.Name,
                MapAssetId = mapAsset.Id,
                IsActive = true,
            };
            db.Scenes.Add(scene);
            await db.SaveChangesAsync();

            var imageUrl = await CreateSignedUrlAsync(s3, storage, mapAsset.StorageKey);
            return Results.Created(
                $"/campaigns/{campaignId}/sessions/{sessionId}/scene",
                await ToResponseAsync(db, scene, imageUrl));
        }).RequireAuthorization();

        // ── อ่าน scene ที่ active ของ session ─────────────────────────────────
        // DM-only ใน Part นี้โดยตั้งใจ — payload ที่ต่างกันตาม role (DM/Player/Table, §13) เป็นงาน
        // ของ Sprint G Part 6 (fog + 3 มุมมอง) ยังไม่ทำที่นี่ ไม่ใช่ลืม
        // Sprint G Part 6 (design doc §13.1/§13.3) — เปิดให้สมาชิกทุกคนเรียกได้ (ไม่ใช่ DM-only แล้ว)
        // payload กรองต่างกันตาม role ที่ชั้นนี้เอง ไม่ใช่ปล่อยให้ client กรองเอง (§13.3)
        // ⚠️ Table view (จอกลาง, credential แยกตาม §16) **ยังไม่ทำในรอบนี้** — ของค้างที่ยังไม่มีเจ้าของ
        // ตามที่ dispatch อนุญาตให้เลื่อนได้ถ้าเวลาไม่พอ รองรับแค่ DM/Player สองมุมมองก่อน
        app.MapGet("/campaigns/{campaignId:guid}/sessions/{sessionId:guid}/scene", async (
            Guid campaignId,
            Guid sessionId,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IAmazonS3 s3,
            MapAssetStorageOptions storage,
            ISceneTokenStore tokenStore) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            var campaign = await db.Campaigns.SingleOrDefaultAsync(c => c.Id == campaignId);
            if (campaign is null)
            {
                return Results.NotFound();
            }
            var isDm = campaign.OwnerUserId == callerUserId.Value;

            var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.CampaignId == campaignId);
            if (session is null)
            {
                return Results.NotFound();
            }

            var scene = await db.Scenes
                .Include(s => s.MapAsset)
                .SingleOrDefaultAsync(s => s.SessionId == sessionId && s.IsActive);
            if (scene is null)
            {
                return Results.NotFound();
            }

            // ⚠️ signed URL generate สดทุกครั้งตรงนี้ — ห้ามอ่านจาก column ที่เก็บไว้ (ไม่มีการเก็บ
            // URL ไว้เลยตั้งแต่แรก ดู doc comment ของ Scene.BackgroundImageUrl) เพื่อให้ผู้ที่ไม่ใช่
            // สมาชิกแคมเปญเข้าไม่ถึงไฟล์แม้รู้ URL เก่า (URL หมดอายุไปแล้วตาม SignedUrlLifetime)
            var imageUrl = scene.MapAsset is null
                ? null
                : await CreateSignedUrlAsync(s3, storage, scene.MapAsset.StorageKey);

            var pendingPositions = await tokenStore.GetPendingPositionsAsync(scene.Id);
            var response = await BuildProjectedResponseAsync(db, scene, imageUrl, pendingPositions, isDm);
            return Results.Ok(response);
        }).RequireAuthorization();

        // ── จัดกริด (ล็อก pixel-per-square) ───────────────────────────────────
        app.MapPatch("/campaigns/{campaignId:guid}/scenes/{sceneId:guid}/grid", async (
            Guid campaignId,
            Guid sceneId,
            UpdateGridRequest request,
            ClaimsPrincipal principal,
            DnDEzDbContext db,
            IAmazonS3 s3,
            MapAssetStorageOptions storage) =>
        {
            var callerUserId = principal.GetUserId();
            if (callerUserId is null)
            {
                return Results.Unauthorized();
            }

            if (request.PixelsPerSquare is <= 0)
            {
                return Results.BadRequest();
            }

            var (_, failure) = await CampaignAuthorization.RequireCampaignDmAsync(db, campaignId, callerUserId.Value);
            if (failure is not null)
            {
                return failure;
            }

            // scene ผูกกับ campaign อ้อมผ่าน session (ตาราง scenes ไม่มี campaign_id ตรง ๆ) — join
            // เองที่ชั้น C# ตรงนี้ด้วย เพราะ RLS policy ที่ join ผ่าน session ให้แล้วในชั้น DB เป็นแค่
            // ตาข่ายรอง ไม่ใช่แทนที่การเช็คที่ชั้น endpoint (เหตุผลเดียวกับที่ทุก endpoint อื่นเช็ค
            // CampaignId ของ resource ตรง ๆ เสมอ)
            var scene = await db.Scenes
                .Include(s => s.MapAsset)
                .Include(s => s.Session)
                .SingleOrDefaultAsync(s => s.Id == sceneId && s.Session.CampaignId == campaignId);
            if (scene is null)
            {
                return Results.NotFound();
            }

            scene.Grid = System.Text.Json.JsonSerializer.Serialize(new
            {
                pixelsPerSquare = request.PixelsPerSquare,
                offsetX = request.OffsetX ?? 0,
                offsetY = request.OffsetY ?? 0,
            });
            await db.SaveChangesAsync();

            var imageUrl = scene.MapAsset is null
                ? null
                : await CreateSignedUrlAsync(s3, storage, scene.MapAsset.StorageKey);
            return Results.Ok(await ToResponseAsync(db, scene, imageUrl));
        }).RequireAuthorization();
    }

    /// <summary>
    /// ตรวจชนิดไฟล์จริงจาก magic bytes — รองรับ PNG/JPEG เท่านั้น (สองชนิดที่พอสำหรับภาพแผนที่)
    /// ห้ามพึ่ง Content-Type header หรือนามสกุลไฟล์เด็ดขาด — client ปลอมสองอย่างนี้ได้ง่ายมาก
    /// (เปลี่ยนนามสกุลไฟล์ .exe เป็น .png ก็ผ่าน content-type/นามสกุลได้ทันทีถ้าไม่เช็คไบต์จริง)
    /// </summary>
    private static string? DetectImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "jpg";
        }

        return null;
    }

    /// <summary>
    /// ⚠️ **AWSSDK.S3 (v4.x) คืน URL แบบ https:// เสมอจาก GetPreSignedURLAsync ไม่สนใจ
    /// `AmazonS3Config.UseHttp` เลย** (ยืนยันจริงด้วยการรัน GetPreSignedURLAsync แยกทดสอบ 2026-09-09
    /// — ไม่ใช่แค่อ่าน doc) ทำให้ต่อ MinIO dev (http ล้วน ไม่มี TLS) ไม่ได้ตรง ๆ เพราะ client จะพยายาม
    /// handshake TLS กับพอร์ตที่ไม่มี TLS จริง แล้วได้ "corrupted frame" error
    ///
    /// **แก้โดยแทนที่ scheme ในสตริง URL ตรง ๆ หลัง generate** — ปลอดภัยเพราะ SigV4 ไม่เซ็น scheme
    /// เข้าไปใน signature เลย (เซ็นแค่ host + path + query + signed headers) ยืนยันจริงด้วยการรัน
    /// GetPreSignedURLAsync แล้วสลับ https→http เอง แล้วยิงจริงว่า signature ยังผ่าน (2026-09-09)
    /// — ทำเฉพาะตอน storage.RewriteSignedUrlToHttp = true (คือ dev/MinIO เท่านั้น) การ deploy จริงกับ
    /// AWS S3 จริง (https ทั้งคู่) ต้องไม่ทำแบบนี้ จึงส่ง flag มาจาก config แทนการเช็คแบบเดา
    /// </summary>
    private static async Task<string> CreateSignedUrlAsync(IAmazonS3 s3, MapAssetStorageOptions storage, string key)
    {
        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = storage.BucketName,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(SignedUrlLifetime),
        });

        return storage.RewriteSignedUrlToHttp ? url.Replace("https://", "http://", StringComparison.Ordinal) : url;
    }

    /// <summary>
    /// response ของ endpoint DM-only (สร้างฉาก / จัดกริด) — ไม่มีอะไรต้องกรอง แต่ **เรียกตัวเดียวกับทางของ GET**
    /// โดยตั้งใจ: มี builder สองตัวที่ต้องคอยทำให้ผลตรงกันเองคือกับดักที่ design doc §2.4 เตือนไว้ (ครั้งนี้
    /// เกือบเกิดจริง — ตอนเพิ่มชื่อ token เข้า response ถ้าแยกสองทางไว้ ฝั่ง DM-only จะไม่มีชื่อเงียบ ๆ)
    /// </summary>
    private static Task<SceneResponse> ToResponseAsync(DnDEzDbContext db, Scene scene, string? imageUrl) =>
        BuildProjectedResponseAsync(db, scene, imageUrl, pendingPositions: null, isDm: true);

    /// <summary>
    /// Sprint G Part 6 (design doc §13.1, §13.3) — ใช้เฉพาะ GET scene (endpoint เดียวที่ไม่ใช่
    /// DM-only) จุดเดียวที่ทำ projection ตาม role ทั้งหมด:
    ///  1. token ที่อยู่นอกพื้นที่หมอกที่เปิดแล้ว **ถูกตัดออกจาก list เลย** (ไม่ใช่ส่งไปแล้วซ่อนที่
    ///     client — §13.3 ห้ามตรง ๆ) DM เห็นทุก token เสมอไม่สนใจหมอก
    ///  2. HP ของ token ที่ผูกกับ Character: ตัวละครผู้เล่นปกติ (ControlledByDm=false) เห็นค่าจริง
    ///     เสมอทั้ง DM/Player (design doc §13.1 "HP ปาร์ตี้ = ตัวเลขจริงทั้ง 3 มุมมอง") ส่วน NPC
    ///     (ControlledByDm=true) ใช้ <see cref="CharacterStatsVisibility.IsVisibleTo"/> เดิมเป๊ะ ๆ
    ///     (เกณฑ์เดียวกับที่ CharacterEndpoints ใช้ กันสองจุดเพี้ยนจากกัน) token ที่ผูกกับ Monster
    ///     ไม่มี HP ให้แสดง (monster_stat_blocks ไม่มี current-hp tracking เลย เก็บแค่ stat block นิ่ง)
    ///  3. **ชื่อ** ของ token ที่เหลือรอดจากข้อ 1 — ทั้ง Character และ Monster · ชื่อของสิ่งที่มองเห็นได้
    ///     ไม่ใช่ความลับ (ผู้เล่นเห็นมันอยู่บนแผนที่ตรงหน้าอยู่แล้ว และ affordance ก็ส่งชื่อเป้าให้ตั้งแต่
    ///     Sprint H Part 7) ความลับคือ "มีอะไรอยู่ในหมอกบ้าง" ซึ่งข้อ 1 ตัดทิ้งไปก่อนถึงตรงนี้แล้ว
    /// </summary>
    private static async Task<SceneResponse> BuildProjectedResponseAsync(
        DnDEzDbContext db, Scene scene, string? imageUrl,
        IReadOnlyDictionary<Guid, TokenPosition>? pendingPositions, bool isDm)
    {
        var tokens = SceneTokenSerialization.Parse(scene.Tokens);
        if (pendingPositions is { Count: > 0 })
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (pendingPositions.TryGetValue(tokens[i].Id, out var pos))
                {
                    tokens[i] = tokens[i] with { X = pos.X, Y = pos.Y };
                }
            }
        }

        var fogAreas = FogLayerSerialization.ParseRevealedAreas(scene.FogLayer);
        var visibleTokens = isDm
            ? tokens
            // กฎ "มองเห็นไหม" อยู่ที่ FogLayerSerialization ที่เดียวทั้งระบบ (Part 9 ใช้ตัวเดียวกัน
            // กรองเป้าก่อนคำนวณ affordance) — ห้ามคัดลอกสูตรวงกลมมาไว้ที่นี่อีกสำเนา
            : tokens.Where(t => FogLayerSerialization.IsVisibleTo(t.X, t.Y, fogAreas, isDm: false)).ToList();

        var characterIds = visibleTokens.Where(t => t.EntityType == "Character").Select(t => t.EntityId).ToList();
        var characters = characterIds.Count == 0
            ? []
            : await db.Characters.Where(c => characterIds.Contains(c.Id)).ToListAsync();
        var charactersById = characters.ToDictionary(c => c.Id);

        // ชื่อมอนสเตอร์ — query แยกเพราะคนละตาราง · ดึงเฉพาะ token ที่ผ่านด่านหมอกมาแล้วเท่านั้น
        // (ถ้าดึงทั้งฉากมาก่อนแล้วค่อยกรอง ชื่อของตัวที่อยู่ในหมอกจะเคยอยู่ในมือ server ฝั่ง response แล้ว
        //  — §1.2 บอกให้กรองก่อนคำนวณเสมอ ไม่ใช่คำนวณแล้วค่อยกรอง)
        var monsterIds = visibleTokens.Where(t => t.EntityType == "Monster").Select(t => t.EntityId).ToList();
        var monsterNames = monsterIds.Count == 0
            ? []
            : await db.MonsterStatBlocks.Where(m => monsterIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, m => m.Name);

        var tokenResponses = visibleTokens.Select(t =>
        {
            if (t.EntityType != "Character" || !charactersById.TryGetValue(t.EntityId, out var character))
            {
                monsterNames.TryGetValue(t.EntityId, out var monsterName);
                return new TokenResponse(t.Id, t.EntityType, t.EntityId, t.X, t.Y, Name: monsterName);
            }

            var hpVisible = CharacterStatsVisibility.IsVisibleTo(character.ControlledByDm, character.NpcStatsVisibleToParty, isDm);
            return hpVisible
                ? new TokenResponse(t.Id, t.EntityType, t.EntityId, t.X, t.Y, character.CurrentHp, character.MaxHp, character.Name)
                : new TokenResponse(t.Id, t.EntityType, t.EntityId, t.X, t.Y, Name: character.Name);
        }).ToList();

        return new SceneResponse(
            scene.Id, scene.SessionId, scene.Name, scene.Grid, scene.MapAssetId, imageUrl, scene.IsActive,
            tokenResponses, fogAreas.Select(a => new FogAreaResponse(a.Id, a.X, a.Y, a.Radius)).ToList(),
            ProjectTerrain(scene, fogAreas, isDm));
    }

    /// <summary>
    /// Sprint H Part 6 — พื้นเดินยาก/กำแพงของฉากตามสิทธิ์ของผู้ดู: DM ได้ทั้งฉาก ผู้เล่นได้เฉพาะช่องในพื้นที่
    /// ที่เปิดหมอกแล้ว · **กรองที่ server เท่านั้น** ไม่ส่งทั้งก้อนไปให้ client ซ่อน (§13.3) — กำแพงในหมอกคือ
    /// ข้อมูลว่าแผนที่ส่วนที่ยังไม่สำรวจหน้าตาเป็นยังไง ซึ่งผู้เล่นยังไม่ควรรู้
    ///
    /// ใช้กติกา "มองเห็นไหม" ตัวเดียวกับ token (<see cref="FogLayerSerialization.IsVisibleTo"/>) ห้ามเขียน
    /// สูตรวงกลมใหม่ · เรียงตาม Y แล้ว X แบบเดียวกับ <c>SceneTerrainEndpoints</c>
    /// </summary>
    private static TerrainResponse ProjectTerrain(Scene scene, IReadOnlyList<FogRevealedArea> fogAreas, bool isDm)
    {
        var layer = SceneTerrainSerialization.Parse(scene.Terrain);

        List<TerrainSquareResponse> Visible(IEnumerable<DnDEz.Rules.GridPoint> squares) => squares
            .Where(p => FogLayerSerialization.IsVisibleTo(p.X, p.Y, fogAreas, isDm))
            .OrderBy(p => p.Y).ThenBy(p => p.X)
            .Select(p => new TerrainSquareResponse(p.X, p.Y))
            .ToList();

        return new TerrainResponse(Visible(layer.Difficult), Visible(layer.Blocked));
    }

}

internal record UploadErrorResponse(string Reason);

internal record MapAssetCreatedResponse(Guid Id, Guid CampaignId, string ReviewStatus);

internal record CreateSceneRequest(string? Name, Guid MapAssetId);

internal record UpdateGridRequest(double? PixelsPerSquare, double? OffsetX, double? OffsetY);

/// <param name="Terrain">Sprint H Part 6 — พื้นเดินยาก/กำแพง กรองตามหมอกแล้วสำหรับผู้เล่น (ดู ProjectTerrain)</param>
internal record SceneResponse(
    Guid Id, Guid SessionId, string Name, string Grid, Guid? MapAssetId, string? ImageUrl, bool IsActive,
    List<TokenResponse> Tokens, List<FogAreaResponse> RevealedAreas, TerrainResponse Terrain);
