using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meshline.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ResumableAccountEstablishment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "LocalDevice");

            migrationBuilder.AddColumn<string>(
                name: "OperationKey",
                table: "SignedRequests",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CertificateValiditySeconds",
                table: "AccountEstablishment",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "DeviceAuthorizationConfirmed",
                table: "AccountEstablishment",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "InitialProfileJson",
                table: "AccountEstablishment",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsInitialState",
                table: "AccountEstablishment",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OperationId",
                table: "AccountEstablishment",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "PendingReplaceDeviceId",
                table: "AccountEstablishment",
                type: "TEXT",
                maxLength: 26,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousLocalDeviceId",
                table: "AccountEstablishment",
                type: "TEXT",
                maxLength: 26,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplaceDeviceId",
                table: "AccountEstablishment",
                type: "TEXT",
                maxLength: 26,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RouteValiditySeconds",
                table: "AccountEstablishment",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Step",
                table: "AccountEstablishment",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DevicePreparation",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ActivatedOperationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DraftCertificateJson = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedSigningKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ProtectedEncryptionKey = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevicePreparation", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DevicePreparation");

            migrationBuilder.DropColumn(
                name: "OperationKey",
                table: "SignedRequests");

            migrationBuilder.DropColumn(
                name: "CertificateValiditySeconds",
                table: "AccountEstablishment");

            migrationBuilder.DropColumn(
                name: "DeviceAuthorizationConfirmed",
                table: "AccountEstablishment");

            migrationBuilder.DropColumn(
                name: "InitialProfileJson",
                table: "AccountEstablishment");

            migrationBuilder.DropColumn(
                name: "IsInitialState",
                table: "AccountEstablishment");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "AccountEstablishment");

            migrationBuilder.DropColumn(
                name: "PendingReplaceDeviceId",
                table: "AccountEstablishment");

            migrationBuilder.DropColumn(
                name: "PreviousLocalDeviceId",
                table: "AccountEstablishment");

            migrationBuilder.DropColumn(
                name: "ReplaceDeviceId",
                table: "AccountEstablishment");

            migrationBuilder.DropColumn(
                name: "RouteValiditySeconds",
                table: "AccountEstablishment");

            migrationBuilder.DropColumn(
                name: "Step",
                table: "AccountEstablishment");

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "LocalDevice",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }
    }
}
