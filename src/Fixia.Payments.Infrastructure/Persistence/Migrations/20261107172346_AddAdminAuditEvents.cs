using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fixia.Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminAuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    admin_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    target_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_audit_events", x => x.id);
                    table.CheckConstraint("ck_admin_audit_events_action", "action IN ('PAYMENT_LIST', 'PAYMENT_VIEW', 'PAYMENT_REFUND', 'AUDIT_LIST')");
                    table.CheckConstraint("ck_admin_audit_events_outcome", "outcome IN ('OK', 'DENIED', 'CONFLICT', 'NOT_FOUND')");
                    table.CheckConstraint("ck_admin_audit_events_target", "(target_type IS NULL AND target_id IS NULL) OR (target_type = 'PAYMENT' AND target_id IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_events_admin",
                table: "admin_audit_events",
                columns: new[] { "admin_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_admin_audit_events_occurred",
                table: "admin_audit_events",
                columns: new[] { "occurred_at", "id" },
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_audit_events");
        }
    }
}
