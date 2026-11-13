using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omro.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialOmroSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "omro");

            migrationBuilder.CreateTable(
                name: "RESOLUTION_TASK",
                schema: "omro",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    risk_probability = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RESOLUTION_TASK", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AGENT_REASONING_TRACE",
                schema: "omro",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResolutionTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    step_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    trace_message = table.Column<string>(type: "text", nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: false),
                    completion_tokens = table.Column<int>(type: "integer", nullable: false),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AGENT_REASONING_TRACE", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AGENT_REASONING_TRACE_RESOLUTION_TASK_ResolutionTaskId",
                        column: x => x.ResolutionTaskId,
                        principalSchema: "omro",
                        principalTable: "RESOLUTION_TASK",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AGENT_REASONING_TRACE_ResolutionTaskId",
                schema: "omro",
                table: "AGENT_REASONING_TRACE",
                column: "ResolutionTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AGENT_REASONING_TRACE",
                schema: "omro");

            migrationBuilder.DropTable(
                name: "RESOLUTION_TASK",
                schema: "omro");
        }
    }
}
