using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureBuddy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemStateConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkItemStateConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    WorkItemType = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StateName = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemStateConfigurations", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemStateConfigurations_WorkItemType",
                table: "WorkItemStateConfigurations",
                column: "WorkItemType");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemStateConfigurations_WorkItemType_StateName",
                table: "WorkItemStateConfigurations",
                columns: new[] { "WorkItemType", "StateName" },
                unique: true);

            // Seed a sensible default so the feature isn't useless before an admin ever opens the
            // config screen. These are the Agile process template's default states (the template this
            // app's own CreateBugFlow already assumes by always creating type "Bug") - an admin can
            // rename/reorder/disable/add to these per project at any time from /admin/work-item-states.
            var seededAt = new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);
            migrationBuilder.InsertData(
                table: "WorkItemStateConfigurations",
                columns: new[] { "WorkItemType", "StateName", "DisplayOrder", "IsEnabled", "CreatedAt", "UpdatedAt" },
                values: new object[,]
                {
                    { "Bug", "New", 0, true, seededAt, seededAt },
                    { "Bug", "Active", 1, true, seededAt, seededAt },
                    { "Bug", "Resolved", 2, true, seededAt, seededAt },
                    { "Bug", "Closed", 3, true, seededAt, seededAt },
                    { "Task", "To Do", 0, true, seededAt, seededAt },
                    { "Task", "In Progress", 1, true, seededAt, seededAt },
                    { "Task", "Done", 2, true, seededAt, seededAt },
                    { "User Story", "New", 0, true, seededAt, seededAt },
                    { "User Story", "Active", 1, true, seededAt, seededAt },
                    { "User Story", "Resolved", 2, true, seededAt, seededAt },
                    { "User Story", "Closed", 3, true, seededAt, seededAt },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkItemStateConfigurations");
        }
    }
}
