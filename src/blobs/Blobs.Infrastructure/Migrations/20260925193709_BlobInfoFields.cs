using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blobs.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BlobInfoFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "Blobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Blobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProducerActivity",
                table: "Blobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProducerExecutionId",
                table: "Blobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProducerRootExecutionId",
                table: "Blobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProducerWorkflow",
                table: "Blobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProducerWorkflowDigest",
                table: "Blobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "Blobs");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Blobs");

            migrationBuilder.DropColumn(
                name: "ProducerActivity",
                table: "Blobs");

            migrationBuilder.DropColumn(
                name: "ProducerExecutionId",
                table: "Blobs");

            migrationBuilder.DropColumn(
                name: "ProducerRootExecutionId",
                table: "Blobs");

            migrationBuilder.DropColumn(
                name: "ProducerWorkflow",
                table: "Blobs");

            migrationBuilder.DropColumn(
                name: "ProducerWorkflowDigest",
                table: "Blobs");
        }
    }
}
