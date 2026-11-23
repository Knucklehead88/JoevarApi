using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace joevarApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveStaffAuthenticator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Staff sign in with username and password only (2026-11-24, at the user's request): two-factor off for every
            // login, the authenticator keys and recovery codes gone, and wrong-code/password lockouts lifted. Blocked
            // logins (locked until year 9999) stay blocked.
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers" SET "TwoFactorEnabled" = false WHERE "TwoFactorEnabled";
                DELETE FROM "AspNetUserTokens" WHERE "LoginProvider" = '[AspNetUserStore]' AND "Name" IN ('AuthenticatorKey', 'RecoveryCodes');
                UPDATE "AspNetUsers" SET "LockoutEnd" = NULL, "AccessFailedCount" = 0
                    WHERE "LockoutEnd" IS NOT NULL AND "LockoutEnd" < '9999-01-01'::timestamptz;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
