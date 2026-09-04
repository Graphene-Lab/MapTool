using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AIOrchestrator.API;

/// <summary>Map lookups for agent use: find addresses, places of interest around a point, and travel routes.
/// Coordinates are decimal degrees (lat -90..90, lon -180..180, period as decimal separator), e.g. 45.46420 9.19000 for Milan.
/// Start with search_address() to turn a name or address into coordinates, then pass the coordinates of a result row to search_poi() or find_route().</summary>
public class MapTool : BaseAgentTool, IDisposable
{
    private const string NominatimEndpoint = "https://nominatim.openstreetmap.org/search";
    private const string OsrmEndpoint = "https://router.project-osrm.org/route/v1";
    private const string UserAgent = "Graphene-MapTool/1.0 (https://github.com/Graphene-Lab/MapTool; map consultation agent tool)";

    // Deterministic fallback chain: when one public Overpass endpoint is down or overloaded
    // the next mirror is tried. All serve the same API.
    private static readonly string[] OverpassEndpoints =
    {
        "https://overpass-api.de/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter",
        "https://overpass.private.coffee/api/interpreter"
    };

    private readonly HttpClient _http;
    private DateTime _lastNominatimRequest = DateTime.MinValue;
    private DateTime _lastOverpassRequest = DateTime.MinValue;
    private readonly object _nominatimGate = new();
    private readonly object _overpassGate = new();

    /// <summary>Parameterless constructor for agent activation. Each method works standalone — no setup call needed.</summary>
    public MapTool()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    void IDisposable.Dispose() => _http.Dispose();

    #region Public methods

    /// <summary>Finds a place, address or establishment by name and returns its coordinates (geocoding).
    /// Accepts natural-language queries: a city ("Milan"), a full address ("Via Roma 10, Milano"), a landmark ("Colosseum") or an establishment name ("Ristorante Da Mario, Firenze").
    /// Including the city in the query gives more precise results.</summary>
    /// <param name="query">Place, address or establishment to find (free text, up to 200 characters).</param>
    /// <param name="maxResults">Maximum number of matches to return (1-10, default 5). Results are ordered by relevance.</param>
    /// <param name="lat">Optional latitude that makes results near this point rank first (from another search_address() row).</param>
    /// <param name="lon">Optional longitude that makes results near this point rank first (from another search_address() row). Provide it together with <paramref name="lat"/>.</param>
    /// <returns>One line per match: "N. display name | type: type | coords: lat lon".
    /// Coordinates are decimal degrees, period as decimal separator.
    /// Empty array when nothing matches; single "Error: ..." line when the call failed.</returns>
    public string[] SearchAddress(string query, int maxResults = 5, double? lat = null, double? lon = null)
    {
        Log.LogStep($"MapTool.SearchAddress: '{query}' (max={maxResults}, bias={lat?.ToString(CultureInfo.InvariantCulture)} {lon?.ToString(CultureInfo.InvariantCulture)})");
        try
        {
            if (string.IsNullOrWhiteSpace(query))
                return Error($"the query is empty. Describe the place to find, e.g. \"Piazza del Duomo, Milano\".");
            if (query.Length > 200)
                return Error($"the query is {query.Length} characters. Keep it under 200 characters, e.g. \"Via Roma 10, Milano\".");
            if (lat.HasValue != lon.HasValue)
                return Error("only one of 'lat'/'lon' was provided. Provide both coordinates or neither.");

            var limit = Math.Clamp(maxResults, 1, 10);
            if (lat is double la && lon is double lo)
            {
                if (!IsValidCoordinates(la, lo))
                    return Error($"the bias coordinates ({FormatCoord(la)}, {FormatCoord(lo)}) are outside the valid ranges. Latitude must be between -90 and 90, longitude between -180 and 180.");
            }

            PaceNominatim();
            var url = $"{NominatimEndpoint}?format=jsonv2&addressdetails=1&limit={limit}&q={Uri.EscapeDataString(query.Trim())}";
            if (lat is double biasLat && lon is double biasLon)
                url += $"&lat={biasLat.ToString(CultureInfo.InvariantCulture)}&lon={biasLon.ToString(CultureInfo.InvariantCulture)}";

            var json = FetchJson(url, retries: 3, retryAfter429Ms: 2500);
            if (json == null)
                return Error("the address service is not responding. Try again later, or make the query more specific (add the city).");

            var rows = ParseNominatimRows(json, limit);
            Log.LogStep($"MapTool.SearchAddress: {rows.Length} match(es)");
            return rows;
        }
        catch (Exception ex)
        {
            Log.LogStep($"MapTool.SearchAddress: failed — {ex.Message}");
            return Error($"unexpected failure — {ex.Message}. Try search_address() with a different query or call it again later.");
        }
    }

