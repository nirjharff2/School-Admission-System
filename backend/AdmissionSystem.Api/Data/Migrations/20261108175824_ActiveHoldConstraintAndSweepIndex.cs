using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AdmissionSystem.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ActiveHoldConstraintAndSweepIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_holds_held_expires_at",
                table: "holds",
                column: "expires_at",
                filter: "status = 'HELD'");

            migrationBuilder.CreateIndex(
                name: "ux_holds_active_student_class",
                table: "holds",
                columns: new[] { "class_id", "student_id" },
                unique: true,
                filter: "status IN ('HELD', 'CONFIRMED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_holds_held_expires_at",
                table: "holds");

            migrationBuilder.DropIndex(
                name: "ux_holds_active_student_class",
                table: "holds");
        }
    }
}
