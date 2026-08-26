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

    private record AuthResponse(string Token, Guid UserId);
}
