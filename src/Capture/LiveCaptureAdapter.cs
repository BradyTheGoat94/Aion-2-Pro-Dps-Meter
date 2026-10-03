using SharpPcap;
using PacketDotNet;
using Aion2DPSPro.Protocol;

namespace Aion2DPSPro.Capture;

public sealed class LiveCaptureAdapter : IDisposable
{
    private readonly IAion2Decoder decoder;
    private readonly List<ICaptureDevice> devices = new();
    private readonly TcpStreamReassembler reassembler = new();
    private readonly object sync = new();
    private readonly Dictionary<string, (uint Seq, int Len, int Hash)> recent = new();
    private string? lockedDevice;
    private string? lockedConversation;

    public event Action<CombatEvent>? EventReceived;
    public event Action<string>? StatusChanged;
    public event Action? PacketCaptured;
    public event Action? DuplicateSuppressed;
    public event Action<string>? FlowLocked;

    public LiveCaptureAdapter(IAion2Decoder decoder) => this.decoder = decoder;

    public void Start()
    {
        var available = CaptureDeviceList.Instance;
        if (available.Count == 0) { StatusChanged?.Invoke("No packet capture adapter found. Install Npcap."); return; }
        foreach (var d in available)
        {
            try
            {
                d.OnPacketArrival += OnPacket;
                d.Open(DeviceModes.Promiscuous, 1000);
                d.Filter = "tcp port 13328";
                d.StartCapture();
                devices.Add(d);
            }
            catch { try { d.Close(); } catch { } }
        }
        StatusChanged?.Invoke($"Waiting for active AION 2 TCP/13328 conversation ({devices.Count} adapters armed).");
    }

    private void OnPacket(object sender, PacketCapture capture)
    {
        var raw = capture.GetPacket();
        var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        var tcp = packet.Extract<TcpPacket>();
        if (tcp?.PayloadData is not { Length: > 0 }) return;
        var ip = packet.Extract<IPPacket>();
        if (ip == null) return;

        string device = (sender as ICaptureDevice)?.Name ?? "unknown";
        string source = $"{ip.SourceAddress}:{tcp.SourcePort}";
        string destination = $"{ip.DestinationAddress}:{tcp.DestinationPort}";
        string direction = $"{source}>{destination}";
        string conversation = string.CompareOrdinal(source, destination) <= 0
            ? $"{source}<>{destination}"
            : $"{destination}<>{source}";

        int hash = HashCode.Combine(tcp.PayloadData.Length,
            tcp.PayloadData[0],
            tcp.PayloadData.Length > 1 ? tcp.PayloadData[^1] : 0);

        lock (sync)
        {
            if (lockedDevice == null)
            {
                lockedDevice = device;
                lockedConversation = conversation;
                FlowLocked?.Invoke($"{device} | {conversation}");
                StatusChanged?.Invoke($"Locked to AION 2 bidirectional conversation: {conversation}");
            }

            if (device != lockedDevice || conversation != lockedConversation) return;

            string duplicateKey = direction;
            if (recent.TryGetValue(duplicateKey, out var last) &&
                last.Seq == tcp.SequenceNumber &&
                last.Len == tcp.PayloadData.Length &&
                last.Hash == hash)
            {
                DuplicateSuppressed?.Invoke();
                return;
            }
            recent[duplicateKey] = (tcp.SequenceNumber, tcp.PayloadData.Length, hash);
        }

        PacketCaptured?.Invoke();

        // Direction stays in the reassembly key so the two TCP sequence spaces never mix.
        foreach (var chunk in reassembler.Push(direction, tcp.SequenceNumber, tcp.PayloadData))
        foreach (var d in decoder.Decode(chunk, DateTime.UtcNow))
            EventReceived?.Invoke(new CombatEvent(DateTime.UtcNow, d.Kind, d.SourceId, d.Source,
                d.TargetId, d.Target, d.Skill, d.Amount, d.DamageType,
                d.CurrentHp, d.MaxHp, d.Effect, d.Stacks, d.SourceClass, d.DamageFlags));
    }

    public void Dispose()
    {
        foreach (var d in devices)
        {
            try { d.StopCapture(); } catch { }
            try { d.Close(); } catch { }
        }
        devices.Clear();
    }
}