    /// <summary>Lists places of a chosen category within a radius around a point, closest first.
    /// Uses the coordinates of a search_address() result row as center.
    /// To filter by any other OpenStreetMap tag (e.g. cuisine), use search_poi_by_tags().</summary>
    /// <param name="category">Category of places to list (e.g. Restaurant, Pharmacy, Hotel, Museum, FuelStation, Parking, ATM).</param>
    /// <param name="lat">Latitude of the center point (decimal degrees, from a search_address() row, e.g. 45.46420).</param>
    /// <param name="lon">Longitude of the center point (decimal degrees, from a search_address() row, e.g. 9.19000).</param>
    /// <param name="radiusMeters">Search radius in meters around the point (1-25000, default 1000). Use 500-1000 for a local search, more for a wider area.</param>
    /// <param name="maxResults">Maximum number of places to return (1-25, default 10).</param>
    /// <returns>One line per place, closest first: "N. name | distance | tagkey:tagvalue | address | coords: lat lon".
    /// Empty array when no place of that category is within the radius; single "Error: ..." line when the call failed.</returns>
    public string[] SearchPoi(PoiCategory category, double lat, double lon, int radiusMeters = 1000, int maxResults = 10)
    {
        Log.LogStep($"MapTool.SearchPoi: '{category}' around ({FormatCoord(lat)}, {FormatCoord(lon)}) r={radiusMeters}m");
        var filter = BuildPoiFilter(category);
        if (filter == null)
            return Error($"category '{category}' is not supported. Use one of the allowed category values (e.g. Restaurant, Pharmacy, ATM).");
        return SearchPoiCore(filter.Value.Key, filter.Value.Value, lat, lon, radiusMeters, maxResults);
    }

    /// <summary>Lists places matching a specific OpenStreetMap tag (key = value) within a radius around a point, closest first.
    /// Use this when search_poi() has no category for the filter, e.g. cuisine=italian, wheelchair=yes, opening_hours=24/7, building=church.</summary>
    /// <param name="osmKey">OpenStreetMap tag key (lowercase, no spaces), e.g. "cuisine", "wheelchair", "opening_hours".</param>
    /// <param name="osmValue">Exact tag value to match, e.g. "italian", "yes", "24/7".</param>
    /// <param name="lat">Latitude of the center point (decimal degrees, from a search_address() row).</param>
    /// <param name="lon">Longitude of the center point (decimal degrees, from a search_address() row).</param>
    /// <param name="radiusMeters">Search radius in meters around the point (1-25000, default 1000).</param>
    /// <param name="maxResults">Maximum number of places to return (1-25, default 10).</param>
    /// <returns>Same row format as search_poi(): "N. name | distance | key:value | address | coords: lat lon", closest first.
    /// Empty array when nothing matches within the radius; single "Error: ..." line when the call failed.</returns>
    public string[] SearchPoiByTags(string osmKey, string osmValue, double lat, double lon, int radiusMeters = 1000, int maxResults = 10)
    {
        Log.LogStep($"MapTool.SearchPoiByTags: '{osmKey}'='{osmValue}' around ({FormatCoord(lat)}, {FormatCoord(lon)}) r={radiusMeters}m");
        if (string.IsNullOrWhiteSpace(osmKey) || string.IsNullOrWhiteSpace(osmValue))
            return Error("the tag key or value is empty. Provide both, e.g. osmKey=\"cuisine\" osmValue=\"italian\".");
        return SearchPoiCore(osmKey.Trim().ToLowerInvariant(), osmValue.Trim(), lat, lon, radiusMeters, maxResults);
    }

