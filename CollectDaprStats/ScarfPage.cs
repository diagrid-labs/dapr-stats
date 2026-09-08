using System.Text;

namespace DaprStats
{
    /// <summary>
    /// Maps a Scarf `referer` value onto the Diagrid site and page it points
    /// at.
    /// </summary>
    /// <remarks>
    /// The Diagrid pixels set `referrerPolicy="no-referrer-when-downgrade"` on
    /// HTTPS pages, so Scarf receives the full page URL. The same logical page
    /// arrives with tracking query strings, mixed case and inconsistent
    /// trailing slashes, and all of those have to collapse onto one key or a
    /// single page fragments into a dozen rows.
    /// </remarks>
    public static class ScarfPage
    {
        // Matches the width of the page_path column
        // (VARCHAR(512) in postgres/postgres_schema.psql).
        private const int MaxPathLength = 512;

        // These two host literals also appear, independently, as the string
        // literals 'diagrid.io' and 'docs.diagrid.io' in
        // scarf_diagrid_company_pages_view (postgres/postgres_schema.psql and
        // postgres/add-scarf-diagrid-page-views-2026-09-07.psql). If either
        // host is ever renamed here, that view's website_views/docs_views
        // columns silently go to 0 while total_views stays correct — a wrong
        // answer that looks like a right one. Changing either value needs a
        // matching view migration.
        private const string WebsiteHost = "diagrid.io";
        private const string WwwWebsiteHost = "www.diagrid.io";
        private const string DocsHost = "docs.diagrid.io";

        /// <summary>
        /// The two Diagrid sites this activity expects to see at least one row
        /// for in a healthy run. Single source of truth for
        /// <see cref="GetScarfPageViews"/>'s per-site zero-row guard.
        /// </summary>
        public static readonly string[] Sites = [WebsiteHost, DocsHost];

        public static bool TryGetPage(string? referer, out string site, out string path)
        {
            site = string.Empty;
            path = string.Empty;

            if (string.IsNullOrWhiteSpace(referer) ||
                !Uri.TryCreate(referer, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!TryNormaliseHost(uri.Host, out var normalisedSite))
            {
                return false;
            }

            // AbsolutePath excludes the query and the fragment, which is what
            // collapses the ?utm_source= and ?ref= variants onto one key.
            if (!TryNormalisePath(uri.AbsolutePath, out var normalisedPath))
            {
                return false;
            }

            site = normalisedSite;
            path = normalisedPath;
            return true;
        }

        /// <summary>
        /// Exact-match allow-list. Anything else — third-party mirrors,
        /// staging and preview hosts, localhost, and any Dapr traffic that
        /// would appear if a pixel were ever shared between the two Scarf
        /// accounts — is dropped.
        /// </summary>
        private static bool TryNormaliseHost(string host, out string site)
        {
            if (host.Equals(DocsHost, StringComparison.OrdinalIgnoreCase))
            {
                site = DocsHost;
                return true;
            }

            if (host.Equals(WebsiteHost, StringComparison.OrdinalIgnoreCase) ||
                host.Equals(WwwWebsiteHost, StringComparison.OrdinalIgnoreCase))
            {
                site = WebsiteHost;
                return true;
            }

            site = string.Empty;
            return false;
        }

        private static bool TryNormalisePath(string absolutePath, out string path)
        {
            path = string.Empty;

            // Unescaped first, so the checks below run on the text that will
            // actually be stored rather than on its encoded form.
            var unescaped = Uri.UnescapeDataString(absolutePath);

            if (unescaped.Any(char.IsControl))
            {
                return false;
            }

            // Lowercased so /Pricing and /pricing do not become two rows.
            var lowered = unescaped.ToLowerInvariant();

            var builder = new StringBuilder(lowered.Length + 1);
            builder.Append('/');

            foreach (var character in lowered)
            {
                if (character == '/' && builder[^1] == '/')
                {
                    continue;
                }

                builder.Append(character);
            }

            // Trailing slash stripped so /pricing/ and /pricing are one row.
            // The root is the exception; it would otherwise become empty.
            if (builder.Length > 1 && builder[^1] == '/')
            {
                builder.Length--;
            }

            if (builder.Length > MaxPathLength)
            {
                // Rejected rather than truncated: an over-long value would
                // fail its whole insert chunk with Postgres error 22001 after
                // the week's DELETE has already run.
                return false;
            }

            path = builder.ToString();
            return true;
        }
    }
}
