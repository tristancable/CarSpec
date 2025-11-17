using System.Reflection;
using System.Text.Json;

namespace CarSpec.Utils;

public static class DtcCatalog
{
    private static readonly Lazy<Dictionary<string, string>> _map =
        new(() => LoadMap(), isThreadSafe: true);

    public static string GetName(string? rawCode)
    {
        var code = Normalize(rawCode);
        if (string.IsNullOrEmpty(code)) return "—";

        if (_map.Value.TryGetValue(code, out var name))
            return name;

        // Heuristic: manufacturer-specific ranges (P1xxx, P3xxx, U1xxx)
        if (IsMfrSpecific(code))
            return "Manufacturer-specific trouble code";

        return "Unknown DTC";
    }

    public static bool TryGetName(string? rawCode, out string name)
    {
        var code = Normalize(rawCode);
        if (!string.IsNullOrEmpty(code) && _map.Value.TryGetValue(code, out name))
            return true;

        name = string.Empty;
        return false;
    }

    private static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        s = s.Trim().ToUpperInvariant();
        // Accept forms like "P0420", "p0420", "  P0420  "
        // If someone passed just digits (0420), we won’t guess a system letter.
        return s;
    }

    private static bool IsMfrSpecific(string code)
        => code.Length == 5 &&
           (code[0] is 'P' or 'U') &&
           code[1] == '1'; // P1xxx / U1xxx (common heuristic)

    private static Dictionary<string, string> LoadMap()
    {
        // If you embed as an *Embedded resource*:
        // Build Action: Embedded resource
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames()
                         .FirstOrDefault(n => n.EndsWith("dtc_sae.json", StringComparison.OrdinalIgnoreCase));

        if (resName is not null)
        {
            using var s = asm.GetManifestResourceStream(resName)!;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(s)
                   ?? new Dictionary<string, string>();
        }

        // Or, if you keep it as a content file (e.g., wwwroot/data/dtc_sae.json),
        // swap the above with File.ReadAllText on a known path.
        return new Dictionary<string, string>();
    }
}