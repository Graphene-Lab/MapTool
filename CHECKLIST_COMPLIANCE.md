# MapTool — TOOL_CHECKLIST Compliance

Checked 2026-09-04 against `AIOrchestrator/API/TOOL_CHECKLIST.md` (current version incl. the
"Completion — compliance file" point). `MapTool` is a pure-network agent tool (public
OpenStreetMap services); it reads/writes no files, so the file-oriented points are N/A.

## Release & layout (plugin tools)

- OK — csproj `<Version>$([System.DateTime]::Now.ToString("1.yy.MM.dd"))</Version>`: date auto-version.
- OK — both channels on `v*` tags: `.github/workflows/plugin-release.yml` (GitHub Release zip for hosts) + `.github/workflows/publish.yml` (NuGet `Graphene.MapTool`).
- OK — AIOrchestrator referenced as sibling (`..\AIOrchestrator\AIOrchestrator.csproj` ProjectReference when present, `Graphene.AIOrchestrator` 1.* package otherwise); never copied into the plugin tree.
- OK — never ships `AIOrchestrator.dll`/dependency graph: plugin-release.yml strips the graph; MapTool has no unique dependency dlls beyond the BCL.
- OK — writes no state: stateless tool, nothing persisted, no files in `Tools/<MapTool>/` or next to the host executable.
- OK — independent of the host launch directory: no filesystem/CWD use, only absolute HTTPS endpoints.

## Agent-facing descriptions

- OK — docs never mention internals (HttpClient, Overpass/OSRM/Nominatim names, parsing).
- OK — minimal text; rendered GeToolDefinitions ≈ 6.8 KB incl. inherited `load_skill` and the enum vocabulary.
- OK — methods state what they do (outcomes), not how.
- OK — nothing mentions a sandbox or virtual paths (no file paths at all).
- N/A — no path-bearing methods.
- OK — every public method (`search_address`, `search_poi`, `search_poi_by_tags`, `find_route`) has `<summary>`, `<param>`, `<returns>` incl. error format and empty-result meaning.
- OK — class summary: one-line competency + cross-method rules (coordinate format, start-with-`search_address` flow).
- OK — no summary/param redundancy; formats live only in `<param>`/`<returns>`.
- OK — one instruction per line; each `///` line is one continuous source line.
- OK — formats specified: decimal degrees with ranges, radius in meters (1–25000), row format, defaults.
- OK — cross-references: params from `search_address()`/`search_poi()` result rows say which method produced them.
- OK — errors are actionable (cause + fix/alternative) and prefixed `Error:`; no raw exceptions reach the agent.
- N/A — no `[[name]]` dynamic placeholders.

## Surface & sandbox

- OK — only agent operations are public (4 methods); system-side internals (HttpClient, pacing, retries, disposal) are private or explicit-interface.
- N/A — no file-handling methods: `SandboxPath` not applicable.
- OK — no host paths, credentials or configuration exposed; requests go only to the public map services.

## Code & conventions

- OK — class name ends with `Tool`; package `Graphene.MapTool`.
- OK — public methods declared on the class; only inherited public member is `BaseAgentTool.LoadSkill` (`IDisposable.Dispose` is explicit-interface).
- OK — `Log.LogStep()` at entry/outcome/failure of every public method.
- OK — derives from `BaseAgentTool`; `IFileTool` N/A (no files handled).
- OK — `GenerateDocumentationFile=True` (tool definitions come from the `.xml` next to the dll).

## Standardized support — shared AIOrchestrator helpers

- N/A — no file content described/listed: `FileManager` not applicable.
- N/A — no files created/modified: `GitSupport.Snapshot` not applicable.
- N/A — no file output: no sandbox-relative path to return.
- N/A — tool parses no LLM text: `Utility.RemoveFencesEncapsulationAndFixTrim` not applicable.
- N/A — no HTML/SVG output: `Utility.EmbedSvgIcons` not applicable.
- N/A — no language detection: `Utility.DetectLanguage` not applicable.
- N/A — no path resolution: `SandboxPath` not applicable (see "Surface & sandbox").

## Completion — compliance file

- OK — this file is shipped at the repository root of the plugin and states every checklist point above.
