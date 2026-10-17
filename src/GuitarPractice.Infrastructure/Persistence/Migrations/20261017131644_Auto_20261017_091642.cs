using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuitarPractice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Auto_20261017_091642 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExerciseTargets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SubcategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 108, nullable: false),
                    TargetBpm = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExerciseTargets", x => x.Id);
                    table.CheckConstraint("CK_ExerciseTarget_Bpm", "TargetBpm >= 1 AND TargetBpm <= 400");
                    table.ForeignKey(
                        name: "FK_ExerciseTargets_Subcategories_SubcategoryId",
                        column: x => x.SubcategoryId,
                        principalTable: "Subcategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExerciseTargets_SubcategoryId_Key",
                table: "ExerciseTargets",
                columns: new[] { "SubcategoryId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExerciseTargets");
        }
    }
}
