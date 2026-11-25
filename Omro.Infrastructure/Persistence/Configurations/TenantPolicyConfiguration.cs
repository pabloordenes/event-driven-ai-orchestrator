using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omro.Domain.Tenants;

namespace Omro.Infrastructure.Persistence.Configurations;

public class TenantPolicyConfiguration : IEntityTypeConfiguration<TenantPolicy>
{
    public void Configure(EntityTypeBuilder<TenantPolicy> builder)
    {
        builder.ToTable("tenant_policies");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(x => x.AnomalyType)
            .HasColumnName("anomaly_type")
            .HasMaxLength(TenantPolicy.MaxAnomalyTypeLength)
            .IsRequired();

        builder.Property(x => x.PolicyVersion)
            .HasColumnName("policy_version")
            .HasDefaultValue(1)
            .IsRequired();

        builder.Property(x => x.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.Property(x => x.DedupWindowMinutes)
            .HasColumnName("dedup_window_minutes");

        builder.Property(x => x.DecisionTableJson)
            .HasColumnName("decision_table_json")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.AnomalyType, x.PolicyVersion })
            .HasDatabaseName("IX_tenant_policies_tenant_anomaly_version")
            .IsUnique();

        builder.HasIndex(x => new { x.TenantId, x.AnomalyType })
            .HasDatabaseName("IX_tenant_policies_active")
            .IsUnique()
            .HasFilter("is_active = true");
    }
}
