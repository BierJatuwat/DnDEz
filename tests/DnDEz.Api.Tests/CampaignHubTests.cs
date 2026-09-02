using System.Net.Http.Json;
using System.Text.Json;
using DnDEz.Api.Realtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DnDEz.Api.Tests;

/// <summary>
/// Sprint C Part 1 — โครง SignalR hub (connection + group + authorization) ยังไม่ broadcast อะไรจริง
/// (Part 2/3 จะ wire dice-roll/HP-update เข้ามาทีหลัง) รอบนี้ทดสอบแค่ว่า:
///
/// contract:
///  • hub อยู่ที่ /hubs/campaign — ต้องมี JWT (query string `access_token` เพราะ browser WebSocket
///    ตั้ง header เองไม่ได้) ไม่งั้น connection ต้อง reject ตั้งแต่ StartAsync
///  • `JoinCampaign(campaignId)` join ได้เฉพาะสมาชิกแคมเปญจริง — ไม่ใช่สมาชิก/แคมเปญไม่มีจริง ต้องโยน
///    HubException ข้อความเดียวกันทั้งคู่ (กัน enumeration เหมือน REST 404)
///  • หลัง join สำเร็จ ต้อง broadcast ถึง group นั้นได้จริง (ยืนยันด้วย server ยิง test message
///    เข้า group ตรง ๆ ผ่าน IHubContext แล้ว client เห็น) — พิสูจน์ pipe ทำงานจริงก่อนที่ Part 2/3
///    จะเอาไปใช้ broadcast ของจริง
///
/// ⚠️ ต้องมี Redis จริงรันอยู่ (docker compose up -d redis) — ไม่ mock backplane ตามธรรมเนียมโปรเจกต์
/// ที่ห้าม mock dependency ภายนอกที่มี integration จริงให้ทดสอบได้
/// </summary>
public class CampaignHubTests : IAsyncLifetime
{
    private const string OwnerConnectionString =
        "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const string Password = "campaign-hub-secret-1";

    private readonly string _suffix = Guid.NewGuid().ToString("N");
    private readonly Guid _campaignId = Guid.NewGuid();
    private readonly Guid _campaignOwnerFixtureId = Guid.NewGuid();

    private WebApplicationFactory<Program> _factory = null!;

    private Guid _memberUserId;
    private string _memberToken = null!;
    private Guid _outsiderUserId;
    private string _outsiderToken = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>();
        (_memberUserId, _memberToken) = await RegisterAsync("member");
        (_outsiderUserId, _outsiderToken) = await RegisterAsync("outsider");

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await using (var insertOwner = new NpgsqlCommand(
            """
            INSERT INTO users (id, email, display_name, password_hash, created_at)
            VALUES (@id, @email, 'Hub Campaign Owner', 'not-a-real-hash', now())
            """, conn))
        {
            insertOwner.Parameters.AddWithValue("id", _campaignOwnerFixtureId);
            insertOwner.Parameters.AddWithValue("email", $"hub-owner-{_suffix}@example.test");
            await insertOwner.ExecuteNonQueryAsync();
        }

        await using (var insertCampaign = new NpgsqlCommand(
            """
            INSERT INTO campaigns
                (id, owner_user_id, name, mode, rules_system, dice_mode, death_save_visibility, created_at)
            VALUES
                (@id, @ownerId, @name, 'Guided', 'srd2024', 'SystemRoll', 'PartyVisible', now())
            """, conn))
        {
            insertCampaign.Parameters.AddWithValue("id", _campaignId);
            insertCampaign.Parameters.AddWithValue("ownerId", _campaignOwnerFixtureId);
            insertCampaign.Parameters.AddWithValue("name", $"Hub Campaign {_suffix}");
            await insertCampaign.ExecuteNonQueryAsync();
        }

