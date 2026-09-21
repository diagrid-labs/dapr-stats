-- NuGet downloads per calendar year, per package, plus a per-year total row.
-- Read-only; run it against daprstats over Neon's HTTP /sql endpoint.
--
-- download_count is a *cumulative lifetime* total per (package, version), so a
-- year's downloads are the sum of the increments between consecutive snapshots,
-- never SUM(download_count). Two things make that non-trivial:
--   * the collection interval is not constant (~15 days until 2026-03, weekly
--     after, with real gaps of 3..30 days), and an interval can straddle
--     1 January, so increments are split across years pro rata by days;
--   * the nine packages were onboarded at different dates (Dapr.Client
--     2023-12-14, AspNetCore/Workflow 2025-07-01, five more 2025-12-16,
--     AgentFramework 2026-08-01) and each arrives carrying its entire download
--     history -- Dapr.AspNetCore showed up with 15.1M downloads on day one.
--
-- Compare growth on per_day, not on downloads: coverage differs per package and
-- per year (2026 is a partial year), and the year totals rise partly because
-- more packages were added to collection rather than because usage tripled.
--
-- Known undercount: a package's first two runs contribute nothing (see the
-- `measured` CTE), so the five packages onboarded 2025-12-16 have no 2025 row
-- at all, and 2023 -- a single baseline snapshot -- drops out entirely.

WITH snap AS (                                  -- one row per package/version/day
    SELECT package_name,
           package_version,
           collection_date::date AS snap_date,
           max(download_count)   AS download_count
    FROM   nuget_dapr_client
    GROUP  BY 1, 2, 3
),
pkg_runs AS (                                   -- the days a package was collected
    SELECT package_name,
           snap_date,
           lag(snap_date) OVER w                          AS prev_run,
           first_value(snap_date) OVER w                  AS first_run
    FROM   (SELECT DISTINCT package_name, snap_date FROM snap) d
    WINDOW w AS (PARTITION BY package_name ORDER BY snap_date)
),
delta AS (
    SELECT s.package_name,
           s.snap_date,
           r.first_run,
           -- a version's own previous snapshot; for a version that debuts after
           -- its package is already tracked, the window opens at the package's
           -- previous run, because that is when it last had zero downloads here
           COALESCE(lag(s.snap_date) OVER w, r.prev_run)                        AS window_start,
           GREATEST(s.download_count - COALESCE(lag(s.download_count) OVER w, 0), 0) AS downloads
    FROM   snap s
    JOIN   pkg_runs r USING (package_name, snap_date)
    WINDOW w AS (PARTITION BY s.package_name, s.package_version ORDER BY s.snap_date)
),
measured AS (
    -- Drop the package's first *two* runs: run 1 is the onboarding baseline
    -- (millions of pre-existing downloads), and the run-1 -> run-2 increment is
    -- not trustworthy either -- NuGet's per-version totals at onboarding read
    -- low for every version at once, so that first interval overstates by 5-16x.
    -- Measurement starts at run 2 and the first real increment lands at run 3.
    SELECT * FROM delta
    WHERE  window_start IS NOT NULL
      AND  window_start > first_run
),
spread AS (
    -- Split each increment over the calendar years its window covers,
    -- pro rata by days. The window is half-open: (window_start, snap_date].
    SELECT y.yr,
           m.downloads
             * ( LEAST(m.snap_date, make_date(y.yr, 12, 31))
                 - GREATEST(m.window_start, make_date(y.yr, 1, 1) - 1) )::numeric
             / (m.snap_date - m.window_start)::numeric AS downloads_in_year,
           m.package_name,
           GREATEST(m.window_start + 1, make_date(y.yr, 1, 1)) AS covered_from,
           LEAST(m.snap_date, make_date(y.yr, 12, 31))         AS covered_to
    FROM   measured m
    CROSS  JOIN LATERAL generate_series(
               extract(year FROM m.window_start + 1)::int,
               extract(year FROM m.snap_date)::int) AS y(yr)
)
SELECT yr AS year,
       CASE WHEN grouping(package_name) = 1
            THEN '** ALL PACKAGES **' ELSE package_name END       AS package,
       round(sum(downloads_in_year))::bigint                       AS downloads,
       -- the ROLLUP row is by construction the largest sum in its year,
       -- so it doubles as the denominator for each package's share
       round(100 * sum(downloads_in_year)
             / max(sum(downloads_in_year)) OVER (PARTITION BY yr), 1) AS pct_of_year,
       min(covered_from)                                           AS measured_from,
       max(covered_to)                                             AS measured_to,
       (max(covered_to) - min(covered_from) + 1)                   AS days,
       round(sum(downloads_in_year)
             / (max(covered_to) - min(covered_from) + 1))::bigint  AS per_day,
       CASE WHEN grouping(package_name) = 1
            THEN count(DISTINCT package_name) END                  AS n_packages
FROM   spread
GROUP  BY yr, ROLLUP(package_name)
ORDER  BY yr, grouping(package_name) DESC, downloads DESC;