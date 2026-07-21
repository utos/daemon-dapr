using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Execution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAmbientEnv : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnvJson",
                table: "WorkflowExecutions",
                type: "nvarchar(max)",
                nullable: true);

            // "{}" rather than EF's default of "": rows written before this column existed
            // belong to runs still in flight, and an empty string is not valid JSON, so
            // reading one back would throw on deserialization.
            migrationBuilder.AddColumn<string>(
                name: "EnvJson",
                table: "ExecutionContexts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnvJson",
                table: "WorkflowExecutions");

            migrationBuilder.DropColumn(
                name: "EnvJson",
                table: "ExecutionContexts");
        }
    }
}
