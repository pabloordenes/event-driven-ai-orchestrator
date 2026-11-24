using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omro.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddResolutionLifecycleAuditColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "expects_external_approval",
                schema: "omro",
                table: "RESOLUTION_TASK",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                schema: "omro",
                table: "RESOLUTION_TASK",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "resolved_at",
                schema: "omro",
                table: "RESOLUTION_TASK",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "expects_external_approval",
                schema: "omro",
                table: "RESOLUTION_TASK");

            migrationBuilder.DropColumn(
                name: "failure_reason",
                schema: "omro",
                table: "RESOLUTION_TASK");

            migrationBuilder.DropColumn(
                name: "resolved_at",
                schema: "omro",
                table: "RESOLUTION_TASK");
        }
    }
}
