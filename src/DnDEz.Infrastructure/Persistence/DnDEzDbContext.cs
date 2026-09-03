using System.Text.Json;
using DnDEz.Domain.Entities;
using DnDEz.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnDEz.Infrastructure.Persistence;

public class DnDEzDbContext(DbContextOptions<DnDEzDbContext> options) : DbContext(options)
{
    // ── Accounts ──────────────────────────────────────────────────────────
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<StorageQuota> StorageQuotas => Set<StorageQuota>();
    public DbSet<CampaignMember> CampaignMembers => Set<CampaignMember>();

    // ── Campaign ──────────────────────────────────────────────────────────
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<AutonomySetting> AutonomySettings => Set<AutonomySetting>();
    public DbSet<StoryBeat> StoryBeats => Set<StoryBeat>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<CharacterResource> CharacterResources => Set<CharacterResource>();
    public DbSet<DeathSaveState> DeathSaveStates => Set<DeathSaveState>();
    public DbSet<CharacterReview> CharacterReviews => Set<CharacterReview>();

    // ── Session ───────────────────────────────────────────────────────────
    public DbSet<GameSession> Sessions => Set<GameSession>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<PrivateMessage> PrivateMessages => Set<PrivateMessage>();
    public DbSet<Scene> Scenes => Set<Scene>();
    public DbSet<Proposal> Proposals => Set<Proposal>();
    public DbSet<RestPhase> RestPhases => Set<RestPhase>();
    public DbSet<PlayerRestAction> PlayerRestActions => Set<PlayerRestAction>();
    public DbSet<RecapSnapshot> RecapSnapshots => Set<RecapSnapshot>();
    public DbSet<ConsultMessage> ConsultMessages => Set<ConsultMessage>();
    public DbSet<DiceRoll> DiceRolls => Set<DiceRoll>();
    public DbSet<Encounter> Encounters => Set<Encounter>();
    public DbSet<EncounterParticipant> EncounterParticipants => Set<EncounterParticipant>();

    // ── Source import / presets ───────────────────────────────────────────
    public DbSet<SourceDocument> SourceDocuments => Set<SourceDocument>();
    public DbSet<RawChunk> RawChunks => Set<RawChunk>();
    public DbSet<MonsterStatBlock> MonsterStatBlocks => Set<MonsterStatBlock>();
    public DbSet<NpcProfile> NpcProfiles => Set<NpcProfile>();
    public DbSet<MapAsset> MapAssets => Set<MapAsset>();
    public DbSet<ExtractedCharacterSheet> ExtractedCharacterSheets => Set<ExtractedCharacterSheet>();
    public DbSet<RulesCorpusEntry> RulesCorpusEntries => Set<RulesCorpusEntry>();
    public DbSet<CharacterPreset> CharacterPresets => Set<CharacterPreset>();

    /// <summary>คอลัมน์ที่เก็บ JSON ทั้งก้อน — ใช้ jsonb เสมอ (ไม่ใช่ json/text)</summary>
    private const string Jsonb = "jsonb";

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("vector");
        b.HasPostgresExtension("pgcrypto");

