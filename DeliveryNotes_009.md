# GNA_DBDayReductions — Revision 009

Date: 2026-09-22

## Revision 009 formatting additions

Epoch readings: PointName_ID displays as an integer; dH is right-aligned.
Reduction passes: PointName_ID and Pass display as integers. Measurement and Pass are centred. Mean, StandardDeviation, StandardError, CandidateCount, CandidateMean, CandidateStandardDeviation, CandidateStandardError, TwiceCandidateSE, MeanDifference, Minimum and Maximum are right-aligned.
Daily results: NoOfObservations, RetainedCount, RejectedCount, AcceptedPasses and Performance are centred. dH, OriginalMean and Original Mean - Final Mean are right-aligned.
All unlisted formats and alignments are preserved. Formatting affects display/copy only; calculations and database behavior are unchanged.

## Retained integer display exceptions

Reduction passes: Count and CandidateCount display zero decimal places.
Daily results: PointName_ID, NoOfObservations, RetainedCount, RejectedCount and AcceptedPasses display zero decimal places.
These are grid-specific display and clipboard exceptions to the four-decimal formatting below. Other columns retain their accepted formatting. Calculation and database behavior are unchanged.

## Retained display conventions

All numeric grid columns display four decimal places, including IDs, counts, measurement values and reduction statistics. RetainedValues uses four decimals for each listed value. This is display/copy formatting only: source values, filtering decisions, calculation precision and database storage remain unchanged.

Date/time columns retain their date and display the time as HH:mm:ss, with no fractional seconds. LocalDate remains date-only. PointName_ID and ReadingCount cell text is centred horizontally and vertically.

Daily results includes Original Mean - Final Mean (MeanDifference). This is the signed original mean minus the final unrounded reduction mean for StatisticsField, consistent with the first-field audit. It is NULL when no mean is available. The column is a review diagnostic and does not change the database schema.

## Installation and test

1. Close the application. Replace the project source with all files in this archive, including the new `.cs` files and `appsettings.json`. The project remains .NET 10 WPF. Existing user settings keep their established location and identity.
2. Build Release and launch. Test the database connection, select the project, and set the inclusive dates under Historic Data / Manual Data Reductions. Only completed project-local days are accepted. Dates before the registered project start are rejected.
3. Open Test and click Test. Confirm the project and date range. Schema preparation remains part of this action, as requested. It may add the required tables/columns before the first review. The Create Database action in GNA_DLRreport is unchanged and deferred to a separate task.
4. Review each table using the three grids: Epoch readings, Reduction passes and Daily results. Epoch readings contains every column of the actual selected source rows, including UTCtime and identity fields. Dates in the pass/final grids are project-local dates. Daily UTCtime represents project-local noon.
5. Select cells or rows, then use Ctrl+C or Copy Selected to copy with column headings. Ctrl+A within a grid selects all cells. Paste into Excel. The grids are read-only and support scrolling, sorting and multiple-cell selection. NULL values represent absent data; zero remains a valid number.
6. Click Commit and Next Table to write the displayed table's Daily values and statistics, then advance to the next table. The last approval completes the run. There are no Daily/statistics row writes for an unapproved table.
7. Cancel stops the run and retains previously committed tables. An uncommitted table is discarded. Schema preparation already committed is retained. The displayed review remains available for copying; restarting Test clears it and begins again. Closing while busy requests cancellation; close again after cancellation completes.

The Reduction passes grid lists Initial, Comparison, After trim and Final stages in chronological order for every date, identity and measurement. It shows counts, means, sample SD/SE, candidate count/mean/SD/SE, twice candidate SE, mean difference, numerical tolerance, proposed minimum/maximum, decisions and the values retained at each stage. DrivesStatistics identifies the first measurement field. A rejected comparison keeps both proposed extreme readings. No-data and fewer-than-five cases are included explicitly. Daily results shows rounded values proposed for storage and the first-field counts/performance summary. The arithmetic and first-field statistics rules are unchanged from Revision 005.

This revision connects the reviewed reducer to Test. Scheduler Start/Stop retain their existing interface state behavior; timed execution, Historic Data Compute and Delete remain for later connection, as agreed. No additional minor interface changes are queued in this delivery.

## Reduction rule

Non-null numeric values are sorted independently for each measurement field in source database column order. Zero and negative observations are valid. With fewer than five values, use their arithmetic mean. With at least five values, compare the current mean with the candidate mean after removing one minimum and one maximum. Candidate SE = candidate sample SD / sqrt(candidate count), with sample variance denominator count minus one. Adopt the candidate only when the absolute mean difference exceeds twice that candidate SE plus the configured numerical tolerance; repeat. Equality retains the larger set. Stop when a pass is rejected or fewer than five values remain. A zero-variance candidate has zero SE. This implements the agreed engineering rule; it is not a formal statistical significance test and cannot distinguish genuine movement from erroneous readings.

