using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omro.Domain.Tenants;

namespace Omro.Infrastructure.Persistence.Configurations;

public class TenantConfigConfiguration : IEntityTypeConfiguration<TenantConfig>
{
    public void Configure(EntityTypeBuilder<TenantConfig> builder)
    {
        builder.ToTable("tenant_config");

        builder.HasKey(x => x.TenantId);
        builder.Property(x => x.TenantId)
            .HasColumnName("tenant_id")
            .ValueGeneratedNever();

        builder.Property(x => x.TenantSlug)
            .HasColumnName("tenant_slug")
            .HasMaxLength(TenantConfig.MaxTenantSlugLength)
            .IsRequired();

        builder.Property(x => x.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(TenantConfig.MaxDisplayNameLength)
            .IsRequired();

        builder.Property(x => x.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(x => x.BasePromptContext)
            .HasColumnName("base_prompt_context")
            .IsRequired();

        builder.Property(x => x.AutonomousExecutionEnabled)
            .HasColumnName("autonomous_execution_enabled")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(x => x.FeatureFlagsJson)
            .HasColumnName("feature_flags_json")
            .HasColumnType("jsonb")
            .HasDefaultValue("{}")
            .IsRequired();

        builder.Property(x => x.HitlProposalTtlMinutes)
            .HasColumnName("hitl_proposal_ttl_minutes")
            .HasDefaultValue(180)
            .IsRequired();

        builder.Property(x => x.HitlProposalMinLeadMinutes)
            .HasColumnName("hitl_proposal_min_lead_minutes")
            .HasDefaultValue(60)
            .IsRequired();

        builder.Property(x => x.DedupWindowMinutes)
            .HasColumnName("dedup_window_minutes")
            .HasDefaultValue(60)
            .IsRequired();

        builder.Property(x => x.AllowedEndpointHostsJson)
            .HasColumnName("allowed_endpoint_hosts_json")
            .HasColumnType("jsonb")
            .HasDefaultValue("[]")
            .IsRequired();

        builder.Property(x => x.AuthScheme)
            .HasColumnName("auth_scheme")
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(TenantAuthScheme.Rs256Jwt)
            .IsRequired();

        builder.Property(x => x.AuthIssuer)
            .HasColumnName("auth_issuer")
            .HasMaxLength(TenantConfig.MaxAuthIssuerLength);

        builder.Property(x => x.AuthAudience)
            .HasColumnName("auth_audience")
            .HasMaxLength(TenantConfig.MaxAuthAudienceLength)
            .IsRequired();

        builder.Property(x => x.AuthRole)
            .HasColumnName("auth_role")
            .HasMaxLength(TenantConfig.MaxAuthRoleLength)
            .HasDefaultValue("omro_service")
            .IsRequired();

        builder.Property(x => x.SigningKeyRef)
            .HasColumnName("signing_key_ref")
            .HasMaxLength(TenantConfig.MaxKeyRefLength);

        builder.Property(x => x.SigningKeyKid)
            .HasColumnName("signing_key_kid")
            .HasMaxLength(TenantConfig.MaxKeyKidLength);

        builder.Property(x => x.HmacSecretRef)
            .HasColumnName("hmac_secret_ref")
            .HasMaxLength(TenantConfig.MaxKeyRefLength);

        builder.Property(x => x.MaxPayloadBytes)
            .HasColumnName("max_payload_bytes")
            .HasDefaultValue(65536)
            .IsRequired();

        builder.Property(x => x.EgressTimeoutSeconds)
            .HasColumnName("egress_timeout_seconds")
            .HasDefaultValue(15)
            .IsRequired();

        builder.Property(x => x.LlmConfigJson)
            .HasColumnName("llm_config_json")
            .HasColumnType("jsonb")
            .HasDefaultValue("{}")
            .IsRequired();

        builder.Property(x => x.ConfigVersion)
            .HasColumnName("config_version")
            .HasDefaultValue(1)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(x => x.TenantSlug)
            .HasDatabaseName("IX_tenant_config_slug")
            .IsUnique();

        builder.HasMany(x => x.Actions)
            .WithOne()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Policies)
            .WithOne()
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(TenantConfig.Actions))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.Metadata.FindNavigation(nameof(TenantConfig.Policies))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}
