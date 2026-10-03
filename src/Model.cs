namespace Aion2DPSPro;

public enum DamageType { Direct, Dot, Crit, Perfect, Back, Frontal, Parry, Double, MultiHit, Unknown }
[Flags]
public enum DamageFlags { None=0, Critical=1, Perfect=2, Double=4, Parry=8, Back=16, Frontal=32, MultiHit=64 }
public enum CombatKind { Damage, Heal, BuffApply, BuffRemove, TargetHp, PlayerName, Zone, Death, Cast, Interrupt, Dispel, DebuffApply, DebuffRemove, Ownership, Despawn, CombatStart, CombatEnd }
public enum MeterCategory { Damage, Healing, DamageTaken, Deaths, Buffs, Debuffs, Interrupts, Dispels }
public enum MeterSegment { Current, Previous, Overall }

public sealed record CombatEvent(DateTime Utc, CombatKind Kind, long SourceId = 0, string Source = "", long TargetId = 0,
    string Target = "", string Skill = "", long Amount = 0, DamageType DamageType = DamageType.Unknown,
    long CurrentHp = 0, long MaxHp = 0, string Effect = "", int Stacks = 0, string SourceClass = "Unknown", DamageFlags DamageFlags = DamageFlags.None,
    long OwnerId = 0, bool IsBoss = false);
public sealed record PlayerStats(string Name, string ClassName, long Damage, double Dps, double Share, long Hits, double CritPercent,
    long ActorId = 0, double ActiveDps = 0, long EntityId = 0);
public sealed record SkillStats(string Name, long Damage, long Hits, double Dps, long ActorId = 0, long Crits = 0,
    double CritPercent = 0, double Share = 0, double Average = 0, long MinHit = 0, long MaxHit = 0, DamageFlags Flags = DamageFlags.None)
{
    public IReadOnlyDictionary<DamageFlags,long> FlagHits {get;init;} = new Dictionary<DamageFlags,long>();
}
public sealed record BuffStats(string Name, double Uptime, int MaxStacks, long SourceId = 0, long TargetId = 0, bool IsDebuff = false, double ActiveSeconds = 0);
public sealed record TargetStats(string Name, long CurrentHp, long MaxHp, double Percent, long DamageTaken);
public sealed record ActorTargetStats(long ActorId,long TargetId,string Name,long Damage,double Share);
public sealed record CategorySnapshot(IReadOnlyList<PlayerStats> Players,IReadOnlyList<SkillStats> Skills,string MetricLabel);
public sealed record MeterSnapshot(bool InFight, bool PreviewMode, double FightSeconds, long FightDamage, double FightDps,
    long OverallDamage, double OverallDps, TargetStats? Target, IReadOnlyList<PlayerStats> Players,
    IReadOnlyList<SkillStats> Skills, IReadOnlyList<BuffStats> Buffs, IReadOnlyList<CombatEvent> RecentEvents)
{
    public IReadOnlyDictionary<MeterCategory,CategorySnapshot> Categories {get;init;} = new Dictionary<MeterCategory,CategorySnapshot>();
    public IReadOnlyList<ActorTargetStats> Targets {get;init;} = Array.Empty<ActorTargetStats>();
    public Guid EncounterId { get; init; }
    public DateTime? StartedUtc { get; init; }
    public string EndReason { get; init; } = "";
    public MeterCategory Category { get; init; }
    public string MetricLabel { get; init; } = "DPS";
    public double ActiveSeconds { get; init; }
}
