namespace Aion2DPSPro.Protocol;

public interface IAion2Decoder
{
    string ProfileId { get; }
    IEnumerable<Aion2Decoded> Decode(byte[] payload, DateTime utc);
}

public sealed record Aion2Decoded(
    CombatKind Kind,
    long SourceId,
    string Source,
    long TargetId,
    string Target,
    string Skill,
    long Amount,
    DamageType DamageType,
    long CurrentHp,
    long MaxHp,
    string Effect,
    int Stacks, string SourceClass = "Unknown", DamageFlags DamageFlags = DamageFlags.None,
    bool SourceIdentityConfirmed = false);


