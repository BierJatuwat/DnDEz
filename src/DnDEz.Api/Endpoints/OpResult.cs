namespace DnDEz.Api.Endpoints;

internal enum OpStatus { Ok, BadRequest, Forbidden, NotFound, Conflict }

/// <summary>
/// ผลของ operation ที่ใช้ร่วมกันระหว่าง route handler กับ tool ของผู้ช่วย DM (2026-09-23) — แยกออกมาเพื่อให้
/// logic มีที่เดียว: route handler แปลงเป็น HTTP response เดิมเป๊ะ ส่วน tool แปลงเป็นข้อความให้ Claude
///
/// <see cref="Error"/> คือรหัส error เดิมของ endpoint นั้น (เช่น "invalid_count") — null = endpoint เดิมตอบ
/// status เปล่าไม่มี body
/// </summary>
internal readonly record struct OpResult<T>(OpStatus Status, T? Value, string? Error)
{
    public static OpResult<T> Ok(T value) => new(OpStatus.Ok, value, null);
    public static OpResult<T> Bad(string? error = null) => new(OpStatus.BadRequest, default, error);
    public static OpResult<T> Forbidden() => new(OpStatus.Forbidden, default, null);
    public static OpResult<T> NotFound() => new(OpStatus.NotFound, default, null);
    public static OpResult<T> Conflict() => new(OpStatus.Conflict, default, null);

    public bool IsOk => Status == OpStatus.Ok;
}
