using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace joevarApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordSmsConsentFromWebForm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Evidence",
                table: "SmsConsents",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ContactPhoneId",
                table: "SmsConsents",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "EvidenceVersion",
                table: "SmsConsents",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneNumber",
                table: "SmsConsents",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            // Older records were always tied to a customer/contractor phone: copy its number over.
            migrationBuilder.Sql("""
                UPDATE "SmsConsents" AS consent
                SET "PhoneNumber" = phone."Number"
                FROM "ContactPhones" AS phone
                WHERE consent."ContactPhoneId" = phone."Id";
                """);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "SmsConsents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SmsConsents_PhoneNumber_RecordedAt",
                table: "SmsConsents",
                columns: new[] { "PhoneNumber", "RecordedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SmsConsents_PhoneNumber_RecordedAt",
                table: "SmsConsents");

            migrationBuilder.DropColumn(
                name: "EvidenceVersion",
                table: "SmsConsents");

            migrationBuilder.DropColumn(
                name: "PhoneNumber",
                table: "SmsConsents");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "SmsConsents");

            migrationBuilder.AlterColumn<string>(
                name: "Evidence",
                table: "SmsConsents",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ContactPhoneId",
                table: "SmsConsents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
