using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace joevarApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBathroomRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BathroomRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Package = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PackageName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StartingPrice = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StreetAddress = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ZipCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    LengthFeet = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    WidthFeet = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    Timeline = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Goals = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BathroomRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BathroomRequestPhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BathroomRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BathroomRequestPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BathroomRequestPhotos_BathroomRequests_BathroomRequestId",
                        column: x => x.BathroomRequestId,
                        principalTable: "BathroomRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BathroomRequestPhotos_BathroomRequestId",
                table: "BathroomRequestPhotos",
                column: "BathroomRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_BathroomRequests_CreatedAt",
                table: "BathroomRequests",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BathroomRequestPhotos");

            migrationBuilder.DropTable(
                name: "BathroomRequests");
        }
    }
}
