# MapTool — notes for coding agents

> Read this file first. This repo is one tool plugin of the AIOffice solution: it is compiled on
> its own and loaded by the hosts from `Tools/MapTool/` at runtime.

## What this is

Map lookups for the agent: address geocoding, places of interest around a location, travel
routes — over the OpenStreetMap services (Nominatim, Overpass, OSRM).

## Read before you edit

| If you touch… | Read first |
|---|---|
| anything | [PLUGIN-GUARDRAILS](../AIOrchestrator/docs-dev/PLUGIN-GUARDRAILS.md) — the contract shared by the 12 plugins; the file lives in the sibling AIOrchestrator repo (private), reachable from the solution checkout |
| a tool method or its XML docs | [AGENT_TOOLS_GUIDE](../AIOrchestrator/API/AGENT_TOOLS_GUIDE.md) |
| the tool checklist | [CHECKLIST_COMPLIANCE.md](CHECKLIST_COMPLIANCE.md) |

## Non-negotiables

1. The class derives from `BaseAgentTool` and keeps `[ToolPurpose(ToolPurpose.Maps)]`.
2. Public methods and their XML docs **are** the agent's surface: no public method without docs,
   and a public method is never renamed (the model calls it by that name).
3. AnyCPU, no `RuntimeIdentifier`, `GenerateDocumentationFile` stays on.
4. The plugin folder never carries `AIOrchestrator.dll`; no state is written into `Tools/`.
5. A new metadata goes in a class attribute (or a `virtual` member with a default) — never an
   `abstract` member: the dlls already shipped must keep loading.

## Where things are

- `MapTool.cs` — the tool class; its public methods are what the model can call
- `MapTool.Harness/` — nested harness, excluded from the library compile
- `CHECKLIST_COMPLIANCE.md` — how the tool was measured against the agent-tool checklist

## Shipping

Publishes a GitHub release zip **and** a NuGet package; AgentBridge ships it in every release
(added to the fetch list on 2026-11-19). AIOffice does not build it into its dev `Tools/`.
