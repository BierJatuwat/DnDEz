using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pgcrypto", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "rules_corpus_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    license = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rules_corpus_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    encrypted_key = table.Column<string>(type: "text", nullable: false),
                    key_suffix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_api_keys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    world_state = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    mode = table.Column<string>(type: "text", nullable: false),
                    rules_system = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    dice_mode = table.Column<string>(type: "text", nullable: false),
                    reaction_timeout_seconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                    character_review_required = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    death_save_visibility = table.Column<string>(type: "text", nullable: false),
                    starting_level = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaigns", x => x.id);
                    table.CheckConstraint("ck_campaigns_reaction_timeout", "reaction_timeout_seconds BETWEEN 5 AND 300");
                    table.CheckConstraint("ck_campaigns_starting_level", "starting_level BETWEEN 1 AND 20");
                    table.CheckConstraint("ck_death_save_visibility_campaign", "death_save_visibility IN ('PartyVisible', 'DmOnly')");
                    table.CheckConstraint("ck_dice_mode_campaign", "dice_mode IN ('SystemRoll', 'DmEntered')");
                    table.CheckConstraint("ck_mode_campaign", "mode IN ('Guided', 'Custom')");
                    table.ForeignKey(
                        name: "fk_campaigns_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "storage_quotas",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    used_bytes = table.Column<long>(type: "bigint", nullable: false),
                    limit_bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_storage_quotas", x => x.user_id);
                    table.CheckConstraint("ck_storage_quota_non_negative", "used_bytes >= 0 AND limit_bytes >= 0");
                    table.ForeignKey(
                        name: "fk_storage_quotas_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "autonomy_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subsystem = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_autonomy_settings", x => x.id);
                    table.CheckConstraint("ck_level_autonomysetting", "level IN ('Manual', 'Suggest', 'Autonomous')");
                    table.CheckConstraint("ck_subsystem_autonomysetting", "subsystem IN ('Narration', 'NpcDialogue', 'RulesCombat', 'MapScene')");
                    table.ForeignKey(
                        name: "fk_autonomy_settings_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "campaign_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_campaign_members", x => x.id);
                    table.CheckConstraint("ck_role_campaignmember", "role IN ('Dm', 'Player')");
                    table.ForeignKey(
                        name: "fk_campaign_members_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_campaign_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "character_presets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "text", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    build = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    level_path = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    level = table.Column<int>(type: "integer", nullable: false),
                    build_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    popularity_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    srd_only = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_character_presets", x => x.id);
                    table.CheckConstraint("ck_presets_global_must_be_srd_only", "scope <> 'Global' OR srd_only = true");
                    table.CheckConstraint("ck_presets_scope_campaign", "(scope = 'Campaign' AND campaign_id IS NOT NULL) OR (scope = 'Global' AND campaign_id IS NULL)");
                    table.CheckConstraint("ck_scope_characterpreset", "scope IN ('Global', 'Campaign')");
                    table.CheckConstraint("ck_source_characterpreset", "source IN ('Authored', 'Harvested')");
                    table.ForeignKey(
                        name: "fk_character_presets_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "characters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sheet = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    level = table.Column<int>(type: "integer", nullable: false),
                    controlled_by_dm = table.Column<bool>(type: "boolean", nullable: false),
                    harvest_consent = table.Column<bool>(type: "boolean", nullable: false),
                    is_orphan = table.Column<bool>(type: "boolean", nullable: false),
                    claim_token = table.Column<string>(type: "text", nullable: true),
                    claim_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_characters", x => x.id);
                    table.CheckConstraint("ck_characters_level", "level BETWEEN 1 AND 20");
                    table.ForeignKey(
                        name: "fk_characters_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_characters_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "consult_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dm_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    linked_beat_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consult_messages", x => x.id);
                    table.CheckConstraint("ck_role_consultmessage", "role IN ('Dm', 'Ai')");
                    table.ForeignKey(
                        name: "fk_consult_messages_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "extracted_character_sheets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    raw_data = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    unresolved = table.Column<string[]>(type: "text[]", nullable: false),
                    review_status = table.Column<string>(type: "text", nullable: false),
                    committed_character_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extracted_character_sheets", x => x.id);
                    table.CheckConstraint("ck_review_status_extractedcharactersheet", "review_status IN ('Pending', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "fk_extracted_character_sheets_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "map_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    crop_source = table.Column<string>(type: "jsonb", nullable: true),
                    grid_alignment = table.Column<string>(type: "jsonb", nullable: true),
                    review_status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_assets", x => x.id);
                    table.CheckConstraint("ck_review_status_mapasset", "review_status IN ('Pending', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "fk_map_assets_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "monster_stat_blocks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    armor_class = table.Column<int>(type: "integer", nullable: false),
                    hit_points = table.Column<int>(type: "integer", nullable: false),
                    actions = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    condition_refs = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    review_status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monster_stat_blocks", x => x.id);
                    table.CheckConstraint("ck_review_status_monsterstatblock", "review_status IN ('Pending', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "fk_monster_stat_blocks_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "npc_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    motive = table.Column<string>(type: "text", nullable: true),
                    voice_hint = table.Column<string>(type: "text", nullable: true),
                    knows_secrets = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    review_status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_npc_profiles", x => x.id);
                    table.CheckConstraint("ck_review_status_npcprofile", "review_status IN ('Pending', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "fk_npc_profiles_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    join_code = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sessions", x => x.id);
                    table.CheckConstraint("ck_mode_gamesession", "mode IN ('Online', 'InPerson', 'Companion')");
                    table.CheckConstraint("ck_status_gamesession", "status IN ('Draft', 'Active', 'Paused', 'Ended')");
                    table.ForeignKey(
                        name: "fk_sessions_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    ocr_status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_documents", x => x.id);
                    table.CheckConstraint("ck_ocr_status_sourcedocument", "ocr_status IN ('Pending', 'Processing', 'Completed', 'Failed')");
                    table.ForeignKey(
                        name: "fk_source_documents_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "story_beats",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true),
                    objective = table.Column<string>(type: "text", nullable: true),
                    entry_trigger = table.Column<string>(type: "text", nullable: true),
                    exit_conditions = table.Column<string[]>(type: "text[]", nullable: false),
                    monster_refs = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    npc_refs = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    secrets = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    dm_notes = table.Column<string>(type: "text", nullable: true),
                    progress = table.Column<string>(type: "text", nullable: false),
                    review_status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_story_beats", x => x.id);
                    table.CheckConstraint("ck_progress_storybeat", "progress IN ('Upcoming', 'Active', 'Passed')");
                    table.CheckConstraint("ck_review_status_storybeat", "review_status IN ('Pending', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "fk_story_beats_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "character_resources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    character_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    current = table.Column<int>(type: "integer", nullable: false),
                    max = table.Column<int>(type: "integer", nullable: false),
                    reset_on = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_character_resources", x => x.id);
                    table.CheckConstraint("ck_character_resources_range", "current >= 0 AND max >= 0 AND current <= max");
                    table.CheckConstraint("ck_reset_on_characterresource", "reset_on IN ('ShortRest', 'LongRest', 'Dawn', 'Never')");
                    table.ForeignKey(
                        name: "fk_character_resources_characters_character_id",
                        column: x => x.character_id,
                        principalTable: "characters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "character_reviews",
                columns: table => new
                {
                    character_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    flagged_reasons = table.Column<string[]>(type: "text[]", nullable: false),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_character_reviews", x => x.character_id);
                    table.CheckConstraint("ck_status_characterreview", "status IN ('Pending', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "fk_character_reviews_characters_character_id",
                        column: x => x.character_id,
                        principalTable: "characters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "death_save_states",
                columns: table => new
                {
                    character_id = table.Column<Guid>(type: "uuid", nullable: false),
                    successes = table.Column<int>(type: "integer", nullable: false),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_death_save_states", x => x.character_id);
                    table.CheckConstraint("ck_death_saves_range", "successes BETWEEN 0 AND 3 AND failures BETWEEN 0 AND 3");
                    table.CheckConstraint("ck_status_deathsavestate", "status IN ('Rolling', 'Stable', 'Dead')");
                    table.ForeignKey(
                        name: "fk_death_save_states_characters_character_id",
                        column: x => x.character_id,
                        principalTable: "characters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dice_rolls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    character_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expression = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rolls = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    total = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    seed = table.Column<long>(type: "bigint", nullable: true),
                    entered_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dice_rolls", x => x.id);
                    table.CheckConstraint("ck_dice_rolls_provenance", "(source = 'SystemRoll' AND seed IS NOT NULL) OR (source = 'DmEntered' AND entered_by_user_id IS NOT NULL)");
                    table.CheckConstraint("ck_source_diceroll", "source IN ('SystemRoll', 'DmEntered')");
                    table.ForeignKey(
                        name: "fk_dice_rolls_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_type = table.Column<string>(type: "text", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    character_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    tool_calls = table.Column<string>(type: "jsonb", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                    table.CheckConstraint("ck_author_type_message", "author_type IN ('Dm', 'Player', 'Ai', 'System')");
                    table.CheckConstraint("ck_source_message", "source IN ('Player', 'DmOverride', 'Ai')");
                    table.ForeignKey(
                        name: "fk_messages_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "private_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_private_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_private_messages_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "proposals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subsystem = table.Column<string>(type: "text", nullable: false),
                    tool_call_json = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proposals", x => x.id);
                    table.CheckConstraint("ck_status_proposal", "status IN ('Pending', 'Approved', 'Edited', 'Rejected')");
                    table.CheckConstraint("ck_subsystem_proposal", "subsystem IN ('Narration', 'NpcDialogue', 'RulesCombat', 'MapScene')");
                    table.ForeignKey(
                        name: "fk_proposals_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recap_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dm_version_text = table.Column<string>(type: "text", nullable: false),
                    player_version_text = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recap_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_recap_snapshots_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rest_phases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    hours = table.Column<int>(type: "integer", nullable: true),
                    watch_order = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    interrupted_at_minutes = table.Column<int>(type: "integer", nullable: true),
                    resolved_percent = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rest_phases", x => x.id);
                    table.CheckConstraint("ck_rest_phases_percent", "resolved_percent IS NULL OR resolved_percent BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_status_restphase", "status IN ('Open', 'Interrupted', 'Resolved')");
                    table.CheckConstraint("ck_type_restphase", "type IN ('Short', 'Long')");
                    table.ForeignKey(
                        name: "fk_rest_phases_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scenes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    grid = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    tokens = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    fog_layer = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    background_image_url = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scenes", x => x.id);
                    table.ForeignKey(
                        name: "fk_scenes_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "raw_chunks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    page_number = table.Column<int>(type: "integer", nullable: true),
                    embedding = table.Column<Vector>(type: "vector(1536)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_raw_chunks", x => x.id);
                    table.ForeignKey(
                        name: "fk_raw_chunks_source_documents_source_document_id",
                        column: x => x.source_document_id,
                        principalTable: "source_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "player_rest_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rest_phase_id = table.Column<Guid>(type: "uuid", nullable: false),
                    character_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hit_dice_spent = table.Column<int>(type: "integer", nullable: false),
                    abilities_used = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    confirmed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_player_rest_actions", x => x.id);
                    table.ForeignKey(
                        name: "fk_player_rest_actions_rest_phases_rest_phase_id",
                        column: x => x.rest_phase_id,
                        principalTable: "rest_phases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_user_id_provider",
                table: "api_keys",
                columns: new[] { "user_id", "provider" });

            migrationBuilder.CreateIndex(
                name: "ix_autonomy_settings_campaign_id_subsystem",
                table: "autonomy_settings",
                columns: new[] { "campaign_id", "subsystem" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaign_members_campaign_id_user_id",
                table: "campaign_members",
                columns: new[] { "campaign_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_campaign_members_user_id",
                table: "campaign_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_campaigns_owner_user_id",
                table: "campaigns",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_character_presets_build_fingerprint_level_scope_campaign_id",
                table: "character_presets",
                columns: new[] { "build_fingerprint", "level", "scope", "campaign_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_character_presets_campaign_id",
                table: "character_presets",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_character_presets_scope_level",
                table: "character_presets",
                columns: new[] { "scope", "level" });

            migrationBuilder.CreateIndex(
                name: "ix_character_resources_character_id",
                table: "character_resources",
                column: "character_id");

            migrationBuilder.CreateIndex(
                name: "ix_characters_campaign_id",
                table: "characters",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_characters_claim_expires_at",
                table: "characters",
                column: "claim_expires_at",
                filter: "is_orphan = true");

            migrationBuilder.CreateIndex(
                name: "ix_characters_claim_token",
                table: "characters",
                column: "claim_token",
                unique: true,
                filter: "claim_token IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_characters_owner_user_id",
                table: "characters",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_consult_messages_campaign_id_created_at",
                table: "consult_messages",
                columns: new[] { "campaign_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_dice_rolls_session_id_created_at",
                table: "dice_rolls",
                columns: new[] { "session_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_extracted_character_sheets_campaign_id_review_status",
                table: "extracted_character_sheets",
                columns: new[] { "campaign_id", "review_status" });

            migrationBuilder.CreateIndex(
                name: "ix_map_assets_campaign_id_review_status",
                table: "map_assets",
                columns: new[] { "campaign_id", "review_status" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_session_id_created_at",
                table: "messages",
                columns: new[] { "session_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_monster_stat_blocks_campaign_id_review_status",
                table: "monster_stat_blocks",
                columns: new[] { "campaign_id", "review_status" });

            migrationBuilder.CreateIndex(
                name: "ix_npc_profiles_campaign_id_review_status",
                table: "npc_profiles",
                columns: new[] { "campaign_id", "review_status" });

            migrationBuilder.CreateIndex(
                name: "ix_player_rest_actions_rest_phase_id_character_id",
                table: "player_rest_actions",
                columns: new[] { "rest_phase_id", "character_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_private_messages_session_id_to_user_id_created_at",
                table: "private_messages",
                columns: new[] { "session_id", "to_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_proposals_session_id_status",
                table: "proposals",
                columns: new[] { "session_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_raw_chunks_campaign_id",
                table: "raw_chunks",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_raw_chunks_source_document_id",
                table: "raw_chunks",
                column: "source_document_id");

            migrationBuilder.CreateIndex(
                name: "ix_recap_snapshots_session_id",
                table: "recap_snapshots",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_rest_phases_session_id",
                table: "rest_phases",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_rules_corpus_entries_category_key",
                table: "rules_corpus_entries",
                columns: new[] { "category", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scenes_one_active_per_session",
                table: "scenes",
                column: "session_id",
                unique: true,
                filter: "is_active = true");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_campaign_id",
                table: "sessions",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_sessions_join_code",
                table: "sessions",
                column: "join_code",
                unique: true,
                filter: "join_code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_source_documents_campaign_id",
                table: "source_documents",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_story_beats_campaign_id_order",
                table: "story_beats",
                columns: new[] { "campaign_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "autonomy_settings");

            migrationBuilder.DropTable(
                name: "campaign_members");

            migrationBuilder.DropTable(
                name: "character_presets");

            migrationBuilder.DropTable(
                name: "character_resources");

            migrationBuilder.DropTable(
                name: "character_reviews");

            migrationBuilder.DropTable(
                name: "consult_messages");

            migrationBuilder.DropTable(
                name: "death_save_states");

            migrationBuilder.DropTable(
                name: "dice_rolls");

            migrationBuilder.DropTable(
                name: "extracted_character_sheets");

            migrationBuilder.DropTable(
                name: "map_assets");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "monster_stat_blocks");

            migrationBuilder.DropTable(
                name: "npc_profiles");

            migrationBuilder.DropTable(
                name: "player_rest_actions");

            migrationBuilder.DropTable(
                name: "private_messages");

            migrationBuilder.DropTable(
                name: "proposals");

            migrationBuilder.DropTable(
                name: "raw_chunks");

            migrationBuilder.DropTable(
                name: "recap_snapshots");

            migrationBuilder.DropTable(
                name: "rules_corpus_entries");

            migrationBuilder.DropTable(
                name: "scenes");

            migrationBuilder.DropTable(
                name: "storage_quotas");

            migrationBuilder.DropTable(
                name: "story_beats");

            migrationBuilder.DropTable(
                name: "characters");

            migrationBuilder.DropTable(
                name: "rest_phases");

            migrationBuilder.DropTable(
                name: "source_documents");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "campaigns");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
