using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omro.Domain.Cognitive;

namespace Omro.Infrastructure.Persistence.Configurations;

public class LlmUsageLogConfiguration : IEntityTypeConfiguration<LlmUsageLog>
{
    public void Configure(EntityTypeBuilder<LlmUsageLog> builder)
    {
        builder.ToTable("llm_usage_log");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(x => x.ResolutionTaskId).HasColumnName("resolution_task_id");
        builder.Property(x => x.ResourceId).HasColumnName("resource_id").IsRequired();
        builder.Property(x => x.AnomalyType).HasColumnName("anomaly_type").HasMaxLength(100).IsRequired();
        builder.Property(x => x.ProviderName).HasColumnName("provider_name").HasMaxLength(64).IsRequired();
        builder.Property(x => x.ModelName).HasColumnName("model_name").HasMaxLength(128).IsRequired();
        builder.Property(x => x.Succeeded).HasColumnName("succeeded").IsRequired();
        builder.Property(x => x.UsedFallbackProvider).HasColumnName("used_fallback_provider").IsRequired();
        builder.Property(x => x.PromptTokens).HasColumnName("prompt_tokens").IsRequired();
        builder.Property(x => x.CompletionTokens).HasColumnName("completion_tokens").IsRequired();
        builder.Property(x => x.TotalTokens).HasColumnName("total_tokens").IsRequired();
        builder.Property(x => x.EstimatedCostUsd)
            .HasColumnName("estimated_cost_usd")
            .HasColumnType("numeric(18,8)")
            .IsRequired();
        builder.Property(x => x.LatencyMs).HasColumnName("latency_ms").IsRequired();
        builder.Property(x => x.ErrorReason).HasColumnName("error_reason").HasMaxLength(500);
        builder.Property(x => x.RecordedAt).HasColumnName("recorded_at").IsRequired();

        builder.HasIndex(x => new { x.TenantId, x.RecordedAt })
            .HasDatabaseName("IX_llm_usage_log_tenant_recorded_at");

        builder.HasIndex(x => x.ResolutionTaskId)
            .HasDatabaseName("IX_llm_usage_log_task_id");

        builder.HasIndex(x => new { x.ProviderName, x.RecordedAt })
            .HasDatabaseName("IX_llm_usage_log_provider_recorded_at");
    }
}
