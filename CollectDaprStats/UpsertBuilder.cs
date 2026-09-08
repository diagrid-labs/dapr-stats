using System.Text;

namespace DaprStats
{
    /// <summary>
    /// Builds the `on conflict (a,b) do update set c=excluded.c` tail that makes
    /// an INSERT idempotent against its unique constraint.
    /// </summary>
    /// <remarks>
    /// Separate and tested for the same reason as <see cref="SqlValuesBuilder"/>:
    /// the clause is generated text, and a transposed column — `set
    /// commit_count=excluded.comment_count` — would corrupt data silently and
    /// permanently. `github_dapr` alone has twelve update columns.
    /// </remarks>
    public static class UpsertBuilder
    {
        /// <summary>
        /// The generated column every one of these constraints leads with. It is
        /// computed by Postgres from `collection_date` and cannot be assigned.
        /// </summary>
        private const string GeneratedColumn = "collection_week";

        /// <param name="keyColumns">
        /// The conflict target, in index order. Must match an existing unique
        /// constraint or Postgres raises 42P10.
        /// </param>
        /// <param name="updateColumns">
        /// The columns to overwrite with the incoming row's values. Excludes the
        /// key columns and <see cref="GeneratedColumn"/>.
        /// </param>
        public static string BuildOnConflict(
            IReadOnlyList<string> keyColumns,
            IReadOnlyList<string> updateColumns)
        {
            if (keyColumns.Count == 0)
            {
                throw new ArgumentException(
                    "At least one key column is required.", nameof(keyColumns));
            }

            if (updateColumns.Count == 0)
            {
                throw new ArgumentException(
                    "At least one update column is required.", nameof(updateColumns));
            }

            foreach (var column in updateColumns)
            {
                // Checked before the overlap rule below: collection_week is
                // normally in the key as well, and this is the more useful
                // message of the two.
                if (string.Equals(column, GeneratedColumn, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"'{GeneratedColumn}' is a generated column and cannot be assigned.",
                        nameof(updateColumns));
                }

                if (keyColumns.Contains(column, StringComparer.Ordinal))
                {
                    throw new ArgumentException(
                        $"Column '{column}' is part of the conflict key and must not be updated.",
                        nameof(updateColumns));
                }
            }

            var builder = new StringBuilder("on conflict (");
            builder.AppendJoin(',', keyColumns);
            builder.Append(") do update set ");

            for (var i = 0; i < updateColumns.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(updateColumns[i])
                       .Append("=excluded.")
                       .Append(updateColumns[i]);
            }

            return builder.ToString();
        }
    }
}
