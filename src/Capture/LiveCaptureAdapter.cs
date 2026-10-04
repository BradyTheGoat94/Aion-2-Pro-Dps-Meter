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
    private readonly CaptureIdentityBridge identityBridge=new();
    private string? lockedScope;
    private readonly TcpStreamReassembler reassembler=new();
    private readonly BlockingCollection<Action> queue=new(4096);
    private readonly Task worker;
    private readonly Dictionary<string,(CurrentClientDecoder Decoder, DateTime Seen)> candidates=new();
    private string? lockedDevice,lockedConversation;
    private DateTime lastPayload,lastEvent;
    private long dropped;
    private volatile bool disposed;
    public Task Completion => worker;
    public event Action<string>? ValidationRecord;
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
        lockedDevice=null; lockedConversation=null; lockedScope=null; selectedDecoder=null; reassembler.Reset(); candidates.Clear(); identityBridge.Clear();
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
        var server=tcp.SourcePort==13328?ip.SourceAddress:ip.DestinationAddress;
        var local=tcp.SourcePort==13328?ip.DestinationAddress:ip.SourceAddress;
        string scope=$"{device}|{local}|{server}";
        if(lockedConversation!=null && utc-lastPayload>TimeSpan.FromSeconds(30)) ResetConnection();
        if(lockedScope!=null && scope!=lockedScope) return;
        bool primary=lockedConversation==conversation && lockedDevice==device;
        string candidateKey=device+conversation;
        if(tcp.Synchronize || tcp.Finished || tcp.Reset)
        {
            if(primary) {ResetConnection();primary=false;}
            else {candidates.Remove(candidateKey);reassembler.Remove(direction);}
            if(tcp.Finished || tcp.Reset)return;
        }
        if(tcp.PayloadData is not {Length:>0})return;
        PacketCaptured?.Invoke();
        IAion2Decoder active=primary?selectedDecoder??decoder:decoder;
        if(!primary && decoder is CurrentClientDecoder baseDecoder)
        {
            if(!candidates.TryGetValue(candidateKey,out var candidate))
            {
                if(candidates.Count>=8)candidates.Remove(candidates.MinBy(x=>x.Value.Seen).Key);
                candidate=(baseDecoder.CreateSibling(),utc);
            }
            candidates[candidateKey]=(candidate.Decoder,utc);active=candidate.Decoder;
        }
        if(primary)lastPayload=utc;
        var chunks=reassembler.Push(direction,unchecked(tcp.SequenceNumber+(tcp.Synchronize?1u:0u)),tcp.PayloadData,utc);
        if(chunks.Count==0)return;
        var decoded=new List<CombatEvent>();
        foreach(var chunk in chunks)
        foreach(var d in active is CurrentClientDecoder c?c.DecodeStream(direction,chunk,utc):active.Decode(chunk,utc))
            decoded.Add(new(utc,d.Kind,d.SourceId,d.Source,d.TargetId,d.Target,d.Skill,d.Amount,d.DamageType,d.CurrentHp,d.MaxHp,d.Effect,d.Stacks,d.SourceClass,d.DamageFlags,SourceIdentityConfirmed:d.SourceIdentityConfirmed));
        foreach(var e in decoded.Where(x=>x.Kind is CombatKind.PlayerName or CombatKind.TargetHp || primary&&x.Kind==CombatKind.Despawn))identityBridge.Observe(scope,e);
        foreach(var e in decoded.Where(x=>x.Kind==CombatKind.PlayerName))ValidationRecord?.Invoke($"{utc:O}|tag=captureIdentity|entity={e.SourceId}|name={e.Source}|scope={scope}|conversation={conversation}|primary={primary}");
        if(lockedConversation==null)
        {
            if(!decoded.Any(x=>(x.Kind is CombatKind.Damage or CombatKind.Heal)&&x.Amount>0))return;
            lockedDevice=device;lockedConversation=conversation;lockedScope=scope;selectedDecoder=active;primary=true;
            // Publish identities captured before combat lock, including identity-only sibling connections.
            foreach(var identity in identityBridge.Identities(scope,utc))EventReceived?.Invoke(identity);
            FlowLocked?.Invoke($"{device} | {conversation}");
        }
        if(!primary)
        {
            foreach(var identity in decoded.Where(x=>x.Kind==CombatKind.PlayerName))EventReceived?.Invoke(identity);
            return;
        }
        lastPayload=utc;if(decoded.Count>0)lastEvent=utc;
        foreach(var e in decoded)
        {
            var resolved=identityBridge.Resolve(scope,e);
            if(e.Kind is CombatKind.Damage or CombatKind.Heal)
                ValidationRecord?.Invoke($"{utc:O}|tag=resolvedCombat|entity={resolved.SourceId}|name={resolved.Source}|target={resolved.TargetId}|scope={scope}|conversation={conversation}");
            EventReceived?.Invoke(resolved);
        }
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
