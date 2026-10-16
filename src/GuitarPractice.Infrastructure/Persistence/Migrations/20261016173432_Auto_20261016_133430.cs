using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GuitarPractice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Auto_20261016_133430 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WeeklyPlans_WeekStart",
                table: "WeeklyPlans");

            migrationBuilder.AddColumn<bool>(
                name: "IsTemplate",
                table: "WeeklyPlans",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "WeeklyPlans",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlanSeed",
                table: "Routines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PracticeLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    SubcategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    ElapsedSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PracticeLogs", x => x.Id);
                    table.CheckConstraint("CK_PracticeLog_Elapsed", "ElapsedSeconds >= 0");
                    table.ForeignKey(
                        name: "FK_PracticeLogs_Subcategories_SubcategoryId",
                        column: x => x.SubcategoryId,
                        principalTable: "Subcategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyPlans_IsTemplate_WeekStart",
                table: "WeeklyPlans",
                columns: new[] { "IsTemplate", "WeekStart" });

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyPlans_Name",
                table: "WeeklyPlans",
                column: "Name",
                unique: true,
                filter: "\"IsTemplate\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLogs_Date_SubcategoryId",
                table: "PracticeLogs",
                columns: new[] { "Date", "SubcategoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PracticeLogs_SubcategoryId",
                table: "PracticeLogs",
                column: "SubcategoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PracticeLogs");

            migrationBuilder.DropIndex(
                name: "IX_WeeklyPlans_IsTemplate_WeekStart",
                table: "WeeklyPlans");

            migrationBuilder.DropIndex(
                name: "IX_WeeklyPlans_Name",
                table: "WeeklyPlans");

            migrationBuilder.DropColumn(
                name: "IsTemplate",
                table: "WeeklyPlans");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "WeeklyPlans");

            migrationBuilder.DropColumn(
                name: "PlanSeed",
                table: "Routines");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyPlans_WeekStart",
                table: "WeeklyPlans",
                column: "WeekStart");
        }
    }
}
