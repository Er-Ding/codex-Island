using System.Text.Json;

namespace CodexIsland.Core;

public sealed record IslandSettings
{
    public int Version { get; init; } = 1;
    public SavedPosition? Position { get; init; }
    public Dictionary<string, IslandSize> DisplaySizes { get; init; } = [];
    public string? CodexPath { get; init; }
}

public sealed class SettingsStore(string? filePath = null)
{
    public string FilePath { get; } = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexIsland", "settings.json");
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public IslandSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 1024 * 1024) return new();
            var value = JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(FilePath), Options);
            if (value?.Version != 1) return new();
            return value with
            {
                Position = value.Position?.IsValid == true ? value.Position : null,
                DisplaySizes = (value.DisplaySizes ?? []).Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && Enum.IsDefined(kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value)
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(IslandSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
