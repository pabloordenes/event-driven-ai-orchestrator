using Omro.Domain.ResolutionTasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Omro.Infrastructure.Persistence.Configurations;

public class ResolutionTaskConfiguration : IEntityTypeConfiguration<ResolutionTask>
{
    public void Configure(EntityTypeBuilder<ResolutionTask> builder)
    {
        builder.ToTable("ResolutionTasks");
        
        builder.HasKey(t => t.Id);
        
        // guardamos el enum como tetto "Ingested"
        builder.Property(t => t.Status)
            .HasConversion<string>()
            .IsRequired();
        
        // patron value object
        builder.OwnsOne(t => t.Anomaly, a =>
        {
            a.Property(p => p.RiskProbability)
                .HasColumnName("Anomaly_RiskProbability")
                .HasColumnType("numeric(18,4)")
                .IsRequired();
            
            a.Property(p => p.ResourceId)
                .HasColumnName("Anomaly_ResourceId")
                .IsRequired();
        });

        builder.Property(t => t.ReasoningTraces)
            .HasField("_reasoningTraces")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}