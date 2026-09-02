using System.Text;
using DaprStats;

namespace CollectDaprStats.Tests;

public class DataDogRumIdentifiedUsersParseUsersTests
{
    private static IReadOnlyList<DataDogRumIdentifiedUsers.Observation> Parse(string json) =>
        DataDogRumIdentifiedUsers.ParseUsers(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ParseUsers_ReturnsEveryBucket()
    {
        // Shape confirmed against the live API in Task 1: one bucket per
        // (id, email, name) combination, grouped by three facets.
        var users = Parse("""
            {
              "meta": { "status": "done" },
              "data": {
                "buckets": [
                  { "by": { "@usr.id": "u1", "@usr.email": "a@example.com", "@usr.name": "Ada",
                            "@usr.organization": "org-1" },
                    "computes": { "c0": 7 } },
                  { "by": { "@usr.id": "u2", "@usr.email": "b@example.com", "@usr.name": "Bo",
                            "@usr.organization": "org-2" },
                    "computes": { "c0": 2 } }
                ]
              }
            }
            """);

        Assert.Equal(
            new[]
            {
                new DataDogRumIdentifiedUsers.Observation("u1", "a@example.com", "Ada", "org-1"),
                new DataDogRumIdentifiedUsers.Observation("u2", "b@example.com", "Bo", "org-2"),
            },
            users);
    }

    [Fact]
    public void ParseUsers_BucketMissingOrganizationKey_YieldsNullOrganization()
    {
        var users = Parse("""
            {
              "data": {
                "buckets": [
                  { "by": { "@usr.id": "u1", "@usr.email": "a@example.com" },
                    "computes": { "c0": 1 } }
                ]
              }
            }
            """);

        var user = Assert.Single(users);
        Assert.Equal("a@example.com", user.Email);
        Assert.Null(user.Organization);
    }

    [Fact]
    public void ParseUsers_BucketMissingEmailKey_YieldsNullEmail()
    {
        // One production id has no email. It must still be registered.
        var users = Parse("""
            {
              "data": {
                "buckets": [
                  { "by": { "@usr.id": "u1", "@usr.name": "Ada" }, "computes": { "c0": 1 } }
                ]
              }
            }
            """);

        var user = Assert.Single(users);
        Assert.Equal("u1", user.UserId);
        Assert.Null(user.Email);
        Assert.Equal("Ada", user.Name);
    }

    [Fact]
    public void ParseUsers_BucketMissingNameKey_YieldsNullName()
    {
        var users = Parse("""
            {
              "data": {
                "buckets": [
                  { "by": { "@usr.id": "u1", "@usr.email": "a@example.com" },
                    "computes": { "c0": 1 } }
                ]
              }
            }
            """);

        var user = Assert.Single(users);
        Assert.Equal("a@example.com", user.Email);
        Assert.Null(user.Name);
    }

    [Fact]
    public void ParseUsers_BucketWithoutUserId_IsSkippedNotThrown()
    {
        // There is nothing to register without an id, and the query filters on
        // @usr.id:* anyway, so this is defensive.
        var users = Parse("""
            {
              "data": {
                "buckets": [
                  { "by": {}, "computes": { "c0": 1 } },
                  { "by": { "@usr.id": "u1" }, "computes": { "c0": 1 } }
                ]
              }
            }
            """);

        Assert.Equal("u1", Assert.Single(users).UserId);
    }

    [Fact]
    public void ParseUsers_EmptyStringValues_AreTreatedAsMissing()
    {
        var users = Parse("""
            {
              "data": {
                "buckets": [
                  { "by": { "@usr.id": "", "@usr.email": "a@example.com" },
                    "computes": { "c0": 1 } },
                  { "by": { "@usr.id": "u1", "@usr.email": "", "@usr.name": "" },
                    "computes": { "c0": 1 } }
                ]
              }
            }
            """);

        var user = Assert.Single(users);
        Assert.Equal("u1", user.UserId);
        Assert.Null(user.Email);
        Assert.Null(user.Name);
    }

    [Fact]
    public void ParseUsers_NonStringAttribute_IsTreatedAsMissing()
    {
        var users = Parse("""
            {
              "data": {
                "buckets": [
                  { "by": { "@usr.id": "u1", "@usr.email": null }, "computes": { "c0": 1 } }
                ]
              }
            }
            """);

        Assert.Null(Assert.Single(users).Email);
    }

    [Fact]
    public void ParseUsers_NoBuckets_ReturnsEmpty()
    {
        Assert.Empty(Parse("""{ "data": { "buckets": [] } }"""));
    }

    [Fact]
    public void ParseUsers_EmptyBody_Throws()
    {
        Assert.Throws<FormatException>(
            () => DataDogRumIdentifiedUsers.ParseUsers(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ParseUsers_NoDataProperty_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("""{ "meta": {} }"""));
    }

    [Fact]
    public void ParseUsers_InvalidJson_Throws()
    {
        Assert.Throws<FormatException>(() => Parse("not json"));
    }
}

public class DataDogRumIdentifiedUsersDeriveEntriesTests
{
    private static readonly DateOnly Week1 = new(2026, 8, 10);
    private static readonly DateOnly Week2 = new(2026, 8, 17);
    private static readonly DateOnly Week3 = new(2026, 8, 24);

    private static (DateOnly, IReadOnlyList<DataDogRumIdentifiedUsers.Observation>) Week(
        DateOnly weekStart,
        params DataDogRumIdentifiedUsers.Observation[] users) => (weekStart, users);

    private static DataDogRumIdentifiedUsers.Observation User(
        string id, string? email = null, string? name = null, string? org = null) =>
        new(id, email, name, org);

    [Fact]
    public void DeriveEntries_EarliestWeekWins()
    {
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2, User("u1")),
            Week(Week3, User("u1")),
        ]);

