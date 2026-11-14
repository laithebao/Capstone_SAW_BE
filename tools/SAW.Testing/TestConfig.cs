using System.Text.RegularExpressions;

namespace SAW.Testing;

public static class TestConfig
{
    // A data parser, not a shell: no variable expansion, command execution or escape rewriting.
    public static Dictionary<string, string> Parse(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var equals = line.IndexOf('=');
            if (equals <= 0 || !Regex.IsMatch(line[..equals].Trim(), "^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new FormatException("Invalid .env entry. Use KEY=value, with optional matching quotes.");
            var value = line[(equals + 1)..].Trim();
            if (value.StartsWith('\'') || value.StartsWith('"'))
            {
                if (value.Length < 2 || value[^1] != value[0]) throw new FormatException("Unclosed quote in .env entry.");
                value = value[1..^1];
            }
            result[line[..equals].Trim()] = value;
        }
        return result;
    }

    public static Dictionary<string, string?> Load(string file)
    {
        var result = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(x => (string)x.Key, x => (string?)x.Value, StringComparer.Ordinal);
        if (File.Exists(file))
            foreach (var entry in Parse(File.ReadAllText(file)))
                // Empty template fields must not hide CI-provided Cloudinary secrets or EDGE_PATH.
                if (entry.Value.Length > 0 || !result.TryGetValue(entry.Key, out var existing) || string.IsNullOrEmpty(existing))
                    result[entry.Key] = entry.Value;
        return result;
    }

    public static string Required(IDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidOperationException($"Missing {key}. Run init and edit .env.test.local, or provide it through the environment.");
}
