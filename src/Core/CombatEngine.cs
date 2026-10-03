using System.Collections.Concurrent;

namespace Aion2DPSPro;

public sealed class CombatEngine
{
    private readonly object gate = new();
    private readonly List<CombatEvent> events = new();
    private readonly Dictionary<long, (string Name, string ClassName, long Damage, long Hits, long Crits)> players = new();
    public bool PreviewMode { get; set; }
    private readonly Dictionary<string, (long Damage, long Hits)> skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BuffWindow> buffs = new(StringComparer.OrdinalIgnoreCase);

    private DateTime? fightStart;
    private DateTime? lastCombat;
    private long overallDamage;
    private DateTime? sessionStart;
    private TargetStats? target;

    public void Apply(CombatEvent e)
    {
        lock (gate)
        {
            events.Add(e);
            if (events.Count > 100_000) events.RemoveRange(0, 10_000);

            if (e.Kind == CombatKind.Damage && e.Amount > 0)
            {
                sessionStart ??= e.Utc;
                fightStart ??= e.Utc;
                lastCombat = e.Utc;
                overallDamage += e.Amount;

                players.TryGetValue(e.SourceId, out var p);
                players[e.SourceId] = (string.IsNullOrWhiteSpace(e.Source) ? $"Actor {e.SourceId}" : e.Source,
                    string.IsNullOrWhiteSpace(e.SourceClass) ? (p.ClassName ?? "Unknown") : e.SourceClass,
                    p.Damage + e.Amount, p.Hits + 1, p.Crits + (e.DamageType == DamageType.Crit ? 1 : 0));

                skills.TryGetValue(e.Skill, out var s);
                skills[e.Skill] = (s.Damage + e.Amount, s.Hits + 1);

                if (e.TargetId != 0)
                    target = new TargetStats(e.Target, e.CurrentHp, e.MaxHp,
                        e.MaxHp > 0 ? e.CurrentHp * 100.0 / e.MaxHp : 0,
                        (target?.DamageTaken ?? 0) + e.Amount);
            }

            if (e.Kind == CombatKind.TargetHp && e.MaxHp > 0)
                target = new TargetStats(e.Target, e.CurrentHp, e.MaxHp,
                    e.CurrentHp * 100.0 / e.MaxHp, target?.DamageTaken ?? 0);

            if (e.Kind == CombatKind.BuffApply)
            {
                if (!buffs.TryGetValue(e.Effect, out var b))
                    buffs[e.Effect] = b = new BuffWindow();
                b.Apply(e.Utc, e.Stacks);
            }
            else if (e.Kind == CombatKind.BuffRemove && buffs.TryGetValue(e.Effect, out var rb))
                rb.Remove(e.Utc);

            // Eight seconds with no combat ends a fight.
            if (fightStart.HasValue && lastCombat.HasValue &&
                (e.Utc - lastCombat.Value).TotalSeconds >= 8)
                FinalizeFightInternal();
        }
    }

    public void ResetFight()
    {
        lock (gate)
        {
            players.Clear();
            skills.Clear();
            events.Clear();
            buffs.Clear();
            fightStart = null;
            lastCombat = null;
            target = null;
        }
    }

    public MeterSnapshot Snapshot()
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            if (fightStart.HasValue && lastCombat.HasValue &&
                (now - lastCombat.Value).TotalSeconds >= 8)
                FinalizeFightInternal();

            var fightSeconds = fightStart.HasValue ? Math.Max(.001, (now - fightStart.Value).TotalSeconds) : 0;
            var sessionSeconds = sessionStart.HasValue ? Math.Max(.001, (now - sessionStart.Value).TotalSeconds) : 0;
            var total = players.Values.Sum(x => x.Damage);

            var rows = players.Values
                .Select(p => new PlayerStats(p.Name, p.ClassName, p.Damage, p.Damage / Math.Max(.001, fightSeconds),
                    total == 0 ? 0 : p.Damage * 100.0 / total, p.Hits, p.Hits == 0 ? 0 : p.Crits * 100.0 / p.Hits))
                .OrderByDescending(p => p.Damage).ToList();

            var skillRows = skills.Select(x => new SkillStats(x.Key, x.Value.Damage, x.Value.Hits,
                x.Value.Damage / Math.Max(.001, fightSeconds)))
                .OrderByDescending(x => x.Damage).Take(25).ToList();

            var buffRows = buffs.Select(x => new BuffStats(x.Key, x.Value.Uptime(now), x.Value.MaxStacks)).ToList();

            return new MeterSnapshot(
                fightStart.HasValue, PreviewMode,
                fightSeconds,
                total,
                total / Math.Max(.001, fightSeconds),
                overallDamage,
                overallDamage / Math.Max(.001, sessionSeconds),
                target,
                rows, skillRows, buffRows,
                events.TakeLast(100).ToList());
        }
    }

    private void FinalizeFightInternal()
    {
        // Keep the session totals; clear only encounter-local state.
        fightStart = null;
        lastCombat = null;
        players.Clear();
        skills.Clear();
        buffs.Clear();
        target = null;
    }

    private sealed class BuffWindow
    {
        private DateTime? start;
        private double seconds;
        public int MaxStacks { get; private set; }

        public void Apply(DateTime t, int stacks)
        {
            if (!start.HasValue) start = t;
            MaxStacks = Math.Max(MaxStacks, stacks);
        }

        public void Remove(DateTime t)
        {
            if (start.HasValue)
            {
                seconds += Math.Max(0, (t - start.Value).TotalSeconds);
                start = null;
            }
        }

        public double Uptime(DateTime now)
        {
            var s = seconds + (start.HasValue ? Math.Max(0, (now - start.Value).TotalSeconds) : 0);
            return Math.Min(100, s / Math.Max(1, 60) * 100);
        }
    }
}
