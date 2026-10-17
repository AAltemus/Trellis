using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuitarPractice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Auto_20261016_205259 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProgressEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    SubcategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    BpmReached = table.Column<int>(type: "INTEGER", nullable: true),
                    Rating = table.Column<int>(type: "INTEGER", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProgressEntries", x => x.Id);
                    table.CheckConstraint("CK_ProgressEntry_Bpm", "BpmReached IS NULL OR (BpmReached >= 1 AND BpmReached <= 400)");
                    table.CheckConstraint("CK_ProgressEntry_Rating", "Rating IS NULL OR (Rating >= 1 AND Rating <= 5)");
                    table.ForeignKey(
                        name: "FK_ProgressEntries_Subcategories_SubcategoryId",
                        column: x => x.SubcategoryId,
                        principalTable: "Subcategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProgressEntries_Date_SubcategoryId",
                table: "ProgressEntries",
                columns: new[] { "Date", "SubcategoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProgressEntries_SubcategoryId",
                table: "ProgressEntries",
                column: "SubcategoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProgressEntries");
        }
    }
}
