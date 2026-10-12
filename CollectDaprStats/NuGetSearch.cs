using System.Text.Json;

namespace DaprStats
{
    /// <summary>
    /// Parsing and selection for NuGet's search API. NuGet runs several search
    /// hosts and they can drift apart: on 2026-10-05 azuresearch-usnc had stalled
    /// and served the same download counts for days while azuresearch-ussc was
    /// current. The NuGet client SDK always used the first host, so the collector
    /// queries every host and keeps the freshest answer.
    /// </summary>
    internal static class NuGetSearch
    {
        public const string ServiceIndexUrl = "https://api.nuget.org/v3/index.json";

        public record VersionDownloads(string Version, long Downloads);

        public record SearchResult(string PackageId, IReadOnlyList<VersionDownloads> Versions)
        {
            public long TotalDownloads => Versions.Sum(v => v.Downloads);
        }

        /// <summary>
        /// The distinct search hosts in the service index. Each host is listed
        /// under several SearchQueryService type versions; it is returned once.
        /// </summary>
        public static IReadOnlyList<Uri> SearchEndpoints(byte[] serviceIndexJson)
        {
            using var doc = JsonDocument.Parse(serviceIndexJson);
            return doc.RootElement.GetProperty("resources").EnumerateArray()
                .Where(r => r.GetProperty("@type").GetString()!.StartsWith("SearchQueryService"))
                .Select(r => new Uri(r.GetProperty("@id").GetString()!))
                .Distinct()
                .ToList();
        }

        /// <summary>The query for one package by exact id, stable versions only.</summary>
        public static Uri BuildQueryUri(Uri endpoint, string packageName) =>
            new($"{endpoint}?q=packageid:{Uri.EscapeDataString(packageName)}&prerelease=false&semVerLevel=2.0.0&take=1");

        /// <summary>The first hit of a search response, or null when there is none.</summary>
        public static SearchResult? ParseSearchResult(byte[] searchJson)
        {
            using var doc = JsonDocument.Parse(searchJson);
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0)
            {
                return null;
            }

            var package = data[0];
            var versions = package.GetProperty("versions").EnumerateArray()
                .Select(v => new VersionDownloads(
                    v.GetProperty("version").GetString()!,
                    v.GetProperty("downloads").GetInt64()))
                .ToList();
            return new SearchResult(package.GetProperty("id").GetString()!, versions);
        }

        /// <summary>
        /// The result with the most downloads. Download counts only grow, so a
        /// stalled host can never win. Nulls (failed hosts, no hit) are skipped.
        /// </summary>
        public static SearchResult? PickFreshest(IEnumerable<SearchResult?> results) =>
            results.OfType<SearchResult>().MaxBy(r => r.TotalDownloads);

        /// <summary>
        /// True when even the freshest host has not moved past the total stored
        /// for an earlier week. On 2026-10-12 both hosts still served the exact
        /// per-version counts of a week before, to every client, while the
        /// nuget.org gallery was well ahead; PickFreshest alone stored a week
        /// of zero downloads.
        /// No earlier week (null or 0) means there is nothing to compare with.
        /// </summary>
        public static bool IsStale(long freshestTotal, long? previousWeekTotal) =>
            previousWeekTotal > 0 && freshestTotal <= previousWeekTotal;

        /// <summary>
        /// The total stored for the most recent earlier collection week, from a
        /// single-cell query result; null when there is no earlier week.
        /// </summary>
        public static long? ParsePreviousWeekTotal(byte[] queryJson)
        {
            if (queryJson.Length == 0)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(queryJson);
            if (doc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var cell = doc.RootElement[0][0];
            return cell.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => long.Parse(cell.GetString()!),
                _ => cell.GetInt64(),
            };
        }
    }
}