        await using (var insertMembership = new NpgsqlCommand(
            """
            INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
            VALUES (@id, @campaignId, @userId, 'Player', now())
            """, conn))
        {
            insertMembership.Parameters.AddWithValue("id", Guid.NewGuid());
            insertMembership.Parameters.AddWithValue("campaignId", _campaignId);
            insertMembership.Parameters.AddWithValue("userId", _memberUserId);
            await insertMembership.ExecuteNonQueryAsync();
        }
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

        var userIds = new[] { _campaignOwnerFixtureId, _memberUserId, _outsiderUserId }
            .Where(id => id != Guid.Empty).ToArray();
        await using (var deleteUsers = new NpgsqlCommand("DELETE FROM users WHERE id = ANY(@ids)", conn))
        {
            deleteUsers.Parameters.AddWithValue("ids", userIds);
            await deleteUsers.ExecuteNonQueryAsync();
        }

        await _factory.DisposeAsync();
    }

    private async Task<(Guid Id, string Token)> RegisterAsync(string label)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register", new
        {
            Email = $"campaign-hub-{label}-{_suffix}@example.test",
            Password,
            DisplayName = $"Campaign Hub {label}",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        return (body!.UserId, body.Token);
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

    [Fact]
    public async Task Member_can_connect_and_join_own_campaign_group()
    {
        await using var connection = BuildConnection(_memberToken);
        await connection.StartAsync();

        // ไม่ throw = ผ่าน (JoinCampaign คืน void — ยืนยัน "join จริง" ในเทสต์ broadcast ถัดไป)
        await connection.InvokeAsync("JoinCampaign", _campaignId);

        await connection.StopAsync();
    }

    [Fact]
    public async Task Outsider_joining_campaign_they_are_not_a_member_of_throws_hub_exception()
    {
        await using var connection = BuildConnection(_outsiderToken);
        await connection.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("JoinCampaign", _campaignId));
        Assert.Contains("campaign_not_found", ex.Message);

        await connection.StopAsync();
    }

    [Fact]
    public async Task Joining_unknown_campaign_id_throws_same_error_as_non_member_no_enumeration()
    {
        await using var connection = BuildConnection(_memberToken);
        await connection.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("JoinCampaign", Guid.NewGuid()));
        Assert.Contains("campaign_not_found", ex.Message);

        await connection.StopAsync();
    }

    [Fact]
    public async Task Connecting_without_jwt_fails_to_start()
    {
        await using var connection = BuildConnection(token: null);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    /// <summary>
    /// พบระหว่าง code review (Sprint C Part 1) — ยืนยันด้วยเทสต์จริงว่า path-scoping ของ
    /// OnMessageReceived ใน Program.cs (ยอมรับ token จาก query string เฉพาะ path ที่ขึ้นต้นด้วย
    /// /hubs/campaign) ปิดจริง ไม่ใช่แค่อ่านโค้ดแล้วเชื่อ — REST endpoint ต้องยังคง reject token ที่มา
    /// ทาง query string เสมอ (ต้องผ่าน Authorization header เท่านั้น) กัน regression ในอนาคตถ้ามีคน
    /// แก้เงื่อนไข path ให้กว้างขึ้นโดยไม่ตั้งใจ
    /// </summary>
    [Fact]
    public async Task Rest_endpoint_rejects_access_token_from_query_string_even_when_valid()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/campaigns?access_token={_memberToken}");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task After_joining_member_receives_broadcast_sent_to_campaign_group()
    {
        await using var connection = BuildConnection(_memberToken);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("TestBroadcast", message => received.TrySetResult(message));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinCampaign", _campaignId);

        // broadcast จริงจาก server เข้า group เดียวกับที่ JoinCampaign ใช้ — พิสูจน์ pipe end-to-end
        // ก่อนที่ Part 2/3 จะเอา IHubContext<CampaignHub> ตัวนี้ไปยิง event ของจริง (DiceRolled/HpUpdated)
        using var scope = _factory.Services.CreateScope();
        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<CampaignHub>>();
        await hubContext.Clients
            .Group(CampaignHub.GroupName(_campaignId))
            .SendAsync("TestBroadcast", "hello");

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(received.Task, completed);
        Assert.Equal("hello", await received.Task);

        await connection.StopAsync();
    }

    /// <summary>
    /// Sprint F งานที่ 3 — พิสูจน์คุณสมบัติหลักที่ CampaignGroupRevalidator มีอยู่เพื่อป้องกัน:
    /// เตะ connection ที่หมดสิทธิ์ (เพราะแถว campaign_members ถูกถอด/ลบนอกแอป — เทสต์นี้จำลองด้วย
    /// การลบตรงผ่าน owner connection) ออกจาก group จริง ไม่ใช่แค่คืนค่าตัวเลขถูกเฉย ๆ
    /// </summary>
    [Fact]
    public async Task Sweep_evicts_connection_after_membership_row_is_deleted_out_of_band()
    {
        await using var connection = BuildConnection(_memberToken);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("TestBroadcast", message => received.TrySetResult(message));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinCampaign", _campaignId);

        // ยืนยันก่อนว่ารับ broadcast ได้จริง (ฐานเทียบ) ก่อนถอดสมาชิกภาพ
        using (var scope = _factory.Services.CreateScope())
        {
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<CampaignHub>>();
            await hubContext.Clients.Group(CampaignHub.GroupName(_campaignId)).SendAsync("TestBroadcast", "before");
        }
        var firstCompleted = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(received.Task, firstCompleted);
        Assert.Equal("before", await received.Task);

        // ลบแถวสมาชิกภาพตรงผ่าน owner connection (dndez เป็น superuser ข้าม RLS ได้) — จำลองเคส
        // สมาชิกภาพถูกถอดนอกแอป (ไม่มีเส้นทาง C# ถอดได้จริงตอนนี้ — ดู doc comment ใน
        // CampaignGroupMembership.cs ว่าทำไม registry ต้องมี backstop สำหรับเคสนี้ด้วย)
        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await using var deleteMembership = new NpgsqlCommand(
                "DELETE FROM campaign_members WHERE campaign_id = @campaignId AND user_id = @userId", conn);
            deleteMembership.Parameters.AddWithValue("campaignId", _campaignId);
            deleteMembership.Parameters.AddWithValue("userId", _memberUserId);
            await deleteMembership.ExecuteNonQueryAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var revalidator = scope.ServiceProvider.GetRequiredService<CampaignGroupRevalidator>();
            var evicted = await revalidator.SweepAsync();
            Assert.Equal(1, evicted);
        }

        var receivedAfter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("TestBroadcast", message => receivedAfter.TrySetResult(message));

        using (var scope = _factory.Services.CreateScope())
        {
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<CampaignHub>>();
            await hubContext.Clients.Group(CampaignHub.GroupName(_campaignId)).SendAsync("TestBroadcast", "after");
        }

        var secondCompleted = await Task.WhenAny(receivedAfter.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.NotSame(receivedAfter.Task, secondCompleted);

        // คืนแถวสมาชิกภาพให้ DisposeAsync ทำงานตามปกติ (ไม่ error ตอนลบ user/campaign ท้ายเทสต์)
        await using (var conn = new NpgsqlConnection(OwnerConnectionString))
        {
            await conn.OpenAsync();
            await using var reinsertMembership = new NpgsqlCommand(
                """
                INSERT INTO campaign_members (id, campaign_id, user_id, role, joined_at)
                VALUES (@id, @campaignId, @userId, 'Player', now())
                """, conn);
            reinsertMembership.Parameters.AddWithValue("id", Guid.NewGuid());
            reinsertMembership.Parameters.AddWithValue("campaignId", _campaignId);
            reinsertMembership.Parameters.AddWithValue("userId", _memberUserId);
            await reinsertMembership.ExecuteNonQueryAsync();
        }

        await connection.StopAsync();
    }

    /// <summary>
    /// กันรีเกรสชันแบบ "fail closed = เตะทุกคนออก" — สมาชิกภาพที่ยังปกติต้องไม่โดนสแกนแล้วเตะ
    /// และต้องรับ broadcast ได้ต่อไปตามปกติหลังสแกน
    /// </summary>
    [Fact]
    public async Task Sweep_does_not_evict_member_whose_membership_is_still_valid()
    {
        await using var connection = BuildConnection(_memberToken);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("TestBroadcast", message => received.TrySetResult(message));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinCampaign", _campaignId);

        using (var scope = _factory.Services.CreateScope())
        {
            var revalidator = scope.ServiceProvider.GetRequiredService<CampaignGroupRevalidator>();
            var evicted = await revalidator.SweepAsync();
            Assert.Equal(0, evicted);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<CampaignHub>>();
            await hubContext.Clients.Group(CampaignHub.GroupName(_campaignId)).SendAsync("TestBroadcast", "still-here");
        }

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(received.Task, completed);
        Assert.Equal("still-here", await received.Task);

        await connection.StopAsync();
    }

    /// <summary>
    /// เส้นทางเตะทันที (ไม่รอรอบสแกน) — ตั้งใจไว้ให้ฟีเจอร์ในอนาคต (เช่น endpoint เตะสมาชิก) เรียก
    /// ตรงจุดที่ถอดสมาชิกภาพ เทสต์นี้ยืนยันว่าเรียกแล้ว group broadcast ไปไม่ถึงจริง
    /// </summary>
    [Fact]
    public async Task EvictMemberAsync_immediately_removes_connection_from_group()
    {
        await using var connection = BuildConnection(_memberToken);

        // ตัวแปร (ไม่ใช่ค่า) ถูก capture ไว้ใน handler — reassign ได้เพื่อใช้รอบสองแยกจากรอบแรก
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("TestBroadcast", message => received.TrySetResult(message));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinCampaign", _campaignId);

        // ฐานเทียบ (code review รอบ 1): ถ้าไม่ยืนยันก่อนว่า "ส่งแล้วถึงจริง" การ assert ว่า
        // "ไม่ถึง" ตอนท้ายจะผ่านฟรีแม้ pipe broadcast จะพังมาตั้งแต่ต้น (vacuous pass)
        using (var scope = _factory.Services.CreateScope())
        {
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<CampaignHub>>();
            await hubContext.Clients.Group(CampaignHub.GroupName(_campaignId)).SendAsync("TestBroadcast", "before");
        }
        var firstCompleted = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(received.Task, firstCompleted);
        Assert.Equal("before", await received.Task);

        received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (var scope = _factory.Services.CreateScope())
        {
            var revalidator = scope.ServiceProvider.GetRequiredService<CampaignGroupRevalidator>();
            await revalidator.EvictMemberAsync(_campaignId, _memberUserId);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<CampaignHub>>();
            await hubContext.Clients.Group(CampaignHub.GroupName(_campaignId)).SendAsync("TestBroadcast", "evicted");
        }

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.NotSame(received.Task, completed);

        await connection.StopAsync();
    }

    /// <summary>
    /// registry ต้องไม่ค้างแถวของ connection ที่หลุดไปแล้ว (memory leak + สแกนของทิ้งขว้างเปล่า ๆ) —
    /// OnDisconnectedAsync ทำงาน async หลัง StopAsync คืนค่า จึง poll แทนเช็คทันที กันเทสต์ flaky
    /// </summary>
    [Fact]
    public async Task Registry_no_longer_tracks_connection_after_disconnect()
    {
        await using var connection = BuildConnection(_memberToken);

        await connection.StartAsync();
        await connection.InvokeAsync("JoinCampaign", _campaignId);

        var registry = _factory.Services.GetRequiredService<CampaignGroupRegistry>();
        Assert.Contains(registry.Snapshot(), s => s.CampaignId == _campaignId && s.UserId == _memberUserId);

        await connection.StopAsync();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        var stillTracked = true;
        while (DateTime.UtcNow < deadline)
        {
            stillTracked = registry.Snapshot().Any(s => s.CampaignId == _campaignId && s.UserId == _memberUserId);
            if (!stillTracked)
            {
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.False(stillTracked);
    }

    private record AuthResponse(string Token, Guid UserId);
}