        var u1 = entries.Single(e => e.UserId == "u1");
        Assert.Equal(Week2, u1.InstallDate);
    }

    [Fact]
    public void DeriveEntries_IdOnlyInNewestWeek_GetsThatWeek()
    {
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2),
            Week(Week3, User("u1")),
        ]);

        Assert.Equal(Week3, entries.Single(e => e.UserId == "u1").InstallDate);
    }

    [Fact]
    public void DeriveEntries_IdInOldestWeekWithData_GetsNullInstallDate()
    {
        // That week is the edge of what was queried, so the id was probably
        // active before it and its true first week is unknowable.
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("u1")),
            Week(Week2, User("u2")),
        ]);

        Assert.Null(entries.Single(e => e.UserId == "u1").InstallDate);
        Assert.Equal(Week2, entries.Single(e => e.UserId == "u2").InstallDate);
    }

    [Fact]
    public void DeriveEntries_LeadingEmptyWeeksAreNotTheBoundary()
    {
        // The boundary is the oldest week that actually produced users. A week
        // that returned nothing (or failed) must not become it.
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1),
            Week(Week2, User("u1")),
            Week(Week3, User("u2")),
        ]);

        Assert.Null(entries.Single(e => e.UserId == "u1").InstallDate);
        Assert.Equal(Week3, entries.Single(e => e.UserId == "u2").InstallDate);
    }

    [Fact]
    public void DeriveEntries_NewestNonNullEmailWins()
    {
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2, User("u1", "old@example.com")),
            Week(Week3, User("u1", "new@example.com")),
        ]);

        Assert.Equal("new@example.com", entries.Single(e => e.UserId == "u1").Email);
    }

    [Fact]
    public void DeriveEntries_NullEmailInNewestWeek_DoesNotEraseKnownEmail()
    {
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2, User("u1", "a@example.com")),
            Week(Week3, User("u1")),
        ]);

        Assert.Equal("a@example.com", entries.Single(e => e.UserId == "u1").Email);
    }

    [Fact]
    public void DeriveEntries_NewestNonNullOrganizationWins()
    {
        // A user moving between organisations is the reason this follows the
        // same newest-non-null rule as email rather than being fixed at first
        // sight.
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2, User("u1", org: "org-old")),
            Week(Week3, User("u1", org: "org-new")),
        ]);

        Assert.Equal("org-new", entries.Single(e => e.UserId == "u1").Organization);
    }

    [Fact]
    public void DeriveEntries_NullOrganizationInNewestWeek_DoesNotEraseKnownOrganization()
    {
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2, User("u1", org: "org-1")),
            Week(Week3, User("u1")),
        ]);

        Assert.Equal("org-1", entries.Single(e => e.UserId == "u1").Organization);
    }

    [Fact]
    public void DeriveEntries_NameFollowsTheSameRule()
    {
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2, User("u1", name: "Ada")),
            Week(Week3, User("u1", name: null)),
        ]);

        Assert.Equal("Ada", entries.Single(e => e.UserId == "u1").Name);
    }

    [Fact]
    public void DeriveEntries_RepeatedIdInOneWeek_YieldsOneEntry()
    {
        // Multi-facet grouping can put the same id in two buckets if an
        // attribute differs between sessions.
        var entries = DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1, User("boundary")),
            Week(Week2, User("u1", "a@example.com"), User("u1", name: "Ada")),
        ]);

        var u1 = Assert.Single(entries.Where(e => e.UserId == "u1"));
        Assert.Equal("a@example.com", u1.Email);
        Assert.Equal("Ada", u1.Name);
    }

    [Fact]
    public void DeriveEntries_NoWeeks_ReturnsEmpty()
    {
        Assert.Empty(DataDogRumIdentifiedUsers.DeriveEntries([]));
    }

    [Fact]
    public void DeriveEntries_AllWeeksEmpty_ReturnsEmpty()
    {
        Assert.Empty(DataDogRumIdentifiedUsers.DeriveEntries(
        [
            Week(Week1),
            Week(Week2),
        ]));
    }
}

public class DataDogRumIdentifiedUsersSearchQueryTests
{
    // Invented service and env -- not the real production values -- so this
    // test does not need updating when a host changes.
    private const string Service = "example-service";
    private const string Env = "example-env.example.com";

    [Fact]
    public void SearchQuery_ReturnsExpectedString()
    {
        // Exact-string assertion: GetDataDogRumIdentifiedData and
        // GetDataDogRumIdentifiedUsers both call this method so the aggregate
        // and the registry always describe the same population. A future
        // edit that changes the shared query for one of them changes it for
        // both, and this test pins the current, known-correct shape.
        var query = DataDogRumIdentifiedUsers.SearchQuery(Service, Env);

        Assert.Equal(
            $"@type:session @session.type:user service:{Service} env:{Env} @usr.id:*",
            query);
    }

    [Fact]
    public void SearchQuery_ContainsAllRequiredTerms()
    {
        var query = DataDogRumIdentifiedUsers.SearchQuery(Service, Env);

        Assert.Contains("@type:session", query);
        Assert.Contains("@session.type:user", query);
        Assert.Contains("service:", query);
        Assert.Contains("env:", query);
        Assert.Contains("@usr.id:*", query);
    }
}
