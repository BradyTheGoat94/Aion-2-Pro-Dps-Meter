using System.IO;
using System.Text.Json;

namespace Aion2DPSPro.Storage;

public sealed class FightStore
{
    private readonly string folder;
    public FightStore(string? folder = null)
    {
        this.folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aion2DPSPro", "History");
        Directory.CreateDirectory(this.folder);
    }
    public async Task SaveAsync(MeterSnapshot snapshot)
    {
        var path=Path.Combine(folder,$"{snapshot.StartedUtc:yyyyMMdd_HHmmss_fff}_{snapshot.EncounterId:N}.json");
        var temp=path+".tmp";
        await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(snapshot,new JsonSerializerOptions {WriteIndented=true}));
        File.Move(temp,path,true);
    }
    public async Task<MeterSnapshot?> LoadAsync(string path) => JsonSerializer.Deserialize<MeterSnapshot>(await File.ReadAllTextAsync(path));
    public IEnumerable<string> List() => Directory.EnumerateFiles(folder,"*.json").OrderByDescending(x=>x);
}
