using System.Text.RegularExpressions;

namespace DaprStats
{
    /// <summary>
    /// Maps a Scarf `referer` value onto the Dapr building block whose
    /// documentation page it points at.
    /// </summary>
    /// <remarks>
    /// Both Dapr pixels set `referrerPolicy="no-referrer-when-downgrade"` on
    /// HTTPS pages, so Scarf receives the full page URL. That URL arrives in
    /// several shapes for the same logical page — versioned subdomains,
    /// third-party mirrors, tracking query strings and localised paths — and
    /// all of them have to collapse onto one key.
    /// </remarks>
    public static class ScarfReferer
    {
        /// <summary>
        /// The key used for `/building-blocks/` itself. Parenthesised so it can
        /// never collide with a real URL segment.
        /// </summary>
        public const string IndexPage = "(index)";

        private const string CanonicalHost = "docs.dapr.io";
        private const string BuildingBlocksSegment = "/building-blocks/";
        private const string BuildingBlocksIndex = "/building-blocks";

        // Matches the width of the building_block column
        // (VARCHAR(255) in postgres/postgres_schema.psql). A segment longer
        // than this cannot be a real building block, and inserting it as-is
        // would fail the whole chunk with Postgres error 22001 after the
        // week's DELETE has already run.
        private const int MaxBuildingBlockLength = 255;

        // Versioned docs subdomains: v1-9, v1-17, v1-13-1.
        private static readonly Regex VersionedHost = new(
            @"^v\d+(-\d+)+\.docs\.dapr\.io$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool TryGetBuildingBlock(string? referer, out string buildingBlock)
        {
            buildingBlock = string.Empty;

            if (string.IsNullOrWhiteSpace(referer) ||
                !Uri.TryCreate(referer, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!IsDaprDocsHost(uri.Host))
            {
                return false;
            }

            // AbsolutePath excludes the query and the fragment, which is what
            // collapses the ?utm_source= and ?ref= variants onto one key.
            var path = uri.AbsolutePath;

            if (path.EndsWith(BuildingBlocksIndex, StringComparison.OrdinalIgnoreCase))
            {
                buildingBlock = IndexPage;
                return true;
            }

            // No special handling for language prefixes is needed: searching for
            // the segment rather than anchoring at the path root makes
            // /zh-hans/developing-applications/building-blocks/pubsub/ and
            // /developing-applications/building-blocks/pubsub/ yield the same key.
            var start = path.IndexOf(BuildingBlocksSegment, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return false;
            }

            var remainder = path[(start + BuildingBlocksSegment.Length)..];
            var slash = remainder.IndexOf('/');
            var segment = slash < 0 ? remainder : remainder[..slash];

            if (segment.Length == 0)
            {
                buildingBlock = IndexPage;
                return true;
            }

            // Unescape before validating: the segment boundary above is
            // chosen on the raw, still-escaped text, so an encoded %2F could
            // otherwise smuggle a literal '/' past it. Validate only after
            // unescaping, on what will actually be stored.
            var unescaped = Uri.UnescapeDataString(segment);

            if (unescaped.Length > MaxBuildingBlockLength ||
                unescaped.Any(c => char.IsControl(c) || c == '/'))
            {
                return false;
            }

            buildingBlock = unescaped.ToLowerInvariant();
            return true;
        }

        private static bool IsDaprDocsHost(string host) =>
            host.Equals(CanonicalHost, StringComparison.OrdinalIgnoreCase) ||
            VersionedHost.IsMatch(host);
    }
}
