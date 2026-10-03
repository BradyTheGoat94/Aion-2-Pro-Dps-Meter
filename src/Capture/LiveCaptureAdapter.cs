using SharpPcap;
using PacketDotNet;
using Aion2DPSPro.Protocol;
using System.Collections.Concurrent;

namespace Aion2DPSPro.Capture;

/// <summary>Capture callbacks only queue bytes. A single worker owns decoding and flow state.</summary>
public sealed class LiveCaptureAdapter : IDisposable
{
    private readonly IAion2Decoder decoder;
    private readonly List<ICaptureDevice> devices=new();
    private readonly TcpStreamReassembler reassembler=new();
    private readonly BlockingCollection<Action> queue=new(4096);
    private readonly Task worker;
    private readonly Dictionary<string,(CurrentClientDecoder Decoder, DateTime Seen)> candidates=new();
    private string? lockedDevice,lockedConversation;
    private DateTime lastPayload,lastEvent;
    private long dropped;
    private bool disposed;
    public Task Completion => worker;
    public event Action<CombatEvent>? EventReceived;
    public event Action<string>? StatusChanged;
    public event Action? PacketCaptured;
    public event Action? DuplicateSuppressed;
    public event Action<string>? FlowLocked;
    public event Action? ConnectionReset;
    public string Health => Interlocked.Read(ref dropped)>0 ? $"Capture overloaded: {Interlocked.Read(ref dropped)} packets dropped; accuracy incomplete" :
        lockedConversation==null ? "Waiting for a validated game conversation" : DateTime.UtcNow-lastPayload>TimeSpan.FromSeconds(15) ? "Capture idle / disconnected" :
        DateTime.UtcNow-lastEvent>TimeSpan.FromSeconds(30) ? "Packets arriving; no decoded events (idle game or decoder mismatch)" : "Capture and decoding active; protocol accuracy unverified";
    public LiveCaptureAdapter(IAion2Decoder decoder)
    {
        this.decoder=decoder;
        reassembler.DuplicateDiscarded += ()=>DuplicateSuppressed?.Invoke();
        reassembler.StreamReset += key =>
        {
            if(selectedDecoder is CurrentClientDecoder selected) selected.ResetStream(key);
            if(decoder is CurrentClientDecoder c) c.ResetStream(key);
            foreach(var candidate in candidates.Values) candidate.Decoder.ResetStream(key);
            StatusChanged?.Invoke("TCP gap/flow reset; incomplete data discarded.");
        };
        worker=Task.Run(()=> { foreach(var action in queue.GetConsumingEnumerable()) try { action(); } catch(Exception ex) { StatusChanged?.Invoke($"Capture/decoder error: {ex.Message}"); } });
    }
    public void Start()
    {
        try
        {
            foreach(var d in CaptureDeviceList.Instance)
            {
                try { d.OnPacketArrival+=OnPacket; d.Open(DeviceModes.Promiscuous,1000); d.Filter="tcp port 13328"; d.StartCapture(); devices.Add(d); }
                catch(Exception ex) { d.OnPacketArrival-=OnPacket; try {d.Close();} catch {} StatusChanged?.Invoke($"Adapter unavailable: {ex.Message}"); }
            }
            StatusChanged?.Invoke(devices.Count==0?"No capture adapter available. Install Npcap.":$"Waiting for validated AION traffic ({devices.Count} adapters).");
        }
        catch(Exception ex) { StatusChanged?.Invoke($"Capture startup failed: {ex.Message}"); }
    }
    private void OnPacket(object sender,PacketCapture capture)
    {
        if(disposed) return;
        var raw=capture.GetPacket();
        var bytes=raw.Data.ToArray(); var link=raw.LinkLayerType;
        var device=(sender as ICaptureDevice)?.Name??"unknown";
        var utc=DateTime.UtcNow;
        try { if(!queue.TryAdd(()=>Process(device,Packet.ParsePacket(link,bytes),utc))) Interlocked.Increment(ref dropped); }
        catch(InvalidOperationException) { }
    }
    private void ResetConnection()
    {
        lockedDevice=null; lockedConversation=null; selectedDecoder=null; reassembler.Reset(); candidates.Clear();
        if(decoder is CurrentClientDecoder c) c.ResetConnection();
        ConnectionReset?.Invoke(); StatusChanged?.Invoke("Connection reset; searching for validated game traffic.");
    }
    private void Process(string device,Packet packet,DateTime utc)
    {
        var tcp=packet.Extract<TcpPacket>(); var ip=packet.Extract<IPPacket>();
        if(tcp==null || ip==null) return;
        string source=$"{ip.SourceAddress}:{tcp.SourcePort}",destination=$"{ip.DestinationAddress}:{tcp.DestinationPort}";
        string conversation=string.CompareOrdinal(source,destination)<=0?$"{source}<>{destination}":$"{destination}<>{source}";
        string direction=$"{device}|{source}>{destination}";
        if(lockedConversation!=null && utc-lastPayload>TimeSpan.FromSeconds(30)) ResetConnection();
        if(lockedConversation!=null && (lockedConversation!=conversation || lockedDevice!=device)) return;
        if(tcp.Synchronize || tcp.Finished || tcp.Reset)
        {
            if(lockedConversation==conversation) ResetConnection();
            else { candidates.Remove(device+conversation); reassembler.Remove(direction); }
            if(tcp.Finished || tcp.Reset) return;
        }
        if(tcp.PayloadData is not {Length:>0}) return;
        PacketCaptured?.Invoke();
        IAion2Decoder active=selectedDecoder??decoder;
        if(lockedConversation==null && decoder is CurrentClientDecoder baseDecoder)
        {
            string key=device+conversation;
            if(!candidates.TryGetValue(key,out var candidate))
            {
                if(candidates.Count>=8) candidates.Remove(candidates.MinBy(x=>x.Value.Seen).Key);
                candidate=(baseDecoder.CreateSibling(),utc);
            }
            candidates[key]=(candidate.Decoder,utc); active=candidate.Decoder;
        }
        if(lockedConversation!=null)lastPayload=utc;
        var chunks=reassembler.Push(direction,unchecked(tcp.SequenceNumber+(tcp.Synchronize?1u:0u)),tcp.PayloadData,utc);
        if(chunks.Count==0) return;
        var decoded=new List<Aion2Decoded>();
        foreach(var chunk in chunks) decoded.AddRange(active is CurrentClientDecoder c?c.DecodeStream(direction,chunk,utc):active.Decode(chunk,utc));
        if(lockedConversation==null)
        {
            if(!decoded.Any(x=>(x.Kind is CombatKind.Damage or CombatKind.Heal) && x.Amount>0)) return;
            lockedDevice=device; lockedConversation=conversation;
            // Continue with the candidate decoder that has already accumulated frame/identity state.
            selectedDecoder=active;
            foreach(var key in candidates.Keys.Where(x=>x!=device+conversation).ToArray()) candidates.Remove(key);
            FlowLocked?.Invoke($"{device} | {conversation}");
        }
        lastPayload=utc;
        if(decoded.Count>0) lastEvent=utc;
        foreach(var d in decoded) EventReceived?.Invoke(new(utc,d.Kind,d.SourceId,d.Source,d.TargetId,d.Target,d.Skill,d.Amount,d.DamageType,d.CurrentHp,d.MaxHp,d.Effect,d.Stacks,d.SourceClass,d.DamageFlags));
    }
    private IAion2Decoder? selectedDecoder;
    public void Dispose()
    {
        if(disposed)return;
        disposed=true;
        foreach(var d in devices) { d.OnPacketArrival-=OnPacket; try { d.StopCapture(); } catch {} try { d.Close(); } catch {} }
        devices.Clear(); queue.CompleteAdding();
        // UI never blocks waiting for work that may post UI updates.
        _=worker.ContinueWith(_=>queue.Dispose());
    }
}