    /// <summary>Calculates the travel route between two points and returns its steps.
    /// Start and end points come from search_address()/search_poi() rows (the "coords: lat lon" values).</summary>
    /// <param name="fromLat">Latitude of the start point (decimal degrees, e.g. 45.46420).</param>
    /// <param name="fromLon">Longitude of the start point (decimal degrees, e.g. 9.19000).</param>
    /// <param name="toLat">Latitude of the destination point (decimal degrees).</param>
    /// <param name="toLon">Longitude of the destination point (decimal degrees).</param>
    /// <param name="mode">Travel mode: Driving (car), Walking (on foot) or Cycling (bicycle). Default Driving.</param>
    /// <returns>Multi-line text: "Distance: X km — Estimated time: N min (mode)." followed by one numbered step per line, e.g. "1. Head east on Via Torino (350 m)".
    /// Single line starting with "Error:" when the route cannot be calculated.</returns>
    public string FindRoute(double fromLat, double fromLon, double toLat, double toLon, TravelMode mode = TravelMode.Driving)
    {
        Log.LogStep($"MapTool.FindRoute: ({FormatCoord(fromLat)}, {FormatCoord(fromLon)}) → ({FormatCoord(toLat)}, {FormatCoord(toLon)}) mode={mode}");
        try
        {
            if (!IsValidCoordinates(fromLat, fromLon))
                return Fail($"the start coordinates ({FormatCoord(fromLat)}, {FormatCoord(fromLon)}) are outside the valid ranges. Latitude must be between -90 and 90, longitude between -180 and 180.");
            if (!IsValidCoordinates(toLat, toLon))
                return Fail($"the destination coordinates ({FormatCoord(toLat)}, {FormatCoord(toLon)}) are outside the valid ranges. Latitude must be between -90 and 90, longitude between -180 and 180.");

            var profile = mode switch
            {
                TravelMode.Walking => "foot",
                TravelMode.Cycling => "bike",
                _ => "driving"
            };

            var url = $"{OsrmEndpoint}/{profile}/{FormatCoord(fromLon)},{FormatCoord(fromLat)};{FormatCoord(toLon)},{FormatCoord(toLat)}?overview=false&steps=true";
            var json = FetchJson(url, retries: 3, retryAfter429Ms: 1500);
            if (json == null)
                return Fail("the route service is not responding. Try again later.");

            var result = ParseOsrmRoute(json, mode);
            Log.LogStep($"MapTool.FindRoute: {result.Replace('\n', ' ')[..Math.Min(result.Length, 120)]}");
            return result;
        }
        catch (Exception ex)
        {
            Log.LogStep($"MapTool.FindRoute: failed — {ex.Message}");
            return Fail($"unexpected failure — {ex.Message}. Check the coordinates and call find_route() again.");
        }
    }

    #endregion

    #region Shared POI pipeline

    private string[] SearchPoiCore(string key, string value, double lat, double lon, int radiusMeters, int maxResults)
    {
        try
        {
            if (!IsValidCoordinates(lat, lon))
                return Error($"the center coordinates ({FormatCoord(lat)}, {FormatCoord(lon)}) are outside the valid ranges. Latitude must be between -90 and 90, longitude between -180 and 180.");
            if (radiusMeters is < 1 or > 25000)
                return Error($"the radius is {radiusMeters} m. It must be between 1 and 25000 meters (500-5000 recommended).");
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                return Error("the tag key or value is empty. Provide both, e.g. osmKey=\"amenity\" osmValue=\"restaurant\".");

            var limit = Math.Clamp(maxResults, 1, 25);
            var safeKey = EscapeQueryValue(key);
            var safeValue = EscapeQueryValue(value);

            // "out center" returns coordinates for ways/relations too (nodes carry lat/lon directly).
            var query = $"[out:json][timeout:30];(nwr(around:{radiusMeters},{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)})[\"{safeKey}\"=\"{safeValue}\"];);out center;";
            PaceOverpass();

            string? json = null;
            string? lastError = null;
            foreach (var endpoint in OverpassEndpoints)
            {
                var mirrorUrl = $"{endpoint}?data={Uri.EscapeDataString(query)}";
                json = FetchJson(mirrorUrl, retries: 3, retryAfter429Ms: 4000);
                if (json != null && TryGetOverpassError(json, out var remark))
                {
                    lastError = $"the places service reported: {remark}. ";
                    json = null;
                    continue;
                }
                if (json != null) break;
                lastError = $"the places service is not responding ({endpoint}). ";
            }
            if (json == null)
                return Error((lastError ?? "") + "Try again later, or reduce the search radius.");

            var hits = ParseOverpassHits(json, lat, lon, limit);
            if (hits.Count == 0)
            {
                Log.LogStep($"MapTool: no '{key}={value}' within {radiusMeters} m of ({FormatCoord(lat)}, {FormatCoord(lon)})");
                return [];
            }

            var rows = hits.Select((h, i) =>
                $"{i + 1}. {h.Name} | {FormatDistance(h.DistanceMeters)} | {h.Key}:{h.Value} | {h.Address} | coords: {FormatCoord(h.Latitude)} {FormatCoord(h.Longitude)}").ToArray();
            Log.LogStep($"MapTool: {rows.Length} '{key}={value}' result(s) near ({FormatCoord(lat)}, {FormatCoord(lon)})");
            return rows;
        }
        catch (Exception ex)
        {
            Log.LogStep($"MapTool.SearchPoiCore: failed — {ex.Message}");
            return Error($"unexpected failure — {ex.Message}. Try a smaller radius or a broader category, or call search_address() to confirm the center coordinates.");
        }
    }

