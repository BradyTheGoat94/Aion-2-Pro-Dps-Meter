namespace Aion2DPSPro;

/// <summary>Thread-safe event aggregation. Unknown identities remain separate by entity ID.</summary>
public sealed class CombatEngine
{
    private readonly object gate = new();
    private readonly Func<DateTime> clock;
    private readonly Dictionary<long, Identity> identities = new();
    private readonly Dictionary<long, long> owners = new();
    private readonly Dictionary<long,long> entityKeys=new();
    private long nextEntityKey=-1;
    private readonly List<Encounter> history = new();
    private Encounter current = new();
    private Encounter overall = new();
    private DateTime? latestUtc;
    public bool PreviewMode { get; set; }
    public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan BossInactivityTimeout { get; set; } = TimeSpan.FromSeconds(120);
    public event Action<MeterSnapshot>? EncounterCompleted;
    public CombatEngine(Func<DateTime>? clock = null) => this.clock = clock ?? (() => DateTime.UtcNow);
    public IReadOnlyList<MeterSnapshot> History { get { lock(gate) return history.Select(x => Build(x, MeterCategory.Damage, false)).ToArray(); } }

    public void Apply(CombatEvent e)
    {
        MeterSnapshot? completed = null;
        lock(gate)
        {
            // Clamp late events for lifecycle decisions; preserve their original timestamps in history.
            var t = latestUtc.HasValue && e.Utc < latestUtc ? latestUtc.Value : e.Utc;
            latestUtc = t;
            if (Expired(t)) completed = Finish("Inactivity");
            if (e.Kind == CombatKind.Zone) { completed = Finish("Zone changed") ?? completed; foreach(var id in identities.Keys.ToArray()) entityKeys[id]=nextEntityKey--; identities.Clear(); owners.Clear(); }
            else if (e.Kind == CombatKind.CombatEnd) completed = Finish("Combat ended") ?? completed;
            else
            {
                if (e.Kind == CombatKind.Despawn)
                {
                    identities.Remove(e.SourceId); owners.Remove(e.SourceId); entityKeys[e.SourceId]=nextEntityKey--;
                    foreach(var child in owners.Where(x=>x.Value==e.SourceId).Select(x=>x.Key).ToArray()) owners.Remove(child);
                }
                else if (e.Kind == CombatKind.Ownership && e.SourceId != 0 && e.OwnerId != 0 && e.SourceId != e.OwnerId)
                    owners[e.SourceId] = e.OwnerId;
                else if (e.SourceId != 0) Remember(e.SourceId, e.Source, e.SourceClass);
                if (e.TargetId != 0) Remember(e.TargetId, e.Target, "Unknown");
                bool activity = (e.Kind is CombatKind.Damage or CombatKind.Heal) && e.Amount > 0 || e.Kind == CombatKind.CombatStart;
                if (activity)
                {
                    if (current.Start == null) current = new Encounter { Start=t };
                    current.Last = t;
                    current.Boss |= e.IsBoss;
                    overall.Start ??= t;
                    overall.Last = t;
                    ApplyTo(current, e, t);
                    ApplyTo(overall, e, t);
                }
                else if (current.Start != null && !current.Completed)
                {
                    ApplyTo(current, e, t);
                    ApplyTo(overall, e, t);
                    if (e.Kind == CombatKind.TargetHp && e.IsBoss && e.MaxHp > 0 && e.CurrentHp == 0)
                        completed = Finish("Boss defeated") ?? completed;
                }
            }
        }
        if (completed != null) EncounterCompleted?.Invoke(completed);
    }

