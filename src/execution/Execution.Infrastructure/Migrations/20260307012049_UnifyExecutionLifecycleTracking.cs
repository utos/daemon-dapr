using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Execution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UnifyExecutionLifecycleTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "SubWorkflowCalls");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "SubWorkflowCalls");

            migrationBuilder.DropColumn(
                name: "FailedAt",
                table: "SubWorkflowCalls");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "SubWorkflowCalls");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "SubWorkflowCalls");

            migrationBuilder.AddColumn<Guid>(
                name: "ParentExecutionId",
                table: "WorkflowExecutions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_ParentExecutionId",
                table: "WorkflowExecutions",
                column: "ParentExecutionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowExecutions_ParentExecutionId",
                table: "WorkflowExecutions");

            migrationBuilder.DropColumn(
                name: "ParentExecutionId",
                table: "WorkflowExecutions");

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAt",
                table: "SubWorkflowCalls",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "SubWorkflowCalls",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FailedAt",
                table: "SubWorkflowCalls",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAt",
                table: "SubWorkflowCalls",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "SubWorkflowCalls",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");
        }
    }
}
