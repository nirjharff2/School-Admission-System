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
            // Rows written before the unique index existed may break it: the old apply path could leave a student's
            // lapsed-but-unswept HELD row next to a new HELD row, and could race into duplicates. Resolve both first so the
            // migration cannot fail startup. Lapsed holds become EXPIRED (what the sweeper would do anyway); of any
            // remaining duplicate active rows for one student and class, keep a CONFIRMED one (money was paid) or else the
            // newest, and cancel the rest.
            migrationBuilder.Sql(
                "UPDATE holds SET status = 'EXPIRED' WHERE status = 'HELD' AND expires_at <= now();");
            migrationBuilder.Sql("""
                UPDATE holds SET status = 'CANCELLED'
                WHERE id IN (
                    SELECT id FROM (
                        SELECT id, ROW_NUMBER() OVER (
                            PARTITION BY class_id, student_id
                            ORDER BY (status = 'CONFIRMED') DESC, created_at DESC, id DESC) AS rn
                        FROM holds
                        WHERE status IN ('HELD', 'CONFIRMED')) ranked
                    WHERE rn > 1);
                """);

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
