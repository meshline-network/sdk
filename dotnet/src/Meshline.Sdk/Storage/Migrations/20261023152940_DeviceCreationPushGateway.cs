using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meshline.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DeviceCreationPushGateway : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InitialPushGateway",
                table: "AccountEstablishment",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitialPushGateway",
                table: "AccountEstablishment");
        }
    }
}
