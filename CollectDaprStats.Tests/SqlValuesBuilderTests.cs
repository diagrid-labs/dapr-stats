using DaprStats;

namespace CollectDaprStats.Tests;

public class SqlValuesBuilderTests
{
    [Fact]
    public void Build_SingleRowNoCasts_NumbersFromOne()
    {
        Assert.Equal("($1,$2,$3)", SqlValuesBuilder.Build(1, [null, null, null]));
    }

    [Fact]
    public void Build_MultipleRows_ContinuesNumberingAcrossRows()
    {
        Assert.Equal("($1,$2),($3,$4),($5,$6)", SqlValuesBuilder.Build(3, [null, null]));
    }

    [Fact]
    public void Build_LeadingDateCast_AppliesToEveryRow()
    {
        Assert.Equal(
            "($1::date,$2,$3),($4::date,$5,$6)",
            SqlValuesBuilder.Build(2, ["date", null, null]));
    }

    [Fact]
    public void Build_SingleCastColumn_StillCommaSeparatesRows()
    {
        Assert.Equal("($1::date),($2::date)", SqlValuesBuilder.Build(2, ["date"]));
    }

    [Fact]
    public void Build_ChunkOfFiveHundredBySeven_EndsAtParameterThreeThousandFiveHundred()
    {
        // The building-block insert's real chunk shape. 3,500 parameters is far
        // inside Postgres' 65,535 limit.
        var clause = SqlValuesBuilder.Build(500, ["date", null, null, null, null, null, null]);

        Assert.StartsWith("($1::date,$2,$3,$4,$5,$6,$7),", clause);
        Assert.EndsWith(",($3494::date,$3495,$3496,$3497,$3498,$3499,$3500)", clause);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Build_NonPositiveRowCount_Throws(int rowCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SqlValuesBuilder.Build(rowCount, [null]));
    }

    [Fact]
    public void Build_NoColumns_Throws()
    {
        Assert.Throws<ArgumentException>(() => SqlValuesBuilder.Build(1, []));
    }
}
