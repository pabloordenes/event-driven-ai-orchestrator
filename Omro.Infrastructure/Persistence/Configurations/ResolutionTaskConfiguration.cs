using Omro.Domain.ResolutionTasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Omro.Infrastructure.Persistence.Configurations;

public class ResolutionTaskConfiguration : IEntityTypeConfiguration<ResolutionTask>
{
    public void Configure(EntityTypeBuilder<ResolutionTask> builder)
    {
        builder.ToTable("RESOLUTION_TASK");
        
        builder.HasKey(t => t.Id);
        
        builder.Property(t => t.TenantId)
            .HasColumnName("tenant_id")
            .IsRequired();
        
        // guardamos el enum como texto "Ingested"
        builder.Property(t => t.Status)
            .HasConversion<string>()
            .IsRequired();
        
        builder.Property(t => t.ExpectsExternalApproval)
            .HasColumnName("expects_external_approval")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();

        builder.Property(t => t.ResolvedAt)
            .HasColumnName("resolved_at");

        builder.Property(t => t.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(1000);
        
        // patron value object
        builder.OwnsOne(t => t.Anomaly, a =>
        {
            a.Property(p => p.RiskProbability)
                .HasColumnName("risk_probability")
                .HasColumnType("numeric(18,4)")
                .IsRequired();

            a.Property(p => p.ResourceId)
                .HasColumnName("resource_id")
                .IsRequired();

            a.Property(p => p.AnomalyType)
                .HasColumnName("anomaly_type")
                .HasMaxLength(100)
                .HasDefaultValue("GenericAnomaly")
                .IsRequired();

            a.Property(p => p.CorrelationId)
                .HasColumnName("correlation_id")
                .IsRequired();
        });

        builder.OwnsMany(t => t.ReasoningTraces, rt =>
        {
            rt.ToTable("AGENT_REASONING_TRACE");
            rt.HasKey(t => t.Id);
            rt.Property(t => t.Id).ValueGeneratedNever();
            
            rt.Property(t => t.AgentName).HasColumnName("agent_name").HasMaxLength(100).IsRequired();
            rt.Property(t => t.StepType).HasColumnName("step_type").HasMaxLength(50).IsRequired();
            rt.Property(t => t.TraceMessage).HasColumnName("trace_message").IsRequired();
            
            // tipado fuerte en JSON para indexación nativa
            rt.Property(t => t.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb");
            
            rt.Property(t => t.PromptTokens).HasColumnName("prompt_tokens").IsRequired();
            rt.Property(t => t.CompletionTokens).HasColumnName("completion_tokens").IsRequired();
            rt.Property(t => t.Timestamp).HasColumnName("timestamp").IsRequired();
            
            rt.WithOwner().HasForeignKey(t => t.ResolutionTaskId);
        });

        builder.Metadata.FindNavigation(nameof(ResolutionTask.ReasoningTraces))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.Metadata.FindNavigation(nameof(ResolutionTask.ActionExecutions))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}