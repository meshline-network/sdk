using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meshline.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountEstablishment",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    RelayId = table.Column<string>(type: "TEXT", maxLength: 42, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountEstablishment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountProfiles",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DocumentJson = table.Column<string>(type: "TEXT", nullable: false),
                    SignerCertificateJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountProfiles", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "AccountRoutes",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DocumentJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountRoutes", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "AccountTimelines",
                columns: table => new
                {
                    RelayId = table.Column<string>(type: "TEXT", maxLength: 42, nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    HasRetentionGap = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSynchronizedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountTimelines", x => x.RelayId);
                });

            migrationBuilder.CreateTable(
                name: "ChannelDescriptors",
                columns: table => new
                {
                    ChannelId = table.Column<string>(type: "TEXT", maxLength: 27, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    DocumentJson = table.Column<string>(type: "TEXT", nullable: false),
                    CertificateJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelDescriptors", x => new { x.ChannelId, x.Revision });
                });

            migrationBuilder.CreateTable(
                name: "ChannelOperations",
                columns: table => new
                {
                    RelayId = table.Column<string>(type: "TEXT", maxLength: 42, nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DocumentJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelOperations", x => new { x.RelayId, x.ResourceId, x.Method });
                });

            migrationBuilder.CreateTable(
                name: "ChannelPosts",
                columns: table => new
                {
                    ChannelId = table.Column<string>(type: "TEXT", maxLength: 27, nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 26, nullable: true),
                    Author = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AcceptedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    PostJson = table.Column<string>(type: "TEXT", nullable: true),
                    AppliedThrough = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelPosts", x => new { x.ChannelId, x.Sequence });
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    ChannelId = table.Column<string>(type: "TEXT", maxLength: 27, nullable: false),
                    RelayId = table.Column<string>(type: "TEXT", maxLength: 42, nullable: false),
                    DescriptorJson = table.Column<string>(type: "TEXT", nullable: true),
                    IsFollowed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    SyncSequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.ChannelId);
                });

            migrationBuilder.CreateTable(
                name: "ContactRequests",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 26, nullable: true),
                    ConsentJson = table.Column<string>(type: "TEXT", nullable: false),
                    SendState = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactRequests", x => new { x.AccountId, x.Direction });
                });

            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Alias = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    GrantFromJson = table.Column<string>(type: "TEXT", nullable: true),
                    GrantToJson = table.Column<string>(type: "TEXT", nullable: true),
                    ConfirmedGrantToJson = table.Column<string>(type: "TEXT", nullable: true),
                    RequiredDeviceRevision = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "ConversationReads",
                columns: table => new
                {
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationReads", x => x.ConversationId);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseBinding",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Context = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 26, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseBinding", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceStates",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DocumentJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceStates", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "GroupAccountMessageCursor",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalSequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupAccountMessageCursor", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupBans",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupBans", x => new { x.GroupId, x.AccountId });
                });

            migrationBuilder.CreateTable(
                name: "GroupEpochs",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    Epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    Commitment = table.Column<string>(type: "TEXT", nullable: true),
                    MemberPublicKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    KeyEntryJson = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedApplicationSecret = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ProtectedClientSecret = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupEpochs", x => new { x.GroupId, x.Epoch });
                });

            migrationBuilder.CreateTable(
                name: "GroupEvents",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CertificateJson = table.Column<string>(type: "TEXT", nullable: true),
                    Epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", nullable: true),
                    Sender = table.Column<string>(type: "TEXT", nullable: true),
                    SenderDeviceId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DecryptedPayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    Rejection = table.Column<string>(type: "TEXT", nullable: true),
                    IsMessage = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupEvents", x => new { x.GroupId, x.Sequence });
                });

            migrationBuilder.CreateTable(
                name: "GroupMemberKeys",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedPrivateKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Shared = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMemberKeys", x => new { x.GroupId, x.PublicKey });
                });

            migrationBuilder.CreateTable(
                name: "GroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Nickname = table.Column<string>(type: "TEXT", nullable: true),
                    NicknameSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    JoinedAtSequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => new { x.GroupId, x.AccountId });
                });

            migrationBuilder.CreateTable(
                name: "GroupOperations",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupOperations", x => new { x.GroupId, x.Method });
                });

            migrationBuilder.CreateTable(
                name: "GroupRotations",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    BaseCommitment = table.Column<string>(type: "TEXT", nullable: false),
                    Commitment = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedSecret = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OwnerPublicKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupRotations", x => x.GroupId);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false),
                    RelayId = table.Column<string>(type: "TEXT", maxLength: 42, nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Owner = table.Column<string>(type: "TEXT", nullable: true),
                    MemberCapacity = table.Column<long>(type: "INTEGER", nullable: false),
                    MemberCount = table.Column<long>(type: "INTEGER", nullable: false),
                    InvitePolicy = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Membership = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: true),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    ManagementHash = table.Column<string>(type: "TEXT", nullable: true),
                    Commitment = table.Column<string>(type: "TEXT", nullable: true),
                    LocallyClosed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.GroupId);
                });

            migrationBuilder.CreateTable(
                name: "HomeRelayMigration",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceRelayId = table.Column<string>(type: "TEXT", maxLength: 42, nullable: false),
                    TargetRelayId = table.Column<string>(type: "TEXT", maxLength: 42, nullable: false),
                    DeviceStateJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileJson = table.Column<string>(type: "TEXT", nullable: true),
                    RouteValiditySeconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HomeRelayMigration", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalDevice",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false),
                    CertificateJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedSigningKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ProtectedEncryptionKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalDevice", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "MessageOutbox",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false),
                    Recipient = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDirect = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    RelayId = table.Column<string>(type: "TEXT", maxLength: 42, nullable: false),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: false),
                    AcceptedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageOutbox", x => x.MessageId);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    LocalSequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Sender = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false),
                    SenderDeviceId = table.Column<string>(type: "TEXT", maxLength: 26, nullable: false),
                    Recipient = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadType = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedPayload = table.Column<byte[]>(type: "BLOB", nullable: true),
                    IsDirect = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.LocalSequence);
                });

            migrationBuilder.CreateTable(
                name: "SignedRequests",
                columns: table => new
                {
                    Method = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RelayId = table.Column<string>(type: "TEXT", maxLength: 42, nullable: false),
                    DocumentJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Pending = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignedRequests", x => x.Method);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelPosts_ChannelId_MessageId_Author",
                table: "ChannelPosts",
                columns: new[] { "ChannelId", "MessageId", "Author" });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_IsFollowed_RelayId",
                table: "Channels",
                columns: new[] { "IsFollowed", "RelayId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_State_UpdatedAt",
                table: "Contacts",
                columns: new[] { "State", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupEvents_GroupId_IsMessage_Sequence",
                table: "GroupEvents",
                columns: new[] { "GroupId", "IsMessage", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageOutbox_State_NextAttemptAt",
                table: "MessageOutbox",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_IsDirect_CreatedAt",
                table: "Messages",
                columns: new[] { "IsDirect", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Recipient_CreatedAt",
                table: "Messages",
                columns: new[] { "Recipient", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Sender_MessageId",
                table: "Messages",
                columns: new[] { "Sender", "MessageId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountEstablishment");

            migrationBuilder.DropTable(
                name: "AccountProfiles");

            migrationBuilder.DropTable(
                name: "AccountRoutes");

            migrationBuilder.DropTable(
                name: "AccountTimelines");

            migrationBuilder.DropTable(
                name: "ChannelDescriptors");

            migrationBuilder.DropTable(
                name: "ChannelOperations");

            migrationBuilder.DropTable(
                name: "ChannelPosts");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "ContactRequests");

            migrationBuilder.DropTable(
                name: "Contacts");

            migrationBuilder.DropTable(
                name: "ConversationReads");

            migrationBuilder.DropTable(
                name: "DatabaseBinding");

            migrationBuilder.DropTable(
                name: "DeviceStates");

            migrationBuilder.DropTable(
                name: "GroupAccountMessageCursor");

            migrationBuilder.DropTable(
                name: "GroupBans");

            migrationBuilder.DropTable(
                name: "GroupEpochs");

            migrationBuilder.DropTable(
                name: "GroupEvents");

            migrationBuilder.DropTable(
                name: "GroupMemberKeys");

            migrationBuilder.DropTable(
                name: "GroupMembers");

            migrationBuilder.DropTable(
                name: "GroupOperations");

            migrationBuilder.DropTable(
                name: "GroupRotations");

            migrationBuilder.DropTable(
                name: "Groups");

            migrationBuilder.DropTable(
                name: "HomeRelayMigration");

            migrationBuilder.DropTable(
                name: "LocalDevice");

            migrationBuilder.DropTable(
                name: "MessageOutbox");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "SignedRequests");
        }
    }
}
