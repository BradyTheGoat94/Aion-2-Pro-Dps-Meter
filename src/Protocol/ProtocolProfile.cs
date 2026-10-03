using System.IO;
using System.Text.Json;
namespace Aion2DPSPro.Protocol;

public sealed record PacketTag(byte A, byte B);
public sealed record ProtocolProfile(
    string Id,
    string Region,
    string ClientBuild,
    int GamePort,
    IReadOnlyDictionary<string, PacketTag> Tags,
    bool Verified = false)
{
    public static ProtocolProfile SafeGlobalScaffold() => new(
        "GLOBAL-CURRENT-NEEDS-CAPTURE-VALIDATION", "Global", "current", 13328,
        new Dictionary<string,PacketTag>(), false);

    public static ProtocolProfile Load(string path) =>
        JsonSerializer.Deserialize<ProtocolProfile>(File.ReadAllText(path), new JsonSerializerOptions{PropertyNameCaseInsensitive=true})
        ?? throw new InvalidDataException("Invalid protocol profile");
}
public sealed record DecoderDiagnostic(DateTime Utc, string Stage, string Message, int Bytes = 0);

