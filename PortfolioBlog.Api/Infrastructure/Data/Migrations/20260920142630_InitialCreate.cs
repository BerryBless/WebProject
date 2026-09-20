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
            migrationBuilder.CreateTable(
                name: "AdminState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    SessionEpoch = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminState", x => x.Id);
                    table.CheckConstraint("CK_AdminState_Single", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "Series",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Series", x => x.Id);
                    table.CheckConstraint("CK_Series_Slug_Format", "\"Slug\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("CK_Series_Title_NotBlank", "length(btrim(\"Title\")) > 0");
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                    table.CheckConstraint("CK_Tags_Name_NoSlash", "position('/' in \"Name\") = 0");
                    table.CheckConstraint("CK_Tags_Name_NotBlank", "length(btrim(\"Name\")) > 0");
                });

            migrationBuilder.CreateTable(
                name: "Posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ContentMarkdown = table.Column<string>(type: "text", nullable: false),
                    SeriesId = table.Column<Guid>(type: "uuid", nullable: true),
                    SeriesOrder = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Posts", x => x.Id);
                    table.CheckConstraint("CK_Posts_Content_Size", "octet_length(\"ContentMarkdown\") <= 204800");
                    table.CheckConstraint("CK_Posts_Series_Pair", "(\"SeriesId\" IS NULL) = (\"SeriesOrder\" IS NULL)");
                    table.CheckConstraint("CK_Posts_SeriesOrder_Positive", "\"SeriesOrder\" IS NULL OR \"SeriesOrder\" > 0");
                    table.CheckConstraint("CK_Posts_Slug_Format", "\"Slug\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("CK_Posts_Title_NotBlank", "length(btrim(\"Title\")) > 0");
                    table.ForeignKey(
                        name: "FK_Posts_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PostTags",
                columns: table => new
                {
                    PostId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagId = table.Column<Guid>(type: "uuid", nullable: false)
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
                });

            migrationBuilder.InsertData(
                table: "AdminState",
                columns: new[] { "Id", "SessionEpoch" },
                values: new object[] { 1, 1 });

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