    private static List<PoiHit> ParseOverpassHits(string json, double centerLat, double centerLon, int maxResults)
    {
        var hits = new List<PoiHit>();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("elements", out var elements))
            return hits;

        foreach (var el in elements.EnumerateArray())
        {
            double lat = 0, lon = 0;
            if (el.TryGetProperty("lat", out var nodeLat))
            {
                lat = nodeLat.GetDouble();
                lon = el.GetProperty("lon").GetDouble();
            }
            else if (el.TryGetProperty("center", out var center) &&
                     center.TryGetProperty("lat", out var cLat) && center.TryGetProperty("lon", out var cLon))
            {
                lat = cLat.GetDouble();
                lon = cLon.GetDouble();
            }
            if (lat == 0 && lon == 0)
                continue; // element without usable coordinates — skip

            var name = "Unnamed";
            var address = "no address";
            string? key = null, value = null;
            if (el.TryGetProperty("tags", out var tags))
            {
                if (tags.TryGetProperty("name", out var tagName))
                    name = tagName.GetString() ?? name;
                else if (tags.TryGetProperty("brand", out var brand))
                    name = brand.GetString() ?? name;

                // Deterministic label: the first of the well-known POI keys present in the tags.
                foreach (var known in new[] { "amenity", "shop", "tourism", "leisure", "historic", "office", "railway", "aeroway" })
                {
                    if (tags.TryGetProperty(known, out var tagVal))
                    {
                        key = known;
                        value = tagVal.GetString() ?? known;
                        break;
                    }
                }

                var street = GetStringProp(tags, "addr:street");
                var number = GetStringProp(tags, "addr:housenumber");
                var city = GetStringProp(tags, "addr:city");
                var house = street != null ? (number != null ? $"{street} {number}" : street) : number;
                address = house != null ? (city != null ? $"{house}, {city}" : house) : (city ?? "no address");
            }

            hits.Add(new PoiHit
            {
                Name = name,
                Key = key ?? "poi",
                Value = value ?? "yes",
                Address = address,
                Latitude = lat,
                Longitude = lon,
                DistanceMeters = Haversine(centerLat, centerLon, lat, lon)
            });
        }

