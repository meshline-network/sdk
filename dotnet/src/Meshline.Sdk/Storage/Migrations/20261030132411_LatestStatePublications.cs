using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meshline.Storage.Migrations
{
    /// <inheritdoc />
    public partial class LatestStatePublications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DocumentJson",
                table: "SignedRequests",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<long>(
                name: "Generation",
                table: "SignedRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "IntentJson",
                table: "SignedRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextAttemptAt",
                table: "SignedRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestState",
                table: "SignedRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "RequestJson",
                table: "GroupOperations",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<long>(
                name: "Generation",
                table: "GroupOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "IntentJson",
                table: "GroupOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextAttemptAt",
                table: "GroupOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Pending",
                table: "GroupOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RequestState",
                table: "GroupOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "DocumentJson",
                table: "ChannelOperations",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<long>(
                name: "Generation",
                table: "ChannelOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "IntentJson",
                table: "ChannelOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextAttemptAt",
                table: "ChannelOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Pending",
                table: "ChannelOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RequestState",
                table: "ChannelOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Generation",
                table: "SignedRequests");

            migrationBuilder.DropColumn(
                name: "IntentJson",
                table: "SignedRequests");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "SignedRequests");

            migrationBuilder.DropColumn(
                name: "RequestState",
                table: "SignedRequests");

            migrationBuilder.DropColumn(
                name: "Generation",
                table: "GroupOperations");

            migrationBuilder.DropColumn(
                name: "IntentJson",
                table: "GroupOperations");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "GroupOperations");

            migrationBuilder.DropColumn(
                name: "Pending",
                table: "GroupOperations");

            migrationBuilder.DropColumn(
                name: "RequestState",
                table: "GroupOperations");

            migrationBuilder.DropColumn(
                name: "Generation",
                table: "ChannelOperations");

            migrationBuilder.DropColumn(
                name: "IntentJson",
                table: "ChannelOperations");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "ChannelOperations");

            migrationBuilder.DropColumn(
                name: "Pending",
                table: "ChannelOperations");

            migrationBuilder.DropColumn(
                name: "RequestState",
                table: "ChannelOperations");

            migrationBuilder.AlterColumn<string>(
                name: "DocumentJson",
                table: "SignedRequests",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "RequestJson",
                table: "GroupOperations",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "DocumentJson",
                table: "ChannelOperations",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
