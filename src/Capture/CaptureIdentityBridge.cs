namespace Aion2DPSPro.Capture;

/// <summary>Shares exact observed entity identities within one game conversation, including duplicate Npcap adapter observations.</summary>
public sealed class CaptureIdentityBridge
{
    private sealed record Entry(CombatEvent Event, DateTime Seen);
    private sealed record NpcEntry(string Name, DateTime Seen);
    private readonly Dictionary<(string Scope,long Id),Entry> names=new();
    private readonly Dictionary<(string Scope,long Id),NpcEntry> npcs=new();
    private static string KeyScope(string scope)
    {
        if(string.IsNullOrWhiteSpace(scope)) return scope;
        int split=scope.IndexOf('|');
        if(split<=0) return scope;
        string adapter=scope[..split];
        return adapter.Contains("NPF_",StringComparison.OrdinalIgnoreCase)?scope[(split+1)..]:scope;
    }
    public void Observe(string scope,CombatEvent e)
    {
        Prune(e.Utc);
        var keyScope=KeyScope(scope);
        if(e.Kind==CombatKind.Despawn)
        {
            names.Remove((keyScope,e.SourceId));
            npcs.Remove((keyScope,e.SourceId));
            return;
        }

        if(e.Kind==CombatKind.PlayerName && e.SourceId>0 && !string.IsNullOrWhiteSpace(e.Source) && !e.Source.StartsWith("Actor "))
        {
            names[(keyScope,e.SourceId)]=new(e,e.Utc);
            if(names.Count>4096)names.Remove(names.MinBy(x=>x.Value.Seen).Key);
            return;
        }

        // TargetHp is only emitted by the protocol dispatcher after a concrete
        // mob identity/name is known. Share that exact NPC identity across
        // duplicate adapters and sibling game sockets just like player names.
        if(e.Kind==CombatKind.TargetHp && e.TargetId>0 && !string.IsNullOrWhiteSpace(e.Target) &&
           !e.Target.StartsWith("Target ") && !e.Target.StartsWith("Actor "))
        {
            npcs[(keyScope,e.TargetId)]=new(e.Target,e.Utc);
            if(npcs.Count>4096)npcs.Remove(npcs.MinBy(x=>x.Value.Seen).Key);
        }
    }
    public IReadOnlyList<CombatEvent> Identities(string scope,DateTime utc)
    {
        Prune(utc);
        return names.Where(x=>x.Key.Scope==KeyScope(scope)).Select(x=>x.Value.Event with {Utc=utc}).ToArray();
    }
    public CombatEvent Resolve(string scope,CombatEvent e)
    {
        Prune(e.Utc);
        var keyScope=KeyScope(scope);
        if(e.SourceId!=0 && (string.IsNullOrWhiteSpace(e.Source)||e.Source.StartsWith("Actor ")))
        {
            if(names.TryGetValue((keyScope,e.SourceId),out var source))
                e=e with {Source=source.Event.Source,SourceClass=e.SourceClass=="Unknown"?source.Event.SourceClass:e.SourceClass,SourceIdentityConfirmed=true};
            else if(npcs.TryGetValue((keyScope,e.SourceId),out var npcSource))
                e=e with {Source=npcSource.Name,SourceClass="NPC",SourceIdentityConfirmed=false};
        }

        if(e.TargetId!=0 && (string.IsNullOrWhiteSpace(e.Target)||e.Target.StartsWith("Target ")||e.Target.StartsWith("Actor ")))
        {
            if(names.TryGetValue((keyScope,e.TargetId),out var target))
                e=e with {Target=target.Event.Source};
            else if(npcs.TryGetValue((keyScope,e.TargetId),out var npcTarget))
                e=e with {Target=npcTarget.Name};
        }
        return e;
    }
    private void Prune(DateTime utc)
    {
        foreach(var key in names.Where(x=>utc-x.Value.Seen>TimeSpan.FromMinutes(10)).Select(x=>x.Key).ToArray())names.Remove(key);
        foreach(var key in npcs.Where(x=>utc-x.Value.Seen>TimeSpan.FromMinutes(10)).Select(x=>x.Key).ToArray())npcs.Remove(key);
    }
    public void Clear(){names.Clear();npcs.Clear();}
}
