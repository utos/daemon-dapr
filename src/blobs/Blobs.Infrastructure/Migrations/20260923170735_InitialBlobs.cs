using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Blobs.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialBlobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlobHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BlobId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RootExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Workflow = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    WorkflowDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Activity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FirstAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Executions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlobHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BlobRunTrees",
                columns: table => new
                {
                    BlobId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RootExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlobRunTrees", x => new { x.BlobId, x.RootExecutionId });
                });

            migrationBuilder.CreateTable(
                name: "Blobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Retention = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MediaType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LastModified = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Blobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlobHistory_BlobId_Kind_RootExecutionId_Workflow",
                table: "BlobHistory",
                columns: new[] { "BlobId", "Kind", "RootExecutionId", "Workflow" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BlobRunTrees_RootExecutionId",
                table: "BlobRunTrees",
                column: "RootExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_Blobs_Retention_State_CreatedAt",
                table: "Blobs",
                columns: new[] { "Retention", "State", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlobHistory");

            migrationBuilder.DropTable(
                name: "BlobRunTrees");

            migrationBuilder.DropTable(
                name: "Blobs");
        }
    }
}
