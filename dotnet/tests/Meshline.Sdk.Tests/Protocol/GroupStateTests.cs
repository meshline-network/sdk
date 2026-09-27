using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Validation;

namespace Meshline.Tests.Protocol;

public sealed class GroupStateTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("empty_description", true)]
    [InlineData("utf8_limits", true)]
    [InlineData("closed", true)]
    [InlineData("zero_count", false)]
    [InlineData("negative_count", false)]
    [InlineData("zero_capacity", false)]
    [InlineData("negative_capacity", false)]
    [InlineData("unknown_status", false)]
    [InlineData("unknown_policy", false)]
    [InlineData("empty_name", false)]
    [InlineData("blank_name", false)]
    [InlineData("long_name", false)]
    [InlineData("blank_description", false)]
    [InlineData("long_description", false)]
    [InlineData("invalid_owner", false)]
    public void Standalone_group_state_checks_existing_preview_constraints(string scenario, bool valid)
    {
        using var account = new AccountSigner();
        var state = CreateState(account.AccountId);
        state = scenario switch
        {
            "empty_description" => state with
            {
                Description = ""
            },
            "utf8_limits" => state with
            {
                Name = new string('\u00e9', 128),
                Description = new string('\u00e9', 2048)
            },
            "closed" => state with
            {
                Status = GroupStatus.Closed
            },
            "zero_count" => state with
            {
                MemberCount = 0
            },
            "negative_count" => state with
            {
                MemberCount = -1
            },
            "zero_capacity" => state with
            {
                MemberCapacity = 0
            },
            "negative_capacity" => state with
            {
                MemberCapacity = -1
            },
            "unknown_status" => state with
            {
                Status = (GroupStatus)99
            },
            "unknown_policy" => state with
            {
                InvitePolicy = (GroupInvitePolicy)99
            },
            "empty_name" => state with
            {
                Name = ""
            },
            "blank_name" => state with
            {
                Name = " \t"
            },
            "long_name" => state with
            {
                Name = new string('\u00e9', 129)
            },
            "blank_description" => state with
            {
                Description = " \t"
            },
            "long_description" => state with
            {
                Description = new string('\u00e9', 2049)
            },
            "invalid_owner" => state with
            {
                Owner = "neo:860833102:invalid"
            },
            "valid" => state,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var violation = state.Validate();
        if (valid)
            Assert.Null(violation);
        else
            Assert.Equal(new ProtocolViolation(ProtocolViolationKind.Format, "The relay returned an invalid group preview."), violation);
    }

    [Fact]
    public void Unsupported_owner_namespace_preserves_adapter_exception()
    {
        var state = CreateState("unsupported:1:owner");

        Assert.Throws<NotSupportedException>(() => state.Validate());
    }

    static GroupState CreateState(string owner) => new()
    {
        GroupId = "grp_" + new string('A', 43),
        Name = "preview",
        Owner = owner,
        MemberCount = 1,
        MemberCapacity = 10,
        Status = GroupStatus.Active,
        InvitePolicy = GroupInvitePolicy.Administrators
    };
}
