namespace Aion2DPSPro.Protocol;

/// <summary>Bounded, overlap-aware TCP reassembly with modular sequence comparisons.</summary>
public sealed class TcpStreamReassembler
{
    private sealed class Flow { public uint? Next; public DateTime Seen; public List<(uint Seq,byte[] Data)> Pending=new(); public DateTime? Gap; }
    private readonly Dictionary<string,Flow> flows=new();
    private readonly object gate=new();
    public TimeSpan GapTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public event Action<string>? StreamReset;
    public IReadOnlyList<byte[]> Push(string flowKey,uint sequence,byte[] payload) => Push(flowKey,sequence,payload,DateTime.UtcNow);
    public IReadOnlyList<byte[]> Push(string flowKey,uint sequence,byte[] payload,DateTime utc)
    {
        var output=new List<byte[]>();
        lock(gate)
        {
            foreach(var stale in flows.Where(x=>utc-x.Value.Seen>TimeSpan.FromMinutes(2)).Select(x=>x.Key).ToArray()) { flows.Remove(stale); StreamReset?.Invoke(stale); }
            if(payload.Length==0) return output;
            if(!flows.TryGetValue(flowKey,out var f))
            {
                if(flows.Count>=64) { var oldest=flows.MinBy(x=>x.Value.Seen).Key; flows.Remove(oldest); StreamReset?.Invoke(oldest); }
                flows[flowKey]=f=new Flow();
            }
            f.Seen=utc; f.Next??=sequence;
            int delta=unchecked((int)(sequence-f.Next.Value));
            if(delta<0)
            {
                long overlap=-(long)delta;
                if(overlap>=payload.Length) return output;
                payload=payload.AsSpan((int)overlap).ToArray(); sequence=f.Next.Value;
            }
            if(!f.Pending.Any(x=>x.Seq==sequence && x.Data.Length>=payload.Length)) f.Pending.Add((sequence,payload.ToArray()));
            while(true)
            {
                int index=f.Pending.FindIndex(x=>unchecked((int)(x.Seq-f.Next.Value))<=0 && unchecked((int)(x.Seq-f.Next.Value))+(long)x.Data.Length>0);
                if(index<0) break;
                var item=f.Pending[index]; f.Pending.RemoveAt(index);
                int skip=(int)unchecked(f.Next.Value-item.Seq);
                var bytes=item.Data.AsSpan(skip).ToArray(); output.Add(bytes); f.Next=unchecked(f.Next.Value+(uint)bytes.Length);
                f.Pending.RemoveAll(x=>unchecked((int)(x.Seq-f.Next.Value))+(long)x.Data.Length<=0);
            }
            if(f.Pending.Count==0) f.Gap=null;
            else
            {
                f.Gap??=utc;
                if(utc-f.Gap.Value>=GapTimeout || f.Pending.Count>256 || f.Pending.Sum(x=>(long)x.Data.Length)>4_000_000)
                {
                    // Drop damaged bytes; never concatenate across missing data. Restart at the next received segment.
                    f.Pending.Clear(); f.Next=null; f.Gap=null; StreamReset?.Invoke(flowKey);
                }
            }
        }
        return output;
    }
    public void Remove(string key) { lock(gate) { flows.Remove(key); StreamReset?.Invoke(key); } }
    public void Reset() { lock(gate) flows.Clear(); }
}
