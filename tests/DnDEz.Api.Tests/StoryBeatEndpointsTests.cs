using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint G Part 2 — API ของ `story_beats` + active-beat (design doc §11.1, §12)
///
/// **contract เต็ม** (ห้ามแก้เทสต์เพื่อให้ผ่าน — แก้ได้เฉพาะกรณีเทสต์เขียนผิด spec จริงและต้องบันทึกเหตุผล):
///
/// GET    /campaigns/{cid}/beats                         [DM เท่านั้น — beat มี Secrets/DmNotes]
///        200 + BeatResponse[] เรียงตาม Order · 404 ไม่ใช่สมาชิก · 403 สมาชิกที่ไม่ใช่ DM
///
/// POST   /campaigns/{cid}/beats                          [DM เท่านั้น]
///        body { Title (required, ห้ามว่าง), Summary?, Objective?, EntryTrigger?, ExitConditions?,
///        DmNotes?, Secrets? } · 201 + BeatResponse (Order = ต่อท้ายอันสุดท้าย, Progress=Upcoming)
///        400 = Title ว่าง/หาย · 403/404 เหมือนบน
///
/// PATCH  /campaigns/{cid}/beats/{bid}                    [DM เท่านั้น]
///        body เดียวกับ POST ทุก field optional — **null = ไม่แก้ฟิลด์นั้น** (partial update)
///        ยกเว้น Title: ส่งมาต้องไม่ว่าง (400) · ไม่แตะ Progress/MonsterRefs/NpcRefs/ReviewStatus
///        200 + BeatResponse ที่อัปเดตแล้ว · 404 = beat ไม่อยู่ในแคมเปญนี้ (รวมกรณีอยู่แคมเปญอื่น)
///
/// DELETE /campaigns/{cid}/beats/{bid}                    [DM เท่านั้น]
///        204 · 404 เหมือนบน
///
/// PATCH  /campaigns/{cid}/beats/order                    [DM เท่านั้น]
///        body { BeatIds: guid[] } ต้องมี **ครบทุก beat ของแคมเปญนี้พอดี** (ไม่ขาด/ไม่เกิน/ไม่ซ้ำ)
///        ตั้ง Order = index ในอาเรย์ (0-based) ทีเดียวทั้งหมด · 200 + BeatResponse[] เรียงตามใหม่
///        400 = อาเรย์ไม่ตรงกับ beat ที่มีอยู่จริง (ขาด/เกิน/ซ้ำ)
///
/// POST   /campaigns/{cid}/sessions/{sid}/active-beat     [DM เท่านั้น — หัวใจของ Part นี้ §12.1]
///        body { BeatId: guid } · beat เดิมที่ Active (ถ้ามีและคนละตัว) → Passed, beat ใหม่ → Active
///        ในธุรกรรมเดียว · broadcast "ActiveBeatChanged" ออกกลุ่มแคมเปญ · 200 + ActiveBeatResponse
///        404 = session ไม่อยู่แคมเปญนี้ / beat ไม่อยู่แคมเปญนี้ · ยิงซ้ำ beat เดิมที่ Active
///        อยู่แล้ว = no-op (ยังคืน 200 แต่ PreviousActiveBeatId == ActiveBeatId ไม่มีการเขียน/broadcast)
///
/// **`ix_story_beats_one_active_per_campaign`** — partial unique index บังคับ Active ได้ทีละอันจริง
/// ระดับ DB (ไม่ใช่แค่ endpoint logic) ดู <see cref="Direct_sql_cannot_set_two_beats_active_in_same_campaign"/>
/// </summary>
public class StoryBeatEndpointsTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Password = "story-beat-endpoints-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _otherCampaignId = Guid.NewGuid();

    private readonly Guid _beat0Id = Guid.NewGuid(); // order 0
    private readonly Guid _beat1Id = Guid.NewGuid(); // order 1
    private readonly Guid _beat2Id = Guid.NewGuid(); // order 2
    private readonly Guid _otherCampaignBeatId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _dmUserId;
    private string _dmToken = null!;
    private Guid _playerUserId;
    private string _playerToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_dmUserId, _dmToken) = await RegisterAsync("dm");
        (_playerUserId, _playerToken) = await RegisterAsync("player");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await InsertCampaignAsync(conn, _campaignId, _dmUserId, $"Beat Campaign {_suffix}");
        await InsertCampaignAsync(conn, _otherCampaignId, _outsiderUserId, $"Other Campaign {_suffix}");
        await InsertMembershipAsync(conn, _campaignId, _dmUserId, "Dm");
        await InsertMembershipAsync(conn, _campaignId, _playerUserId, "Player");
        await InsertMembershipAsync(conn, _otherCampaignId, _outsiderUserId, "Dm");
        await InsertSessionAsync(conn, _sessionId, _campaignId);

        await InsertBeatAsync(conn, _beat0Id, _campaignId, 0, "Arrival at port");
        await InsertBeatAsync(conn, _beat1Id, _campaignId, 1, "Drowned temple");
        await InsertBeatAsync(conn, _beat2Id, _campaignId, 2, "Spire ascent");
        await InsertBeatAsync(conn, _otherCampaignBeatId, _otherCampaignId, 0, "Outsider's beat");
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using (var deleteCampaigns = new NpgsqlCommand("DELETE FROM campaigns WHERE id = ANY(@ids)", conn))
        {
            deleteCampaigns.Parameters.AddWithValue("ids", new[] { _campaignId, _otherCampaignId });
            await deleteCampaigns.ExecuteNonQueryAsync();
        }
        await using (var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn))
        {
            deleteUsers.Parameters.AddWithValue("ids", new[] { _dmUserId, _playerUserId, _outsiderUserId });
            await deleteUsers.ExecuteNonQueryAsync();
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

    private static async Task InsertBeatAsync(
        NpgsqlConnection conn, Guid id, Guid campaignId, int order, string title)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO story_beats
                (id, campaign_id, "order", title, exit_conditions, monster_refs, npc_refs, secrets,
                 progress, review_status)
            VALUES
                (@id, @campaignId, @order, @title, ARRAY[]::text[], ARRAY[]::uuid[], ARRAY[]::uuid[],
                 '[]', 'Upcoming', 'Approved')
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("campaignId", campaignId);
        cmd.Parameters.AddWithValue("order", order);
        cmd.Parameters.AddWithValue("title", title);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> GetProgressAsync(NpgsqlConnection conn, Guid beatId)
    {
        await using var cmd = new NpgsqlCommand("SELECT progress FROM story_beats WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", beatId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"storybeat-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"StoryBeat {label}",
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

    private HubConnection BuildConnection(string? token)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/campaign"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = _factory.Server.CreateWebSocketClient();
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .Build();
    }

    private record AuthResponse(string Token, Guid UserId);

    private record BeatResponse(
        Guid Id, int Order, string Title, string? Summary, string? Objective, string? EntryTrigger,
        string[] ExitConditions, string? DmNotes, string Secrets, string Progress, string ReviewStatus);

    private record ActiveBeatResponse(Guid SessionId, Guid CampaignId, Guid ActiveBeatId, Guid? PreviousActiveBeatId);

    // ── GET list ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_lists_beats_ordered_by_order()
    {
        var response = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/beats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var beats = await response.Content.ReadFromJsonAsync<List<BeatResponse>>(JsonOptions);
        Assert.NotNull(beats);
        Assert.Equal(3, beats!.Count);
        Assert.Equal(new[] { "Arrival at port", "Drowned temple", "Spire ascent" }, beats.Select(b => b.Title));
        Assert.Equal(new[] { 0, 1, 2 }, beats.Select(b => b.Order));
    }

    [Fact]
    public async Task Player_listing_beats_gets_403()
    {
        var response = await AuthedClient(_playerToken).GetAsync($"/campaigns/{_campaignId}/beats");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Outsider_listing_beats_gets_404_not_403()
    {
        var response = await AuthedClient(_outsiderToken).GetAsync($"/campaigns/{_campaignId}/beats");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── POST create ───────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_creates_beat_appended_at_end_with_upcoming_progress()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync($"/campaigns/{_campaignId}/beats", new
        {
            Title = "New chapter",
            Summary = "A summary",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var beat = await response.Content.ReadFromJsonAsync<BeatResponse>(JsonOptions);
        Assert.NotNull(beat);
        Assert.Equal("New chapter", beat!.Title);
        Assert.Equal(3, beat.Order); // ต่อท้าย beat0/1/2 ที่มีอยู่แล้ว (order 0,1,2)
        Assert.Equal("Upcoming", beat.Progress);
    }

    [Fact]
    public async Task Create_with_empty_title_returns_400()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync($"/campaigns/{_campaignId}/beats", new
        {
            Title = "",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Player_creating_beat_gets_403()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync($"/campaigns/{_campaignId}/beats", new
        {
            Title = "Sneaky beat",
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── PATCH update ──────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_updates_beat_fields_partially()
    {
        var response = await AuthedClient(_dmToken).PatchAsJsonAsync($"/campaigns/{_campaignId}/beats/{_beat1Id}", new
        {
            Summary = "Updated summary",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var beat = await response.Content.ReadFromJsonAsync<BeatResponse>(JsonOptions);
        Assert.Equal("Updated summary", beat!.Summary);
        Assert.Equal("Drowned temple", beat.Title); // ไม่ส่ง Title มา → ไม่เปลี่ยน
    }

    [Fact]
    public async Task Update_with_empty_title_returns_400()
    {
        var response = await AuthedClient(_dmToken).PatchAsJsonAsync($"/campaigns/{_campaignId}/beats/{_beat1Id}", new
        {
            Title = "",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Updating_beat_from_other_campaign_returns_404()
    {
        var response = await AuthedClient(_dmToken).PatchAsJsonAsync(
            $"/campaigns/{_campaignId}/beats/{_otherCampaignBeatId}", new { Summary = "hijack" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── DELETE ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dm_deletes_beat()
    {
        var response = await AuthedClient(_dmToken).DeleteAsync($"/campaigns/{_campaignId}/beats/{_beat2Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await AuthedClient(_dmToken).GetAsync($"/campaigns/{_campaignId}/beats");
        var beats = await getResponse.Content.ReadFromJsonAsync<List<BeatResponse>>(JsonOptions);
        Assert.DoesNotContain(beats!, b => b.Id == _beat2Id);
    }

    [Fact]
    public async Task Player_deleting_beat_gets_403()
    {
        var response = await AuthedClient(_playerToken).DeleteAsync($"/campaigns/{_campaignId}/beats/{_beat0Id}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── PATCH order (reorder) ────────────────────────────────────────────

    [Fact]
    public async Task Dm_reorders_all_beats_in_one_call()
    {
        var response = await AuthedClient(_dmToken).PatchAsJsonAsync($"/campaigns/{_campaignId}/beats/order", new
        {
            BeatIds = new[] { _beat2Id, _beat0Id, _beat1Id },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var beats = await response.Content.ReadFromJsonAsync<List<BeatResponse>>(JsonOptions);
        Assert.NotNull(beats);
        Assert.Equal(new[] { _beat2Id, _beat0Id, _beat1Id }, beats!.Select(b => b.Id));
        Assert.Equal(new[] { 0, 1, 2 }, beats.Select(b => b.Order));
    }

    [Fact]
    public async Task Reorder_with_missing_beat_id_returns_400()
    {
        // ขาด _beat2Id ไป — ต้องปฏิเสธ ไม่ใช่เงียบ ๆ ปล่อยให้ order ไม่ครบ
        var response = await AuthedClient(_dmToken).PatchAsJsonAsync($"/campaigns/{_campaignId}/beats/order", new
        {
            BeatIds = new[] { _beat0Id, _beat1Id },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reorder_with_beat_from_another_campaign_returns_400()
    {
        var response = await AuthedClient(_dmToken).PatchAsJsonAsync($"/campaigns/{_campaignId}/beats/order", new
        {
            BeatIds = new[] { _beat0Id, _beat1Id, _otherCampaignBeatId },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reorder_with_duplicate_beat_id_returns_400()
    {
        // beat0 ซ้ำสองรอบ แทน beat2 — ความยาวอาเรย์ครบ 3 แต่ id ไม่ครบตามจริง (มี beat2 หายไป)
        var response = await AuthedClient(_dmToken).PatchAsJsonAsync($"/campaigns/{_campaignId}/beats/order", new
        {
            BeatIds = new[] { _beat0Id, _beat0Id, _beat1Id },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── POST active-beat ──────────────────────────────────────────────────

    [Fact]
    public async Task Setting_active_beat_promotes_new_and_demotes_previous_in_one_transaction()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        var first = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/active-beat", new { BeatId = _beat0Id });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("Active", await GetProgressAsync(conn, _beat0Id));

        var second = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/active-beat", new { BeatId = _beat1Id });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<ActiveBeatResponse>(JsonOptions);
        Assert.Equal(_beat1Id, body!.ActiveBeatId);
        Assert.Equal(_beat0Id, body.PreviousActiveBeatId);

        Assert.Equal("Passed", await GetProgressAsync(conn, _beat0Id));   // beat เดิม → Passed
        Assert.Equal("Active", await GetProgressAsync(conn, _beat1Id));  // beat ใหม่ → Active
        Assert.Equal("Upcoming", await GetProgressAsync(conn, _beat2Id)); // ตัวที่ไม่เกี่ยวไม่ถูกแตะ
    }

    [Fact]
    public async Task Setting_active_beat_to_already_active_beat_is_a_no_op()
    {
        await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/active-beat", new { BeatId = _beat0Id });

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/active-beat", new { BeatId = _beat0Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ActiveBeatResponse>(JsonOptions);
        Assert.Equal(_beat0Id, body!.ActiveBeatId);
        Assert.Equal(_beat0Id, body.PreviousActiveBeatId);
    }

    [Fact]
    public async Task Active_beat_from_another_campaign_returns_404()
    {
        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/active-beat", new { BeatId = _otherCampaignBeatId });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Player_setting_active_beat_gets_403()
    {
        var response = await AuthedClient(_playerToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/active-beat", new { BeatId = _beat0Id });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Setting_active_beat_broadcasts_to_campaign_group()
    {
        await using var connection = BuildConnection(_dmToken);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>("ActiveBeatChanged", payload => received.TrySetResult(payload));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinCampaign", _campaignId);

        var response = await AuthedClient(_dmToken).PostAsJsonAsync(
            $"/campaigns/{_campaignId}/sessions/{_sessionId}/active-beat", new { BeatId = _beat0Id });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(received.Task, completed);

        var payload = await received.Task;
        Assert.Equal(_beat0Id, payload.GetProperty("activeBeatId").GetGuid());
    }

    // ── DB-level invariant — ไม่พึ่งแค่ endpoint logic ───────────────────────

    [Fact]
    public async Task Direct_sql_cannot_set_two_beats_active_in_same_campaign()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using var setFirst = new NpgsqlCommand(
            "UPDATE story_beats SET progress = 'Active' WHERE id = @id", conn);
        setFirst.Parameters.AddWithValue("id", _beat0Id);
        await setFirst.ExecuteNonQueryAsync();

        await using var setSecond = new NpgsqlCommand(
            "UPDATE story_beats SET progress = 'Active' WHERE id = @id", conn);
        setSecond.Parameters.AddWithValue("id", _beat1Id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => setSecond.ExecuteNonQueryAsync());
        Assert.Equal("23505", ex.SqlState); // unique_violation
    }
}
