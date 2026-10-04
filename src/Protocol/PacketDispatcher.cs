namespace Aion2DPSPro.Protocol;

/// Experimental parser adapted from the public A2Meter event layout. Every read is bounds-checked;
/// implausible packets are rejected and logged rather than emitted as combat data.
public sealed class PacketDispatcher
{
    private long? selfEntityId;
    private readonly HashSet<long> recentCombatIds = new();
    private readonly ProtocolProfile profile;
    private readonly Dictionary<long, PlayerIdentity> identities = new();
    private readonly Dictionary<long, string> globalPlayerNames = new();
    private readonly Dictionary<long, long> sessionToGlobal = new();
    private readonly Dictionary<long, PlayerIdentity> partyIdentities = new();
    private readonly HashSet<long> recentCombatEntityIds = new();
    private readonly Dictionary<long, MobIdentity> mobs = new();
    private readonly Dictionary<long, long> summonOwners = new();
    private readonly HashSet<long> confirmedSummons = new();
    public event Action<DecoderDiagnostic>? Diagnostic;
    public event Action<string>? ValidationRecord;
    public PacketDispatcher(ProtocolProfile profile) => this.profile = profile;

    public IEnumerable<Aion2Decoded> Dispatch(byte[] frame, DateTime utc)
    {
        int p=0;
        if (!ProtocolUtils.TryReadVarUInt(frame, ref p, out _, out _)) yield break;
        if (p+1>=frame.Length) yield break;
        byte a=frame[p], b=frame[p+1];
                if (b == 0x97 && (a == 0x01 || a == 0x02 || a == 0x0B))
        {
            foreach (var partyEvt in TryObservePartyIdentities(frame, p + 2, utc, a))
                yield return partyEvt;
            yield break;
        }
        
// Current Global references expose remaining HP on 00 8D in addition to the
        // configured 01 8D boss-HP opcode.
        if (a == 0x00 && b == 0x8D)
        {
            var hpEvt = TryRemainHp(frame, p + 2, utc);
            if (hpEvt is not null) yield return hpEvt;
            yield break;
        }

        // Authoritative summon -> owner relation used by current Global builds.
        // Layout: 04 8D <summon varint> <fixed4> <owner varint> <meta varint> <nameLen> <UTF-8 owner name>.
        if (a == 0x04 && b == 0x8D)
        {
            var ownerEvt = TrySummonOwnership(frame, p + 2, utc);
            if (ownerEvt is not null) yield return ownerEvt;
            yield break;
        }

// Trace identity evidence before rejecting unknown opcodes. Several lifecycle
        // packets are intentionally not combat tags, but can carry the missing
        // session/global relationship needed to resolve Actor #### rows.
        // Current Global 33 36 self-info is identity data too. Handle it
        // before profile dispatch so the local player name is never gated by
        // an unverified/missing opcode profile.
        if (a == 0x33 && b == 0x36)
        {
            var selfEvt = ObserveSelfIdentity(frame, p + 2, utc);
            if (selfEvt is not null)
            {
                Diagnostic?.Invoke(new(utc, "parse", "Parsed selfInfo identity", frame.Length));
                yield return selfEvt;
            }
            yield break;
        }

        // Current Global 45 36 user-info is identity data, not combat data.
        // Parse it independently of profile verification/tags.
        if (a == 0x45 && b == 0x36)
        {
            var identityEvt = ObserveIdentity(frame, p + 2, utc, "otherInfo");
            if (identityEvt is not null)
            {
                Diagnostic?.Invoke(new(utc, "parse", "Parsed otherInfo identity", frame.Length));
                ValidationRecord?.Invoke($"{utc:O}|tag=identity|packet=otherInfo|id={identityEvt.SourceId}|name={identityEvt.Source}|raw={Convert.ToHexString(frame)}");
                yield return identityEvt;
            }
            else
            {
                TraceIdentityLifecycle(frame, utc);
                TraceCombatIdentityCandidates(frame, utc);
            }
            yield break;
        }

        TraceIdentityLifecycle(frame, utc);
        TraceGlobalSessionCandidates(frame, utc);
        TraceCombatIdentityCandidates(frame, utc);
        TraceExtendedIdentityCandidates(frame, utc);

        // Only accept 20 36 when it is the frame's actual opcode immediately
        // after the leading varint. Fresh captures contain incidental 20 36 byte
        // sequences inside unrelated packets, which must never create ID links.
        if (a == 0x20 && b == 0x36)
        {
            var sessionLink = TryGlobalSessionLink(frame, p, utc);
            if (sessionLink is not null)
                yield return sessionLink;
            yield break;
        }

        var embeddedIdentity = TryEmbeddedIdentity(frame, utc);
        if (embeddedIdentity is not null)
            yield return embeddedIdentity;

        var kind = profile.Tags.FirstOrDefault(kv => kv.Value.A==a && kv.Value.B==b).Key;
        if (kind is null) { Diagnostic?.Invoke(new(utc,"dispatch",$"Unknown tag 0x{a:X2}{b:X2}",frame.Length)); yield break; }

        if (kind == "mobSpawn")
        {
            var identityEvt = ObserveEntityBridge(frame, p + 2, utc, "mobSpawn");
            if (identityEvt is not null) yield return identityEvt;
            TryRegisterSummonOwner(frame, p + 2, utc);
            var mobEvt = TryMobSpawn(frame, p + 2, utc);
            if (mobEvt is not null) yield return mobEvt;
            yield break;
        }

        Aion2Decoded? evt = kind switch {
            "damage" => TryDamage(frame, p+2, utc),
            "dot" => TryDot(frame, p+2, utc),
            "bossHp" => TryBossHp(frame, p+2, utc),
            "entityRemoved" => RemoveEntity(frame, p+2),
            "selfInfo" => ObserveSelfIdentity(frame, p+2, utc),
            "otherInfo" => ObserveIdentity(frame, p+2, utc, "otherInfo"),
            "charLookup" => TryCharacterLookup(frame, p+2, utc),
            _ => null
        };
        if (evt is not null) { Diagnostic?.Invoke(new(utc,"parse",$"Parsed {kind}",frame.Length)); var candidates = kind == "damage" ? DescribeVarintCandidates(frame, FindPostSkillPosition(frame, p+2)) : "";
      ValidationRecord?.Invoke($"{utc:O}|tag={kind}|src={evt.SourceId}|tgt={evt.TargetId}|skill={evt.Skill}|amount={evt.Amount}|type={evt.DamageType}|candidates={candidates}|raw={Convert.ToHexString(frame)}"); yield return evt; }
        else Diagnostic?.Invoke(new(utc,"dispatch",$"Matched {kind}; no validated event emitted",frame.Length));
    }