        // Closest first (stable for equal distances); duplicates (node + building for the same
        // place) can appear and are kept — each carries its own coordinates.
        return hits.OrderBy(h => h.DistanceMeters).ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase).Take(maxResults).ToList();
    }

    private static string[] ParseNominatimRows(string json, int limit)
    {
        var rows = new List<string>(limit);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var display = item.TryGetProperty("display_name", out var d) ? d.GetString() : null;
            if (display == null) continue;
            var type = item.TryGetProperty("type", out var t) ? t.GetString()
                : (item.TryGetProperty("addresstype", out var at) ? at.GetString() : "place");
            if (!item.TryGetProperty("lat", out var latEl) || !item.TryGetProperty("lon", out var lonEl))
                continue;
            if (!double.TryParse(latEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(lonEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                continue;

            rows.Add($"{rows.Count + 1}. {display} | type: {type} | coords: {FormatCoord(lat)} {FormatCoord(lon)}");
        }
        return rows.ToArray();
    }

    private static string ParseOsrmRoute(string json, TravelMode mode)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var codeEl) && codeEl.GetString() != "Ok")
            return "Error: the route service could not find a route between these two points (it may be unreachable, e.g. over water or off-road). Use search_address() to confirm both places, or try a different travel mode.";

        if (!root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array || routes.GetArrayLength() == 0)
            return "Error: the route service returned no route between these two points. Use search_address() to confirm both places, or try a different travel mode.";

        var route = routes[0];
        var distance = route.TryGetProperty("distance", out var dEl) ? dEl.GetDouble() : 0;
        var duration = route.TryGetProperty("duration", out var tEl) ? tEl.GetDouble() : 0;

        // OSRM steps carry no instruction text: the direction is composed deterministically
        // from the maneuver type/modifier and the street name.
        var steps = new List<string>();
        if (route.TryGetProperty("legs", out var legs) && legs.ValueKind == JsonValueKind.Array)
        {
            foreach (var leg in legs.EnumerateArray())
            {
                if (!leg.TryGetProperty("steps", out var stepArr) || stepArr.ValueKind != JsonValueKind.Array) continue;
                foreach (var step in stepArr.EnumerateArray())
                {
                    if (!step.TryGetProperty("maneuver", out var maneuver)) continue;
                    var stepDistance = step.TryGetProperty("distance", out var sd) ? sd.GetDouble() : 0;
                    if (stepDistance <= 0) continue; // depart/arrive bookends carry no walking distance
                    var type = GetStringProp(maneuver, "type") ?? "continue";
                    var modifier = GetStringProp(maneuver, "modifier");
                    var street = GetStringProp(step, "name");
                    steps.Add($"{steps.Count + 1}. {BuildInstruction(type, modifier, street)} ({FormatDistance(stepDistance)})");
                }
            }
        }

        var modeLabel = mode switch { TravelMode.Walking => "walking", TravelMode.Cycling => "cycling", _ => "car" };
        var text = $"Distance: {FormatDistance(distance)} — Estimated time: {FormatDuration(duration)} (by {modeLabel}).";
        if (steps.Count > 0)
            text += "\n" + string.Join("\n", steps);
        return text;
    }

    private static string BuildInstruction(string type, string? modifier, string? street)
    {
        var mod = string.IsNullOrWhiteSpace(modifier) ? null : modifier;
        var onStreet = string.IsNullOrWhiteSpace(street) ? null : street;
        var head = "Continue";
        var joiner = " onto "; // entering a different road

        switch (type)
        {
            case "depart": head = "Head"; joiner = " on "; break;              // the starting road
            case "turn": head = "Turn"; break;
            case "end of road": head = "At the end of the road, turn"; break;
            case "merge": head = "Merge"; break;
            case "fork": head = "Keep"; break;
            case "use lane": head = "Keep"; joiner = " on "; break;
            case "continue":
            case "new name":
            case "notification": head = "Continue"; joiner = " on "; break;    // same road
            case "roundabout":
            case "rotary":
            case "exit roundabout": head = "At the roundabout, take the exit"; mod = null; break;
            case "on ramp": head = "Take the ramp"; break;
            case "off ramp": head = "Take the exit ramp"; break;
            case "exit": head = "Take the exit"; break;
            default: head = "Continue"; joiner = " on "; break;
        }

        var text = head;
        if (mod != null)
            text += $" {mod}";
        if (onStreet != null)
            text += $"{joiner}{onStreet}";
        return text;
    }

    private static bool TryGetOverpassError(string json, out string remark)
    {
        remark = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("remark", out var el)) return false;
            var text = el.GetString();
            if (string.IsNullOrEmpty(text)) return false;
            // Overpass failure remarks (timeouts, overload, rate limits) mean "no usable data",
            // unlike warnings that accompany a partial but valid result set.
            if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("runtime error", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("too fast", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("reduced", StringComparison.OrdinalIgnoreCase))
            {
                remark = text;
                return true;
            }
        }
        catch (JsonException)
        {
            // not JSON after all — caller treats it as a failed mirror
        }
        return false;
    }

    #endregion

    #region HTTP + helpers

    private string? FetchJson(string url, int retries, int retryAfter429Ms)
    {
        // Bounded deterministic retry for transient failures (network hiccups, busy servers,
        // HTTP 429/5xx). The caller maps a final null to an actionable error message.
        for (var attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                using var response = _http.GetAsync(url).GetAwaiter().GetResult();
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt + 1 < retries)
                        System.Threading.Thread.Sleep(retryAfter429Ms);
                    continue;
                }
                if (response.IsSuccessStatusCode)
                {
                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (body.StartsWith('[') || body.StartsWith('{'))
                        return body;
                    // A JSON service answering plain text is a proxy error page, not data.
                }
            }
            catch
            {
                // transient network failure — retry below
            }
            if (attempt + 1 < retries)
                System.Threading.Thread.Sleep(600 * (attempt + 1));
        }
        return null;
    }

    private void PaceNominatim()
    {
        // Nominatim public service policy: at most ~1 request/second. Kept per instance.
        Pace(_nominatimGate, ref _lastNominatimRequest, TimeSpan.FromMilliseconds(1100));
    }

    private void PaceOverpass()
    {
        // Overpass public mirrors expect spaced requests; a short quiet interval between
        // consecutive POI searches reduces rate-limit/overload refusals.
        Pace(_overpassGate, ref _lastOverpassRequest, TimeSpan.FromMilliseconds(1500));
    }

    private static void Pace(object gate, ref DateTime lastRequest, TimeSpan minInterval)
    {
        lock (gate)
        {
            var elapsed = DateTime.UtcNow - lastRequest;
            var wait = minInterval - elapsed;
            if (lastRequest != DateTime.MinValue && wait > TimeSpan.Zero)
                System.Threading.Thread.Sleep(wait);
            lastRequest = DateTime.UtcNow;
        }
    }

    private static (string Key, string Value)? BuildPoiFilter(PoiCategory category) => category switch
    {
        // Food & Drink
        PoiCategory.Restaurant => ("amenity", "restaurant"),
        PoiCategory.FastFood => ("amenity", "fast_food"),
        PoiCategory.Bar => ("amenity", "bar"),
        PoiCategory.Cafe => ("amenity", "cafe"),
        PoiCategory.Pub => ("amenity", "pub"),
        PoiCategory.IceCream => ("amenity", "ice_cream"),
        PoiCategory.Bakery => ("shop", "bakery"),
        PoiCategory.Butcher => ("shop", "butcher"),

        // Shops
        PoiCategory.Supermarket => ("shop", "supermarket"),
        PoiCategory.ConvenienceStore => ("shop", "convenience"),
        PoiCategory.ClothingStore => ("shop", "clothes"),
        PoiCategory.ElectronicsStore => ("shop", "electronics"),
        PoiCategory.Bookstore => ("shop", "books"),
        PoiCategory.HardwareStore => ("shop", "hardware"),
        PoiCategory.SportsShop => ("shop", "sports"),
        PoiCategory.ToyStore => ("shop", "toys"),
        PoiCategory.Optician => ("shop", "optician"),
        PoiCategory.Laundry => ("shop", "laundry"),
        PoiCategory.DryCleaning => ("shop", "dry_cleaning"),
        PoiCategory.Hairdresser => ("shop", "hairdresser"),
        PoiCategory.BeautySalon => ("shop", "beauty"),
        PoiCategory.CarDealership => ("shop", "car"),
        PoiCategory.CarRepair => ("shop", "car_repair"),
        PoiCategory.MotorcycleShop => ("shop", "motorcycle"),
        PoiCategory.BicycleShop => ("shop", "bicycle"),
        PoiCategory.Florist => ("shop", "florist"),
        PoiCategory.JewelryStore => ("shop", "jewelry"),
        PoiCategory.FurnitureStore => ("shop", "furniture"),

        // Public services
        PoiCategory.Pharmacy => ("amenity", "pharmacy"),
        PoiCategory.Hospital => ("amenity", "hospital"),
        PoiCategory.Doctors => ("amenity", "doctors"),
        PoiCategory.Dentist => ("amenity", "dentist"),
        PoiCategory.Veterinary => ("amenity", "veterinary"),
        PoiCategory.School => ("amenity", "school"),
        PoiCategory.Kindergarten => ("amenity", "kindergarten"),
        PoiCategory.College => ("amenity", "college"),
        PoiCategory.University => ("amenity", "university"),
        PoiCategory.Library => ("amenity", "library"),
        PoiCategory.PostOffice => ("amenity", "post_office"),
        PoiCategory.Police => ("amenity", "police"),
        PoiCategory.FireStation => ("amenity", "fire_station"),
        PoiCategory.PublicToilet => ("amenity", "toilets"),
        PoiCategory.ATM => ("amenity", "atm"),
        PoiCategory.Bank => ("amenity", "bank"),
        PoiCategory.FuelStation => ("amenity", "fuel"),
        PoiCategory.Parking => ("amenity", "parking"),

        // Tourism & leisure
        PoiCategory.Hotel => ("tourism", "hotel"),
        PoiCategory.Hostel => ("tourism", "hostel"),
        PoiCategory.GuestHouse => ("tourism", "guest_house"),
        PoiCategory.CampSite => ("tourism", "camp_site"),
        PoiCategory.Museum => ("tourism", "museum"),
        PoiCategory.ArtGallery => ("tourism", "art_gallery"),
        PoiCategory.Theatre => ("amenity", "theatre"),
        PoiCategory.Cinema => ("amenity", "cinema"),
        PoiCategory.Monument => ("historic", "monument"),
        PoiCategory.Attraction => ("tourism", "attraction"),
        PoiCategory.Zoo => ("tourism", "zoo"),
        PoiCategory.Aquarium => ("tourism", "aquarium"),
        PoiCategory.Viewpoint => ("tourism", "viewpoint"),
        PoiCategory.Park => ("leisure", "park"),
        PoiCategory.Nightclub => ("amenity", "nightclub"),
        PoiCategory.Casino => ("amenity", "casino"),

        // Transport
        PoiCategory.Station => ("railway", "station"),
        PoiCategory.Airport => ("aeroway", "aerodrome"),
        PoiCategory.BicycleRental => ("amenity", "bicycle_rental"),
        PoiCategory.CarRental => ("amenity", "car_rental"),
        PoiCategory.BusStation => ("amenity", "bus_station"),
        PoiCategory.FerryTerminal => ("amenity", "ferry_terminal"),
        PoiCategory.SubwayStation => ("railway", "subway_entrance"),

        // Other
        PoiCategory.PlaceOfWorship => ("amenity", "place_of_worship"),
        PoiCategory.CommunityCenter => ("amenity", "community_centre"),
        PoiCategory.SocialClub => ("amenity", "social_centre"),

        _ => null
    };

    private static bool IsValidCoordinates(double lat, double lon) => lat is >= -90 and <= 90 && lon is >= -180 and <= 180;

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6371000;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    private static string FormatDistance(double meters) => meters >= 1000
        ? (meters / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " km"
        : meters.ToString("0", CultureInfo.InvariantCulture) + " m";

    private static string FormatDuration(double seconds)
    {
        var minutes = (int)Math.Round(seconds / 60);
        if (minutes < 60) return $"{minutes} min";
        return $"{minutes / 60} h {minutes % 60} min";
    }

    private static string FormatCoord(double value) => value.ToString("0.00000", CultureInfo.InvariantCulture);

    private static string EscapeQueryValue(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string? GetStringProp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var el) ? el.GetString() : null;

    private static string[] Error(string detail) => [$"Error: {detail}"];

    private static string Fail(string detail) => $"Error: {detail}";

    private sealed class PoiHit
    {
        public required string Name { get; init; }
        public required string Key { get; init; }
        public required string Value { get; init; }
        public required string Address { get; init; }
        public required double Latitude { get; init; }
        public required double Longitude { get; init; }
        public required double DistanceMeters { get; init; }
    }

    #endregion
}

