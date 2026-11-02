using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace joevarApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKitchenBookingAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "KitchenBookings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "KitchenBookings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StreetAddress",
                table: "KitchenBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ZipCode",
                table: "KitchenBookings",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "City",
                table: "KitchenBookings");

            migrationBuilder.DropColumn(
                name: "State",
                table: "KitchenBookings");

            migrationBuilder.DropColumn(
                name: "StreetAddress",
                table: "KitchenBookings");

            migrationBuilder.DropColumn(
                name: "ZipCode",
                table: "KitchenBookings");
        }
    }
}
