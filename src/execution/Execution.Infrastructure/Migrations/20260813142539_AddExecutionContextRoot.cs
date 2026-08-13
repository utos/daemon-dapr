using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Execution.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionContextRoot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RootExecutionId",
                table: "ExecutionContexts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionContexts_RootExecutionId",
                table: "ExecutionContexts",
                column: "RootExecutionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExecutionContexts_RootExecutionId",
                table: "ExecutionContexts");

            migrationBuilder.DropColumn(
                name: "RootExecutionId",
                table: "ExecutionContexts");
        }
    }
}
