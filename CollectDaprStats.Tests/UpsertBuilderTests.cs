using DaprStats;

namespace CollectDaprStats.Tests;

public class UpsertBuilderTests
{
    [Fact]
    public void BuildOnConflict_MultiColumnKey_ListsKeysThenAssignments()
    {
        Assert.Equal(
            "on conflict (collection_week,namespace,image_name) do update set " +
            "collection_date=excluded.collection_date,pull_count=excluded.pull_count",
            UpsertBuilder.BuildOnConflict(
                ["collection_week", "namespace", "image_name"],
                ["collection_date", "pull_count"]));
    }

    [Fact]
    public void BuildOnConflict_SingleColumnKey_OmitsCommaInKeyList()
    {
        // discord_dapr and diagrid_dashboard have no entity dimension: one row
        // per week, so collection_week is the whole key.
        Assert.Equal(
            "on conflict (collection_week) do update set " +
            "collection_date=excluded.collection_date,member_count=excluded.member_count",
            UpsertBuilder.BuildOnConflict(
                ["collection_week"],
                ["collection_date", "member_count"]));
    }

    [Fact]
    public void BuildOnConflict_PreservesGivenColumnOrder()
    {
        // Order is preserved rather than sorted, so the golden-string tests in
        // CollectorInsertSqlTests are stable against this builder.
        Assert.Equal(
            "on conflict (collection_week,b,a) do update set z=excluded.z,y=excluded.y",
            UpsertBuilder.BuildOnConflict(["collection_week", "b", "a"], ["z", "y"]));
    }

    [Fact]
    public void BuildOnConflict_EmptyKeyColumns_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            UpsertBuilder.BuildOnConflict([], ["collection_date"]));

        Assert.Equal("keyColumns", ex.ParamName);
    }

    [Fact]
    public void BuildOnConflict_EmptyUpdateColumns_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            UpsertBuilder.BuildOnConflict(["collection_week"], []));

        Assert.Equal("updateColumns", ex.ParamName);
    }

    [Fact]
    public void BuildOnConflict_GeneratedColumnInUpdates_Throws()
    {
        // collection_week is GENERATED ALWAYS. Postgres would reject the
        // assignment at runtime; failing here is cheaper. Checked before the
        // key-overlap rule so the message names the real problem even when
        // collection_week is also in the key, which it normally is.
        var ex = Assert.Throws<ArgumentException>(() =>
            UpsertBuilder.BuildOnConflict(
                ["collection_week", "namespace"],
                ["collection_week", "pull_count"]));

        Assert.Equal("updateColumns", ex.ParamName);
        Assert.Contains("generated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildOnConflict_KeyColumnAlsoInUpdates_Throws()
    {
        // Updating a conflict-key column would change the very value that
        // matched, so it is always a mistake.
        var ex = Assert.Throws<ArgumentException>(() =>
            UpsertBuilder.BuildOnConflict(
                ["collection_week", "repo_name"],
                ["repo_name", "commit_count"]));

        Assert.Equal("updateColumns", ex.ParamName);
        Assert.Contains("repo_name", ex.Message);
    }
}
