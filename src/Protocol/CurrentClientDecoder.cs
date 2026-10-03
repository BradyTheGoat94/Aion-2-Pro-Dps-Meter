namespace Aion2DPSPro.Protocol;

public sealed class CurrentClientDecoder : IAion2Decoder
{
    private readonly ProtocolProfile profile;
    private readonly StreamFramer framer = new();
    private readonly PacketDispatcher dispatcher;
    public string ProfileId => profile.Id;
    public event Action<DecoderDiagnostic>? Diagnostic;
    public event Action<string>? ValidationRecord;

    public CurrentClientDecoder(ProtocolProfile? profile = null)
    {
        this.profile = profile ?? ProtocolProfile.SafeGlobalScaffold();
        dispatcher = new PacketDispatcher(this.profile);
        framer.Diagnostic += d => Diagnostic?.Invoke(d);
        dispatcher.Diagnostic += d => Diagnostic?.Invoke(d);
        dispatcher.ValidationRecord += s => ValidationRecord?.Invoke(s);
    }

    public IEnumerable<Aion2Decoded> Decode(byte[] payload, DateTime utc)
    {
        foreach (var frame in framer.Push(payload, utc))
            foreach (var evt in dispatcher.Dispatch(frame, utc))
                yield return evt;
    }
}


