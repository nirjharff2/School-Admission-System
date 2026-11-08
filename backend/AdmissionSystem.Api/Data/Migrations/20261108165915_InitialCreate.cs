using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdmissionSystem.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "classes",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    total_seats = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_classes", x => x.id);
                    table.CheckConstraint("ck_classes_total_seats_positive", "total_seats >= 1");
                });

            migrationBuilder.CreateTable(
                name: "holds",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    class_id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    student_id = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holds", x => x.id);
                    table.ForeignKey(
                        name: "FK_holds_classes_class_id",
                        column: x => x.class_id,
                        principalTable: "classes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_classes_created_at",
                table: "classes",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_holds_class_id_status_expires_at",
                table: "holds",
                columns: new[] { "class_id", "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_holds_student_id_created_at",
                table: "holds",
                columns: new[] { "student_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holds");

            migrationBuilder.DropTable(
                name: "classes");
        }
    }
}
