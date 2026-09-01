using System.Text;

namespace DaprStats
{
    /// <summary>
    /// Builds the `($1::date,$2),($3::date,$4)` clause of a multi-row INSERT.
    /// </summary>
    /// <remarks>
    /// The Dapr Postgres binding takes one statement and a flat parameter array
    /// per invocation, so writing hundreds of rows one statement at a time
    /// costs hundreds of round trips. Chunked inserts avoid that, at the price
    /// of generating the placeholder numbering — which is why this is a
    /// separate, tested unit rather than string concatenation at the call site.
    /// </remarks>
    public static class SqlValuesBuilder
    {
        /// <param name="rowCount">Rows in this statement. Must be at least one.</param>
        /// <param name="columnCasts">
        /// One entry per column, in column order. A non-null entry appends
        /// `::<cast>` to that column's placeholder; null leaves it bare.
        /// </param>
        public static string Build(int rowCount, IReadOnlyList<string?> columnCasts)
        {
            if (rowCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rowCount), rowCount, "At least one row is required.");
            }

            if (columnCasts.Count == 0)
            {
                throw new ArgumentException("At least one column is required.", nameof(columnCasts));
            }

            var builder = new StringBuilder();
            var parameter = 1;

            for (var row = 0; row < rowCount; row++)
            {
                if (row > 0)
                {
                    builder.Append(',');
                }

                builder.Append('(');

                for (var column = 0; column < columnCasts.Count; column++)
                {
                    if (column > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append('$').Append(parameter++);

                    if (columnCasts[column] is { } cast)
                    {
                        builder.Append("::").Append(cast);
                    }
                }

                builder.Append(')');
            }

            return builder.ToString();
        }
    }
}
