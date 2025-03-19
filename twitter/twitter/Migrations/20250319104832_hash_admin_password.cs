using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace twitter.Migrations
{
    /// <inheritdoc />
    public partial class hash_admin_password : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: 1,
                column: "Password",
                value: "$2a$11$syMHGVO15Mj1jh14jiY3R.vb3LpnZNL1.JLXhSRgTGYca28ry1VCS");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "Users",
                keyColumn: "Id",
                keyValue: 1,
                column: "Password",
                value: "admin");
        }
    }
}
