using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omro.Domain.Tenants;

namespace Omro.Infrastructure.Persistence.Configurations;

public class TenantActionConfiguration : IEntityTypeConfiguration<TenantAction>
{
    public void Configure(EntityTypeBuilder<TenantAction> builder)
    {
        builder.ToTable("tenant_actions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(x => x.ActionType)
            .HasColumnName("action_type")
            .HasMaxLength(TenantAction.MaxActionTypeLength)
            .IsRequired();

        builder.Property(x => x.Description)
            .HasColumnName("description")
            .HasMaxLength(TenantAction.MaxDescriptionLength)
            .IsRequired();

        builder.Property(x => x.EndpointUrl)
            .HasColumnName("endpoint_url")
            .HasMaxLength(TenantAction.MaxEndpointUrlLength)
            .IsRequired();

        builder.Property(x => x.HttpMethod)
            .HasColumnName("http_method")
            .HasMaxLength(TenantAction.MaxHttpMethodLength)
            .HasDefaultValue("POST")
            .IsRequired();

        builder.Property(x => x.PayloadSchemaJson)
            .HasColumnName("payload_schema_json")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(x => x.DefaultPayloadTemplateJson)
            .HasColumnName("default_payload_template_json")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(x => x.LlmEditableFieldsJson)
            .HasColumnName("llm_editable_fields_json")
            .HasColumnType("jsonb")
            .HasDefaultValue("[]")
            .IsRequired();

        builder.Property(x => x.RequiresApproval)
            .HasColumnName("requires_approval")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(x => x.AllowedPhases)
            .HasColumnName("allowed_phases")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.RetryPolicyJson)
            .HasColumnName("retry_policy_json")
            .HasColumnType("jsonb")
            .HasDefaultValue("{}")
            .IsRequired();

        builder.Property(x => x.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(x => x.SchemaVersion)
            .HasColumnName("schema_version")
            .HasDefaultValue(1)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.ActionType })
            .HasDatabaseName("IX_tenant_actions_tenant_action_type")
            .IsUnique();

        builder.HasIndex(x => new { x.TenantId, x.IsActive })
            .HasDatabaseName("IX_tenant_actions_tenant_active");
    }
}
