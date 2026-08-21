using DnDEz.Domain.Enums;

namespace DnDEz.Domain.Entities;

// ═══ บัญชีผู้ใช้ · API key · โควตา ═══ dm-side-design §7, §8

/// <summary>DM ต้องมีบัญชีเต็มเสมอ · ผู้เล่นเลือก quick play ได้ (ไม่มี User row)</summary>
public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }

    public StorageQuota? StorageQuota { get; set; }
    public ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
    public ICollection<CampaignMember> Memberships { get; set; } = new List<CampaignMember>();
}

/// <summary>
/// BYOK — key ผูกกับ user ไม่ใช่ campaign (เผื่ออนาคตผู้เล่นใช้ key ตัวเอง)
/// เข้ารหัสด้วย ASP.NET Data Protection API · master key เก็บนอก DB
/// ห้าม log ค่า key เต็มที่ไหนเลย · UI โชว์แค่ KeySuffix
/// </summary>
public class ApiKey
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public string Provider { get; set; } = "anthropic";

    /// <summary>ciphertext เท่านั้น — ห้ามเก็บ plaintext ลงคอลัมน์นี้เด็ดขาด</summary>
    public string EncryptedKey { get; set; } = null!;

    /// <summary>4 หลักท้ายไว้โชว์ใน UI</summary>
    public string KeySuffix { get; set; } = null!;

    public ApiKeyStatus Status { get; set; } = ApiKeyStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastValidatedAt { get; set; }
}

/// <summary>โควตา per account — แชร์ทั้งบัญชีไม่ว่าเล่นเป็น DM หรือ player · ลบแคมเปญต้องคืนโควตาจริง</summary>
public class StorageQuota
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public long UsedBytes { get; set; }
    public long LimitBytes { get; set; }
}

public class CampaignMember
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Campaign Campaign { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public CampaignRole Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
}
