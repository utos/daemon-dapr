using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Execution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionWorkflowIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkflowDigest",
                table: "WorkflowExecutions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowNamespace",
                table: "WorkflowExecutions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowRegistry",
                table: "WorkflowExecutions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExecutions_WorkflowDigest",
                table: "WorkflowExecutions",
                column: "WorkflowDigest");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowExecutions_WorkflowDigest",
                table: "WorkflowExecutions");

            migrationBuilder.DropColumn(
                name: "WorkflowDigest",
                table: "WorkflowExecutions");

            migrationBuilder.DropColumn(
                name: "WorkflowNamespace",
                table: "WorkflowExecutions");

            migrationBuilder.DropColumn(
                name: "WorkflowRegistry",
                table: "WorkflowExecutions");
        }
    }
}
