using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WatchBack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedResults",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedResults", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedResults_ExpiresAtUnixMs",
                table: "CachedResults",
                column: "ExpiresAtUnixMs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedResults");
        }
    }
}
