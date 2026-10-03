namespace Aion2DPSPro.Capture;

/// <summary>Shares exact observed entity identities within one game conversation, including duplicate Npcap adapter observations.</summary>
public sealed class CaptureIdentityBridge
{
    private sealed record Entry(CombatEvent Event, DateTime Seen);
    private readonly Dictionary<(string Scope,long Id),Entry> names=new();
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
        if(e.Kind==CombatKind.Despawn) {names.Remove((KeyScope(scope),e.SourceId));return;}
        if(e.Kind!=CombatKind.PlayerName || e.SourceId<=0 || string.IsNullOrWhiteSpace(e.Source) || e.Source.StartsWith("Actor "))return;
        names[(KeyScope(scope),e.SourceId)]=new(e,e.Utc);
        if(names.Count>4096)names.Remove(names.MinBy(x=>x.Value.Seen).Key);
    }
    public IReadOnlyList<CombatEvent> Identities(string scope,DateTime utc)
    {
        Prune(utc);
        return names.Where(x=>x.Key.Scope==KeyScope(scope)).Select(x=>x.Value.Event with {Utc=utc}).ToArray();
    }
    public CombatEvent Resolve(string scope,CombatEvent e)
    {
        Prune(e.Utc);
        if(e.SourceId!=0 && (string.IsNullOrWhiteSpace(e.Source)||e.Source.StartsWith("Actor ")) && names.TryGetValue((KeyScope(scope),e.SourceId),out var source))
            e=e with {Source=source.Event.Source,SourceClass=e.SourceClass=="Unknown"?source.Event.SourceClass:e.SourceClass};
        if(e.TargetId!=0 && (string.IsNullOrWhiteSpace(e.Target)||e.Target.StartsWith("Target ")||e.Target.StartsWith("Actor ")) && names.TryGetValue((KeyScope(scope),e.TargetId),out var target))e=e with {Target=target.Event.Source};
        return e;
    }
    private void Prune(DateTime utc)
    {
        foreach(var key in names.Where(x=>utc-x.Value.Seen>TimeSpan.FromMinutes(10)).Select(x=>x.Key).ToArray())names.Remove(key);
    }
    public void Clear()=>names.Clear();
}
