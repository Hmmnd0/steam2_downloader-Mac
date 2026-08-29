using System.Reflection;

namespace Steam2Browser;

/// <summary>
/// The per-depot AES-128 keys, lifted from keys.cpp in the extractor source and embedded as
/// "depot hexkey" lines. Only 4758 depots have a key, so a good share of the archive cannot be
/// decrypted at all — worth telling the user before they spend hours downloading one of them.
/// </summary>
public static class DepotKeys
{
    private static readonly Dictionary<int, byte[]> Keys = Load();

    public static int Count => Keys.Count;

    public static bool Has(int depot) => Keys.ContainsKey(depot);

    public static byte[]? Get(int depot) => Keys.GetValueOrDefault(depot);

    /// <summary>Parses a 32-character hex key supplied by the user via --key.</summary>
    public static byte[]? ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.Trim();
        if (hex.Length != 32) return null;

        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }

    private static Dictionary<int, byte[]> Load()
    {
        var result = new Dictionary<int, byte[]>(5000);

        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Steam2Browser.depotkeys.txt");
        if (stream is null) return result;

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;

            int space = line.IndexOf(' ');
            if (space <= 0) continue;

            if (!int.TryParse(line.AsSpan(0, space), out int depot)) continue;

            try { result[depot] = Convert.FromHexString(line.AsSpan(space + 1)); }
            catch (FormatException) { /* skip a malformed line rather than fail startup */ }
        }

        return result;
    }
}
