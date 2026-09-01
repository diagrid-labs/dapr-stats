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

            buildingBlock = segment.Length == 0
                ? IndexPage
                : Uri.UnescapeDataString(segment).ToLowerInvariant();

            return true;
        }

        private static bool IsDaprDocsHost(string host) =>
            host.Equals(CanonicalHost, StringComparison.OrdinalIgnoreCase) ||
            VersionedHost.IsMatch(host);
    }
}
