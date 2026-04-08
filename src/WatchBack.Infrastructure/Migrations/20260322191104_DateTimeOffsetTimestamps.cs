using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WatchBack.Infrastructure.Migrations
{
    /// <summary>
    /// Intentionally empty migration — the DateTimeOffset timestamp change was implemented
    /// directly in the initial schema rather than as a separate alter-column operation.
    /// The migration is kept to avoid breaking existing databases that have this ID recorded
    /// in their <c>__EFMigrationsHistory</c> table.
    /// </summary>
    public partial class DateTimeOffsetTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
