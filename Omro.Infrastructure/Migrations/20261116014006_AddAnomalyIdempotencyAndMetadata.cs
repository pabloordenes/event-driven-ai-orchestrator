using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omro.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnomalyIdempotencyAndMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "anomaly_type",
                schema: "omro",
                table: "RESOLUTION_TASK",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "AppointmentNoShowRisk");

            migrationBuilder.AddColumn<Guid>(
                name: "correlation_id",
                schema: "omro",
                table: "RESOLUTION_TASK",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_RESOLUTION_TASK_tenant_resource_type",
                schema: "omro",
                table: "RESOLUTION_TASK",
                columns: new[] { "tenant_id", "resource_id", "anomaly_type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RESOLUTION_TASK_tenant_resource_type",
                schema: "omro",
                table: "RESOLUTION_TASK");

            migrationBuilder.DropColumn(
                name: "anomaly_type",
                schema: "omro",
                table: "RESOLUTION_TASK");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "omro",
                table: "RESOLUTION_TASK");
        }
    }
}
