using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace joevarApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKitchenBookingStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DecidedAt",
                table: "KitchenBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecidedBy",
                table: "KitchenBookings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "KitchenBookings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.CreateIndex(
                name: "IX_KitchenBookings_Status",
                table: "KitchenBookings",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_KitchenBookings_Status",
                table: "KitchenBookings");

            migrationBuilder.DropColumn(
                name: "DecidedAt",
                table: "KitchenBookings");

            migrationBuilder.DropColumn(
                name: "DecidedBy",
                table: "KitchenBookings");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "KitchenBookings");
        }
    }
}
