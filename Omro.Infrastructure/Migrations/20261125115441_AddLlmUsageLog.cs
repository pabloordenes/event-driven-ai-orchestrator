using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omro.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmUsageLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "llm_usage_log",
                schema: "omro",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resolution_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    anomaly_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    provider_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    used_fallback_provider = table.Column<bool>(type: "boolean", nullable: false),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: false),
                    completion_tokens = table.Column<int>(type: "integer", nullable: false),
                    total_tokens = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost_usd = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    error_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_usage_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_llm_usage_log_provider_recorded_at",
                schema: "omro",
                table: "llm_usage_log",
                columns: new[] { "provider_name", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_llm_usage_log_task_id",
                schema: "omro",
                table: "llm_usage_log",
                column: "resolution_task_id");

            migrationBuilder.CreateIndex(
                name: "IX_llm_usage_log_tenant_recorded_at",
                schema: "omro",
                table: "llm_usage_log",
                columns: new[] { "tenant_id", "recorded_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "llm_usage_log",
                schema: "omro");
        }
    }
}