The original observation count, final retained/rejected counts, original/final means and accepted trimming pass count in the statistics row describe only the first measurement field. That same field determines Daily.NoOfObservations and Performance. No usable values in that field => NULL DailyMean, NULL NoOfObservations and Fail; at least one => Pass. Other fields can still have means when the first field is NULL. Statistical counts are stored as zero for the empty case. Means are rounded only on writing to the Daily column's scale; audit means retain up to ten decimal places.

Local date boundaries use the active project's TimeZoneId: [local midnight, next local midnight). UTCtime contains the UTC equivalent of project-local noon. Daylight-saving days may span 23 or 25 UTC hours. At a midnight clock gap the first valid minute starts the day; at an ambiguous boundary the earlier UTC occurrence is used. A wholly skipped calendar day is rejected.

## Database scope and preparation

All Epochs tables are discovered from the database. The 16 tables in the supplied script are mapped in appsettings.json. Unexpected Epochs tables or unsupported columns fail discovery before any schema/data writes. Each table requires an explicit project-ownership rule; adding a new table requires adding and validating its mapping. Metadata fields such as ReadingCount, LatestReading and ReplacementName are excluded from numeric reductions.

Active registrations are enumerated even without epochs so empty days produce Fail records. Deleted registrations and deleted epoch rows are excluded. Point ownership comes from PointName.Project_ID; pair ownership from PrismPairs.Track_ID -> Track.Project_ID; arrays from PrismArray.Project_ID (StructuralArray also uses PrismArrayPoint); devices from GeotecSensors.Project_ID. Pair/array member ownership is checked. There are no changes to these ownership tables or relationships.

Array-type mappings are 1 Structural, 2 Tunnel, 3 PrismCrackGauge, 4 PrismTilt. Device mappings use SensorType strings Tilt and Vibration. These values are explicit configuration, not inferred from device names. Existing epochs whose registration type conflicts with the configuration are rejected rather than silently omitted. A registered device with no epochs is assigned by its configured SensorType. ImportEnabled does not exclude a registered device from performance coverage. Point-based tables cover every active project point, as requested.

On the first test the reducer prepares the following schema changes in one transaction:

- Creates dbo.DailyReductionStatistics when absent.
- Adds nullable int NoOfObservations to every Daily table that lacks it.
- Allows NULL in Daily numeric measurement columns, including Tilt and Vibration, for no-data days.
- Adds source measurement fields missing from the corresponding Daily table. In the supplied schema these include ShortTwistRatio and LongTwistRatio, stored as decimal(18,6) means.
- Creates a missing Daily table using registered identity keys, UTCtime, nullable measurements, IsDeleted, NoOfObservations and the appropriate ownership foreign key. This fallback uses a composite identity/timestamp primary key; it does not reproduce unrelated indexes or a source-specific surrogate identity column.

The connection therefore needs permission to prepare these table changes as well as read epochs/registrations and write Daily/statistics records. Preparation commits separately before reduction. It remains if a later table is cancelled or fails.

Daily values overwrite an existing identity + UTCtime row, including clearing IsDeleted, or append if absent. Duplicate existing rows for that key cause that table to roll back for correction. Statistics overwrite by project + source schema/table + local date + entity identity. Structural arrays preserve both Array_ID and PointName_ID. The audit table also records project time zone, local date, UTC noon, first measurement name, algorithm version, tolerance and computation UTC timestamp. Performance stores only Pass or Fail.

Each source table is first read in a short serializable transaction. That read transaction finishes before waiting for review, so no database row locks are held while the operator examines the grids. After approval, project ownership, registrations and all selected source values are checked again in the write transaction. A change causes rollback and asks the operator to rerun Test; it cannot silently commit values different from those reviewed. Each source table's whole selected date range and its statistics commit together using a serializable transaction. A database application lock prevents overlapping runs of this reducer. Previously committed tables survive later failures. No production database is modified merely by building or launching the application; schema preparation begins only after confirming Test; Daily/statistics row writes additionally require Commit and Next Table.

## Validation

Release build: .NET SDK 10.0.401; final build and verification results are recorded in Validation_009.txt.

Offline WPF control tests use isolated preferences and generate no images. SQL integration tests create a uniquely named disposable database from the supplied schema, seed synthetic projects/readings, and delete that database afterward. They cover all 16 table pairs, trimming, missing data, local/DST boundaries, project isolation, schema preparation, duplicate rejection, reruns, audit rollback, partial completion, concurrency protection and validation failures. Revision 009 also tests review gating, trace/result consistency, cancellation before and between commits, input changes during review, first-field trace statistics and reviewed rerun deduplication. These are development tests, not acceptance against the deployed production database. Visual checking and an actual paste into Excel remain with Julian.




