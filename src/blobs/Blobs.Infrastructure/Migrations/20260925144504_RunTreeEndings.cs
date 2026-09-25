using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blobs.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RunTreeEndings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TreeEndedAt",
                table: "BlobRunTrees",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TreeOutcome",
                table: "BlobRunTrees",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BlobRunTrees_BlobId_TreeEndedAt",
                table: "BlobRunTrees",
                columns: new[] { "BlobId", "TreeEndedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BlobRunTrees_BlobId_TreeEndedAt",
                table: "BlobRunTrees");

            migrationBuilder.DropColumn(
                name: "TreeEndedAt",
                table: "BlobRunTrees");

            migrationBuilder.DropColumn(
                name: "TreeOutcome",
                table: "BlobRunTrees");
        }
    }
}
