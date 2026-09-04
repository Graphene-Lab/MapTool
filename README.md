# MapTool

Map consultation agent tool for AIOrchestrator (plugin): finds addresses and places of
interest, and plans travel routes — powered by the free, no-API-key OpenStreetMap services.

## Agent surface

| Agent method | Purpose |
|---|---|
| `search_address(query, maxResults, lat, lon)` | Turns a place name/address into coordinates (geocoding, Nominatim). Start here. |
| `search_poi(category, lat, lon, radiusMeters, maxResults)` | Lists places of a chosen category (restaurant, pharmacy, hotel, ATM, parking, …) around a point, closest first (Overpass). |
| `search_poi_by_tags(osmKey, osmValue, lat, lon, radiusMeters, maxResults)` | Same, but for any OpenStreetMap tag (e.g. `cuisine=italian`, `wheelchair=yes`). |
| `find_route(fromLat, fromLon, toLat, toLon, mode)` | Calculates a route (car / walking / cycling) with step-by-step directions (OSRM). |

Typical agent flow: `search_address("Via Roma 10, Milano")` → take the `coords:` of a result
row → `search_poi` or `find_route` with those coordinates.

## Data sources & fair use

- **Geocoding** — Nominatim (`nominatim.openstreetmap.org`). Public service: the tool identifies
  itself with a descriptive User-Agent and paces requests to ~1/s (its usage policy), with bounded
  retry on `429`.
- **Places of interest** — Overpass API. The tool falls back across three public mirrors
  (overpass-api.de → kumi.systems → private.coffee) on timeouts/failures.
- **Routes** — OSRM demo server (`router.project-osrm.org`).

These are free community services without API keys. Their usage policies require respectful
volume; the tool is designed for occasional agent lookups, keeps every request bounded, and
never runs bulk data extraction.

## What was improved over a naive MapTool sketch

- Class adapted to the agent-tool contract (`BaseAgentTool`, namespace `AIOrchestrator.API`,
  agent-oriented XML docs, `Log.LogStep` tracing, explicit `Dispose` hidden from the agent).
- Deterministic "Error: cause — fix — alternative" strings on every failure path instead of
  raw exceptions reaching the LLM.
- Coordinates for ways/relations via Overpass `out center;` (a naive `lat/lon` read misses them).
- Closest-first ordering (haversine) with bounded `maxResults`, radius validation and capping.
- Transient-failure retry + Overpass mirror fallback + Nominatim rate-limit pacing.
- No fabricated claims: custom-tag search does exact value matching, as OverpassQL supports.

## Development

```bash
dotnet build MapTool.csproj
dotnet run --project MapTool.Harness   # deterministic real-API checks (needs internet)
```

The harness writes one line per test to `%TEMP%\maptool_test_results.txt` ending with a `DONE`
marker, so an external watcher can wait deterministically on a run.

## Release

Push a tag `v*` (date version, e.g. `v1.26.09.04`) — the two workflows publish both channels:

- `plugin-release.yml` → GitHub Release with the self-contained `MapTool-<version>.zip`,
  which the hosts (AgentBridge, AIOffice) install into `Tools/MapTool/`.
- `publish.yml` → NuGet package `Graphene.MapTool` (the repository needs the `NUGET_API_KEY`
  secret — same key as the other Graphene-Lab repos).

A plain push is a code-only sync and publishes nothing. The repository must stay **public**
(the hosts download the release zip anonymously).

## License

[Andrea Bruno License 1.4](LICENSE.md)
