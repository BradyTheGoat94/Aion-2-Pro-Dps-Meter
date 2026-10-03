namespace Aion2DPSPro.Protocol;

public sealed class CurrentClientDecoder : IAion2Decoder
{
    private readonly ProtocolProfile profile;
    private readonly Dictionary<string, StreamFramer> framers = new();
    private PacketDispatcher dispatcher;
    public string ProfileId => profile.Id;
    public event Action<DecoderDiagnostic>? Diagnostic;
    public event Action<string>? ValidationRecord;

    public CurrentClientDecoder(ProtocolProfile? profile = null)
    {
        this.profile = profile ?? ProtocolProfile.SafeGlobalScaffold();
        dispatcher = new PacketDispatcher(this.profile);

        dispatcher.Diagnostic += d => Diagnostic?.Invoke(d);
        dispatcher.ValidationRecord += s => ValidationRecord?.Invoke(s);
    }

    public CurrentClientDecoder CreateSibling()
    {
        var child=new CurrentClientDecoder(profile);
        child.Diagnostic += d => Diagnostic?.Invoke(d);
        child.ValidationRecord += s => ValidationRecord?.Invoke(s);
        return child;
    }
    public void ResetStream(string stream) => framers.Remove(stream);
    public void ResetConnection() { framers.Clear(); dispatcher = new PacketDispatcher(profile); WireDispatcher(); }
    private void WireDispatcher()
    {
        dispatcher.Diagnostic += d => Diagnostic?.Invoke(d);
        dispatcher.ValidationRecord += s => ValidationRecord?.Invoke(s);
    }
    public IEnumerable<Aion2Decoded> Decode(byte[] payload, DateTime utc) => DecodeStream("default",payload,utc);
    public IEnumerable<Aion2Decoded> DecodeStream(string stream, byte[] payload, DateTime utc)
    {
        if(!framers.TryGetValue(stream,out var framer))
        {
            framers[stream]=framer=new StreamFramer();
            framer.Diagnostic += d => Diagnostic?.Invoke(d);
        }
        foreach(var frame in framer.Push(payload,utc))
            foreach(var evt in dispatcher.Dispatch(frame,utc)) yield return evt;
    }
}