    private void Remember(long id, string name, string cls)
    {
        identities.TryGetValue(id, out var old);
        bool named = !string.IsNullOrWhiteSpace(name) && !name.StartsWith("Actor ") && !name.StartsWith("Target ");
        identities[id] = new Identity(named ? name : old?.Name ?? $"Actor {id}", cls != "Unknown" && !string.IsNullOrWhiteSpace(cls) ? cls : old?.ClassName ?? "Unknown");
        // Freeze resolved names in historical records rather than relabeling them after ID reuse.
        if (current.Start != null && !current.Completed) current.Names[Key(id)] = identities[id];
        overall.Names[Key(id)] = identities[id];
    }
    private long Key(long id) => entityKeys.GetValueOrDefault(id,id);
    private long Owner(long id)
    {
        var visited = new HashSet<long>();
        while (owners.TryGetValue(id, out var next) && visited.Add(id)) id = next;
        return id;
    }
    private void ApplyTo(Encounter encounter, CombatEvent e, DateTime t)
    {
        encounter.Events.Enqueue(e);
        while(encounter.Events.Count > 2000) encounter.Events.Dequeue();
        long rawSource=Owner(e.SourceId), rawTarget=Owner(e.TargetId);
        long source=Key(rawSource),targetId=Key(rawTarget);
        if (identities.TryGetValue(rawSource, out var si)) encounter.Names[source] = si;
        if (identities.TryGetValue(rawTarget, out var ti)) encounter.Names[targetId] = ti;
        if ((e.Kind is CombatKind.Damage or CombatKind.Heal) && e.Amount > 0)
        {
            var category = e.Kind == CombatKind.Damage ? MeterCategory.Damage : MeterCategory.Healing;
            Add(encounter, source, category, e);
            if (e.Kind == CombatKind.Damage) Add(encounter, targetId, MeterCategory.DamageTaken, e);
            if (encounter.LastActivity.HasValue)
                encounter.ActiveSeconds += Math.Min(5, Math.Max(0, (t-encounter.LastActivity.Value).TotalSeconds));
            encounter.LastActivity=t;
            if (e.Kind == CombatKind.Damage && e.TargetId != 0)
            {
                encounter.TargetDamage.TryGetValue(e.TargetId, out var damage);
                encounter.TargetDamage[e.TargetId] = damage + e.Amount;
                if (encounter.TargetId != e.TargetId) { encounter.TargetId=e.TargetId; encounter.Target=null; }
                var old=encounter.Target;
                encounter.Target = new TargetStats(e.Target, e.MaxHp>0?e.CurrentHp:old?.CurrentHp??0, e.MaxHp>0?e.MaxHp:old?.MaxHp??0,
                    e.MaxHp>0?Math.Clamp(e.CurrentHp*100.0/e.MaxHp,0,100):old?.Percent??0,damage+e.Amount);
            }
        }
        if (e.Kind is CombatKind.Death or CombatKind.Interrupt or CombatKind.Dispel)
        {
            var category = e.Kind == CombatKind.Death ? MeterCategory.Deaths : e.Kind == CombatKind.Interrupt ? MeterCategory.Interrupts : MeterCategory.Dispels;
            Add(encounter, e.Kind == CombatKind.Death ? targetId != 0 ? targetId : source : source, category, e with {Amount=1});
        }
        if (e.Kind == CombatKind.TargetHp && e.MaxHp>0)
        {
            encounter.TargetId=e.TargetId;
            encounter.TargetDamage.TryGetValue(e.TargetId,out var damage);
            encounter.Target=new(e.Target,e.CurrentHp,e.MaxHp,Math.Clamp(e.CurrentHp*100.0/e.MaxHp,0,100),damage);
            encounter.Boss |= e.IsBoss;
        }
        if (e.Kind is CombatKind.BuffApply or CombatKind.BuffRemove or CombatKind.DebuffApply or CombatKind.DebuffRemove)
        {
            bool debuff = e.Kind is CombatKind.DebuffApply or CombatKind.DebuffRemove;
            var key=(e.Effect,source,targetId,debuff);
            if (!encounter.Buffs.TryGetValue(key,out var b)) encounter.Buffs[key]=b=new BuffWindow();
            if (e.Kind is CombatKind.BuffApply or CombatKind.DebuffApply) b.Apply(t,e.Stacks);
            else b.Remove(t);
        }
    }
    private static void Add(Encounter encounter, long id, MeterCategory category, CombatEvent e)
    {
        var key=(id,category,e.Skill);
        if (!encounter.Metrics.TryGetValue(key,out var s)) encounter.Metrics[key]=s=new Stat();
        s.Amount+=e.Amount; s.Hits++;
        if ((e.DamageFlags & DamageFlags.Critical)!=0 || e.DamageType==DamageType.Crit) s.Crits++;
        s.Min=Math.Min(s.Min,e.Amount); s.Max=Math.Max(s.Max,e.Amount); s.Flags |= e.DamageFlags;
    }
    private bool Expired(DateTime t) => current.Start != null && !current.Completed && current.Last.HasValue &&
        t-current.Last.Value >= (current.Boss ? BossInactivityTimeout : InactivityTimeout);
    private MeterSnapshot? Finish(string reason)
    {
        if (current.Start == null || current.Completed) return null;
        current.Completed=true; current.EndReason=reason;
        current.Duration=Seconds(current);
        foreach(var b in current.Buffs.Values) b.Remove(current.Last ?? current.Start.Value);
        foreach(var b in overall.Buffs.Values) b.Remove(current.Last ?? current.Start.Value);
        var result=Build(current,MeterCategory.Damage,false);
        history.Add(current);
        if(history.Count>100) history.RemoveAt(0);
        overall.CompletedDuration+=current.Duration;
        return result;
    }
    public void ResetFight()
    {
        MeterSnapshot? completed;
        lock(gate) { completed=Finish("Manual reset"); current=new(); }
        if(completed!=null) EncounterCompleted?.Invoke(completed);
    }
    public MeterSnapshot Snapshot() => Snapshot(MeterSegment.Current,MeterCategory.Damage);
    public MeterSnapshot Snapshot(MeterSegment segment, MeterCategory category)
    {
        MeterSnapshot? completed;
        MeterSnapshot result;
        lock(gate)
        {
            completed=Expired(clock())?Finish("Inactivity"):null;
            var selected=segment==MeterSegment.Overall?overall:segment==MeterSegment.Previous?history.LastOrDefault()??new Encounter():current;
            result=Build(selected,category,segment==MeterSegment.Overall);
        }
        if(completed!=null) EncounterCompleted?.Invoke(completed);
        return result;
    }
    private static double Seconds(Encounter e) => e.Start.HasValue && e.Last.HasValue ? Math.Max(1,(e.Last.Value-e.Start.Value).TotalSeconds) : 0;
    private MeterSnapshot Build(Encounter e, MeterCategory category, bool isOverall, bool includeCategories=true)
    {
        double seconds=isOverall?overall.CompletedDuration+(current.Start!=null&&!current.Completed?Seconds(current):0):e.Completed?e.Duration:Seconds(e);
        double divisor=Math.Max(1,seconds);
        var totals=e.Metrics.Where(x=>x.Key.Category==category).GroupBy(x=>x.Key.Id).ToDictionary(x=>x.Key,x=>x.Sum(y=>y.Value.Amount));
        if(category is MeterCategory.Buffs or MeterCategory.Debuffs)
            totals=e.Buffs.Where(x=>x.Key.Debuff==(category==MeterCategory.Debuffs)).GroupBy(x=>x.Key.Target).ToDictionary(x=>x.Key,x=>(long)Math.Round(x.Sum(y=>y.Value.Seconds(e.Last??clock()))));
        long total=totals.Values.Sum();
        var rows=totals.Select(x=>
        {
            var name=e.Names.GetValueOrDefault(x.Key)??new Identity($"Actor {x.Key}","Unknown");
            var stats=e.Metrics.Where(y=>y.Key.Id==x.Key && y.Key.Category==category).Select(y=>y.Value).ToList();
            long hits=stats.Sum(y=>y.Hits), crits=stats.Sum(y=>y.Crits);
            bool rate=category is MeterCategory.Damage or MeterCategory.Healing or MeterCategory.DamageTaken;
            return new PlayerStats(name.Name,name.ClassName,x.Value,rate?x.Value/divisor:x.Value,total==0?0:x.Value*100.0/total,hits,hits==0?0:crits*100.0/hits,x.Key,x.Value/Math.Max(1,e.ActiveSeconds),entityKeys.FirstOrDefault(y=>y.Value==x.Key).Key is var raw && raw!=0?raw:x.Key);
        }).OrderByDescending(x=>x.Damage).ToArray();
        var skills=e.Metrics.Where(x=>x.Key.Category==category).Select(x=>new SkillStats(x.Key.Skill,x.Value.Amount,x.Value.Hits,x.Value.Amount/divisor,
            x.Key.Id,x.Value.Crits,x.Value.Hits==0?0:x.Value.Crits*100.0/x.Value.Hits,totals.GetValueOrDefault(x.Key.Id)==0?0:x.Value.Amount*100.0/totals[x.Key.Id],
            x.Value.Hits==0?0:x.Value.Amount*1.0/x.Value.Hits,x.Value.Min==long.MaxValue?0:x.Value.Min,x.Value.Max,x.Value.Flags)).OrderByDescending(x=>x.Damage).ToArray();
        var buffs=e.Buffs.Select(x=>new BuffStats(x.Key.Name,Math.Clamp(x.Value.Seconds(e.Last??clock())/divisor*100,0,100),x.Value.MaxStacks,x.Key.Source,x.Key.Target,x.Key.Debuff,x.Value.Seconds(e.Last??clock()))).ToArray();
        long damage=e.Metrics.Where(x=>x.Key.Category==MeterCategory.Damage).Sum(x=>x.Value.Amount);
        long overallDamage=overall.Metrics.Where(x=>x.Key.Category==MeterCategory.Damage).Sum(x=>x.Value.Amount);
        double overallSeconds=overall.CompletedDuration+(current.Start!=null&&!current.Completed?Seconds(current):0);
        return new(e.Start!=null&&!e.Completed,PreviewMode,seconds,damage,damage/divisor,overallDamage,overallDamage/Math.Max(1,overallSeconds),e.Target,rows,skills,buffs,e.Events.ToArray())
        { Categories=includeCategories?Enum.GetValues<MeterCategory>().ToDictionary(c=>c,c=> {var snapshot=Build(e,c,isOverall,false);return new CategorySnapshot(snapshot.Players,snapshot.Skills,snapshot.MetricLabel);}):new Dictionary<MeterCategory,CategorySnapshot>(), EncounterId=e.Id,StartedUtc=e.Start,EndReason=e.EndReason,Category=category,MetricLabel=category==MeterCategory.Healing?"HPS":category is MeterCategory.Damage or MeterCategory.DamageTaken?"DPS":category is MeterCategory.Buffs or MeterCategory.Debuffs?"SECONDS":"COUNT",ActiveSeconds=e.ActiveSeconds };
    }
    private sealed record Identity(string Name,string ClassName);
    private sealed class Stat { public long Amount,Hits,Crits,Max; public long Min=long.MaxValue; public DamageFlags Flags; }
    private sealed class Encounter
    {
        public Guid Id=Guid.NewGuid(); public DateTime? Start,Last,LastActivity; public bool Completed,Boss; public string EndReason="";
        public double Duration,CompletedDuration,ActiveSeconds; public long TargetId; public TargetStats? Target;
        public Dictionary<long,Identity> Names=new(); public Dictionary<long,long> TargetDamage=new();
        public Dictionary<(long Id,MeterCategory Category,string Skill),Stat> Metrics=new();
        public Dictionary<(string Name,long Source,long Target,bool Debuff),BuffWindow> Buffs=new(); public Queue<CombatEvent> Events=new();
    }
    private sealed class BuffWindow
    {
        private DateTime? start; private double seconds; public int MaxStacks;
        public void Apply(DateTime t,int stacks) { start??=t; MaxStacks=Math.Max(MaxStacks,stacks); }
        public void Remove(DateTime t) { if(start.HasValue) { seconds+=Math.Max(0,(t-start.Value).TotalSeconds); start=null; } }
        public double Seconds(DateTime t)=>seconds+(start.HasValue?Math.Max(0,(t-start.Value).TotalSeconds):0);
    }
}
