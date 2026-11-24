using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omro.Domain.ResolutionTasks;

namespace Omro.Infrastructure.Persistence.Configurations;

public class ActionExecutionConfiguration : IEntityTypeConfiguration<ActionExecution>
{
    public void Configure(EntityTypeBuilder<ActionExecution> builder)
    {
        builder.ToTable("ACTION_EXECUTION");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.ResolutionTaskId)
            .HasColumnName("resolution_task_id")
            .IsRequired();

        builder.Property(x => x.Phase)
            .HasColumnName("phase")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.ActionIndex)
            .HasColumnName("action_index")
            .IsRequired();

        builder.Property(x => x.ActionType)
            .HasColumnName("action_type")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Endpoint)
            .HasColumnName("endpoint")
            .HasMaxLength(1000)
            .IsRequired();

        builder.Property(x => x.PayloadJson)
            .HasColumnName("payload_json")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(x => x.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(x => x.Attempts)
            .HasColumnName("attempts")
            .IsRequired();

        builder.Property(x => x.HttpStatus)
            .HasColumnName("http_status");

        builder.Property(x => x.Error)
            .HasColumnName("error")
            .HasMaxLength(1000);

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.ExecutedAt)
            .HasColumnName("executed_at");

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(x => new { x.ResolutionTaskId, x.Phase, x.ActionIndex })
            .HasDatabaseName("IX_ACTION_EXECUTION_task_phase_index")
            .IsUnique();

        builder.HasIndex(x => x.Status)
            .HasDatabaseName("IX_ACTION_EXECUTION_status");

        builder.HasOne<ResolutionTask>()
            .WithMany()
            .HasForeignKey(x => x.ResolutionTaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
