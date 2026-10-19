using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuitarPractice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Auto_20261019_154843 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProgressExercise_NoteValue",
                table: "ProgressExercises");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProgressExercise_NoteValue",
                table: "ProgressExercises",
                sql: "NoteValue IS NULL OR (NoteValue >= 1 AND NoteValue <= 8)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProgressExercise_NoteValue",
                table: "ProgressExercises");

            // Hand-added: the old schema stops at sextuplets; septuplet/32nd lines lose their note value.
            migrationBuilder.Sql("UPDATE ProgressExercises SET NoteValue = NULL WHERE NoteValue > 6;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProgressExercise_NoteValue",
                table: "ProgressExercises",
                sql: "NoteValue IS NULL OR (NoteValue >= 1 AND NoteValue <= 6)");
        }
    }
}
