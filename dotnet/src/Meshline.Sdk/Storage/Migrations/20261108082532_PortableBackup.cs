using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meshline.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PortableBackup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayloadJson",
                table: "MessageOutbox",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PayloadJson",
                table: "MessageOutbox");
        }
    }
}
