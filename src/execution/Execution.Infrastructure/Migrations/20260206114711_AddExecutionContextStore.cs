using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Execution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionContextStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityOutputs",
                columns: table => new
                {
                    ExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OutputJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityOutputs", x => new { x.ExecutionId, x.ActivityName });
                });

            migrationBuilder.CreateTable(
                name: "ExecutionContexts",
                columns: table => new
                {
                    ExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BundleJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EntryPoint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InputJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionContexts", x => x.ExecutionId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityOutputs");

            migrationBuilder.DropTable(
                name: "ExecutionContexts");
        }
    }
}
