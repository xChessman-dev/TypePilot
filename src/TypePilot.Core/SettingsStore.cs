using System.Text.Json;

namespace TypePilot.Core;

public sealed class PilotSettings
{
    public bool AutoCorrect { get; set; } = true;
    public bool FixLayout { get; set; } = true;
    public bool GlobalEnabled { get; set; }
    public List<string> AllowedProcesses { get; set; } = ["notepad", "typepilot"];
    public List<string> PersonalWords { get; set; } = [];
    public string AiEndpoint { get; set; } = "http://127.0.0.1:17864";
    public string AiRoot { get; set; } = "F:/DevTools/TypePilotAI";
}

public sealed class SettingsStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public async Task<PilotSettings> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!File.Exists(path)) return new();
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Settings file is too large.");
            return JsonSerializer.Deserialize<PilotSettings>(await File.ReadAllTextAsync(path), Json) ?? new();
        }
        finally { _gate.Release(); }
    }
    public async Task SaveAsync(PilotSettings settings)
    {
        await _gate.WaitAsync();
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            _gate.Release();
        }
    }
    public static IReadOnlyList<string> ParseDictionary(string text)
    {
        if (text.Length > 200000) throw new InvalidDataException("Dictionary limit: 200 KB.");
        var words = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > 5000 || words.Any(w => !TextEngine.IsWord(w))) throw new InvalidDataException("Use one word per line; letters only, 2–48 characters, at most 5000 words.");
        return words.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
