using System.Globalization;
using AIOrchestrator;
using AIOrchestrator.API;

namespace MapToolTests;

/// <summary>Deterministic checks for MapTool against the real OpenStreetMap services
/// (Nominatim geocoding, Overpass POI, OSRM routing). Needs internet. Every test verifies
/// the tool OUTCOME (row format, coordinates, ordering), not just the absence of errors.</summary>
static class Program
{
    static int ok, fail, total;

    // External watcher waits for the DONE marker:
    // powershell -Command "$f='<temp>\maptool_test_results.txt'; while(-not (Get-Content $f -Raw -ErrorAction SilentlyContinue) -match 'DONE'){ Start-Sleep 1 }; Get-Content $f -Tail 20"
    static readonly string ResultsFile = Path.Combine(Path.GetTempPath(), "maptool_test_results.txt");

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "agent")
        {
            RunAgentTest();
            return;
        }

        File.WriteAllText(ResultsFile, $"RUN {DateTime.Now:HH:mm:ss}\n");
        Log.IsEnabled = true;
        WriteResult("STARTED");

        // Render the agent-facing definitions exactly as the orchestrator would emit them.
        var defs = UISupportGeneric.Analyzer.GetToolDefinitions(typeof(MapTool));
        var defsFile = Path.Combine(Path.GetTempPath(), "maptool_tooldefs.txt");
        File.WriteAllText(defsFile, defs);
        Console.WriteLine($"Tool definitions ({defs.Length} chars) -> {defsFile}");

        Run(1, "validation: empty geocode query", () =>
        {
            var rows = new MapTool().SearchAddress("");
            return rows.Length == 1 && rows[0].StartsWith("Error:") ? null : "expected an Error row for an empty query";
        });

        Run(2, "validation: invalid coordinates", () =>
        {
            using var tool = new MapTool();
            var badLat = tool.SearchPoi(PoiCategory.Restaurant, 99, 9.19);
            if (badLat.Length != 1 || !badLat[0].StartsWith("Error:")) return "invalid latitude not rejected";
            var badRadius = tool.SearchPoi(PoiCategory.Restaurant, 45.4642, 9.19, radiusMeters: 0);
            if (badRadius.Length != 1 || !badRadius[0].StartsWith("Error:")) return "radius 0 not rejected";
            var badRoute = tool.FindRoute(999, 0, 45.46, 9.19);
            if (!badRoute.StartsWith("Error:")) return "invalid route coordinates not rejected";
            return null;
        });

        var duomo = new double?[2];
        Run(3, "geocode: Piazza del Duomo, Milano", () =>
        {
            var rows = new MapTool().SearchAddress("Piazza del Duomo, Milano", maxResults: 3);
            if (rows.Length == 0 || rows[0].StartsWith("Error:")) return $"geocode failed: {(rows.Length > 0 ? rows[0] : "no rows")}";
            var c = FindCoords(rows);
            if (c == null) return $"no 'coords:' segment in row: {rows[0]}";
            if (Math.Abs(c.Value.Lat - 45.4642) > 0.02 || Math.Abs(c.Value.Lon - 9.1900) > 0.02)
                return $"unexpected Duomo coordinates: {c.Value.Lat}, {c.Value.Lon}";
            duomo[0] = c.Value.Lat;
            duomo[1] = c.Value.Lon;
            return null;
        });

        Run(4, "POI: restaurants around Duomo, closest first", () =>
        {
            var rows = new MapTool().SearchPoi(PoiCategory.Restaurant, duomo[0]!.Value, duomo[1]!.Value, radiusMeters: 1500, maxResults: 8);
            if (rows.Length == 0 || rows[0].StartsWith("Error:")) return $"poi search failed: {(rows.Length > 0 ? rows[0] : "no rows")}";
            Console.WriteLine($"  sample: {rows[0]}");
            var distances = rows.Select(r => DistanceMeters(r)).ToArray();
            if (distances.Any(d => d == null)) return $"unparseable distance in row: {rows[Array.IndexOf(distances, null)]}";
            for (var i = 1; i < distances.Length; i++)
                if (distances[i]! < distances[i - 1]!) return $"rows not sorted by distance: {rows[i - 1]} | {rows[i]}";
            return null;
        });

        Run(5, "POI by tags: hotels around Duomo", () =>
        {
            var rows = new MapTool().SearchPoiByTags("tourism", "hotel", duomo[0]!.Value, duomo[1]!.Value, radiusMeters: 1500, maxResults: 5);
            if (rows.Length == 0 || rows[0].StartsWith("Error:")) return $"tag search failed: {(rows.Length > 0 ? rows[0] : "no rows")}";
            return rows.Any(r => r.Contains("tourism:hotel")) ? null : $"missing tourism:hotel label in row: {rows[0]}";
        });

        Run(6, "route: walking Duomo → Teatro alla Scala", () =>
        {
            var result = new MapTool().FindRoute(duomo[0]!.Value, duomo[1]!.Value, 45.46746, 9.18949, TravelMode.Walking);
            if (result.StartsWith("Error:")) return result;
            if (!result.Contains("by walking")) return "missing travel mode in route result";
            if (!result.Contains("\n1. ")) return "missing step-by-step instructions";
            Console.WriteLine($"  sample: {result.Replace("\n", "\n          ")}");
            return null;
        });

        Run(7, "route: driving Duomo → Linate airport", () =>
        {
            using var tool = new MapTool();
            var result = tool.FindRoute(duomo[0]!.Value, duomo[1]!.Value, 45.4494, 9.2763, TravelMode.Driving);
            if (result.StartsWith("Error:")) return result;
            if (!result.Contains("by car")) return "missing 'by car' in route result";
            if (!result.Contains("\n1. ")) return "missing step-by-step instructions";
            return null;
        });

        Run(8, "route: cycling Duomo → Stazione Centrale", () =>
        {
            using var tool = new MapTool();
            var result = tool.FindRoute(duomo[0]!.Value, duomo[1]!.Value, 45.4860, 9.2043, TravelMode.Cycling);
            if (result.StartsWith("Error:")) return result;
            if (!result.Contains("by cycling")) return "missing 'by cycling' in route result";
            if (!result.Contains("\n1. ")) return "missing step-by-step instructions";
            return null;
        });

        Run(9, "POI categories: pharmacy, ATM and museum around Duomo", () =>
        {
            using var tool = new MapTool();
            foreach (var cat in new[] { PoiCategory.Pharmacy, PoiCategory.ATM, PoiCategory.Museum })
            {
                var rows = tool.SearchPoi(cat, duomo[0]!.Value, duomo[1]!.Value, radiusMeters: 1500, maxResults: 3);
                if (rows.Length == 0 || rows[0].StartsWith("Error:"))
                    return $"{cat} search failed: {(rows.Length > 0 ? rows[0] : "no rows within 1500 m of the Duomo")}";
            }
            return null;
        });

        Run(10, "no matches return empty arrays (not errors)", () =>
        {
            using var tool = new MapTool();
            var poi = tool.SearchPoiByTags("amenity", "zz_nonexistent_tag", duomo[0]!.Value, duomo[1]!.Value, radiusMeters: 600);
            if (poi.Length != 0) return $"expected an empty array for a nonexistent tag, got: {string.Join(" | ", poi)}";
            var addr = tool.SearchAddress("zzqqxxww nonexistent locality 987654321", maxResults: 3);
            if (addr.Length != 0) return $"expected an empty array for a nonsense address, got: {string.Join(" | ", addr)}";
            return null;
        });

        Run(11, "OSM fidelity: every PoiCategory maps to canonical clauses (offline)", () =>
        {
            foreach (var cat in Enum.GetValues<PoiCategory>())
            {
                var clauses = MapTool.BuildPoiFilters(cat);
                if (clauses.Count == 0) return $"{cat} maps to no OSM tag clause";
                if (clauses.Any(c => string.IsNullOrWhiteSpace(c.Key) || string.IsNullOrWhiteSpace(c.Value)))
                    return $"{cat} maps to an empty clause";
            }

            // Spot checks of canonical (language-independent) OSM values.
            string? Find(PoiCategory cat, string key) => MapTool.BuildPoiFilters(cat).FirstOrDefault(c => c.Key == key).Value;
            var checks = new (PoiCategory Cat, string Key, string Value)[]
            {
                (PoiCategory.Restaurant, "amenity", "restaurant"),
                (PoiCategory.Bakery, "shop", "bakery"),
                (PoiCategory.DryCleaning, "shop", "dry_cleaning"),
                (PoiCategory.Pharmacy, "amenity", "pharmacy"),
                (PoiCategory.ATM, "amenity", "atm"),
                (PoiCategory.ChargingStation, "amenity", "charging_station"),
                (PoiCategory.CurrencyExchange, "amenity", "bureau_de_change"),
                (PoiCategory.Gym, "leisure", "fitness_centre"),
                (PoiCategory.Playground, "leisure", "playground"),
                (PoiCategory.Hotel, "tourism", "hotel"),
                (PoiCategory.Theatre, "amenity", "theatre"),
                (PoiCategory.Cinema, "amenity", "cinema"),
                (PoiCategory.SubwayStation, "railway", "subway_entrance"),
                (PoiCategory.TramStop, "railway", "tram_stop"),
                (PoiCategory.Airport, "aeroway", "aerodrome"),
                (PoiCategory.CommunityCenter, "amenity", "community_centre")
            };
            foreach (var (cat, key, value) in checks)
            {
                var got = Find(cat, key);
                if (got != value) return $"{cat}: expected {key}={value}, got {key}={got}";
            }
            if (!MapTool.BuildPoiFilters(PoiCategory.Theatre).Any(c => c is ("tourism", "theatre")))
                return "Theatre should also match tourism=theatre (regional tagging)";
            return null;
        });

        Console.WriteLine($"\n{ok}/{total} passed, {fail} failed {(fail == 0 ? "ALL OK!" : "")}");
        WriteResult($"DONE {ok}/{total} passed, {fail} failed");
        Environment.ExitCode = fail == 0 ? 0 : 1;
    }

    static void Run(int num, string name, Func<string?> body)
    {
        total++;
        Console.Write($"T{num}: {name}... ");
        try
        {
            var problem = body();
            if (problem == null)
            {
                ok++;
                Console.WriteLine("PASS");
                WriteResult($"T{num} PASS");
            }
            else
            {
                fail++;
                Console.WriteLine($"FAIL: {problem}");
                WriteResult($"T{num} FAIL: {problem}");
            }
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine($"CRASH {ex.GetType().Name}: {ex.Message}");
            WriteResult($"T{num} CRASH {ex.GetType().Name}: {ex.Message}");
        }
    }

    static void WriteResult(string line) => File.AppendAllText(ResultsFile, line + Environment.NewLine);

    static (double Lat, double Lon)? FindCoords(string[] rows)
    {
        foreach (var row in rows)
        {
            var idx = row.IndexOf("coords: ", StringComparison.Ordinal);
            if (idx < 0) continue;
            var parts = row[(idx + 8)..].Trim().Split(' ');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                return (lat, lon);
        }
        return null;
    }

    static double? DistanceMeters(string row)
    {
        var fields = row.Split('|');
        if (fields.Length < 2) return null;
        var d = fields[1].Trim();
        if (d.EndsWith(" m", StringComparison.Ordinal) &&
            double.TryParse(d[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return m;
        if (d.EndsWith(" km", StringComparison.Ordinal) &&
            double.TryParse(d[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var km)) return km * 1000;
        return null;
    }

    /// <summary>Agent-driven check: the LLM must drive MapTool through a realistic multi-step
    /// map request (geocode → POI → route) via AgentHarness. Run with: dotnet run -- agent.
    /// Verifies the LLM-facing contract (method discoverability, parameter cross-references)
    /// by inspecting the MapTool LogStep lines afterwards.</summary>
    static void RunAgentTest()
    {
        Console.WriteLine("Agent-driven MapTool test (DeepSeekBridge)...");
        var testDir = Path.Combine(Path.GetTempPath(), "MapAgent_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(testDir);
        Setup.DocumentsPath = testDir;
        Setup.ProviderConfig = ProviderConfigs.Get("DeepSeekBridge");
        Log.IsEnabled = true;

        const string prompt =
            "You are in Milan. Give the user walking directions from Piazza del Duomo to Teatro alla Scala:\n" +
            "distance, estimated time and the main steps of the route. Reply in Italian.";

        try
        {
            var orch = new AgentHarness("DeepSeekBridge");
            using var done = new ManualResetEventSlim();
            AgentHarness.AgentProgressEventArgs? final = null;
            orch.AgentProgress += (_, e) =>
            {
                if (e.State is AgentHarness.AgentState.Completed or AgentHarness.AgentState.Failed)
                { final = e; done.Set(); }
            };
            var task = Task.Run(() => orch.ExecuteAction(prompt, new[] { "MapTool" }, maxIterations: 40));
            done.Wait(TimeSpan.FromSeconds(120));
            var result = task.GetAwaiter().GetResult();

            var logFile = Log.CurrentLogFile ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", Environment.ProcessId + ".txt");
            Console.WriteLine($"State: {final?.State} | Iterations: {result.Iterations} | ms: {result.TotalElapsedMs}");
            Console.WriteLine($"Log file: {logFile}");
            Console.WriteLine($"Agent error: {(string.IsNullOrEmpty(result.Error) ? "(none)" : result.Error)}");

            var allLines = File.Exists(logFile) ? File.ReadAllLines(logFile).ToList() : [];
            var actions = allLines.Where(l => l.Contains("MapTool.")).Select(l => l.Trim()).ToList();
            Console.WriteLine("MapTool actions in log:");
            foreach (var a in actions) Console.WriteLine($"  {a}");

            var routeOk = actions.Any(a => a.Contains("FindRoute") && a.Contains("Walking"));
            var completed = final?.State == AgentHarness.AgentState.Completed && string.IsNullOrEmpty(result.Error);

            Console.WriteLine($"\nChecks: route(walking)={routeOk} completed={completed}");
            Console.WriteLine(routeOk && completed ? "AGENT TEST PASS" : "AGENT TEST FAIL");
            Environment.ExitCode = routeOk && completed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AGENT TEST CRASH {ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }
}
