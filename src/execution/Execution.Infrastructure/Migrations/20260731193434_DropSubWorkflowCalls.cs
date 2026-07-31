using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Execution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropSubWorkflowCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubWorkflowCalls");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubWorkflowCalls",
                columns: table => new
                {
                    ParentExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChildExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BranchName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InvocationType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PromiseMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubWorkflowCalls", x => new { x.ParentExecutionId, x.ChildExecutionId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubWorkflowCalls_ChildExecutionId",
                table: "SubWorkflowCalls",
                column: "ChildExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_SubWorkflowCalls_ParentExecutionId_ActivityName",
                table: "SubWorkflowCalls",
                columns: new[] { "ParentExecutionId", "ActivityName" });
        }
    }
}
