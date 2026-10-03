namespace Aion2DPSPro;

public enum DamageType { Direct, Dot, Crit, Perfect, Back, Frontal, Parry, Double, MultiHit, Unknown }
public enum CombatKind { Damage, Heal, BuffApply, BuffRemove, TargetHp, PlayerName, Zone, Death, Cast }

public sealed record CombatEvent(DateTime Utc, CombatKind Kind, long SourceId = 0, string Source = "", long TargetId = 0,
    string Target = "", string Skill = "", long Amount = 0, DamageType DamageType = DamageType.Unknown,
    long CurrentHp = 0, long MaxHp = 0, string Effect = "", int Stacks = 0, string SourceClass = "Unknown");

public sealed record PlayerStats(string Name, string ClassName, long Damage, double Dps, double Share, long Hits, double CritPercent);
public sealed record SkillStats(string Name, long Damage, long Hits, double Dps);
public sealed record BuffStats(string Name, double Uptime, int MaxStacks);
public sealed record TargetStats(string Name, long CurrentHp, long MaxHp, double Percent, long DamageTaken);
public sealed record MeterSnapshot(bool InFight, bool PreviewMode, double FightSeconds, long FightDamage, double FightDps,
    long OverallDamage, double OverallDps, TargetStats? Target, IReadOnlyList<PlayerStats> Players,
    IReadOnlyList<SkillStats> Skills, IReadOnlyList<BuffStats> Buffs, IReadOnlyList<CombatEvent> RecentEvents);