/// <summary>Categories of places searchable with search_poi() — food, shops, public services, tourism, transport and more.</summary>
public enum PoiCategory
{
    // Food & Drink
    Restaurant, FastFood, Bar, Cafe, Pub, IceCream, Bakery, Butcher,
    // Shops
    Supermarket, ConvenienceStore, ClothingStore, ElectronicsStore, Bookstore, HardwareStore,
    SportsShop, ToyStore, Optician, Laundry, DryCleaning, Hairdresser, BeautySalon,
    CarDealership, CarRepair, MotorcycleShop, BicycleShop, Florist, JewelryStore, FurnitureStore,
    // Public services
    Pharmacy, Hospital, Doctors, Dentist, Veterinary, School, Kindergarten, College, University,
    Library, PostOffice, Police, FireStation, PublicToilet, ATM, Bank, FuelStation, Parking,
    // Tourism & leisure
    Hotel, Hostel, GuestHouse, CampSite, Museum, ArtGallery, Theatre, Cinema, Monument,
    Attraction, Zoo, Aquarium, Viewpoint, Park, Nightclub, Casino,
    // Transport
    Station, Airport, BicycleRental, CarRental, BusStation, FerryTerminal, SubwayStation,
    // Other
    PlaceOfWorship, CommunityCenter, SocialClub
}

/// <summary>Travel mode for find_route().</summary>
public enum TravelMode
{
    /// <summary>By car.</summary>
    Driving,
    /// <summary>On foot.</summary>
    Walking,
    /// <summary>By bicycle.</summary>
    Cycling
}
