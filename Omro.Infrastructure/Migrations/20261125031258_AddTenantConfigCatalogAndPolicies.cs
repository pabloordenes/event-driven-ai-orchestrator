using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Omro.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantConfigCatalogAndPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "anomaly_type",
                schema: "omro",
                table: "RESOLUTION_TASK",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "GenericAnomaly",
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldDefaultValue: "AppointmentNoShowRisk");

            migrationBuilder.CreateTable(
                name: "tenant_config",
                schema: "omro",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    display_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    base_prompt_context = table.Column<string>(type: "text", nullable: false),
                    autonomous_execution_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    feature_flags_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    hitl_proposal_ttl_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 180),
                    hitl_proposal_min_lead_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 60),
                    dedup_window_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 60),
                    allowed_endpoint_hosts_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    auth_scheme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Rs256Jwt"),
                    auth_issuer = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    auth_audience = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    auth_role = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false, defaultValue: "omro_service"),
                    signing_key_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    signing_key_kid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    hmac_secret_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    max_payload_bytes = table.Column<int>(type: "integer", nullable: false, defaultValue: 65536),
                    egress_timeout_seconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 15),
                    llm_config_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    config_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_config", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "shadow_decisions",
                schema: "omro",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resolution_task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    anomaly_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    matched_rule_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    risk_probability = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    intended_action_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    actual_action_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    suppression_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    evaluation_snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shadow_decisions", x => x.id);
                    table.ForeignKey(
                        name: "FK_shadow_decisions_RESOLUTION_TASK_resolution_task_id",
                        column: x => x.resolution_task_id,
                        principalSchema: "omro",
                        principalTable: "RESOLUTION_TASK",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_shadow_decisions_tenant_config_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "omro",
                        principalTable: "tenant_config",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_actions",
                schema: "omro",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    endpoint_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    http_method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "POST"),
                    payload_schema_json = table.Column<string>(type: "jsonb", nullable: false),
                    default_payload_template_json = table.Column<string>(type: "jsonb", nullable: false),
                    llm_editable_fields_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    requires_approval = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    allowed_phases = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    retry_policy_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_actions", x => x.id);
                    table.ForeignKey(
                        name: "FK_tenant_actions_tenant_config_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "omro",
                        principalTable: "tenant_config",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_policies",
                schema: "omro",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    anomaly_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    dedup_window_minutes = table.Column<int>(type: "integer", nullable: true),
                    decision_table_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_policies", x => x.id);
                    table.ForeignKey(
                        name: "FK_tenant_policies_tenant_config_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "omro",
                        principalTable: "tenant_config",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shadow_decisions_task_rule",
                schema: "omro",
                table: "shadow_decisions",
                columns: new[] { "resolution_task_id", "matched_rule_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shadow_decisions_tenant_evaluated_at",
                schema: "omro",
                table: "shadow_decisions",
                columns: new[] { "tenant_id", "evaluated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_actions_tenant_action_type",
                schema: "omro",
                table: "tenant_actions",
                columns: new[] { "tenant_id", "action_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_actions_tenant_active",
                schema: "omro",
                table: "tenant_actions",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_config_slug",
                schema: "omro",
                table: "tenant_config",
                column: "tenant_slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_policies_active",
                schema: "omro",
                table: "tenant_policies",
                columns: new[] { "tenant_id", "anomaly_type" },
                unique: true,
                filter: "is_active = true");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_policies_tenant_anomaly_version",
                schema: "omro",
                table: "tenant_policies",
                columns: new[] { "tenant_id", "anomaly_type", "policy_version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shadow_decisions",
                schema: "omro");

            migrationBuilder.DropTable(
                name: "tenant_actions",
                schema: "omro");

            migrationBuilder.DropTable(
                name: "tenant_policies",
                schema: "omro");

            migrationBuilder.DropTable(
                name: "tenant_config",
                schema: "omro");

            migrationBuilder.AlterColumn<string>(
                name: "anomaly_type",
                schema: "omro",
                table: "RESOLUTION_TASK",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "AppointmentNoShowRisk",
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldDefaultValue: "GenericAnomaly");
        }
    }
}