    private Aion2Decoded? RemoveEntity(ReadOnlySpan<byte> frame,int p)
    {
        if(!ReadV(frame,ref p,out var value) || value==0 || value>long.MaxValue)return null;
        long id=(long)value;
        // Captures show this removal for named players who continue attacking.
        // Do not treat this unverified visibility packet as proof of player ID reuse.
        if(id==selfEntityId || identities.ContainsKey(id)) {
            Diagnostic?.Invoke(new(DateTime.UtcNow,"identity-map","Retained known player identity across entity removal",frame.Length));
            return null;
        }
        identities.Remove(id);globalPlayerNames.Remove(id);partyIdentities.Remove(id);sessionToGlobal.Remove(id);
        mobs.Remove(id);summonOwners.Remove(id);confirmedSummons.Remove(id);recentCombatIds.Remove(id);recentCombatEntityIds.Remove(id);
        foreach(var key in sessionToGlobal.Where(x=>x.Value==id).Select(x=>x.Key).ToArray())sessionToGlobal.Remove(key);
        foreach(var key in summonOwners.Where(x=>x.Value==id).Select(x=>x.Key).ToArray())summonOwners.Remove(key);
        return new(CombatKind.Despawn,id,"",0,"","",0,DamageType.Unknown,0,0,"",0);
    }

    private Aion2Decoded? TryDamage(ReadOnlySpan<byte> d, int p, DateTime utc)
    {
        if (!ReadV(d, ref p, out var target)) return null;
        if (!ReadV(d, ref p, out var flags1)) return null;
        var category=(int)(flags1 & 0xF); if (category<4 || category>7) return null;
        if (!ReadV(d, ref p, out _)) return null;
        if (!ReadV(d, ref p, out var actor) || actor==target || actor==0 || target==0) return null;
        recentCombatIds.Add((long)actor); recentCombatIds.Add((long)target);

        // Public reference resolves skill IDs from packet bytes using a skill database. Until that
        // catalog is embedded, accept a bounded varint candidate and retain its numeric identity.
        if (!TryReadSkillField(d, ref p, out var skill)) return null;
        int postSkillPos = p;
        if (!ReadV(d, ref p, out var damageType)) return null;

        int[] trailing={0,0,0,0,8,12,10,14};
        byte mods=0, direction=0; int consumedSpecial=0;
        if (category >= 5 && p + 2 < d.Length && d[p + 1] == 0)
        {
            mods = d[p];
            direction = d[p + 2];
            p += 3;
            consumedSpecial = 3;
        }
        int trailer=trailing[category]-consumedSpecial; if (trailer<0 || p+trailer>d.Length) return null; p+=trailer;
        if (!ReadV(d, ref p, out _)) return null;
        if (!ReadV(d, ref p, out _)) return null;
        if (!ReadV(d, ref p, out var damage) || damage==0 || damage>9_000_000_000UL) return null;

        // Some multi-hit packets put the hit ordinal (1..5) in the generic
        // damage slot. Recover only when the packet tail also contains the
        // validated stable/base -> real-hit pair.
        if (damage <= 5)
        {
            if (TryRecoverAlternateDamage(d, out var recoveredDamage))
                damage = recoveredDamage;
        }

        RememberCombatEntity(actor);
        RememberCombatEntity(target);
        var dtype = DecodeType((byte)damageType, mods, direction);
        var dflags = DecodeFlags((byte)damageType, mods, direction);
        Diagnostic?.Invoke(new(utc,"damage-flags",$"rawType={damageType} mods=0x{mods:X2} direction=0x{direction:X2} decoded={dtype} flags={dflags}",d.Length));
        ValidationRecord?.Invoke($"{utc:O}|tag=damageFlags|actor={actor}|target={target}|skill={SkillName(checked((int)skill))}|rawType={damageType}|mods=0x{mods:X2}|direction=0x{direction:X2}|decoded={dtype}|crit={damageType==3}|parry={(mods&0x02)!=0}|perfect={(mods&0x04)!=0}|double={(mods&0x08)!=0}|back={direction==1}|front={direction==2}");
        long actorId = checked((long)actor);
        long targetId = checked((long)target);
        long originalActorId = actorId;
        if (summonOwners.TryGetValue(actorId, out var ownerId))
        {
            actorId = ownerId;
            ValidationRecord?.Invoke($"{utc:O}|tag=summonDamage|summon={originalActorId}|owner={ownerId}|skill={SkillName(checked((int)skill))}|amount={damage}");
        }
        string actorName;
        string actorClass;
        if (mobs.TryGetValue(actorId, out var sourceMob))
        {
            actorName = sourceMob.Name;
            actorClass = "NPC";
        }
        else
        {
            actorName = ResolveName(actorId, "Actor");
            actorClass = identities.TryGetValue(actorId, out var knownIdentity) && knownIdentity.ClassName != "Unknown"
                ? knownIdentity.ClassName : ClassFromSkill(skill);
        }
        bool sourceIdentityConfirmed = !mobs.ContainsKey(actorId) && identities.ContainsKey(actorId);
        var targetName = ResolveTargetName(targetId);
        long currentHp = 0, maxHp = 0;
        if (mobs.TryGetValue(targetId, out var mobState)) { currentHp = mobState.CurrentHp; maxHp = mobState.MaxHp; }
        return new(CombatKind.Damage, actorId, actorName, targetId, targetName,
            SkillName(checked((int)skill)), (long)damage, dtype, currentHp,maxHp,"",0, actorClass, dflags, sourceIdentityConfirmed);
    }

    private Aion2Decoded? TryDot(ReadOnlySpan<byte> d, int p, DateTime utc)
    {
        if (!ReadV(d, ref p, out var target) || p>=d.Length) return null;
        bool hasExtra=(d[p++] & 2)!=0;
        if (!ReadV(d, ref p, out var actor) || actor==target || actor==0) return null;
        if (!ReadV(d, ref p, out var heal)) return null;
        if (p+4>d.Length) return null;
        uint raw=(uint)(d[p] | d[p+1]<<8 | d[p+2]<<16 | d[p+3]<<24); p+=4;
        long damage=0; if (hasExtra && ReadV(d,ref p,out var x)) damage=(long)x;
        RememberCombatEntity(actor); RememberCombatEntity(target);
        uint skill=raw/100; if (skill==0) return null;
        if (damage<=0 && heal<=0) return null;
        long actorId = checked((long)actor);
        long targetId = checked((long)target);
        long originalActorId = actorId;
        if (summonOwners.TryGetValue(actorId, out var ownerId))
        {
            actorId = ownerId;
            ValidationRecord?.Invoke($"{utc:O}|tag=summonDot|summon={originalActorId}|owner={ownerId}|skill={SkillName(checked((int)skill))}|amount={(damage>0?damage:(long)heal)}");
        }
        string actorName;
        string actorClass;
        if (mobs.TryGetValue(actorId, out var sourceMob))
        {
            actorName = sourceMob.Name;
            actorClass = "NPC";
        }
        else
        {
            actorName = ResolveName(actorId, "Actor");
            actorClass = identities.TryGetValue(actorId, out var knownIdentity) && knownIdentity.ClassName != "Unknown"
                ? knownIdentity.ClassName : ClassFromSkill((int)skill);
        }
        bool sourceIdentityConfirmed = !mobs.ContainsKey(actorId) && identities.ContainsKey(actorId);
        return new(damage>0?CombatKind.Damage:CombatKind.Heal, actorId,actorName,targetId,ResolveTargetName(targetId),
            SkillName(checked((int)skill)), damage>0?damage:(long)heal, DamageType.Dot,0,0,"",0, actorClass, DamageFlags.None, sourceIdentityConfirmed);
    }

