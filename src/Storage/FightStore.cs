using System.IO;
using System.Text.Json;

namespace Aion2DPSPro.Storage;

public sealed class FightStore
{
    private readonly string folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Aion2DPSPro", "History");

    public FightStore() => Directory.CreateDirectory(folder);

    public async Task SaveAsync(MeterSnapshot snapshot)
    {
        var name = Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.json");
        await File.WriteAllTextAsync(name, JsonSerializer.Serialize(snapshot,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public IEnumerable<string> List() =>
        Directory.EnumerateFiles(folder, "*.json").OrderByDescending(x => x);
}

