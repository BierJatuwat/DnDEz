using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DnDEz.Infrastructure.Persistence;

/// <summary>
/// ใช้ตอน design-time เท่านั้น (dotnet ef migrations add / database update)
/// connection string อ่านจาก env DNDEZ_DB — ถ้าไม่ตั้งจะ fallback ไปที่ docker-compose local
/// </summary>
public class DnDEzDbContextFactory : IDesignTimeDbContextFactory<DnDEzDbContext>
{
    public DnDEzDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("DNDEZ_DB")
                   ?? "Host=localhost;Port=5432;Database=dndez;Username=dndez;Password=dndez";

        var options = new DbContextOptionsBuilder<DnDEzDbContext>()
            .UseNpgsql(conn, o => o.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DnDEzDbContext(options);
    }
}
