using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Execution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyActivityOutputPK : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ActivityOutputs",
                table: "ActivityOutputs");

            migrationBuilder.DropColumn(
                name: "ActivityName",
                table: "ActivityOutputs");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ActivityOutputs",
                table: "ActivityOutputs",
                column: "ExecutionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ActivityOutputs",
                table: "ActivityOutputs");

            migrationBuilder.AddColumn<string>(
                name: "ActivityName",
                table: "ActivityOutputs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ActivityOutputs",
                table: "ActivityOutputs",
                columns: new[] { "ExecutionId", "ActivityName" });
        }
    }
}
