using DnDEz.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace DnDEz.Api.Security;

/// <summary>
/// transaction ที่คลุม request หนึ่งตัว พร้อมตั้ง `app.user_id`/`app.campaign_id` ให้ RLS — เดิมอยู่ในตัว
/// middleware ของ `Program.cs` ตรง ๆ ย้ายมาเป็น scoped service (2026-09-23) เพื่อให้ endpoint ที่ทำงานยาว
/// หลายขั้นเรียก <see cref="CheckpointAsync"/> ได้
///
/// ## ทำไมต้อง checkpoint
/// ผู้ช่วย DM (`AssistantEndpoints`) เรียก Claude หลายรอบในรีเควสต์เดียว (หลายวินาทีต่อรอบ) — ถ้าคลุมด้วย
/// transaction เดียว (1) row lock ของสิ่งที่ tool เขียนไปแล้วจะค้างอยู่ตลอดช่วงรอ Claude ขวางรีเควสต์ของ
/// ผู้เล่นที่แตะแถวเดียวกัน (2) ถ้ารอบท้าย ๆ พัง ทั้งหมดถูก rollback รวมของที่ broadcast ไปหาผู้เล่นแล้ว
/// หน้าจอผู้เล่นกับ DB จะไม่ตรงกัน
///
/// ## ⚠️ สิ่งที่ห้ามพลาด
/// `set_config(..., true)` มีอายุเท่า transaction — commit แล้ว context หายทันที ตัว checkpoint จึง**ต้อง**
/// ตั้งค่าซ้ำทุกครั้งที่เปิด transaction ใหม่ (ถ้าลืม RLS จะกรองทุกแถวทิ้ง = fail closed ไม่ใช่รั่ว แต่ฟีเจอร์พัง)
/// </summary>
public sealed class RlsRequestTransaction(DnDEzDbContext db)
{
    private IDbContextTransaction? _transaction;
    private Guid? _userId;
    private Guid? _campaignId;

    internal async Task BeginAsync(Guid? userId, Guid? campaignId, CancellationToken cancellationToken)
    {
        _userId = userId;
        _campaignId = campaignId;
        await OpenAsync(cancellationToken);
    }

    /// <summary>
    /// commit งานที่ทำมาถึงตอนนี้ แล้วเปิด transaction ใหม่ที่มี RLS context เดิม — ผู้เรียกต้อง
    /// `SaveChangesAsync` ก่อน (checkpoint ไม่ save ให้) · ใช้หลัง tool ที่เขียนข้อมูล และก่อนเรียก network
    /// ที่นาน ๆ ทุกครั้ง
    /// </summary>
    public async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null)
        {
            throw new InvalidOperationException("ยังไม่ได้เปิด transaction ของ request — middleware ต้องเรียก BeginAsync ก่อน");
        }

        await _transaction.CommitAsync(cancellationToken);
        await _transaction.DisposeAsync();
        _transaction = null;
        await OpenAsync(cancellationToken);
    }

    /// <summary>
    /// ทิ้งงานตั้งแต่ checkpoint ล่าสุด แล้วเปิด transaction ใหม่ที่มี RLS context เดิม — ใช้เมื่อขั้นหนึ่งพังกลางทาง
    /// (Postgres ปฏิเสธทุกคำสั่งใน transaction ที่ error ไปแล้วจนกว่าจะ rollback) · ผู้เรียกต้อง
    /// `ChangeTracker.Clear()` เองด้วย ไม่งั้น entity ที่ค้างจะถูก save ซ้ำรอบหน้า
    /// </summary>
    public async Task RollbackToCheckpointAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync(cancellationToken);
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        await OpenAsync(cancellationToken);
    }

    internal async Task CommitAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.CommitAsync(CancellationToken.None);
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    internal async Task RollbackAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync(CancellationToken.None);
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        _transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // ค่าทั้งสองส่งเป็น parameter เสมอ (ExecuteSqlInterpolated* = parameterized ไม่ใช่ string concat)
        // ต่อให้เป็น Guid ที่ parse แล้วก็ไม่ต่อสตริงเข้า SQL — กันนิสัยเสียที่จะลามไปที่อื่น
        if (_userId is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.user_id', {_userId.Value.ToString()}, true)", cancellationToken);
        }

        if (_campaignId is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.campaign_id', {_campaignId.Value.ToString()}, true)", cancellationToken);
        }
    }
}
