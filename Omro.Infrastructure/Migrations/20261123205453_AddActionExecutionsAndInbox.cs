using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omro.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddActionExecutionsAndInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ACTION_EXECUTION",
                schema: "omro",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    resolution_task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    action_index = table.Column<int>(type: "integer", nullable: false),
                    action_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    endpoint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    executed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ACTION_EXECUTION", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ACTION_EXECUTION_RESOLUTION_TASK_resolution_task_id",
                        column: x => x.resolution_task_id,
                        principalSchema: "omro",
                        principalTable: "RESOLUTION_TASK",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PROCESSED_MESSAGE",
                schema: "omro",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PROCESSED_MESSAGE", x => new { x.message_id, x.consumer_name });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ACTION_EXECUTION_status",
                schema: "omro",
                table: "ACTION_EXECUTION",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_ACTION_EXECUTION_task_phase_index",
                schema: "omro",
                table: "ACTION_EXECUTION",
                columns: new[] { "resolution_task_id", "phase", "action_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PROCESSED_MESSAGE_expires_at",
                schema: "omro",
                table: "PROCESSED_MESSAGE",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ACTION_EXECUTION",
                schema: "omro");

            migrationBuilder.DropTable(
                name: "PROCESSED_MESSAGE",
                schema: "omro");
        }
    }
}
