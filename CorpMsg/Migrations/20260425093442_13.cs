using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CorpMsg.Migrations
{
    /// <inheritdoc />
    public partial class _13 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_User_Company_Username",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_User_FullName",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_User_Username",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Department_Company_Name",
                table: "Departments");

            migrationBuilder.DropIndex(
                name: "IX_BannedWord_Word",
                table: "BannedWords");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_User_Company_Username",
                table: "Users",
                columns: new[] { "CompanyId", "Username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_User_FullName",
                table: "Users",
                column: "FullName");

            migrationBuilder.CreateIndex(
                name: "IX_User_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Department_Company_Name",
                table: "Departments",
                columns: new[] { "CompanyId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BannedWord_Word",
                table: "BannedWords",
                column: "Word");
        }
    }
}
