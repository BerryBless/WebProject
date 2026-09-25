using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PortfolioBlog.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AdminState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    SessionEpoch = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminState", x => x.Id);
                    table.CheckConstraint("CK_AdminState_Single", "`Id` = 1");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    FileName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContentType = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StoragePath = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Sha256 = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.CheckConstraint("CK_Attachments_ContentType", "`ContentType` IN ('image/png', 'image/jpeg', 'image/gif', 'image/webp')");
                    table.CheckConstraint("CK_Attachments_FileName_NotBlank", "CHAR_LENGTH(TRIM(`FileName`)) > 0");
                    table.CheckConstraint("CK_Attachments_Sha256", "REGEXP_LIKE(`Sha256`, '^[0-9a-f]{64}\\\\z', 'c')");
                    table.CheckConstraint("CK_Attachments_Size", "`SizeBytes` BETWEEN 1 AND 10485760");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Series",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Slug = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Series", x => x.Id);
                    table.CheckConstraint("CK_Series_Slug_Format", "REGEXP_LIKE(`Slug`, '^[a-z0-9]+(-[a-z0-9]+)*\\\\z', 'c')");
                    table.CheckConstraint("CK_Series_Title_NotBlank", "CHAR_LENGTH(TRIM(`Title`)) > 0");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Name = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NormalizedName = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                    table.CheckConstraint("CK_Tags_Name_NoSlash", "LOCATE('/', `Name`) = 0");
                    table.CheckConstraint("CK_Tags_Name_NotBlank", "CHAR_LENGTH(TRIM(`Name`)) > 0");
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Slug = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false, collation: "utf8mb4_bin")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Summary = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContentMarkdown = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SeriesId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    SeriesOrder = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Version = table.Column<uint>(type: "int unsigned", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Posts", x => x.Id);
                    table.CheckConstraint("CK_Posts_Content_Size", "LENGTH(`ContentMarkdown`) <= 204800");
                    table.CheckConstraint("CK_Posts_Series_Pair", "(`SeriesId` IS NULL) = (`SeriesOrder` IS NULL)");
                    table.CheckConstraint("CK_Posts_SeriesOrder_Positive", "`SeriesOrder` IS NULL OR `SeriesOrder` > 0");
                    table.CheckConstraint("CK_Posts_Slug_Format", "REGEXP_LIKE(`Slug`, '^[a-z0-9]+(-[a-z0-9]+)*\\\\z', 'c')");
                    table.CheckConstraint("CK_Posts_Title_NotBlank", "CHAR_LENGTH(TRIM(`Title`)) > 0");
                    table.ForeignKey(
                        name: "FK_Posts_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PostTags",
                columns: table => new
                {
                    PostId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    TagId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostTags", x => new { x.PostId, x.TagId });
                    table.ForeignKey(
                        name: "FK_PostTags_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PostTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "AdminState",
                columns: new[] { "Id", "SessionEpoch" },
                values: new object[] { 1, 1 });

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

            migrationBuilder.CreateIndex(
                name: "IX_Posts_CreatedAt_Id",
                table: "Posts",
                columns: new[] { "CreatedAt", "Id" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_SeriesId_SeriesOrder_CreatedAt_Id",
                table: "Posts",
                columns: new[] { "SeriesId", "SeriesOrder", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Slug",
                table: "Posts",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostTags_TagId_PostId",
                table: "PostTags",
                columns: new[] { "TagId", "PostId" });

            migrationBuilder.CreateIndex(
                name: "IX_Series_Slug",
                table: "Series",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_NormalizedName",
                table: "Tags",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminState");

            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "PostTags");

            migrationBuilder.DropTable(
                name: "Posts");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "Series");
        }
    }
}
