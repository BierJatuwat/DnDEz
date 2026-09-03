using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DnDEz.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEncounterTurnNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "turn_number",
                table: "encounters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_encounters_turn_number",
                table: "encounters",
                sql: "turn_number >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_encounters_turn_number",
                table: "encounters");

            migrationBuilder.DropColumn(
                name: "turn_number",
                table: "encounters");
        }
    }
}
