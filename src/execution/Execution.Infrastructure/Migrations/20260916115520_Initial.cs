using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Execution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    position = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    stream = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    data = table.Column<string>(type: "json", nullable: false),
                    metadata = table.Column<string>(type: "json", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.position);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionContexts",
                columns: table => new
                {
                    ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RootExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    BundleJson = table.Column<string>(type: "text", nullable: false),
                    EntryPoint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InputJson = table.Column<string>(type: "text", nullable: false),
                    EnvJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionContexts", x => x.ExecutionId);
                });

            migrationBuilder.CreateTable(
                name: "parked_events",
                columns: table => new
                {
                    position = table.Column<long>(type: "bigint", nullable: false),
                    subscription = table.Column<string>(type: "text", nullable: false),
                    stream = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "text", nullable: false),
                    parked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_parked_events", x => new { x.position, x.subscription });
                });

            migrationBuilder.CreateTable(
                name: "subscription_checkpoints",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscription_checkpoints", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ParentExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    WorkflowName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    WorkflowVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    WorkflowNamespace = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WorkflowRegistry = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    WorkflowDigest = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "text", nullable: true),
                    WorkflowDefinitionJson = table.Column<string>(type: "text", nullable: true),
                    InputJson = table.Column<string>(type: "text", nullable: true),
                    EnvJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowExecutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_events_event_type",
                table: "events",
                column: "event_type")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_events_stream_revision",
                table: "events",
                columns: new[] { "stream", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionContexts_RootExecutionId",
                table: "ExecutionContexts",
                column: "RootExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_CreatedAt",
                table: "WorkflowExecutions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_ParentExecutionId",
                table: "WorkflowExecutions",
                column: "ParentExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_Status",
                table: "WorkflowExecutions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_WorkflowDigest",
                table: "WorkflowExecutions",
                column: "WorkflowDigest");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_WorkflowName_WorkflowVersion",
                table: "WorkflowExecutions",
                columns: new[] { "WorkflowName", "WorkflowVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "ExecutionContexts");

            migrationBuilder.DropTable(
                name: "parked_events");

            migrationBuilder.DropTable(
                name: "subscription_checkpoints");

            migrationBuilder.DropTable(
                name: "WorkflowExecutions");
        }
    }
}
