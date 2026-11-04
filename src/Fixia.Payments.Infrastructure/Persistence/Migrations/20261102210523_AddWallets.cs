using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fixia.Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWallets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "base_amount",
                table: "payments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "commission_amount",
                table: "payments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "commission_rate",
                table: "payments",
                type: "numeric(5,4)",
                precision: 5,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "commission_rule_id",
                table: "payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "contract_id",
                table: "payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "commission_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rate = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    valid_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_rules", x => x.id);
                    table.CheckConstraint("ck_commission_rules_rate", "rate > 0 AND rate < 1");
                    table.CheckConstraint("ck_commission_rules_validity", "valid_to IS NULL OR valid_to > valid_from");
                });

            migrationBuilder.CreateTable(
                name: "wallets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    available_balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    held_balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallets", x => x.id);
                    table.CheckConstraint("ck_wallets_available_non_negative", "available_balance >= 0");
                    table.CheckConstraint("ck_wallets_held_non_negative", "held_balance >= 0");
                });

            migrationBuilder.CreateTable(
                name: "wallet_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    available_after = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    held_after = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_entries", x => x.id);
                    table.CheckConstraint("ck_wallet_entries_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_wallet_entries_balances_non_negative", "available_after >= 0 AND held_after >= 0");
                    table.ForeignKey(
                        name: "fk_wallet_entries_payment",
                        column: x => x.payment_id,
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_wallet_entries_wallet",
                        column: x => x.wallet_id,
                        principalTable: "wallets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "commission_rules",
                columns: new[] { "id", "created_at", "description", "rate", "valid_from", "valid_to" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000015"), new DateTime(2026, 11, 2, 0, 0, 0, 0, DateTimeKind.Utc), "Tarifa inicial aprobada por el PO (GC-476): 15 % a cargo del cliente", 0.15m, new DateTime(2026, 11, 2, 0, 0, 0, 0, DateTimeKind.Utc), null });

            migrationBuilder.InsertData(
                table: "wallets",
                columns: new[] { "id", "available_balance", "created_at", "currency", "held_balance", "owner_id", "owner_type", "updated_at" },
                values: new object[] { new Guid("00000000-0000-0000-0000-0000000f1a10"), 0m, new DateTime(2026, 11, 2, 0, 0, 0, 0, DateTimeKind.Utc), "COP", 0m, new Guid("00000000-0000-0000-0000-00000000f1a1"), "Platform", null });

            migrationBuilder.CreateIndex(
                name: "ix_payments_commission_rule_id",
                table: "payments",
                column: "commission_rule_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_amount_positive",
                table: "payments",
                sql: "amount > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_breakdown",
                table: "payments",
                sql: "(base_amount IS NULL AND commission_amount IS NULL) OR (base_amount > 0 AND commission_amount >= 0 AND amount = base_amount + commission_amount)");

            migrationBuilder.CreateIndex(
                name: "ux_commission_rules_valid_from",
                table: "commission_rules",
                column: "valid_from",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wallet_entries_payment_id",
                table: "wallet_entries",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_wallet_entries_wallet_created",
                table: "wallet_entries",
                columns: new[] { "wallet_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ux_wallets_owner_currency",
                table: "wallets",
                columns: new[] { "owner_id", "currency" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_payments_commission_rule",
                table: "payments",
                column: "commission_rule_id",
                principalTable: "commission_rules",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_payments_commission_rule",
                table: "payments");

            migrationBuilder.DropTable(
                name: "commission_rules");

            migrationBuilder.DropTable(
                name: "wallet_entries");

            migrationBuilder.DropTable(
                name: "wallets");

            migrationBuilder.DropIndex(
                name: "ix_payments_commission_rule_id",
                table: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_amount_positive",
                table: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_breakdown",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "base_amount",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "commission_amount",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "commission_rate",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "commission_rule_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "contract_id",
                table: "payments");
        }
    }
}