    private void TryRegisterSummonOwner(ReadOnlySpan<byte> d, int p, DateTime utc)
    {
        int q = p;
        if (!ReadV(d, ref q, out var summonU) || summonU == 0 || summonU > long.MaxValue) return;
        long summonId = (long)summonU;
        if (q >= d.Length) return;

        // Current 41 36 spawn mask: low byte is entity kind. 0x5F is a summon/pet.
        // Do not treat ordinary NPCs or transient skill-effect entities as summons.
        byte kind = d[q];
        if (kind != 0x5F) return;
        confirmedSummons.Add(summonId);
        ValidationRecord?.Invoke($"{utc:O}|tag=summonSpawn|summon={summonId}|kind=0x{kind:X2}");

        // Prefer the structured parent_key/legion block carried by 41 36.
        // Live capture 2026-10-03 proved this exact shape for SevenSins:
        // parent_key=82824, legion_id=15, pad=0, server_id=2102, legion="Karma".
        // The trailing string is legion metadata; the u32 parent_key is the owner.
        if (TryFindSpawnParentKey(d, q, summonId, out var ownerId, out var legion))
        {
            summonOwners[summonId] = ownerId;
            ValidationRecord?.Invoke($"{utc:O}|tag=summonOwner|summon={summonId}|owner={ownerId}|source=4136-parent-key|legion={legion}");
        }
    }

