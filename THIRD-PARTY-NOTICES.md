# Third-party notices for AwardChecker

Original AwardChecker code, synthetic test fixtures and documentation use the
[MIT License](LICENSE). This notice does not relicense third-party software,
award rules, station lists, user profiles or log data.

## Required platform components, not redistributed

- **Microsoft .NET Framework 4.8 / Windows Forms** is required on Windows.
  Obtain it from Microsoft under Microsoft's applicable terms.
  Reference: <https://dotnet.microsoft.com/download/dotnet-framework/net48>.
- **Microsoft Access Database Engine / ACE OLE DB** (12.0 or 16.0), or the
  compatible x86 **Microsoft Jet 4.0** fallback, is required to read DXKeeper
  databases. AwardChecker does not redistribute, install or modify these
  providers. Obtain compatible software from Microsoft and follow its terms.
- **DXKeeper** is external software from the DXLab Suite. AwardChecker is an
  independent reader, not an official DXLab component, and does not bundle it.
  Reference: <https://www.dxlabsuite.com/dxkeeper/>.

## Build-time components

The .NET SDK/MSBuild and Microsoft .NET Framework reference assemblies are
used to build the application. SDK restore can retrieve reference assemblies
from NuGet when a targeting pack is absent. Their own Microsoft/package terms
apply; no SDK, reference-assembly package or .NET runtime is in the source export.
The application has no explicit third-party NuGet package references.

## User-supplied content and external services

No default award template, station list, copied event-rule document or live log
is distributed. Synthetic tests describe fictional activities and do not claim
eligibility for real awards. Users supply their own authorized rules, lists,
profiles and DXKeeper data; applicable data and award terms remain their own.

The copy/paste AI-assist feature contacts no service. Operators choose whether
to paste their text into an external AI chat and must review that service's
privacy and usage terms themselves.

Ko-fi (<https://ko-fi.com/battlehook>) is an optional external support service.
AwardChecker does not process payments.
