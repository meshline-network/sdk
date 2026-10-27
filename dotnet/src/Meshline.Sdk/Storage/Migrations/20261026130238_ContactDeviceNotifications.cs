using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meshline.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ContactDeviceNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DeviceNotificationRevision",
                table: "Contacts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Older databases have no fanout checkpoint. Establish the known own
            // state as their baseline without creating a broadcast on upgrade.
            // Existing outbox requests retain responsibility for their delivery.
            migrationBuilder.Sql("""
                UPDATE Contacts SET DeviceNotificationRevision = COALESCE(
                    (SELECT Revision FROM DeviceStates
                     WHERE AccountId = (SELECT AccountId FROM DatabaseBinding WHERE Id = 1)), 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceNotificationRevision",
                table: "Contacts");
        }
    }
}
