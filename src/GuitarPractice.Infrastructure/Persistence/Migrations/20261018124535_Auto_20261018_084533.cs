using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuitarPractice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Auto_20261018_084533 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProgressExercise_NoteValue",
                table: "ProgressExercises");

            migrationBuilder.AddColumn<int>(
                name: "RemovedRun",
                table: "ProgressExercises",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProgressExercise_NoteValue",
                table: "ProgressExercises",
                sql: "NoteValue IS NULL OR (NoteValue >= 1 AND NoteValue <= 6)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProgressExercise_RemovedRun",
                table: "ProgressExercises",
                sql: "RemovedRun IS NULL OR RemovedRun >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProgressExercise_NoteValue",
                table: "ProgressExercises");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProgressExercise_RemovedRun",
                table: "ProgressExercises");

            migrationBuilder.DropColumn(
                name: "RemovedRun",
                table: "ProgressExercises");

            // Hand-added: the old schema only knows quarters to 16ths; quintuplet/sextuplet lines lose their note value.
            migrationBuilder.Sql("UPDATE ProgressExercises SET NoteValue = NULL WHERE NoteValue > 4;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProgressExercise_NoteValue",
                table: "ProgressExercises",
                sql: "NoteValue IS NULL OR (NoteValue >= 1 AND NoteValue <= 4)");
        }
    }
}
