using System.Collections.Concurrent;

namespace Aion2DPSPro.Protocol;

/// <summary>
/// Reassembles TCP payloads by 4-tuple and sequence number. This prevents AION frames split
/// across TCP segments from being silently lost. It is intentionally protocol-agnostic.
/// </summary>
public sealed class TcpStreamReassembler
{
    private sealed class Flow
    {
        public uint? Next;
        public SortedDictionary<uint, byte[]> Pending { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Flow> flows = new();

    public IEnumerable<byte[]> Push(string flowKey, uint sequence, byte[] payload)
    {
        if (payload.Length == 0) yield break;
        var flow = flows.GetOrAdd(flowKey, _ => new Flow());
        lock (flow)
        {
            if (flow.Next is null) flow.Next = sequence;
            if (sequence < flow.Next.Value) yield break; // duplicate/retransmit already consumed
            flow.Pending[sequence] = payload.ToArray();

            while (flow.Next is uint next && flow.Pending.Remove(next, out var bytes))
            {
                flow.Next = unchecked(next + (uint)bytes.Length);
                yield return bytes;
            }
        }
    }

    public void Reset() => flows.Clear();
}

