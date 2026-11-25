using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omro.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceEternalTaskUniquenessWithWindowedDedup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "http_method",
                schema: "omro",
                table: "ACTION_EXECUTION",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "POST");

            migrationBuilder.CreateIndex(
                name: "IX_ACTION_EXECUTION_idempotency_key",
                schema: "omro",
                table: "ACTION_EXECUTION",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.Sql(@"DROP INDEX IF EXISTS omro.""IX_RESOLUTION_TASK_tenant_resource_type"";");
            migrationBuilder.Sql(
                @"CREATE UNIQUE INDEX ""IX_RESOLUTION_TASK_active_dedup"" ON omro.""RESOLUTION_TASK"" (tenant_id, resource_id, anomaly_type) WHERE ""Status"" NOT IN ('Resolved', 'Failed');");
            migrationBuilder.Sql(
                @"CREATE INDEX ""IX_RESOLUTION_TASK_window_lookup"" ON omro.""RESOLUTION_TASK"" (tenant_id, resource_id, anomaly_type, ""CreatedAt"" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS omro.""IX_RESOLUTION_TASK_window_lookup"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS omro.""IX_RESOLUTION_TASK_active_dedup"";");

            migrationBuilder.CreateIndex(
                name: "IX_RESOLUTION_TASK_tenant_resource_type",
                schema: "omro",
                table: "RESOLUTION_TASK",
                columns: new[] { "tenant_id", "resource_id", "anomaly_type" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_ACTION_EXECUTION_idempotency_key",
                schema: "omro",
                table: "ACTION_EXECUTION");

            migrationBuilder.DropColumn(
                name: "http_method",
                schema: "omro",
                table: "ACTION_EXECUTION");
        }
    }
}
