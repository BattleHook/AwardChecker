# AwardChecker

Windows desktop award analysis for DXKeeper logs. The launcher selects a
registered, architecture-compatible OLE DB provider before starting the
application. Database reads remain explicitly requested and **read-only**.
AwardChecker does not update QSOs, control DXKeeper, submit logs, or contact
award services.

[User guide](docs/index.html) | [Support](#support) | [License](LICENSE)

Version **1.0.5** renames AwardScorer to AwardChecker and removes the built-in
EUDXF template and station list. New profiles contain no award stations or
achievement levels; import your own authorized rules/lists or load an existing
profile. Existing `.award.xml` profiles and user defaults remain compatible.
The source export contains no logs, saved personal profiles or release binaries.

## Requirements and build

- Windows with .NET Framework 4.8 for the selected process architecture.
- A compatible Microsoft ACE OLE DB provider (12.0 or 16.0). On 64-bit Windows
  AwardChecker prefers 64-bit ACE, then 32-bit ACE; 32-bit Jet 4.0 is the final
  fallback. ODBC registration alone is not sufficient.
- A .NET SDK for development. The Framework reference assemblies are restored
  from NuGet when a Framework targeting pack is not installed.

```powershell
dotnet restore Tests\AwardChecker.Tests.csproj --verbosity normal
dotnet build Tests\AwardChecker.Tests.csproj --configuration Release --no-restore --verbosity normal
dotnet build AwardChecker.csproj --configuration Release -p:PlatformTarget=x64 --no-restore --verbosity normal
dotnet restore AwardChecker.Launcher.csproj --verbosity normal
dotnet build AwardChecker.Launcher.csproj --configuration Release --no-restore --verbosity normal
& .\Tests\bin\Release\net48\AwardChecker.Tests.exe
```

The Release launcher is `bin\Release\net48\AwardChecker.exe`; architecture-
specific application builds are under `bin\Release\net48\x86` and `x64`.
The tests build the x86 application. Copy the launcher/configuration, both
architecture-specific app/configuration pairs, this README, LICENSE,
THIRD-PARTY-NOTICES.md and docs/index.html into the portable
layout below, in a writable folder of your choice. Start `AwardChecker.exe`.
The runtime contains the launcher executable/configuration, x86/x64 application
executables/configurations and those public documents, plus Profiles, Defaults and Reports
folders. No installer is needed. Version 1.0.4 corrected summary band/mode
counts to use counted QSOs, showing mode-group counts when grouping is selected.
Version 1.0.3 added the smarter local rule
parser and the operator-driven AI-assist import; version 1.0.2 added clearer tab labels:
active tabs are green, bold 11 pt; inactive tabs are black, bold 9 pt.
Tab contents retain their existing fonts. Anniversary-rule import,
calendar-month counting, UTC times and driver selection remain included.

## Workflow

The window title shows the app version (`AwardChecker v1.0.5`). Loading a saved
profile adds its saved profile name; editing the profile name does not change
the title until the profile is successfully saved. New unsaved profiles show
the app title without a profile name.

1. Start a new profile, load a saved profile, or
   choose **Import rules text...** to paste the event rules or open a local
   `.txt`/`.md` rules file. The importer runs offline and drafts only explicitly
   recognized fields. Review the interpretation and warnings, then choose
   **Apply draft to editor**; this does not save it.
   For rules the local parser cannot interpret, choose **Copy AI prompt**: it
   copies your rules text plus a strict answer format to the clipboard. Paste
   that into your own AI chat (Gemini, ChatGPT, Copilot, ...), copy the AI's
   answer back into the same box and choose **Analyze rules** - the structured
   answer is detected and strictly validated locally (rejected tokens become
   review items). AwardChecker itself never contacts a network; you decide what
   leaves the PC and remain responsible for verifying every imported value.
   Add/verify the eligible main-station callsigns on tab 2 before scoring.
2. Select the DXKeeper database with **Browse**.
3. Set the profile name, Begin/End UTC dates and times, minimum qualification points,
   counting rule and points.
4. Import/edit main and optional member calls. TXT import **replaces** that
   category's list; it does not guess categories in a mixed input.
5. Tick eligible bands and modes; assign mode groups. Add rows for additional
   modes. Each mode can belong to exactly one group.
6. Configure category-specific award levels. Hunter categories (Mixed Mode,
   CW, SSB and DIGI) use points plus any configured unique-main-station
   requirement. Activator / All Bands uses QSO counts and is visibly marked
   unevaluated when scoring a Hunter log. Rows sort by category and threshold
   automatically when read or saved. The basic minimum qualification (for
   example, 50 points) remains separate from achievement levels.
7. Save a named `.award.xml` profile. **Save as** duplicates a configuration;
   edit its name/dates for the next event.
8. **Read and score**, review exclusions/warnings, then optionally export QSO,
   station or qualification CSV reports.

Saved profiles contain their station lists, selected bands, enabled/disabled mode
definitions, grouping, thresholds, notes and last-saved UTC time. They do not
contain a database path or a copy of the log. Imported TXT lists are copied into
the profile; subsequent external TXT edits require reimporting.
Rule-text import is a conservative local parser, not an AI rule interpreter.
Its review identifies missing station callsigns, optional VHF/UHF and ranking
rules that are outside scoring. Category-specific Hunter thresholds and
Activator QSO thresholds are shown in the draft; Activator awards are not
evaluated from a Hunter log. FM remains disabled when VHF/UHF is optional.
Check and edit every imported rule before scoring and saving.
Unnumbered DURATION/BANDS/MODES/SCORING/AWARD CLASSES sections are supported,
including month-name date ranges, explicit special-event callsigns, FT rates
for explicitly listed FT8/FT4, and name-first award-class thresholds.
The 160m-to-6m range expands to common amateur bands; review local additions.
Monthly station/band/mode counting uses calendar months, not rolling 30-day
windows. Individual FT8 and FT4 remain separate unless mode grouping is selected.
Same-month duplicates score zero; reviewed/incomplete QSOs do not consume a key.
Missing timezone or minimum qualification remains an explicit review item;
achievement thresholds are not inferred as the basic minimum.
Begin defaults to **00:00 UTC** and End to **23:59 UTC**. Times use `HH:mm`;
the entire final minute is included (23:59 includes seconds through 23:59:59).
The same range applies to database reads, scoring, CSV/HTML reports and the
rule-import review. Daily limits reset at 00:00 UTC; monthly limits reset at
00:00 UTC on the first day of each month.
Existing date-only profiles retain their whole-day behavior. Imported explicit
UTC times are preserved; omitted times use the defaults with a review warning.
Reversed date/time ranges are rejected before scoring or saving.
Lists reject non-ASCII lookalikes (for example, slashed O instead of digit 0);
correct copied callsigns explicitly. Invalid list input reports the field and
offending token in an AwardChecker error dialog; it is not silently normalized.

Results are a snapshot. Database refresh is explicit; changing scoring rules or
the database selection invalidates old results. Header clicks sort result tables.
**QSO columns** opens a separate checklist with Move up / Move down. Its order
is edited by clicking names to select rows; only checkbox clicks toggle
visibility (Space also toggles the selected row for keyboard users). Order
and visibility are saved per award profile. **Save as user default** stores an
award-independent layout in `Defaults\User-Columns.xml` beside the executable.
**Restore user default** and **Restore app default** load that layout into the
editor; Apply changes only the current profile, which must then be saved.
Restoring app defaults never deletes the user default or changes other profiles.
New profiles start with app defaults; restore the user default explicitly when
wanted. Old profiles without a layout also use app defaults.

### Portable runtime folders

The agreed runtime layout is:

```text
AwardChecker\
  AwardChecker.exe                (AnyCPU .NET Framework 4.8 launcher)
  AwardChecker.exe.config
  x64\
    AwardChecker.App.exe
    AwardChecker.App.exe.config
  x86\
    AwardChecker.App.exe
    AwardChecker.App.exe.config
  README.md
  LICENSE
  THIRD-PARTY-NOTICES.md
  docs\
    index.html
  Profiles\
  Defaults\
    User-Columns.xml
  Reports\
```

At startup the launcher verifies the .NET Framework 4.8 Release registration,
then checks each OLE DB ProgID/CLSID, its InprocServer32 DLL and the DLL's PE
architecture through the matching registry view. It prefers x64 ACE 16.0/12.0,
then x86 ACE 16.0/12.0, then x86 Jet 4.0. An x86 or Jet fallback displays an
explicit warning; missing compatible prerequisites stop launch with an
actionable diagnostic. The selected provider and architecture are passed to
the child process. A failed database read never triggers provider switching or
an automatic retry during that session.

The launcher is the portable runtime root. Profile Load/Save as dialogs start
in root `Profiles`; CSV/HTML export dialogs start in root `Reports`. These paths
resolve from the launcher directory, not the child executable or current working
directory, so existing profiles/defaults stay beside the launcher. Folders are
created when needed; the default file is created only on Save as user default.
Use a writable runtime folder: access errors are displayed, with no silent
fallback to AppData. Existing profiles/defaults elsewhere are not moved or
overwritten. During development these folders belong beside the launcher under
`bin\Debug\net48`, not beside the source files. Development profiles/defaults
are not automatically copied into a delivered runtime.
The selected DXKeeper database remains in its existing location and read-only.
After a successful **Read and score**, its path is remembered in
`Defaults\Last-Database.txt`, independently of award profiles. On next startup
the path is restored, but the database is not opened automatically. If it is
unavailable, a status message asks you to choose another database. Preference
read/write failures are shown in an AwardChecker error dialog.

The result summary counts distinct bands and effective modes from counted QSOs
only, not the award's configured selections. When mode grouping is selected,
it shows distinct mode groups instead. Review and excluded QSOs do not contribute.

CSV exports include all QSO detail fields in the profile's column order
(including hidden columns) and apply the current QSO row filter. Blank detail
values display as unavailable. QSO Number and contacted rig are not report
columns. Name, QTH, Locator and Comment are read from the logged QSO; no online
lookup or substitution from your own location is performed. Category precedes
Points in the app-default layout. ContestID, SRX, SRXString, STX and STXString
are also available when present in the DXKeeper QSO record.

### Location sections and report

The QSO table's section picker separates QSOs by **MyQTHID**, with the
corresponding MyQTH name/address/locator and station callsign(s) above each table.
The default **All MyQTHIDs** selection retains the combined QSO list and the
selected QSO row filter. Pick an individual MyQTHID for a location-only table.
Missing MyQTH records are explicitly labelled unavailable; they do not prevent
otherwise eligible QSOs from scoring.

Station scores highlight stations with **CountedQsos > 0** in green. The
**Only stations with counted QSOs** checkbox hides unworked/review-only stations.
Station CSV export follows that station filter. Highlighting/filtering uses the
counted QSO count, not points, so a counted zero-point contact still qualifies.

**Sectioned report** exports a local HTML report, suitable for opening/printing
in a browser. It includes a separate header/table for each MyQTHID in the
selected QSO view, uses visible columns in profile order, and retains one
combined award score. Location sections never reset duplicate counting.
Drag QSO table header borders to adjust column widths. Save the award profile
to retain resized widths, or use **Save as user default** in the column editor
to share them across awards. Restoring the app default restores automatic widths.
Resized widths apply to exported HTML too; HTML columns are not browser-draggable.
Text wraps in explicitly sized report columns instead of being clipped. Columns
without a saved width fit their content, with Comment wrapping in a bounded column.
Blank Submode values remain empty in the table and report.
Wide reports scroll horizontally on screen.
When printing many columns, select landscape/scaling or hide unneeded columns;
the report does not guarantee all columns fit one printed sheet.
The screen's section selection affects only the displayed table: CSV and HTML
exports include all sections of the selected QSO view.

Location headers may contain your address. Review reports before sharing.

## Scoring contract

- Choose **Category-based points (Main / Member)** or **Mode-based points**
  on tab 3. Category-based is the default for new and older profiles;
  it uses the Main/Member values on tab 2. Mode-based disables those controls
  and uses the editable **Points per QSO** column on tab 3 for both categories.
  Save the profile to retain the selected basis and rates.
- Suggested editable mode rates: FT8/FT4/FT2 = 2; RTTY/RTTYR and
  PSK/PSK31/PSK63/PSK125 = 4; SSB/USB/LSB = 7; CW = 10.
  Other modes need explicit rates. Every enabled mode must have an integer
  rate between 0 and 1,000,000 when using mode-based scoring; blank is not zero.
  Only checked modes are eligible; adding a mode to the editor does not enable
  it automatically.
- Points do not change eligibility, station lists, main-station thresholds or
  duplicate rules. Equal rates do not merge groups. If FT8 and RTTY share DIGI
  and counting uses groups, the earliest eligible contact wins that key, not
  the highest-point contact. Review grouping/repeat limits for your award.
  HTML and CSV record the scoring basis and rates; QSO points use that basis.
- Dates are UTC, from start midnight through the entire inclusive end date.
- Callsigns match exactly after trimming and case normalization. No automatic
  `/P`, `/M`, regional prefix or event-suffix removal is performed.
- Main calls take precedence over member calls with an overlap warning.
- Counting can be per station, station/band, or station/band/mode (or mode group),
  once per event period or UTC day.
- A separate minimum-points threshold reports whether the event's basic
  qualification score has been reached; it does not replace achievement levels.
- The earliest eligible QSO wins a duplicate key.
- Unknown/unselected modes, missing bands/modes, non-`Y` `QSO_COMPLETE`, and
  missing required own-station identity are held for review and earn no points.
- A recorded submode takes precedence over its parent mode.
- Points and unique-main-station requirements are independent AND conditions.
  A main station counts once toward the unique requirement, even if it earns
  points on several combinations. Member stations never count as main stations.
- Frequency is MHz and power is logged transmit power in watts. `MyRig` and
  `MyAntenna` come from the QSO's referenced MyQTH record. They reflect that
  record's current definition, not an independently archived equipment history.
- CSV cells are quoted and text formulas neutralized for spreadsheet viewing.

Results are provisional calculations, not a guarantee of official award acceptance.

## Compatibility, data and privacy

The renamed application retains the profile XML schema, `Defaults\User-Columns.xml`,
and `Defaults\Last-Database.txt` formats. Move your existing Profiles, Defaults
and Reports together with the portable application when changing its folder;
do not overwrite these folders with an empty distribution.
The structured AI-answer header remains `AWARDSCORER-IMPORT: 1` for compatibility.
It is a format identifier, not the current product name.

No award template or station list is bundled. Obtain permission for rules or
datasets you redistribute; a profile can include copied station lists and rule
notes. The application has no automatic online lookup or AI API integration.
Never post real databases, private rules, station paths, addresses or unreviewed
CSV/HTML reports in public issues. Test fixtures use synthetic data.

## Support

Read the [user guide](docs/index.html) first. Use the repository's Issues tab
to report bugs or propose features, including version, Windows/provider
architecture, reproduction steps and expected/actual results with synthetic data.
See [third-party notices](THIRD-PARTY-NOTICES.md) for platform dependencies.

If you find AwardChecker useful, optional support is available through
[Ko-fi](https://ko-fi.com/battlehook). No payment is required to use it.

## License

Original code and documentation are available under the [MIT License](LICENSE).
External runtimes, providers, award rules and user data keep their own terms;
see [third-party notices](THIRD-PARTY-NOTICES.md).
