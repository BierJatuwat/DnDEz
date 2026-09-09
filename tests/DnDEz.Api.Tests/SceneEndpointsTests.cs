using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 4 — อัปโหลดแผนที่ (map asset) + scene + จัดกริด (design doc §11.1, §22)
///
/// **contract เต็ม** (ห้ามแก้เทสต์เพื่อให้ผ่าน — แก้ได้เฉพาะกรณีเทสต์เขียนผิด spec จริงและต้องบันทึกเหตุผล):
///
/// POST /campaigns/{cid}/map-assets                       [DM เท่านั้น, multipart/form-data field "file"]
///      201 + { Id, CampaignId, ReviewStatus="Approved" } · 400 = ไม่ใช่ไฟล์รูปจริง (magic bytes)/
///      ใหญ่เกิน MaxUploadBytes/quota เต็ม · 403/404 เหมือน endpoint อื่น
///
/// POST /campaigns/{cid}/sessions/{sid}/scene             [DM เท่านั้น]
///      body { Name, MapAssetId } · 201 + SceneResponse (IsActive=true, ImageUrl เป็น signed URL
///      สด) · scene เดิมที่ active (ถ้ามี) ถูก demote อัตโนมัติ (ix_scenes_one_active_per_session)
///      400 = MapAssetId ไม่อยู่ในแคมเปญนี้ · 404 = session ไม่อยู่ในแคมเปญนี้
///
/// GET  /campaigns/{cid}/sessions/{sid}/scene             [DM เท่านั้น — Part นี้ยังไม่ทำ 3 มุมมอง]
///      200 + SceneResponse ของ scene ที่ active · 404 = ไม่มี active scene / session มองไม่เห็น
///
/// PATCH /campaigns/{cid}/scenes/{scid}/grid              [DM เท่านั้น]
///      body { PixelsPerSquare, OffsetX?, OffsetY? } · 200 + SceneResponse ที่ Grid อัปเดตแล้ว
///      400 = PixelsPerSquare <= 0 · 404 = scene ไม่อยู่ในแคมเปญนี้
///
/// **signed URL ต้องสั้น + bucket ต้องไม่ public** — ดู
/// <see cref="Get_scene_returns_signed_url_that_resolves_to_uploaded_bytes"/> และ
/// <see cref="Anonymous_unsigned_request_to_object_is_denied"/>
/// </summary>
public class SceneEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "scene-endpoints-secret-1";

    // 1x1 transparent PNG ที่เล็กที่สุดเท่าที่ทำได้ (fixture มาตรฐานที่ใช้ทดสอบ image upload ทั่วไป)
    private static readonly byte[] MinimalPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAIAAAUAAen63NgAAAAASUVORK5CYII=");

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

    private const string Bucket = "dndez-map-assets";

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Scene Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertSessionAsync(conn, _sessionId, _campaignId);
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using (var deleteCampaigns = new NpgsqlCommand("DELETE FROM campaigns WHERE id = @id", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("id", _campaignId);
            await deleteCampaigns.ExecuteNonQueryAsync();
        }
        await using (var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn))
        {
            deleteUsers.Parameters.AddWithValue("ids", new[] { _dmUserId, _playerUserId, _outsiderUserId });
            await deleteUsers.ExecuteNonQueryAsync();
        }
        // reset quota เดิมของ _dmUserId เผื่อเทสต์ quota_exceeded ตั้งไว้ต่ำ ไม่งั้นรันซ้ำแล้วชนกันข้ามเทสต์
        await using (var deleteQuota = new NpgsqlCommand("DELETE FROM storage_quotas WHERE user_id = @id", conn))
        {
            deleteQuota.Parameters.AddWithValue("id", _dmUserId);
            await deleteQuota.ExecuteNonQueryAsync();
        }
        await _factory.DisposeAsync();
    }

    // ── fixture helpers ─────────────────────────────────────────────────────

    private static async Task InsertCampaignAsync(NpgsqlConnection conn, Guid id, Guid ownerUserId, string name)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES (@id, @ownerId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("ownerId", ownerUserId);
        cmd.Parameters.AddWithValue("name", name);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertMembershipAsync(NpgsqlConnection conn, Guid campaignId, Guid userId, string role)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, @role, now())
            """, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("role", role);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertSessionAsync(NpgsqlConnection conn, Guid id, Guid campaignId)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO sessions (id, campaign_id, mode, status, join_code, started_at, ended_at)
            VALUES (@id, @campaignId, 'Online', 'Active', NULL, now(), NULL)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"scene-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Scene {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
    }

    private HttpClient AuthedClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static MultipartFormDataContent BuildUploadContent(byte[] bytes, string fileName, string contentType)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);
        return content;
    }

    private async Task<Guid> UploadValidMapAssetAsync()
    {
        var response = await AuthedClient(_dmToken).PostAsync(
            $"/campaigns/{_campaignId}/map-assets", BuildUploadContent(MinimalPngBytes, "map.png", "image/png"));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<MapAssetCreatedResponse>(JsonOptions);
        return body!.Id;
    }

    private record AuthResponse(string Token, Guid UserId);

    private record MapAssetCreatedResponse(Guid Id, Guid CampaignId, string ReviewStatus);

    private record UploadErrorResponse(string Reason);

    private record SceneResponse(Guid Id, Guid SessionId, string Name, string Grid, Guid? MapAssetId, string? ImageUrl, bool IsActive);

    // ── อัปโหลด ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_uploads_valid_png_successfully()
    {
        var response = await AuthedClient(_dmToken).PostAsync(
            $"/campaigns/{_campaignId}/map-assets", BuildUploadContent(MinimalPngBytes, "map.png", "image/png"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MapAssetCreatedResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal("Approved", body!.ReviewStatus);
    }

    [Fact]
    public async Task Upload_increments_storage_quota_used_bytes()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await UploadValidMapAssetAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT used_bytes FROM storage_quotas WHERE user_id = @id", conn);
        cmd.Parameters.AddWithValue("id", _dmUserId);
        var usedBytes = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(MinimalPngBytes.Length, usedBytes);
    }

    [Fact]
    public async Task Upload_rejects_non_image_bytes_disguised_with_png_extension_and_content_type()
    {
        // ไฟล์ข้อความล้วน ๆ แต่งชื่อ + Content-Type ให้ดูเหมือนรูป — endpoint ต้องเช็ค magic bytes จริง
        // ไม่ใช่เชื่อสองอย่างนี้ ไม่งั้นข้อความนี้จะผ่านเป็น "รูป" ได้ทั้งที่ไม่ใช่เลย
        var fakeBytes = System.Text.Encoding.UTF8.GetBytes("<script>alert('not a real image')</script>");
        var response = await AuthedClient(_dmToken).PostAsync(
            $"/campaigns/{_campaignId}/map-assets", BuildUploadContent(fakeBytes, "map.png", "image/png"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UploadErrorResponse>(JsonOptions);
        Assert.Equal("invalid_file_type", body!.Reason);
    }

    [Fact]
    public async Task Upload_over_size_limit_is_rejected()
    {
        var oversized = new byte[15 * 1024 * 1024 + 1];
        var response = await AuthedClient(_dmToken).PostAsync(
            $"/campaigns/{_campaignId}/map-assets", BuildUploadContent(oversized, "big.png", "image/png"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UploadErrorResponse>(JsonOptions);
        Assert.Equal("file_too_large", body!.Reason);
    }

    [Fact]
    public async Task Upload_exceeding_quota_is_rejected()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using (var setQuota = new NpgsqlCommand(
            """
            INSERT INTO storage_quotas (user_id, used_bytes, limit_bytes) VALUES (@id, 0, 10)
            ON CONFLICT (user_id) DO UPDATE SET used_bytes = 0, limit_bytes = 10
            """, conn))
        {
            setQuota.Parameters.AddWithValue("id", _dmUserId);
            await setQuota.ExecuteNonQueryAsync();
        }

        // MinimalPngBytes ยาวกว่า 10 ไบต์แน่นอน (จริง ๆ ยาวกว่า 60 ไบต์)
        var response = await AuthedClient(_dmToken).PostAsync(
            $"/campaigns/{_campaignId}/map-assets", BuildUploadContent(MinimalPngBytes, "map.png", "image/png"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UploadErrorResponse>(JsonOptions);
        Assert.Equal("quota_exceeded", body!.Reason);
    }

    [Fact]
    public async Task Player_uploading_gets_403()
    {
        var response = await AuthedClient(_playerToken).PostAsync(
            $"/campaigns/{_campaignId}/map-assets", BuildUploadContent(MinimalPngBytes, "map.png", "image/png"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── สร้าง scene ────────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_creates_scene_from_uploaded_map_asset()
    {
        var mapAssetId = await UploadValidMapAssetAsync();

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/scene", new { Name = "The Drowned Temple", MapAssetId = mapAssetId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var scene = await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);
        Assert.NotNull(scene);
        Assert.True(scene!.IsActive);
        Assert.Equal(mapAssetId, scene.MapAssetId);
        Assert.NotNull(scene.ImageUrl);
    }

    [Fact]
    public async Task Creating_second_scene_demotes_the_first()
    {
        var mapAssetId = await UploadValidMapAssetAsync();

        var first = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/scene", new { Name = "Scene One", MapAssetId = mapAssetId });
        var firstScene = await first.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);

        var second = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/scene", new { Name = "Scene Two", MapAssetId = mapAssetId });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT is_active FROM scenes WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", firstScene!.Id);
        Assert.False((bool)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Create_scene_with_map_asset_from_another_campaign_returns_400()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        var otherCampaignId = Guid.NewGuid();
        await InsertCampaignAsync(conn, otherCampaignId, _outsiderUserId, $"Other {_suffix}");
        var foreignMapAssetId = Guid.NewGuid();
        await using (var insertMap = new NpgsqlCommand(
            """
            INSERT INTO map_assets (id, campaign_id, storage_key, review_status)
            VALUES (@id, @campaignId, 'unused-key', 'Approved')
            """, conn))
        {
            insertMap.Parameters.AddWithValue("id", foreignMapAssetId);
            insertMap.Parameters.AddWithValue("campaignId", otherCampaignId);
            await insertMap.ExecuteNonQueryAsync();
        }

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/scene",
            new { Name = "Hijack attempt", MapAssetId = foreignMapAssetId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var cleanup = new NpgsqlCommand("DELETE FROM campaigns WHERE id = @id", conn);
        cleanup.Parameters.AddWithValue("id", otherCampaignId);
        await cleanup.ExecuteNonQueryAsync();
    }

    // ── อ่าน scene ────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_scene_when_none_active_returns_404()
    {
        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_scene_returns_signed_url_that_resolves_to_uploaded_bytes()
    {
        var mapAssetId = await UploadValidMapAssetAsync();
        await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/scene", new { Name = "Scene", MapAssetId = mapAssetId });

        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var scene = await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);
        Assert.NotNull(scene!.ImageUrl);

        // พิสูจน์ว่า signed URL ใช้งานได้จริง (ไม่ใช่แค่ string หน้าตาเหมือน URL) — โหลดผ่าน HttpClient
        // ตรง ๆ แล้วเทียบ byte ต่อ byte กับไฟล์ที่อัปโหลดไป
        using var plainClient = new HttpClient();
        var imageBytes = await plainClient.GetByteArrayAsync(scene.ImageUrl);
        Assert.Equal(MinimalPngBytes, imageBytes);
    }

    // ── security — bucket ต้องไม่ public ──────────────────────────────────

    [Fact]
    public async Task Anonymous_unsigned_request_to_object_is_denied()
    {
        var mapAssetId = await UploadValidMapAssetAsync();

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT storage_key FROM map_assets WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", mapAssetId);
        var storageKey = (string)(await cmd.ExecuteScalarAsync())!;

        // ยิงตรงไป MinIO โดยไม่มี signature เลย (เหมือนคนที่รู้แค่ storage key เดา ๆ หรือรู้ path
        // แต่ไม่มี query string signature ที่ signed URL แนบมาด้วย) ต้องโดนปฏิเสธ ไม่ใช่โหลดได้เฉย ๆ
        using var plainClient = new HttpClient();
        var response = await plainClient.GetAsync($"http://localhost:9000/{Bucket}/{storageKey}");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // ── จัดกริด ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_updates_grid_pixels_per_square()
    {
        var mapAssetId = await UploadValidMapAssetAsync();
        var createResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/scene", new { Name = "Scene", MapAssetId = mapAssetId });
        var scene = await createResponse.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);

        var response = await AuthedClient(_dmToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{scene!.Id}/grid",
            new { PixelsPerSquare = 42.5, OffsetX = 3, OffsetY = 7 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);
        Assert.Contains("42.5", updated!.Grid);
    }

    [Fact]
    public async Task Grid_with_non_positive_pixels_per_square_returns_400()
    {
        var mapAssetId = await UploadValidMapAssetAsync();
        var createResponse = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/scene", new { Name = "Scene", MapAssetId = mapAssetId });
        var scene = await createResponse.Content.ReadFromJsonAsync<SceneResponse>(JsonOptions);

        var response = await AuthedClient(_dmToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/scenes/{scene!.Id}/grid", new { PixelsPerSquare = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Outsider_gets_404_on_scene_endpoints()
    {
        var response = await AuthedClient(_outsiderToken).GetAsync($"/campaigns/{_campaignId}/sessions/{_sessionId}/scene");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
