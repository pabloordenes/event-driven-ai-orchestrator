using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omro.Domain.ResolutionTasks;
using Omro.Domain.Tenants;

namespace Omro.Infrastructure.Persistence.Configurations;

public class ShadowDecisionConfiguration : IEntityTypeConfiguration<ShadowDecision>
{
    public void Configure(EntityTypeBuilder<ShadowDecision> builder)
    {
        builder.ToTable("shadow_decisions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(x => x.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();

        builder.Property(x => x.ResolutionTaskId)
            .HasColumnName("resolution_task_id")
            .IsRequired();

        builder.Property(x => x.ResourceId)
            .HasColumnName("resource_id")
            .IsRequired();

        builder.Property(x => x.AnomalyType)
            .HasColumnName("anomaly_type")
            .HasMaxLength(ShadowDecision.MaxAnomalyTypeLength)
            .IsRequired();

        builder.Property(x => x.PolicyId)
            .HasColumnName("policy_id")
            .IsRequired();

        builder.Property(x => x.PolicyVersion)
            .HasColumnName("policy_version")
            .IsRequired();

        builder.Property(x => x.MatchedRuleId)
            .HasColumnName("matched_rule_id")
            .HasMaxLength(ShadowDecision.MaxMatchedRuleIdLength)
            .IsRequired();

        builder.Property(x => x.RiskProbability)
            .HasColumnName("risk_probability")
            .HasColumnType("numeric(18,4)")
            .IsRequired();

        builder.Property(x => x.IntendedActionType)
            .HasColumnName("intended_action_type")
            .HasMaxLength(ShadowDecision.MaxActionTypeLength)
            .IsRequired();

        builder.Property(x => x.ActualActionType)
            .HasColumnName("actual_action_type")
            .HasMaxLength(ShadowDecision.MaxActionTypeLength)
            .IsRequired();

        builder.Property(x => x.SuppressionReason)
            .HasColumnName("suppression_reason")
            .HasMaxLength(ShadowDecision.MaxSuppressionReasonLength)
            .IsRequired();

        builder.Property(x => x.EvaluationSnapshotJson)
            .HasColumnName("evaluation_snapshot_json")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(x => x.CorrelationId)
            .HasColumnName("correlation_id")
            .IsRequired();

        builder.Property(x => x.EvaluatedAt)
            .HasColumnName("evaluated_at")
            .IsRequired();

        builder.HasIndex(x => new { x.ResolutionTaskId, x.MatchedRuleId })
            .HasDatabaseName("IX_shadow_decisions_task_rule")
            .IsUnique();

        builder.HasIndex(x => new { x.TenantId, x.EvaluatedAt })
            .HasDatabaseName("IX_shadow_decisions_tenant_evaluated_at");

        builder.HasOne<TenantConfig>()
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ResolutionTask>()
            .WithMany()
            .HasForeignKey(x => x.ResolutionTaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
