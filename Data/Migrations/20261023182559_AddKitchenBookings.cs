using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace joevarApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKitchenBookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KitchenBookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AppointmentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AppointmentTime = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    Selections = table.Column<List<string>>(type: "text[]", nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KitchenBookings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KitchenBookings_AppointmentDate",
                table: "KitchenBookings",
                column: "AppointmentDate");

            migrationBuilder.CreateIndex(
                name: "IX_KitchenBookings_CreatedAt",
                table: "KitchenBookings",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KitchenBookings");
        }
    }
}
