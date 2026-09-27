using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Groups;

public sealed class GroupRecoverySubmitTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_a_valid_acceptance_interval_completes_the_pending_operation(bool valid)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new GroupRelay(fixture);
        relay.Install();
        var manager = fixture.Client.GroupManager;
        var group = await manager.CreateGroupAsync(fixture.Relay.RelayId, new()
        {
            Name = "offline",
            MemberCapacity = 10,
            InvitePolicy = GroupInvitePolicy.Administrators
        }, Token);
        var result = new GroupRecoverySubmitResult
        {
            AcceptedAt = 0,
            ExpiresAt = valid ? 1 : 0
        };
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "group.member.recovery.submit" ? OfflineRelay.Json(result) : relay.Respond(request));
        if (valid)
        {
            var actual = await manager.RequestKeyRecoveryAsync(group.Ref, Token);

            Assert.Equal(DateTimeOffset.UnixEpoch, actual.AcceptedAt);
            Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), actual.ExpiresAt);

            await using var database = fixture.Database.Open();

            Assert.Empty(await database.GroupOperations.ToListAsync(Token));
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => manager.RequestKeyRecoveryAsync(group.Ref, Token));

            Assert.Equal("The relay returned an invalid recovery acceptance interval.", error.Message);

            await using var database = fixture.Database.Open();

            Assert.Equal("group.member.recovery.submit", Assert.Single(await database.GroupOperations.ToListAsync(Token)).Method);
        }
    }
}
