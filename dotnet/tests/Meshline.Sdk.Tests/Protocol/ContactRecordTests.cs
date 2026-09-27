using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Tests.Protocol;

public sealed class ContactRecordTests
{
    [Theory]
    [InlineData(-1L, false)]
    [InlineData(0L, true)]
    [InlineData(253402300799L, false)]
    [InlineData(253402300800L, false)]
    [InlineData(9007199254740991L, false)]
    public void Contact_timestamps_fit_the_supported_date_range(long updatedAt, bool valid)
    {
        using var time = Clock.Use(new ManualClock());
        using var account = new AccountSigner();
        var record = Record(account.AccountId) with { UpdatedAt = updatedAt };

        var violation = record.Validate();

        Assert.Equal(valid ? (ProtocolViolationKind?)null : ProtocolViolationKind.Time, violation?.Kind);
        Assert.Equal(violation, new AccountContactSync { Records = [record] }.Validate(TestNetwork.Context));
    }

    [Theory]
    [InlineData(-3600, true)]
    [InlineData(0, true)]
    [InlineData(299, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    [InlineData(3600, false)]
    public void Contact_timestamps_allow_at_most_five_minutes_of_future_clock_skew(int offsetSeconds, bool valid)
    {
        var clock = new ManualClock();
        using var time = Clock.Use(clock);
        using var account = new AccountSigner();
        var record = Record(account.AccountId) with { UpdatedAt = clock.GetUtcNow().ToUnixTimeSeconds() + offsetSeconds };

        var violation = record.Validate();

        Assert.Equal(valid ? (ProtocolViolationKind?)null : ProtocolViolationKind.Time, violation?.Kind);
        Assert.Equal(violation, new AccountContactSync { Records = [record] }.Validate(TestNetwork.Context));
        if (!valid) Assert.Equal("The contact update time is more than five minutes ahead of the local clock.", violation!.Message);
    }

    [Theory]
    [InlineData("valid", null)]
    [InlineData("empty_alias", null)]
    [InlineData("utf8_limit", null)]
    [InlineData("deleted", null)]
    [InlineData("invalid_account", ProtocolViolationKind.Format)]
    [InlineData("unsupported_account", ProtocolViolationKind.Unsupported)]
    [InlineData("negative_time", ProtocolViolationKind.Time)]
    [InlineData("invalid_status", ProtocolViolationKind.Format)]
    [InlineData("blank_alias", ProtocolViolationKind.Format)]
    [InlineData("long_alias", ProtocolViolationKind.Format)]
    [InlineData("deleted_incoming", ProtocolViolationKind.Format)]
    [InlineData("deleted_outgoing", ProtocolViolationKind.Format)]
    public void Standalone_and_nested_record_checks_preserve_existing_constraints(string scenario, ProtocolViolationKind? expectedKind)
    {
        using var account = new AccountSigner();
        var record = Record(account.AccountId);
        var grant = Grant(account.AccountId, account.AccountId);
        record = scenario switch
        {
            "empty_alias" => record with
            {
                Alias = ""
            },
            "utf8_limit" => record with
            {
                Alias = new string('\u00e9', 128)
            },
            "deleted" => record with
            {
                Status = ContactRelationshipState.Deleted
            },
            "invalid_account" => record with
            {
                Account = "neo:860833102:invalid"
            },
            "unsupported_account" => record with
            {
                Account = "unsupported:1:account"
            },
            "negative_time" => record with
            {
                UpdatedAt = -1
            },
            "invalid_status" => record with
            {
                Status = (ContactRelationshipState)99
            },
            "blank_alias" => record with
            {
                Alias = " \t"
            },
            "long_alias" => record with
            {
                Alias = new string('\u00e9', 129)
            },
            "deleted_incoming" => record with
            {
                Status = ContactRelationshipState.Deleted,
                GrantFromContact = grant
            },
            "deleted_outgoing" => record with
            {
                Status = ContactRelationshipState.Deleted,
                GrantToContact = grant
            },
            _ => record
        };
        var violation = record.Validate();

        Assert.Equal(expectedKind, violation?.Kind);
        Assert.Equal(violation, new AccountContactSync { Records = [record] }.Validate(TestNetwork.Context));

        var expectedMessage = scenario switch
        {
            "negative_time" => "The contact update time must be nonnegative.",
            "invalid_status" => "The contact relationship state is invalid.",
            "blank_alias" or "long_alias" => "A nonempty contact alias must contain non-whitespace text and cannot exceed 256 UTF-8 bytes.",
            "deleted_incoming" or "deleted_outgoing" => "A deleted contact record cannot contain grants.",
            "unsupported_account" => "Account namespace 'unsupported' is not supported.",
            _ => null
        };
        if (expectedMessage is not null)
            Assert.Equal(expectedMessage, violation?.Message);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("duplicate")]
    [InlineData("default_array")]
    [InlineData("null_record")]
    [InlineData("incoming_direction")]
    [InlineData("outgoing_direction")]
    [InlineData("different_incoming_owner")]
    [InlineData("different_outgoing_owner")]
    [InlineData("self")]
    [InlineData("invalid_incoming_grant")]
    [InlineData("invalid_outgoing_grant")]
    [InlineData("expired_grant")]
    public void Synchronization_preserves_batch_and_grant_checks(string scenario)
    {
        using var time = Clock.Use(new ManualClock());
        using var owner = new AccountSigner();
        using var firstContact = new AccountSigner();
        using var secondContact = new AccountSigner();
        var first = Record(firstContact.AccountId) with
        {
            GrantFromContact = Grant(firstContact.AccountId, owner.AccountId),
            GrantToContact = Grant(owner.AccountId, firstContact.AccountId)
        };
        var second = Record(secondContact.AccountId) with
        {
            GrantFromContact = Grant(secondContact.AccountId, owner.AccountId)
        };
        var batch = new AccountContactSync
        {
            Records = [first, second]
        };
        batch = scenario switch
        {
            "duplicate" => batch with
            {
                Records = [first, first]
            },
            "default_array" => batch with
            {
                Records = default
            },
            "null_record" => batch with
            {
                Records = [null!]
            },
            "incoming_direction" => batch with
            {
                Records = [first with
                {
                    GrantFromContact = Grant(owner.AccountId, firstContact.AccountId)
                }
                ]
            },
            "outgoing_direction" => batch with
            {
                Records = [first with
                {
                    GrantToContact = Grant(firstContact.AccountId, owner.AccountId)
                }
                ]
            },
            "different_incoming_owner" => batch with
            {
                Records = [first, second with
                {
                    GrantFromContact = Grant(secondContact.AccountId, firstContact.AccountId)
                }
                ]
            },
            "different_outgoing_owner" => batch with
            {
                Records = [first, second with
                {
                    GrantFromContact = null,
                    GrantToContact = Grant(firstContact.AccountId, secondContact.AccountId)
}
                ]
            },
            "self" => batch with
            {
                Records = [first, Record(owner.AccountId)]
            },
            "invalid_incoming_grant" => batch with
            {
                Records = [first with
                {
                    GrantFromContact = first.GrantFromContact!with
                    {
                        Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty
                    }
                }
                ]
            },
            "invalid_outgoing_grant" => batch with
            {
                Records = [first with
                {
                    GrantToContact = first.GrantToContact!with
                    {
                        Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty
                    }
                }
                ]
            },
            "expired_grant" => batch with
            {
                Records = [first with
                {
                    GrantFromContact = first.GrantFromContact!with
                    {
                        ExpiresAt = Clock.UtcNow.ToUnixTimeSeconds()
                    }
                }
                ]
            },
            _ => batch
        };
        // These entries pass their own checks; only the enclosing batch validates grants and relationships.
        if (!batch.Records.IsDefault)
            foreach (var record in batch.Records)
                if (record is not null)
                    Assert.Null(record.Validate());
        var violation = batch.Validate(TestNetwork.Context);
        if (scenario == "valid")
            Assert.Null(violation);
        else
        {
            var expected = scenario switch
            {
                "duplicate" => new ProtocolViolation(ProtocolViolationKind.Conflict, "A contact synchronization batch cannot contain duplicate accounts."),
                "default_array" => new ProtocolViolation(ProtocolViolationKind.Format, "Contact records must be an initialized array when present."),
                "null_record" => new ProtocolViolation(ProtocolViolationKind.Format, "Contact records cannot contain null."),
                "incoming_direction" or "different_incoming_owner" => new ProtocolViolation(ProtocolViolationKind.Identity, "An incoming contact grant must be issued by the contact to the synchronized account."),
                "outgoing_direction" or "different_outgoing_owner" => new ProtocolViolation(ProtocolViolationKind.Identity, "An outgoing contact grant must be issued by the synchronized account to the contact."),
                "self" => new ProtocolViolation(ProtocolViolationKind.Identity, "Contact records cannot include the synchronized account itself."),
                "expired_grant" => new ProtocolViolation(ProtocolViolationKind.Time, "The contact grant has expired."),
                _ => new ProtocolViolation(ProtocolViolationKind.Format, "The contact grant must contain at least one device signature.")
            };

            Assert.Equal(expected, violation);
        }
    }

    static ContactRecord Record(string account) => new()
    {
        Account = account,
        Status = ContactRelationshipState.Active,
        UpdatedAt = 0
    };
    static ContactGrant Grant(string grantor, string grantee) => new()
    {
        Grantor = grantor,
        Grantee = grantee,
        Signatures = ImmutableDictionary<string, ImmutableArray<byte>>.Empty.Add("dev_" + new string('A', 22), [.. new byte[64]])
    };
}