    private bool TryFindSpawnParentKey(ReadOnlySpan<byte> d, int searchFrom, long summonId, out long ownerId, out string legion)
    {
        ownerId = 0;
        legion = "";
        int end = Math.Min(d.Length - 13, searchFrom + 180);
        for (int i = Math.Max(0, searchFrom); i <= end; i++)
        {
            uint parent = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(i, 4));
            if (parent == 0 || parent > 9_999_999 || parent == summonId) continue;

            uint legionId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(i + 4, 4));
            ushort pad = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(i + 8, 2));
            ushort serverId = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(i + 10, 2));
            int nameLen = d[i + 12];
            if (pad != 0 || serverId == 0 || serverId > 9_999 || nameLen > 40 || i + 13 + nameLen > d.Length)
                continue;

            string candidate;
            try { candidate = System.Text.Encoding.UTF8.GetString(d.Slice(i + 13, nameLen)); }
            catch { continue; }
            if (nameLen > 0 && candidate.Any(char.IsControl)) continue;

            // Strong final guard: owner must already be independently known as a
            // player/party/combat entity. This keeps random packet bytes from merging rows.
            long p64 = parent;
            if (!identities.ContainsKey(p64) && !partyIdentities.ContainsKey(p64) && !recentCombatEntityIds.Contains(p64))
                continue;

            ownerId = p64;
            legion = candidate;
            return true;
        }
        return false;
    }

    private Aion2Decoded? TrySummonOwnership(ReadOnlySpan<byte> d, int p, DateTime utc)
    {
        if (!ReadV(d, ref p, out var summonU) || summonU < 100 || summonU > long.MaxValue) return null;
        long summonId = (long)summonU;
        if (p + 4 > d.Length) return null;
        p += 4;
        if (!ReadV(d, ref p, out var ownerU) || ownerU < 100 || ownerU > long.MaxValue) return null;
        long ownerId = (long)ownerU;
        if (ownerId == summonId || !confirmedSummons.Contains(summonId)) return null;

        string ownerName = "";
        int namePos = p;
        if (ReadV(d, ref namePos, out _) && namePos < d.Length)
        {
            int len = d[namePos++];
            if (len >= 1 && len <= 48 && namePos + len <= d.Length)
            {
                try
                {
                    var candidate = System.Text.Encoding.UTF8.GetString(d.Slice(namePos, len));
                    if (candidate.Length > 0 && candidate.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-'))
                        ownerName = candidate;
                }
                catch { }
            }
        }

        summonOwners[summonId] = ownerId;
        if (!string.IsNullOrWhiteSpace(ownerName))
            identities[ownerId] = new PlayerIdentity(ownerName, identities.TryGetValue(ownerId, out var old) ? old.ClassName : "Unknown");

        ValidationRecord?.Invoke($"{utc:O}|tag=summonOwner|summon={summonId}|owner={ownerId}|source=048D|name={ownerName}|raw={Convert.ToHexString(d)}");
        return !string.IsNullOrWhiteSpace(ownerName)
            ? new(CombatKind.PlayerName, ownerId, ownerName, 0, "", "", 0, DamageType.Unknown, 0, 0, "", 0)
            : null;
    }

    private string ResolveTargetName(long id)
        => mobs.TryGetValue(id, out var mob) ? mob.Name : ResolveName(id, "Target");

    private Aion2Decoded? TryMobSpawn(ReadOnlySpan<byte> d, int p, DateTime utc)
    {
        if (!ReadV(d, ref p, out var entityU) || entityU == 0 || entityU > long.MaxValue) return null;
        long entity = (long)entityU;
        int searchFrom = p;
        int searchLimit = Math.Min(searchFrom + 60, d.Length - 2);
        int marker = -1;
        for (int i = searchFrom; i < searchLimit; i++)
        {
            if (i + 2 < d.Length && d[i] == 0 && (d[i + 1] & 0xBF) == 0 && d[i + 2] == 2)
            { marker = i + 2; break; }
        }
        if (marker < 5) return null;
        int codePos = marker - 5;
        if (codePos < 0 || codePos + 3 > d.Length) return null;
        int mobCode = d[codePos] | (d[codePos + 1] << 8) | (d[codePos + 2] << 16);
        if (mobCode <= 0) return null;

        long maxHp = 0;
        int hpFrom = marker + 1;
        int hpTo = Math.Min(marker + 67, d.Length - 2);
        for (int i = hpFrom; i < hpTo; i++)
        {
            if (d[i] != 1) continue;
            int q = i + 1;
            if (!ReadV(d, ref q, out var maxU) || maxU == 0 || maxU > long.MaxValue) continue;
            if (!ReadV(d, ref q, out var curU) || curU > long.MaxValue) continue;
            if (curU < maxU) continue;
            maxHp = (long)curU;
            break;
        }

        string name = PublicGameData.MobName(mobCode) ?? $"Target {entity}";
        mobs[entity] = new MobIdentity(mobCode, name, maxHp, maxHp);
        ValidationRecord?.Invoke($"{utc:O}|tag=mobIdentity|entity={entity}|mobCode={mobCode}|name={name}|maxHp={maxHp}");
        if (maxHp > 0)
            return new(CombatKind.TargetHp,0,"",entity,name,"",0,DamageType.Unknown,maxHp,maxHp,"",0);
        return null;
    }

    private Aion2Decoded? TryBossHp(ReadOnlySpan<byte> d, int p, DateTime utc)
    {
        if (!ReadV(d,ref p,out var entityU) || entityU==0 || entityU>long.MaxValue) return null;
        long entity=(long)entityU;

        // A2Meter Global layout: entity varint, 02 01 00, current HP LE32, zero LE32.
        if (p + 11 <= d.Length && d[p] == 2 && d[p+1] == 1 && d[p+2] == 0)
        {
            p += 3;
            long hp = (uint)(d[p] | (d[p+1] << 8) | (d[p+2] << 16) | (d[p+3] << 24));
            long max = mobs.TryGetValue(entity, out var known) ? known.MaxHp : 0;
            string name = mobs.TryGetValue(entity, out known) ? known.Name : $"Target {entity}";
            if (mobs.TryGetValue(entity, out known))
                mobs[entity] = known with { CurrentHp = hp, MaxHp = Math.Max(known.MaxHp, hp) };
            max = mobs.TryGetValue(entity, out known) ? known.MaxHp : max;
            ValidationRecord?.Invoke($"{utc:O}|tag=targetHp|entity={entity}|current={hp}|max={max}|name={name}|source=018D");
            if (max > 0) return new(CombatKind.TargetHp,0,"",entity,name,"",0,DamageType.Unknown,hp,max,"",0);
        }
        return null;
    }

    private Aion2Decoded? TryRemainHp(ReadOnlySpan<byte> d, int p, DateTime utc)
    {
        if (!ReadV(d, ref p, out var entityU) || entityU == 0 || entityU > long.MaxValue) return null;
        long entity = (long)entityU;
        // RATmeter Global layout: entity, three varints, then current HP as LE64.
        for (int n=0;n<3;n++) if (!ReadV(d, ref p, out _)) return null;
        if (p + 8 > d.Length) return null;
        ulong hpU = 0;
        for (int i=0;i<8;i++) hpU |= (ulong)d[p+i] << (8*i);
        if (hpU > long.MaxValue) return null;
        long hp = (long)hpU;
        if (!mobs.TryGetValue(entity, out var known))
        {
            ValidationRecord?.Invoke($"{utc:O}|tag=targetHpCandidate|entity={entity}|current={hp}|max=0|source=008D");
            return null;
        }
        long max = Math.Max(known.MaxHp, hp);
        mobs[entity] = known with { CurrentHp = hp, MaxHp = max };
        ValidationRecord?.Invoke($"{utc:O}|tag=targetHp|entity={entity}|current={hp}|max={max}|name={known.Name}|source=008D");
        return new(CombatKind.TargetHp,0,"",entity,known.Name,"",0,DamageType.Unknown,hp,max,"",0);
    }

    private static bool IsPlausibleSkillCode(int code)
    {
        return (code >= 11_000_000 && code < 20_000_000) ||
               (code >= 1_000_000 && code < 10_000_000) ||
               (code >= 100_000 && code < 200_000) ||
               (code >= 29_000_000 && code < 31_000_000);
    }

    private static bool TryReadSkillField(ReadOnlySpan<byte> d, ref int p, out int skill)
    {
        skill = 0;
        for (int i = 0; i < 7 && p + i + 4 <= d.Length; i++)
        {
            int raw = d[p+i] | (d[p+i+1] << 8) | (d[p+i+2] << 16) | (d[p+i+3] << 24);
            if (raw <= 0) continue;

            int candidate = raw;
            if (IsPlausibleSkillCode(candidate))
            {
                skill = candidate;
                p += i + 5;
                return true;
            }

            if (raw % 100 == 0)
            {
                candidate = raw / 100;
                if (IsPlausibleSkillCode(candidate))
                {
                    skill = candidate;
                    p += i + 5;
                    return true;
                }
            }
        }
        return false;
    }

    private static int FindPostSkillPosition(ReadOnlySpan<byte> d, int p)
    {
        if (!ReadV(d, ref p, out _)) return -1;
        if (!ReadV(d, ref p, out _)) return -1;
        if (!ReadV(d, ref p, out _)) return -1;
        if (!ReadV(d, ref p, out _)) return -1;
        if (!TryReadSkillField(d, ref p, out _)) return -1;
        return p;
    }

    private static string DescribeVarintCandidates(ReadOnlySpan<byte> d, int start)
    {
        if (start < 0 || start >= d.Length) return "none";
        var parts = new List<string>();
        int p = start;
        for (int n = 0; n < 12 && p < d.Length; n++)
        {
            int at = p;
            if (!ReadV(d, ref p, out var value)) break;
            parts.Add($"{at}:{value}");
        }
        return string.Join(",", parts);
    }

    private static bool TryRecoverAlternateDamage(ReadOnlySpan<byte> d, out ulong damage)
    {
        damage = 0;
        // Alternate packets observed in validation carry a stable actor/base
        // value near the tail, followed immediately by the real varying hit.
        // Search only the final portion and require a plausible non-tiny hit.
        // The charged Punishment packet has a longer tail than ordinary
        // damage packets. In live Global captures its authoritative pair is
        // 11350 -> <damage> (for example 11350 -> 21636), while the generic
        // layout can otherwise land on the later hit-count value (3).
        //
        // Keep this structural rather than skill-name-specific: only recover
        // when a plausible stable/base value is immediately followed by a
        // plausible hit, but scan enough of the tail to include that pair.
        int start = Math.Max(0, d.Length - 18);
        for (int i = start; i < d.Length; i++)
        {
            int p = i;
            if (!ReadV(d, ref p, out var first)) continue;
            if (first < 5_000 || first > 50_000) continue;
            if (!ReadV(d, ref p, out var hit)) continue;
            if (hit < 20 || hit > 5_000_000) continue;
            damage = hit;
            return true;
        }
        return false;
    }

    private sealed record PlayerIdentity(string Name, string ClassName);
    private sealed record MobIdentity(int Code, string Name, long MaxHp, long CurrentHp);

    private string ResolveName(long id, string fallback)
        => identities.TryGetValue(id, out var x) ? x.Name : $"{fallback} {id}";

    private void RememberGlobalPlayerIdentity(long globalId, string name, string className, DateTime utc, string source)
    {
        if (globalId <= 0 || string.IsNullOrWhiteSpace(name)) return;
        var resolvedClass = className;
        if (resolvedClass == "Unknown" && identities.TryGetValue(globalId, out var existing))
            resolvedClass = existing.ClassName;

        var identity = new PlayerIdentity(name, resolvedClass);
        identities[globalId] = identity;
        globalPlayerNames[globalId] = name;

        // If this trusted identity is the current local session actor and a
        // validated 20 36 header already linked that session to a stable/global
        // character id, learn the stable identity too. This is what allows a
        // later zone/session change to resolve back to the same character.
        if (globalId == selfEntityId && sessionToGlobal.TryGetValue(globalId, out var stableGlobal) && stableGlobal > 0)
        {
            identities[stableGlobal] = identity;
            globalPlayerNames[stableGlobal] = name;
            ValidationRecord?.Invoke($"{utc:O}|tag=sessionPromotedGlobal|session={globalId}|global={stableGlobal}|name={name}|class={resolvedClass}|source={source}");
        }

        // A 20 36 packet can link the stable/global character id to the
        // short-lived combat/session id. Names may arrive before or after that
        // link, so promote global -> session whenever the stable side is known.
        foreach (var link in sessionToGlobal.Where(x => x.Value == globalId).ToArray())
        {
            identities[link.Key] = identity;
            ValidationRecord?.Invoke($"{utc:O}|tag=lateGlobalSessionName|session={link.Key}|global={globalId}|name={name}|class={resolvedClass}|source={source}");
        }
    }

    private Aion2Decoded? ObserveIdentity(ReadOnlySpan<byte> d, int p, DateTime utc, string packetKind)
    {
        int start = p;
        if (!ReadV(d, ref p, out var rawId) || rawId == 0 || rawId > long.MaxValue) return null;
        long id = (long)rawId;
        string? best = null;
        int nameOffset = -1;
        if (TryReadStructuredCharacterName(d, start, out var structuredName, out nameOffset))
            best = structuredName;
        if (string.IsNullOrWhiteSpace(best) || best.Length < 2) { var failedCandidates = DescribeIdentityCandidates(d, start);
        Diagnostic?.Invoke(new(utc,"identity",$"{packetKind} id={id} no validated name idCandidates={failedCandidates}",d.Length));
        ValidationRecord?.Invoke($"{utc:O}|tag=identity|packet={packetKind}|id={id}|name=|idCandidates={failedCandidates}|raw={Convert.ToHexString(d)}"); return null; }
        int jobCode = 0;
        int serverId = 0;
        string className = "Unknown";
        if (nameOffset >= 0)
        {
            int afterName = nameOffset + 2 + d[nameOffset + 1];
            int q = afterName;
            if (q < d.Length && ReadV(d, ref q, out var jobU) && jobU >= 5 && jobU <= 40)
            {
                jobCode = (int)jobU;
                className = ClassFromJobCode(jobCode);
            }
            serverId = FindLikelyServerId(d, afterName);
        }
        RememberGlobalPlayerIdentity(id, best, className, utc, packetKind);
        Diagnostic?.Invoke(new(utc,"identity-map",$"Mapped entity {id} -> {best} class={className} job={jobCode} server={serverId}",d.Length));
        var idCandidates = DescribeIdentityCandidates(d, start);
        Diagnostic?.Invoke(new(utc,"identity",$"{packetKind} id={id} name={best} class={className} job={jobCode} server={serverId} idCandidates={idCandidates}",d.Length));
        ValidationRecord?.Invoke($"{utc:O}|tag=identity|packet={packetKind}|id={id}|name={best}|class={className}|jobCode={jobCode}|server={serverId}|nameOffset={nameOffset}|idCandidates={idCandidates}|bridgeFields={DescribeBridgeFields(d,start)}|bridgeStrings={DescribeBridgeStrings(d,start)}|combatIdHits={FindCombatIdEncodings(d)}|raw={Convert.ToHexString(d)}");
        ValidationRecord?.Invoke($"{utc:O}|tag=identityMap|entity={id}|name={best}|class={className}|jobCode={jobCode}|server={serverId}|source={packetKind}");
        return new(CombatKind.PlayerName,id,best,0,"","",0,DamageType.Unknown,0,0,"",0,className);
    }

    private static int FindLikelyServerId(ReadOnlySpan<byte> d, int afterName)
    {
        // Mirrors the current public A2Meter UserInfo parser: the server id is
        // carried later in the same player-info packet rather than next to the
        // combat entity id. Keep this metadata-only; names remain keyed by the
        // explicit packet entity id.
        int scanStart = Math.Min(afterName + 75, d.Length);
        int scanEnd = Math.Min(afterName + 108, d.Length) - 1;
        for (int i = scanStart; i < scanEnd; i++)
        {
            int sid = d[i] | (d[i + 1] << 8);
            if (sid >= 1001 && sid <= 2999)
                return sid;
        }
        return 0;
    }

    private static string DescribeIdentityCandidates(ReadOnlySpan<byte> d, int start)
    {
        var parts = new List<string>();
        int end = Math.Min(d.Length, start + 96);
        for (int i = Math.Max(0, start); i < end; i++)
        {
            int q = i;
            if (!ReadV(d, ref q, out var v)) continue;
            if (v >= 1000 && v <= 500000)
                parts.Add($"{i}:{v}");
            if (parts.Count >= 24) break;
        }
        return string.Join(",", parts);
    }

    private static string DescribeBridgeFields(ReadOnlySpan<byte> d, int start)
    {
        var parts = new List<string>();
        int end = Math.Min(d.Length, start + 160);
        for (int i = Math.Max(0, start); i < end; i++)
        {
            int q = i;
            if (!ReadV(d, ref q, out var v)) continue;
            if (v > 0 && v <= 200000000)
                parts.Add($"{i}:v={v}");
            if (parts.Count >= 40) break;
        }
        return string.Join(",", parts);
    }

    private static string DescribeBridgeStrings(ReadOnlySpan<byte> d, int start)
    {
        var found = new List<string>();
        int end = Math.Min(d.Length, start + 256);
        int i = Math.Max(0, start);
        while (i < end)
        {
            int j = i;
            while (j < end && d[j] >= 0x20 && d[j] <= 0x7E) j++;
            if (j - i >= 3)
            {
                var t = System.Text.Encoding.UTF8.GetString(d.Slice(i, Math.Min(j-i, 32)));
                found.Add($"{i}:{t.Replace("|","/")}");
                if (found.Count >= 8) break;
            }
            i = Math.Max(i + 1, j + 1);
        }
        return string.Join(",", found);
    }

    private Aion2Decoded? ObserveEntityBridge(ReadOnlySpan<byte> d, int p, DateTime utc, string packetKind)
    {
        int start = p;
        var fields = DescribeBridgeFields(d, p);
        var strings = DescribeBridgeStrings(d, p);
        if (packetKind == "mobSpawn")
        {
            int q = p;
            if (ReadV(d, ref q, out var rawEntity) && rawEntity > 0 && rawEntity <= long.MaxValue &&
                TryReadMobSpawnName(d, start, out var spawnName))
            {
                // In a 0x5F summon spawn this string is the OWNER name, not the
                // summon entity's player identity. Ownership is handled separately.
                if (q < d.Length && d[q] == 0x5F)
                {
                    ValidationRecord?.Invoke($"{utc:O}|tag=summonSpawnMetadata|summon={(long)rawEntity}|string={spawnName}|note=not-owner-name");
                    return null;
                }
                long entity = (long)rawEntity;
                long combatEntity = entity;
                for (int i = Math.Max(start, 0); i + 3 < d.Length; i++)
                {
                    long candidate = d[i] | ((long)d[i+1] << 8) | ((long)d[i+2] << 16) | ((long)d[i+3] << 24);
                    if (candidate != entity && recentCombatIds.Contains(candidate))
          {
              int marker = i + 4;
              if (marker + 3 < d.Length && d[marker] == 54 && d[marker + 1] == 8)
              {
                  int len = d[marker + 2];
                  if (len >= 3 && len <= 24 && marker + 3 + len <= d.Length &&
                      System.Text.Encoding.UTF8.GetString(d.Slice(marker + 3, len)) == spawnName)
                  {
                      combatEntity = candidate;
                      break;
                  }
              }
          }
                }
                identities[entity] = new PlayerIdentity(spawnName, "Unknown");
                identities[combatEntity] = new PlayerIdentity(spawnName, "Unknown");
                ValidationRecord?.Invoke($"{utc:O}|tag=spawnIdentity|entity={entity}|combatEntity={combatEntity}|name={spawnName}|fields={fields}|strings={strings}|combatIdHits={FindCombatIdEncodings(d)}|raw={Convert.ToHexString(d)}");
                Diagnostic?.Invoke(new(utc,"spawn-identity",$"Mapped spawn entity {entity} -> {spawnName}",d.Length));
                return new(CombatKind.PlayerName,combatEntity,spawnName,0,"","",0,DamageType.Unknown,0,0,"",0);
            }
        }
        ValidationRecord?.Invoke($"{utc:O}|tag=entityBridge|packet={packetKind}|fields={fields}|strings={strings}|combatIdHits={FindCombatIdEncodings(d)}|raw={Convert.ToHexString(d)}");
        Diagnostic?.Invoke(new(utc,"entity-bridge",$"{packetKind} fields={fields}",d.Length));
        return null;
    }

    private static bool TryReadMobSpawnName(ReadOnlySpan<byte> d, int start, out string name)
    {
        name = "";
        int end = Math.Min(d.Length - 3, start + 256);
        for (int i = Math.Max(0, start); i < end; i++)
        {
            if (d[i] != 0x36 || d[i + 1] != 0x08) continue;
            int len = d[i + 2];
            if (len < 3 || len > 24 || i + 3 + len > d.Length) continue;
            var bytes = d.Slice(i + 3, len);
            bool valid = true;
            for (int j = 0; j < bytes.Length; j++)
            {
                byte b = bytes[j];
                if (!((b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z') ||
                      (b >= (byte)'0' && b <= (byte)'9') || b == (byte)'_' || b == (byte)'-'))
                { valid = false; break; }
            }
            if (!valid) continue;
            name = System.Text.Encoding.UTF8.GetString(bytes);
            return true;
        }
        return false;
    }

    private static bool TryReadStructuredCharacterName(ReadOnlySpan<byte> d, int start, out string name, out int offset)
    {
        name = "";
        offset = -1;
        int end = Math.Min(d.Length - 2, start + 96);
        for (int i = Math.Max(0, start); i < end; i++)
        {
            // Global's July 2026 nickname update changed the character-name
            // marker observed in 45 36 user-info packets from 0x07 to 0x17.
            // Accept both layouts; the following length/name validation remains
            // identical and prevents arbitrary strings from becoming identities.
            if (d[i] != 0x07 && d[i] != 0x17) continue;
            int len = d[i + 1];
            if (len < 2 || len > 24 || i + 2 + len > d.Length) continue;
            var bytes = d.Slice(i + 2, len);
            bool valid = true;
            for (int j = 0; j < bytes.Length; j++)
            {
                byte b = bytes[j];
                if (!((b >= (byte)'A' && b <= (byte)'Z') ||
                      (b >= (byte)'a' && b <= (byte)'z') ||
                      (b >= (byte)'0' && b <= (byte)'9') ||
                      b == (byte)'_' || b == (byte)'-')) { valid = false; break; }
            }
            if (!valid) continue;
            name = System.Text.Encoding.UTF8.GetString(bytes);
            offset = i;
            return true;
        }
        return false;
    }

    private void RememberCombatEntity(ulong id)
    {
        if (id == 0 || id > 500000) return;
        recentCombatEntityIds.Add((long)id);
        if (recentCombatEntityIds.Count > 128)
            recentCombatEntityIds.Remove(recentCombatEntityIds.First());
    }

    private string FindCombatIdEncodings(ReadOnlySpan<byte> d)
    {
        var hits = new List<string>();
        foreach (var id in recentCombatEntityIds)
        {
            ulong u = (ulong)id;
            for (int i = 0; i < d.Length; i++)
            {
                int q = i;
                if (ReadV(d, ref q, out var vv) && vv == u)
                    hits.Add($"{id}:varint@{i}");
                if (i + 2 <= d.Length && (ulong)(d[i] | (d[i+1] << 8)) == u)
                    hits.Add($"{id}:le16@{i}");
                if (i + 3 <= d.Length && (ulong)(d[i] | (d[i+1] << 8) | (d[i+2] << 16)) == u)
                    hits.Add($"{id}:le24@{i}");
                if (i + 4 <= d.Length)
                {
                    uint le32 = (uint)(d[i] | (d[i+1] << 8) | (d[i+2] << 16) | (d[i+3] << 24));
                    if (le32 == u) hits.Add($"{id}:le32@{i}");
                }
                if (hits.Count >= 48) return string.Join(",", hits.Distinct());
            }
        }
        return string.Join(",", hits.Distinct());
    }

    private Aion2Decoded? TryCharacterLookup(ReadOnlySpan<byte> d, int p, DateTime utc)
    {
        int start = p;
        // Current lookup layout: padding(2), 0x07, varint name length, UTF-8 name,
        // job(1), zeros(3), marker(1), local-like flag(1), level(1), zeros(7),
        // entityId LE32, serverId LE16.
        if (p + 3 > d.Length) return null;
        p += 2;
        if (p >= d.Length || d[p] != 0x07) return null;
        p++;
        if (!ReadV(d, ref p, out var nameLenU)) return null;
        if (nameLenU < 1 || nameLenU > 72 || nameLenU > int.MaxValue) return null;
        int nameLen = (int)nameLenU;
        if (p + nameLen > d.Length) return null;
        string name;
        try { name = System.Text.Encoding.UTF8.GetString(d.Slice(p, nameLen)); }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(name)) return null;
        p += nameLen;
        if (p + 20 > d.Length) return null;
        int jobCode = d[p]; p++;
        p += 3;
        p++;
        p++;
        int level = d[p]; p++;
        p += 7;
        long entityId = (long)((uint)d[p] | ((uint)d[p+1] << 8) | ((uint)d[p+2] << 16) | ((uint)d[p+3] << 24));
        p += 4;
        int serverId = d[p] | (d[p+1] << 8);
        if (entityId <= 0 || entityId > int.MaxValue) return null;
        string lookupClass = ClassFromJobCode(jobCode);
        RememberGlobalPlayerIdentity(entityId, name, lookupClass, utc, "charLookup");
        var combatHit = recentCombatEntityIds.Contains(entityId) ? "YES" : "NO";
        Diagnostic?.Invoke(new(utc,"char-lookup",$"Mapped lookup entity {entityId} -> {name} job={jobCode} level={level} server={serverId} combatMatch={combatHit}",d.Length));
        ValidationRecord?.Invoke($"{utc:O}|tag=charLookupIdentity|entity={entityId}|name={name}|job={jobCode}|level={level}|server={serverId}|combatMatch={combatHit}|raw={Convert.ToHexString(d)}");
        return new(CombatKind.PlayerName, entityId, name, 0, "", "", 0, DamageType.Unknown, 0,0,"",0);
    }

    private Aion2Decoded? ObserveSelfIdentity(ReadOnlySpan<byte> d, int p, DateTime utc)
    {
        int start = p;
        if (!ReadV(d, ref p, out var idU) || idU == 0 || idU > long.MaxValue) return null;
        long id = (long)idU;
        string best = "";
        int nameOffset = -1;
        int end = Math.Min(d.Length - 1, p + 32);
        for (int i = p; i < end; i++)
        {
            int len = d[i];
            if (len < 2 || len > 24 || i + 1 + len > d.Length) continue;
            bool valid = true;
            for (int j = 0; j < len; j++)
            {
                byte b = d[i + 1 + j];
                if (!((b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z') ||
                      (b >= (byte)'0' && b <= (byte)'9') || b == (byte)'_' || b == (byte)'-')) { valid = false; break; }
            }
            if (!valid) continue;
            best = System.Text.Encoding.UTF8.GetString(d.Slice(i + 1, len));
            nameOffset = i;
            break;
        }
        if (string.IsNullOrWhiteSpace(best)) {
            ValidationRecord?.Invoke($"{utc:O}|tag=selfIdentity|entity={id}|name=|status=no-name|raw={Convert.ToHexString(d)}");
            return null;
        }
        int serverId = 0;
        int jobCode = 0;
        int extra = -1;
        string className = "Unknown";
        int afterName = nameOffset >= 0 ? nameOffset + 1 + d[nameOffset] : -1;
        if (afterName >= 0 && afterName + 7 <= d.Length)
        {
            int sid = d[afterName] | (d[afterName + 1] << 8);
            int job = d[afterName + 2] | (d[afterName + 3] << 8) | (d[afterName + 4] << 16) | (d[afterName + 5] << 24);
            int extraByte = d[afterName + 6];
            if (sid >= 1001 && sid <= 2999 && job >= 5 && job <= 40 && extraByte <= 2)
            {
                serverId = sid;
                jobCode = job;
                extra = extraByte;
                className = ClassFromJobCode(jobCode);
            }
        }
        selfEntityId=id;
        RememberGlobalPlayerIdentity(id, best, className, utc, "selfInfo");
        Diagnostic?.Invoke(new(utc,"identity-map",$"Mapped self entity {id} -> {best} class={className} job={jobCode} server={serverId}",d.Length));
        ValidationRecord?.Invoke($"{utc:O}|tag=selfIdentity|entity={id}|name={best}|class={className}|jobCode={jobCode}|server={serverId}|extra={extra}|nameOffset={nameOffset}|raw={Convert.ToHexString(d)}");
        ValidationRecord?.Invoke($"{utc:O}|tag=identityMap|entity={id}|name={best}|class={className}|jobCode={jobCode}|server={serverId}|source=selfInfo");
        return new(CombatKind.PlayerName, id, best, 0, "", "", 0, DamageType.Unknown, 0,0,"",0,className);
    }

    private IEnumerable<Aion2Decoded> TryObservePartyIdentities(byte[] d, int start, DateTime utc, byte opcode)
    {
        // Current party roster layout places CharacterId 8 bytes before the
        // length-prefixed nickname and ServerId immediately before the name.
        // Require all three structural checks before accepting a mapping.
        var emitted = new HashSet<long>();
        int end = Math.Min(d.Length, start + 4096);
        for (int off = Math.Max(start + 8, 8); off < end; off++)
        {
            int len = d[off];
            if (len < 2 || len > 48 || off + 1 + len > end) continue;
            int sid = d[off - 2] | (d[off - 1] << 8);
            if (sid < 1001 || sid > 2021) continue;
            uint cid = (uint)(d[off - 8] | (d[off - 7] << 8) | (d[off - 6] << 16) | (d[off - 5] << 24));
            if (cid == 0 || cid > 500000) continue;
            string name;
            try { name = System.Text.Encoding.UTF8.GetString(d, off + 1, len); } catch { continue; }
            if (string.IsNullOrWhiteSpace(name) || name.All(char.IsDigit)) continue;
            bool valid = true;
            foreach (char c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) { valid = false; break; }
            }
            if (!valid) continue;
            long id = cid;
            int afterName = off + 1 + len;
            int jobCode = 0;
            int level = 0;
            string className = "Unknown";
            if (afterName + 8 <= end)
            {
                jobCode = d[afterName] | (d[afterName+1] << 8) | (d[afterName+2] << 16) | (d[afterName+3] << 24);
                level = d[afterName+4] | (d[afterName+5] << 8) | (d[afterName+6] << 16) | (d[afterName+7] << 24);
                if (level >= 1 && level <= 55)
                {
                    className = ClassFromJobCode(jobCode);
                }
            }
            partyIdentities[id] = new PlayerIdentity(name, className);
            RememberGlobalPlayerIdentity(id, name, className, utc, "party");
            ValidationRecord?.Invoke($"{utc:O}|tag=partyIdentity|opcode=0x{opcode:X2}|entity={id}|name={name}|server={sid}|jobCode={jobCode}|class={className}|level={level}|combatMatch={recentCombatEntityIds.Contains(id)}");
            Diagnostic?.Invoke(new(utc,"party-identity",$"Mapped party entity {id} -> {name} class={className} job={jobCode} level={level} server={sid}",d.Length));
            if (emitted.Add(id))
                yield return new(CombatKind.PlayerName, id, name, 0, "", "", 0, DamageType.Unknown, 0,0,"",0);
        }
    }

    private static string ClassFromJobCode(int code) => code switch
    {
        >= 5 and <= 8 => "Gladiator",
        >= 9 and <= 12 => "Templar",
        >= 13 and <= 16 => "Ranger",
        >= 17 and <= 20 => "Assassin",
        >= 21 and <= 24 => "Spiritmaster",
        >= 25 and <= 28 => "Sorcerer",
        >= 29 and <= 32 => "Cleric",
        >= 33 and <= 36 => "Chanter",
        >= 37 and <= 40 => "Brawler",
        _ => "Unknown"
    };

    private static string ClassFromSkill(int skill)
    {
        int family = Math.Abs(skill) / 1000000;
        return family switch { 11 => "Gladiator", 12 => "Templar", 13 => "Assassin", 14 => "Ranger", 15 => "Sorcerer", 16 => "Spiritmaster", 17 => "Cleric", 18 => "Chanter", _ => "Unknown" };
    }

    private Aion2Decoded? TryEmbeddedIdentity(ReadOnlySpan<byte> d, DateTime utc)
    {
        for (int i = 0; i + 2 < d.Length; i++)
        {
            string? kind = null;
            if (d[i] == 51 && d[i + 1] == 54) kind = "selfInfo";
            else if (d[i] == 69 && d[i + 1] == 54) kind = "otherInfo";
            if (kind is null) continue;

            var evt = kind == "selfInfo"
                ? ObserveSelfIdentity(d, i + 2, utc)
                : ObserveIdentity(d, i + 2, utc, kind);
            if (evt is not null)
            {
                ValidationRecord?.Invoke($"{utc:O}|tag=embeddedIdentity|packet={kind}|entity={evt.SourceId}|name={evt.Source}|offset={i}|raw={Convert.ToHexString(d)}");
                return evt;
            }
        }
        return null;
    }

    private void TraceGlobalSessionCandidates(ReadOnlySpan<byte> d, DateTime utc)
    {
        for (int i = 0; i + 1 < d.Length; i++)
        {
            if (d[i] != 0x20 || d[i + 1] != 0x36) continue;
            int from = Math.Max(0, i - 8);
            int count = Math.Min(d.Length - from, 48);
            var window = d.Slice(from, count);
            var vals = new List<string>();
            for (int q0 = i + 2; q0 < Math.Min(d.Length, i + 24); q0++)
            {
                int q = q0;
                if (ReadV(d, ref q, out var v)) vals.Add($"{q0-i}:v={v}");
            }
            ValidationRecord?.Invoke($"{utc:O}|tag=globalSessionCandidate|offset={i}|frameLen={d.Length}|varints={string.Join(",", vals)}|window={Convert.ToHexString(window)}|raw={Convert.ToHexString(d)}");
        }
    }

    private Aion2Decoded? TryGlobalSessionLink(ReadOnlySpan<byte> d, int tagOffset, DateTime utc)
    {
        int p = tagOffset + 2;
        if (p + 2 > d.Length) return null;
        p += 2;
        if (!ReadV(d, ref p, out var rawSession) || rawSession == 0 || rawSession > long.MaxValue) return null;
        if (p + 8 > d.Length) return null;
        p += 4;
        uint global = (uint)(d[p] | (d[p+1] << 8) | (d[p+2] << 16) | (d[p+3] << 24));
        if (global == 0) return null;
        long session = (long)rawSession;
        long globalId = global;
        sessionToGlobal[session] = globalId;
        ValidationRecord?.Invoke($"{utc:O}|tag=globalSessionLink|session={session}|global={globalId}|offset={tagOffset}|raw={Convert.ToHexString(d)}");
        PlayerIdentity? known = null;
        if (identities.TryGetValue(globalId, out var direct))
            known = direct;
        else if (partyIdentities.TryGetValue(globalId, out var party))
            known = party;
        else if (globalPlayerNames.TryGetValue(globalId, out var globalName) && !string.IsNullOrWhiteSpace(globalName))
            known = new PlayerIdentity(globalName, "Unknown");

        // The live 2026-10-04 sequence proves the same stable id can appear
        // behind different local session ids across a transition. If the old
        // session is already the trusted local player, seed the stable id now
        // so the next session can resolve immediately.
        if (known is null && session == selfEntityId && identities.TryGetValue(session, out var localIdentity))
        {
            known = localIdentity;
            identities[globalId] = localIdentity;
            globalPlayerNames[globalId] = localIdentity.Name;
            ValidationRecord?.Invoke($"{utc:O}|tag=sessionPromotedGlobal|session={session}|global={globalId}|name={localIdentity.Name}|class={localIdentity.ClassName}|source=globalSessionLink");
        }

        if (known is null || string.IsNullOrWhiteSpace(known.Name)) return null;
        identities[session] = known;
        ValidationRecord?.Invoke($"{utc:O}|tag=globalSessionName|session={session}|global={globalId}|name={known.Name}|class={known.ClassName}");
        return new(CombatKind.PlayerName,session,known.Name,0,"","",0,DamageType.Unknown,0,0,"",0,known.ClassName);
    }

    private void TraceExtendedIdentityCandidates(ReadOnlySpan<byte> d, DateTime utc)
    {
        if (recentCombatEntityIds.Count == 0 || d.Length < 8) return;
        var hits = FindCombatIdEncodings(d);
        if (string.IsNullOrWhiteSpace(hits)) return;
        if (!TryReadStructuredCharacterName(d, 0, out var name, out var nameOffset)) return;

        int afterName = nameOffset + 2 + d[nameOffset + 1];
        int q = afterName;
        if (!ReadV(d, ref q, out var jobU) || jobU < 5 || jobU > 40) return;

        int p = 0;
        if (!ReadV(d, ref p, out _) || p + 1 >= d.Length) return;
        byte a = d[p], b = d[p + 1];
        ValidationRecord?.Invoke($"{utc:O}|tag=extendedIdentityCandidate|opcode={a:X2}{b:X2}|combatIdHits={hits}|name={name}|jobCode={(int)jobU}|class={ClassFromJobCode((int)jobU)}|server={FindLikelyServerId(d, afterName)}|raw={Convert.ToHexString(d)}");
    }

    private void TraceCombatIdentityCandidates(ReadOnlySpan<byte> d, DateTime utc)
    {
        if (recentCombatEntityIds.Count == 0 || d.Length < 4) return;
        var hits = FindCombatIdEncodings(d);
        if (string.IsNullOrWhiteSpace(hits)) return;

        int p = 0;
        if (!ReadV(d, ref p, out _) || p + 1 >= d.Length) return;
        byte a = d[p], b = d[p + 1];

        // Damage and DOT packets already log explicit source/target IDs.
        if ((a == 0x04 && b == 0x38) || (a == 0x05 && b == 0x38)) return;

        ValidationRecord?.Invoke($"{utc:O}|tag=combatIdentityCandidate|opcode={a:X2}{b:X2}|combatIdHits={hits}|strings={DescribeBridgeStrings(d,0)}|frameLen={d.Length}|raw={Convert.ToHexString(d)}");
    }

    private void TraceIdentityLifecycle(ReadOnlySpan<byte> d, DateTime utc)
    {
        if (d.Length < 4) return;
        int p = 0;
        if (!ReadV(d, ref p, out _)) return;
        if (p + 1 >= d.Length) return;
        byte a = d[p], b = d[p + 1];
        bool interesting = (b == 0x36 && (a == 0x20 || a == 0x33 || a == 0x41 || a == 0x45 || a == 0x49)) ||
                           (b == 0x97 && (a == 0x01 || a == 0x02 || a == 0x0B || a == 0x1D));
        if (!interesting) return;
        ValidationRecord?.Invoke($"{utc:O}|tag=identityLifecycle|opcode={a:X2}{b:X2}|frameLen={d.Length}|raw={Convert.ToHexString(d)}");
    }

    private static string SkillName(int skill)
    {
        return PublicGameData.SkillName(skill);
    }

    private static bool ReadV(ReadOnlySpan<byte> d, ref int p, out ulong value) {
        value=0; int shift=0; for(int n=0;n<10 && p<d.Length;n++) { byte b=d[p++]; value|=(ulong)(b&0x7F)<<shift; if((b&0x80)==0)return true; shift+=7; } value=0; return false;
    }
    private static DamageFlags DecodeFlags(byte t, byte mods, byte direction) {
        DamageFlags f = DamageFlags.None;
        if (t == 3) f |= DamageFlags.Critical;
        if ((mods & 0x02)!=0) f |= DamageFlags.Parry;
        if ((mods & 0x04)!=0) f |= DamageFlags.Perfect;
        if ((mods & 0x08)!=0) f |= DamageFlags.Double;
        if (direction == 0x01) f |= DamageFlags.Back;
        if (direction == 0x02) f |= DamageFlags.Frontal;
        return f;
    }
    private static DamageType DecodeType(byte t, byte mods, byte direction) {
        if (t == 3) return DamageType.Crit;
        if ((mods & 0x04)!=0) return DamageType.Perfect;
        if ((mods & 0x08)!=0) return DamageType.Double;
        if ((mods & 0x02)!=0) return DamageType.Parry;
        if (direction == 0x01) return DamageType.Back;
        if (direction == 0x02) return DamageType.Frontal;
        return DamageType.Direct;
    }
}





