        // enum ทุกตัวเก็บเป็น text ให้อ่าน schema ตรง ๆ ได้ + CHECK constraint กำกับ
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
                if (type.IsEnum) prop.SetProviderClrType(typeof(string));
            }
        }

        ConfigureAccounts(b);
        ConfigureCampaign(b);
        ConfigureSession(b);
        ConfigureSourceImport(b);
    }

    private static void ConfigureAccounts(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        b.Entity<ApiKey>(e =>
        {
            e.HasOne(x => x.User).WithMany(x => x.ApiKeys)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.KeySuffix).HasMaxLength(8);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            // Sprint D Part 1 — พบว่า index เดิมไม่ unique (ตั้งแต่ InitialSchema) แปลว่า upsert
            // "หนึ่ง key ต่อหนึ่ง provider ต่อ user" ไม่ถูกบังคับที่ชั้น DB เลย ถ้า race กันสอง
            // request พร้อมกันจะได้ 2 แถวซ้อนสำหรับ provider เดียวกัน — ตารางนี้ไม่เคยถูกใช้งานจริง
            // มาก่อน (Sprint D เป็นคนแรกที่ต่อ endpoint เข้า) จึงแก้ตอนนี้ก่อนมีข้อมูลจริงในตาราง
            e.HasIndex(x => new { x.UserId, x.Provider }).IsUnique();
            EnumCheck<ApiKey, ApiKeyStatus>(e, x => x.Status, "status");
        });

        b.Entity<StorageQuota>(e =>
        {
            e.HasKey(x => x.UserId);
            e.HasOne(x => x.User).WithOne(x => x.StorageQuota)
                .HasForeignKey<StorageQuota>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("ck_storage_quota_non_negative", "used_bytes >= 0 AND limit_bytes >= 0"));
        });

        b.Entity<CampaignMember>(e =>
        {
            e.HasIndex(x => new { x.CampaignId, x.UserId }).IsUnique();
            e.HasOne(x => x.Campaign).WithMany(x => x.Members)
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(x => x.Memberships)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.JoinedAt).HasDefaultValueSql("now()");
            EnumCheck<CampaignMember, CampaignRole>(e, x => x.Role, "role");
        });
    }

    private static void ConfigureCampaign(ModelBuilder b)
    {
        b.Entity<Campaign>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.RulesSystem).HasMaxLength(32);
            e.Property(x => x.JoinCode).HasMaxLength(32);
            // DM กำหนดเอง (ไม่ใช่ CSPRNG) แต่ยังบังคับไม่ให้ซ้ำข้ามแคมเปญ — เหมือน GameSession.JoinCode
            e.HasIndex(x => x.JoinCode).IsUnique().HasFilter("join_code IS NOT NULL");
            e.Property(x => x.WorldState).HasColumnType(Jsonb).HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.ReactionTimeoutSeconds).HasDefaultValue(30);
            e.Property(x => x.CharacterReviewRequired).HasDefaultValue(true);
            e.Property(x => x.StartingLevel).HasDefaultValue(1);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.OwnerUser).WithMany()
                .HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.OwnerUserId);

            EnumCheck<Campaign, CampaignMode>(e, x => x.Mode, "mode");
            EnumCheck<Campaign, DiceMode>(e, x => x.DiceMode, "dice_mode");
            EnumCheck<Campaign, DeathSaveVisibility>(e, x => x.DeathSaveVisibility, "death_save_visibility");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_campaigns_reaction_timeout", "reaction_timeout_seconds BETWEEN 5 AND 300");
                t.HasCheckConstraint("ck_campaigns_starting_level", "starting_level BETWEEN 1 AND 20");
            });
        });

        b.Entity<AutonomySetting>(e =>
        {
            e.HasOne(x => x.Campaign).WithMany(x => x.AutonomySettings)
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CampaignId, x.Subsystem }).IsUnique();
            EnumCheck<AutonomySetting, AutonomySubsystem>(e, x => x.Subsystem, "subsystem");
            EnumCheck<AutonomySetting, AutonomyLevel>(e, x => x.Level, "level");
        });

        b.Entity<StoryBeat>(e =>
        {
            e.HasOne(x => x.Campaign).WithMany(x => x.StoryBeats)
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Secrets).HasColumnType(Jsonb).HasDefaultValueSql("'[]'::jsonb");
            e.HasIndex(x => new { x.CampaignId, x.Order });
            EnumCheck<StoryBeat, BeatProgress>(e, x => x.Progress, "progress");
            EnumCheck<StoryBeat, ReviewStatus>(e, x => x.ReviewStatus, "review_status");
        });

        b.Entity<Character>(e =>
        {
            e.HasOne(x => x.Campaign).WithMany(x => x.Characters)
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.OwnerUser).WithMany()
                .HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Sheet).HasColumnType(Jsonb).HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.CampaignId);
            e.HasIndex(x => x.ClaimToken).IsUnique().HasFilter("claim_token IS NOT NULL");
            // orphan ที่ยังไม่ถูก claim — Hangfire job กวาดลบตามรอบ
            e.HasIndex(x => x.ClaimExpiresAt).HasFilter("is_orphan = true");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_characters_level", "level BETWEEN 1 AND 20");
                // ช่วง ±20 เป็น **sanity bound ของระบบนี้เอง ไม่ใช่กฎ 5e** (SRD ไม่ได้กำหนดเพดาน
                // initiative modifier ไว้) — มีไว้กันค่าขยะ/overflow หลุดเข้า DB เท่านั้น
                t.HasCheckConstraint(
                    "ck_characters_initiative_modifier", "initiative_modifier BETWEEN -20 AND 20");
            });
        });

        b.Entity<CharacterResource>(e =>
        {
            e.HasOne(x => x.Character).WithMany(x => x.Resources)
                .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.CharacterId);
            EnumCheck<CharacterResource, ResourceResetOn>(e, x => x.ResetOn, "reset_on");
            e.ToTable(t => t.HasCheckConstraint("ck_character_resources_range", "current >= 0 AND max >= 0 AND current <= max"));
        });

        b.Entity<DeathSaveState>(e =>
        {
            e.HasKey(x => x.CharacterId);
            e.HasOne(x => x.Character).WithOne(x => x.DeathSaveState)
                .HasForeignKey<DeathSaveState>(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            EnumCheck<DeathSaveState, DeathSaveStatus>(e, x => x.Status, "status");
            // กติกา SRD: สำเร็จ 3 = stable, ล้มเหลว 3 = ตาย — เกินนี้ไม่ได้
            e.ToTable(t => t.HasCheckConstraint("ck_death_saves_range", "successes BETWEEN 0 AND 3 AND failures BETWEEN 0 AND 3"));
        });

        b.Entity<CharacterReview>(e =>
        {
            e.HasKey(x => x.CharacterId);
            e.HasOne(x => x.Character).WithOne(x => x.Review)
                .HasForeignKey<CharacterReview>(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade);
            EnumCheck<CharacterReview, ReviewStatus>(e, x => x.Status, "status");
        });
    }

    private static void ConfigureSession(ModelBuilder b)
    {
        b.Entity<GameSession>(e =>
        {
            e.ToTable("sessions");
            e.HasOne(x => x.Campaign).WithMany(x => x.Sessions)
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.JoinCode).HasMaxLength(6);
            // join code ต้องไม่ซ้ำในหมู่เซสชันที่ยัง active อยู่
            e.HasIndex(x => x.JoinCode).IsUnique().HasFilter("join_code IS NOT NULL");
            e.HasIndex(x => x.CampaignId);
            EnumCheck<GameSession, SessionMode>(e, x => x.Mode, "mode");
            EnumCheck<GameSession, SessionStatus>(e, x => x.Status, "status");
        });

        b.Entity<Message>(e =>
        {
            e.HasOne(x => x.Session).WithMany(x => x.Messages)
                .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ToolCalls).HasColumnType(Jsonb);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.SessionId, x.CreatedAt });
            EnumCheck<Message, MessageAuthorType>(e, x => x.AuthorType, "author_type");
            EnumCheck<Message, ActionSource>(e, x => x.Source, "source");
        });

        b.Entity<PrivateMessage>(e =>
        {
            e.HasOne(x => x.Session).WithMany()
                .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.SessionId, x.ToUserId, x.CreatedAt });
        });

        b.Entity<Scene>(e =>
        {
            e.HasOne(x => x.Session).WithMany(x => x.Scenes)
                .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Grid).HasColumnType(Jsonb).HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.Tokens).HasColumnType(Jsonb).HasDefaultValueSql("'[]'::jsonb");
            e.Property(x => x.FogLayer).HasColumnType(Jsonb).HasDefaultValueSql("'{}'::jsonb");
            // มี active scene ได้ทีละอันต่อ session
            e.HasIndex(x => x.SessionId).IsUnique().HasFilter("is_active = true")
                .HasDatabaseName("ix_scenes_one_active_per_session");
        });

        b.Entity<Proposal>(e =>
        {
            e.HasOne(x => x.Session).WithMany(x => x.Proposals)
                .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ToolCallJson).HasColumnType(Jsonb);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            // คิวที่ DM ต้องเห็น — query หลักคือ pending ของ session นี้
            e.HasIndex(x => new { x.SessionId, x.Status });
            EnumCheck<Proposal, AutonomySubsystem>(e, x => x.Subsystem, "subsystem");
            EnumCheck<Proposal, ProposalStatus>(e, x => x.Status, "status");
        });

        b.Entity<RestPhase>(e =>
        {
            e.HasOne(x => x.Session).WithMany()
                .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.SessionId);
            EnumCheck<RestPhase, RestType>(e, x => x.Type, "type");
            EnumCheck<RestPhase, RestStatus>(e, x => x.Status, "status");
            e.ToTable(t => t.HasCheckConstraint("ck_rest_phases_percent", "resolved_percent IS NULL OR resolved_percent BETWEEN 0 AND 100"));
        });

        b.Entity<PlayerRestAction>(e =>
        {
            e.HasOne(x => x.RestPhase).WithMany(x => x.Actions)
                .HasForeignKey(x => x.RestPhaseId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.AbilitiesUsed).HasColumnType(Jsonb).HasDefaultValueSql("'[]'::jsonb");
            e.HasIndex(x => new { x.RestPhaseId, x.CharacterId }).IsUnique();
        });

        b.Entity<RecapSnapshot>(e =>
        {
            e.HasOne(x => x.Session).WithMany()
                .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.SessionId);
        });

        b.Entity<ConsultMessage>(e =>
        {
            e.HasOne(x => x.Campaign).WithMany()
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.CampaignId, x.CreatedAt });
            EnumCheck<ConsultMessage, ConsultRole>(e, x => x.Role, "role");
        });

        b.Entity<DiceRoll>(e =>
        {
            e.HasOne(x => x.Session).WithMany()
                .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Expression).HasMaxLength(64);
            e.Property(x => x.Rolls).HasColumnType(Jsonb).HasDefaultValueSql("'[]'::jsonb");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.SessionId, x.CreatedAt });
            EnumCheck<DiceRoll, DiceMode>(e, x => x.Source, "source");
            // SystemRoll ต้องมี seed เสมอ (ไว้ replay) · DmEntered ต้องรู้ว่าใครกรอก
            e.ToTable(t => t.HasCheckConstraint(
                "ck_dice_rolls_provenance",
                "(source = 'SystemRoll' AND seed IS NOT NULL) OR (source = 'DmEntered' AND entered_by_user_id IS NOT NULL)"));
        });

        b.Entity<Encounter>(e =>
        {
            e.ToTable("encounters");
            e.HasOne(x => x.Campaign).WithMany()
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Session).WithMany()
                .HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            // ห้าม Cascade — Encounter.CurrentParticipantId ชี้ไป EncounterParticipant ซึ่งเอง
            // ก็มี FK กลับมาที่ Encounter (EncounterId) ถ้าตั้ง Cascade ทั้งคู่จะเกิด
            // "multiple cascade paths" ตอน `dotnet ef migrations add` · SetNull ถูกต้องอยู่แล้วเชิง
            // ความหมายด้วย: ลบ participant ทิ้ง ไม่ควรพา encounter ทั้งวงหายไปด้วย
            e.HasOne(x => x.CurrentParticipant).WithMany()
                .HasForeignKey(x => x.CurrentParticipantId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.StartedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.SessionId, x.EndedAt });
            e.ToTable(t => t.HasCheckConstraint("ck_encounters_turn_number", "turn_number >= 0"));
        });

        b.Entity<EncounterParticipant>(e =>
        {
            e.ToTable("encounter_participants");
            e.HasOne(x => x.Encounter).WithMany(x => x.Participants)
                .HasForeignKey(x => x.EncounterId).OnDelete(DeleteBehavior.Cascade);
            // Cascade (ไม่ใช่ Restrict) — คอมเมนต์เดิมอ้างว่าต้องเลี่ยง "multiple cascade paths"
            // แต่นั่นเป็นข้อจำกัดของ SQL Server ไม่ใช่ Postgres · Postgres รองรับหลายเส้นทาง
            // cascade มาถึงตารางเดียวกันได้ตามปกติ
            // และ Restrict สร้างปัญหาจริง: การลบแคมเปญ cascade ลงไปทั้ง characters และ encounters
            // พร้อมกัน ถ้า RI trigger ของ characters ทำงานก่อนที่ encounter_participants จะถูกลบ
            // ตาม encounters จะได้ FK violation แบบ non-deterministic (ขึ้นกับลำดับ trigger)
            e.HasOne(x => x.Character).WithMany()
                .HasForeignKey(x => x.CharacterId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.EncounterId, x.TurnOrder }).IsUnique();
            // กันตัวละครเดียวกันถูกใส่เข้า encounter เดียวกันซ้ำสองครั้ง
            e.HasIndex(x => new { x.EncounterId, x.CharacterId }).IsUnique();
            e.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "ck_encounter_participants_initiative_roll", "initiative_roll BETWEEN 1 AND 20");
                t.HasCheckConstraint(
                    "ck_encounter_participants_initiative_total",
                    "initiative_total = initiative_roll + initiative_modifier");
                t.HasCheckConstraint("ck_encounter_participants_turn_order", "turn_order >= 0");
            });
        });
    }

    private static void ConfigureSourceImport(ModelBuilder b)
    {
        b.Entity<SourceDocument>(e =>
        {
            e.HasOne(x => x.Campaign).WithMany()
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.FileName).HasMaxLength(400);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => x.CampaignId);
            EnumCheck<SourceDocument, OcrStatus>(e, x => x.OcrStatus, "ocr_status");
        });

        b.Entity<RawChunk>(e =>
        {
            e.HasOne(x => x.SourceDocument).WithMany()
                .HasForeignKey(x => x.SourceDocumentId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Embedding).HasColumnType("vector(1536)");
            // ทุก vector query ต้อง filter campaign_id ก่อนเสมอ (namespace แยกต่อแคมเปญ)
            e.HasIndex(x => x.CampaignId);
        });

        b.Entity<MonsterStatBlock>(e =>
        {
            e.HasOne(x => x.Campaign).WithMany()
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Actions).HasColumnType(Jsonb).HasDefaultValueSql("'[]'::jsonb");
            e.Property(x => x.ConditionRefs).HasColumnType(Jsonb).HasDefaultValueSql("'[]'::jsonb");
            e.HasIndex(x => new { x.CampaignId, x.ReviewStatus });
            EnumCheck<MonsterStatBlock, ReviewStatus>(e, x => x.ReviewStatus, "review_status");
        });

        b.Entity<NpcProfile>(e =>
        {
            e.HasOne(x => x.Campaign).WithMany()
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.KnowsSecrets).HasColumnType(Jsonb).HasDefaultValueSql("'[]'::jsonb");
            e.HasIndex(x => new { x.CampaignId, x.ReviewStatus });
            EnumCheck<NpcProfile, ReviewStatus>(e, x => x.ReviewStatus, "review_status");
        });

        b.Entity<MapAsset>(e =>
        {
            e.HasOne(x => x.Campaign).WithMany()
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.CropSource).HasColumnType(Jsonb);
            e.Property(x => x.GridAlignment).HasColumnType(Jsonb);
            e.HasIndex(x => new { x.CampaignId, x.ReviewStatus });
            EnumCheck<MapAsset, ReviewStatus>(e, x => x.ReviewStatus, "review_status");
        });

        b.Entity<ExtractedCharacterSheet>(e =>
        {
            e.HasOne(x => x.Campaign).WithMany()
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.RawData).HasColumnType(Jsonb).HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.CampaignId, x.ReviewStatus });
            EnumCheck<ExtractedCharacterSheet, ReviewStatus>(e, x => x.ReviewStatus, "review_status");
        });

        b.Entity<RulesCorpusEntry>(e =>
        {
            e.Property(x => x.Category).HasMaxLength(32);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Title).HasMaxLength(200);
            e.HasIndex(x => new { x.Category, x.Key }).IsUnique();
        });

        b.Entity<CharacterPreset>(e =>
        {
            e.HasOne(x => x.Campaign).WithMany()
                .HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Build).HasColumnType(Jsonb).HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.LevelPath).HasColumnType(Jsonb).HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.BuildFingerprint).HasMaxLength(64);
            e.Property(x => x.SrdOnly).HasDefaultValue(true);
            e.Property(x => x.PopularityCount).HasDefaultValue(1);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

            // dedupe: build เดียวกันที่ level เดียวกันเก็บแถวเดียว แล้วนับ popularity แทน
            e.HasIndex(x => new { x.BuildFingerprint, x.Level, x.Scope, x.CampaignId }).IsUnique();
            e.HasIndex(x => new { x.Scope, x.Level });

            EnumCheck<CharacterPreset, PresetScope>(e, x => x.Scope, "scope");
            EnumCheck<CharacterPreset, PresetSource>(e, x => x.Source, "source");

            e.ToTable(t =>
            {
                // Scope=Campaign ต้องมี campaign_id · Scope=Global ต้องไม่มี
                t.HasCheckConstraint("ck_presets_scope_campaign",
                    "(scope = 'Campaign' AND campaign_id IS NOT NULL) OR (scope = 'Global' AND campaign_id IS NULL)");

                // ⚠️ กฎสำคัญที่สุดของ harvest — เนื้อหาจาก source import ห้ามหลุดเข้าคลังกลาง
                t.HasCheckConstraint("ck_presets_global_must_be_srd_only",
                    "scope <> 'Global' OR srd_only = true");
            });
        });
    }

    /// <summary>สร้าง CHECK constraint จากค่าที่เป็นไปได้ของ enum — กันค่าขยะเล็ดลอดเข้า DB</summary>
    private static void EnumCheck<TEntity, TEnum>(
        EntityTypeBuilder<TEntity> builder,
        System.Linq.Expressions.Expression<Func<TEntity, TEnum>> _,
        string columnName)
        where TEntity : class
        where TEnum : struct, Enum
    {
        var values = string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"));
        var table = typeof(TEntity).Name;
        builder.ToTable(t => t.HasCheckConstraint($"ck_{columnName}_{table.ToLowerInvariant()}", $"{columnName} IN ({values})"));
    }
}
