using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WatchBack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixProviderConfigCompositeIndex : Migration
    {
        private static readonly string[] s_compositeColumns = ["ProviderName", "ConfigKey"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProviderConfigs_ProviderName",
                table: "ProviderConfigs");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderConfigs_ProviderName_ConfigKey",
                table: "ProviderConfigs",
                columns: s_compositeColumns,
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProviderConfigs_ProviderName_ConfigKey",
                table: "ProviderConfigs");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderConfigs_ProviderName",
                table: "ProviderConfigs",
                column: "ProviderName",
                unique: true);
        }
    }
}
