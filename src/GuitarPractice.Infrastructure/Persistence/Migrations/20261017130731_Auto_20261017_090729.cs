using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuitarPractice.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ProgressEntries.BpmReached (one BPM per block per day) → ProgressExercises (several named exercises per block,
    /// each with BPM and note value). Hand-ordered so no logged BPM is lost:
    /// 1. copy existing BPMs into a temporary carry table;
    /// 2. drop the column (SQLite rebuilds ProgressEntries; nothing references it yet, so no cascade can fire);
    /// 3. create ProgressExercises and move the carried BPMs in as unnamed exercises;
    /// 4. drop the carry table.
    /// </summary>
    public partial class Auto_20261017_090729 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE TABLE \"ProgressBpmCarry\" (\"EntryId\" INTEGER NOT NULL, \"Bpm\" INTEGER NOT NULL);" +
                "INSERT INTO \"ProgressBpmCarry\" (\"EntryId\", \"Bpm\") " +
                "SELECT \"Id\", \"BpmReached\" FROM \"ProgressEntries\" WHERE \"BpmReached\" IS NOT NULL;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProgressEntry_Bpm",
                table: "ProgressEntries");

            migrationBuilder.DropColumn(
                name: "BpmReached",
                table: "ProgressEntries");

            migrationBuilder.CreateTable(
                name: "ProgressExercises",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProgressEntryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Bpm = table.Column<int>(type: "INTEGER", nullable: true),
                    NoteValue = table.Column<int>(type: "INTEGER", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgressExercises", x => x.Id);
                    table.CheckConstraint("CK_ProgressExercise_Bpm", "Bpm IS NULL OR (Bpm >= 1 AND Bpm <= 400)");
                    table.CheckConstraint("CK_ProgressExercise_NoteValue", "NoteValue IS NULL OR (NoteValue >= 1 AND NoteValue <= 4)");
                    table.ForeignKey(
                        name: "FK_ProgressExercises_ProgressEntries_ProgressEntryId",
                        column: x => x.ProgressEntryId,
                        principalTable: "ProgressEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProgressExercises_ProgressEntryId_SortOrder",
                table: "ProgressExercises",
                columns: new[] { "ProgressEntryId", "SortOrder" });

            migrationBuilder.Sql(
                "INSERT INTO \"ProgressExercises\" (\"ProgressEntryId\", \"Name\", \"Bpm\", \"NoteValue\", \"SortOrder\") " +
                "SELECT \"EntryId\", '', \"Bpm\", NULL, 0 FROM \"ProgressBpmCarry\";" +
                "DROP TABLE \"ProgressBpmCarry\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keep the best BPM per entry, then drop the child table before rebuilding ProgressEntries.
            migrationBuilder.Sql(
                "CREATE TABLE \"ProgressBpmCarry\" (\"EntryId\" INTEGER NOT NULL, \"Bpm\" INTEGER NOT NULL);" +
                "INSERT INTO \"ProgressBpmCarry\" (\"EntryId\", \"Bpm\") " +
                "SELECT \"ProgressEntryId\", MAX(\"Bpm\") FROM \"ProgressExercises\" WHERE \"Bpm\" IS NOT NULL GROUP BY \"ProgressEntryId\";");

            migrationBuilder.DropTable(
                name: "ProgressExercises");

            migrationBuilder.AddColumn<int>(
                name: "BpmReached",
                table: "ProgressEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProgressEntry_Bpm",
                table: "ProgressEntries",
                sql: "BpmReached IS NULL OR (BpmReached >= 1 AND BpmReached <= 400)");

            migrationBuilder.Sql(
                "UPDATE \"ProgressEntries\" SET \"BpmReached\" = " +
                "(SELECT \"Bpm\" FROM \"ProgressBpmCarry\" WHERE \"EntryId\" = \"ProgressEntries\".\"Id\");" +
                "DROP TABLE \"ProgressBpmCarry\";");
        }
    }
}
