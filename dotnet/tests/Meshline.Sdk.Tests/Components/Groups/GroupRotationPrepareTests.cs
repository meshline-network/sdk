using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Tests.Components.Groups;

public sealed class GroupRotationPrepareTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("expired")]
    [InlineData("count")]
    [InlineData("changed_interval")]
    public async Task Invalid_preparation_never_commits_the_rotation(string scenario)
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
            MemberCapacity = 100,
            InvitePolicy = GroupInvitePolicy.Administrators
        }, Token);
        if (scenario == "changed_interval")
        {
            // Seed enough retained members to exercise two preparation batches.
            await using var database = fixture.Database.Open();
            var owner = await database.GroupMembers.SingleAsync(Token);
            for (var i = 0; i < 64; i++)
            {
                using var account = new AccountSigner();
                database.GroupMembers.Add(new()
                {
                    GroupId = group.Ref.GroupId,
                    AccountId = account.AccountId,
                    Role = GroupRole.Member,
                    PublicKey = owner.PublicKey.ToArray()
                });
            }

            await database.SaveChangesAsync(Token);
        }

        var expiresAt = Clock.UtcNow.ToUnixTimeSeconds() + 600;
        var batches = new List<int>();
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method != "group.secret.rotation.prepare")
                return Task.FromResult(relay.Respond(request));
            var preparation = ProtocolModel.FromJson<GroupRotationPrepareRequest>(request.Body!)!;
            batches.Add(preparation.ClientSecretBoxes.Count);
            var result = new GroupRotationPrepareResult
            {
                Prepared = scenario == "count" ? 0 : batches.Sum(),
                ExpiresAt = scenario == "expired" ? Clock.UtcNow.ToUnixTimeSeconds() : expiresAt + batches.Count - 1
            };
            if (scenario != "expired")
                Assert.Null(result.Validate(TestNetwork.Context));
            return Task.FromResult(OfflineRelay.Json(result));
        };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => manager.RotateSecretAsync(group.Ref, cancellationToken: Token));

        Assert.Equal("The relay returned an invalid or changed rotation preparation interval.", error.Message);
        Assert.Equal(scenario == "changed_interval" ? new[] { 64, 1 } : new[] { 1 }, batches);
        Assert.DoesNotContain(fixture.Relay.Requests, request => request.Method == "group.secret.rotation.commit");

        await using var verify = fixture.Database.Open();
        var rotation = Assert.Single(await verify.GroupRotations.ToListAsync(Token));
        if (scenario == "changed_interval")
            Assert.Equal(expiresAt, rotation.ExpiresAt);
        else
            Assert.Null(rotation.ExpiresAt);

        Assert.Empty(await verify.GroupOperations.ToListAsync(Token));
    }
}
