using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Storage;

sealed class MeshlineDbContext : DbContext
{
    public DbSet<DatabaseBinding> Bindings { get; set; }
    public DbSet<LocalDeviceRecord> LocalDevices { get; set; }
    public DbSet<DeviceStateRecord> DeviceStates { get; set; }
    public DbSet<AccountRouteRecord> AccountRoutes { get; set; }
    public DbSet<AccountProfileRecord> AccountProfiles { get; set; }
    public DbSet<SignedRequestRecord> SignedRequests { get; set; }
    public DbSet<HomeRelayMigrationRecord> HomeRelayMigrations { get; set; }
    public DbSet<AccountEstablishmentRecord> AccountEstablishments { get; set; }
    public DbSet<ChannelRecord> Channels { get; set; }
    public DbSet<ChannelDescriptorRecord> ChannelDescriptors { get; set; }
    public DbSet<ChannelPostRecord> ChannelPosts { get; set; }
    public DbSet<ChannelOperationRecord> ChannelOperations { get; set; }
    public DbSet<StoredMessageRecord> Messages { get; set; }
    public DbSet<MessageOutboxRecord> MessageOutbox { get; set; }
    public DbSet<AccountTimelineRecord> AccountTimelines { get; set; }
    public DbSet<ConversationReadRecord> ConversationReads { get; set; }
    public DbSet<ContactStateRecord> Contacts { get; set; }
    public DbSet<ContactRequestRecord> ContactRequests { get; set; }
    public DbSet<GroupAccountMessageCursorRecord> GroupAccountMessageCursors { get; set; }
    public DbSet<GroupRecord> Groups { get; set; }
    public DbSet<GroupMemberRecord> GroupMembers { get; set; }
    public DbSet<GroupBanRecord> GroupBans { get; set; }
    public DbSet<GroupEpochRecord> GroupEpochs { get; set; }
    public DbSet<GroupMemberKeyRecord> GroupMemberKeys { get; set; }
    public DbSet<GroupEventRecord> GroupEvents { get; set; }
    public DbSet<GroupOperationRecord> GroupOperations { get; set; }
    public DbSet<GroupRotationRecord> GroupRotations { get; set; }

    public MeshlineDbContext(DatabaseOptions options, SqliteOpenMode mode = SqliteOpenMode.ReadWrite) : this(CreateOptions(options, mode)) { }

    public MeshlineDbContext(DbContextOptions<MeshlineDbContext> options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTimestampConverter>();

    static DbContextOptions<MeshlineDbContext> CreateOptions(DatabaseOptions options, SqliteOpenMode mode)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = options.Path, Mode = mode }.ToString();
        return new DbContextOptionsBuilder<MeshlineDbContext>().UseSqlite(connectionString).Options;
    }
}
