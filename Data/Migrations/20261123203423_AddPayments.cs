using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace joevarApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "KitchenBookings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DepositCents",
                table: "KitchenBookings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaidAt",
                table: "KitchenBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RefundedAt",
                table: "KitchenBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeCheckoutSessionId",
                table: "KitchenBookings",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripePaymentIntentId",
                table: "KitchenBookings",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeCustomerId",
                table: "Customers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomerStages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerStages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SiteSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "CustomerStageCompletions",
                columns: table => new
                {
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    StageId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerStageCompletions", x => new { x.CustomerId, x.StageId });
                    table.ForeignKey(
                        name: "FK_CustomerStageCompletions_CustomerStages_StageId",
                        column: x => x.StageId,
                        principalTable: "CustomerStages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomerStageCompletions_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "CustomerStages",
                columns: new[] { "Id", "CreatedAt", "Key", "Name", "Position" },
                values: new object[,]
                {
                    { new Guid("5a1f0c1e-0001-4000-8000-000000000001"), new DateTimeOffset(new DateTime(2026, 11, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "deposit", "Paid Deposit", 0 },
                    { new Guid("5a1f0c1e-0002-4000-8000-000000000002"), new DateTimeOffset(new DateTime(2026, 11, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, "Received Blueprint", 1 },
                    { new Guid("5a1f0c1e-0003-4000-8000-000000000003"), new DateTimeOffset(new DateTime(2026, 11, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, "Installed Cabinets", 2 },
                    { new Guid("5a1f0c1e-0004-4000-8000-000000000004"), new DateTimeOffset(new DateTime(2026, 11, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, "Installed Countertop", 3 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_KitchenBookings_CustomerId",
                table: "KitchenBookings",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_KitchenBookings_StripeCheckoutSessionId",
                table: "KitchenBookings",
                column: "StripeCheckoutSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerStageCompletions_StageId",
                table: "CustomerStageCompletions",
                column: "StageId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerStages_Key",
                table: "CustomerStages",
                column: "Key",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_KitchenBookings_Customers_CustomerId",
                table: "KitchenBookings",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_KitchenBookings_Customers_CustomerId",
                table: "KitchenBookings");

            migrationBuilder.DropTable(
                name: "CustomerStageCompletions");

            migrationBuilder.DropTable(
                name: "SiteSettings");

            migrationBuilder.DropTable(
                name: "CustomerStages");

            migrationBuilder.DropIndex(
                name: "IX_KitchenBookings_CustomerId",
                table: "KitchenBookings");

            migrationBuilder.DropIndex(
                name: "IX_KitchenBookings_StripeCheckoutSessionId",
                table: "KitchenBookings");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "KitchenBookings");

            migrationBuilder.DropColumn(
                name: "DepositCents",
                table: "KitchenBookings");

            migrationBuilder.DropColumn(
                name: "PaidAt",
                table: "KitchenBookings");

            migrationBuilder.DropColumn(
                name: "RefundedAt",
                table: "KitchenBookings");

            migrationBuilder.DropColumn(
                name: "StripeCheckoutSessionId",
                table: "KitchenBookings");

            migrationBuilder.DropColumn(
                name: "StripePaymentIntentId",
                table: "KitchenBookings");

            migrationBuilder.DropColumn(
                name: "StripeCustomerId",
                table: "Customers");
        }
    }
}
