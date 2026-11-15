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
        
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();
        
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
        });

        builder.OwnsMany(t => t.ReasoningTraces, rt =>
        {
            rt.ToTable("AGENT_REASONING_TRACE");
            rt.HasKey(t => t.Id);
            
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
    }
}