using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioBlog.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.CheckConstraint("CK_Attachments_ContentType", "\"ContentType\" IN ('image/png', 'image/jpeg', 'image/gif', 'image/webp')");
                    table.CheckConstraint("CK_Attachments_FileName_NotBlank", "length(btrim(\"FileName\")) > 0");
                    table.CheckConstraint("CK_Attachments_Sha256", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_Attachments_Size", "\"SizeBytes\" BETWEEN 1 AND 10485760");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_CreatedAt_Id",
                table: "Attachments",
                columns: new[] { "CreatedAt", "Id" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_Sha256",
                table: "Attachments",
                column: "Sha256",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attachments");
        }
    }
}
