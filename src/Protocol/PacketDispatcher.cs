namespace Aion2DPSPro.Protocol;

/// Experimental parser adapted from the public A2Meter event layout. Every read is bounds-checked;
/// implausible packets are rejected and logged rather than emitted as combat data.
public sealed class PacketDispatcher
{
    private readonly HashSet<long> recentCombatIds = new();
    private readonly ProtocolProfile profile;
    private readonly Dictionary<long, PlayerIdentity> identities = new();
    private readonly Dictionary<long, string> globalPlayerNames = new();
    private readonly Dictionary<long, long> sessionToGlobal = new();
    private readonly Dictionary<long, PlayerIdentity> partyIdentities = new();
    private readonly HashSet<long> recentCombatEntityIds = new();
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
        
var kind = profile.Tags.FirstOrDefault(kv => kv.Value.A==a && kv.Value.B==b).Key;
        if (kind is null) { Diagnostic?.Invoke(new(utc,"dispatch",$"Unknown tag 0x{a:X2}{b:X2}",frame.Length)); yield break; }

        TraceIdentityLifecycle(frame, utc);
        TraceGlobalSessionCandidates(frame, utc);
        var embeddedLink = TryEmbeddedGlobalSessionLink(frame, utc);
        if (embeddedLink is not null)
            yield return embeddedLink;

        var embeddedIdentity = TryEmbeddedIdentity(frame, utc);
        if (embeddedIdentity is not null)
            yield return embeddedIdentity;

        Aion2Decoded? evt = kind switch {
            "damage" => TryDamage(frame, p+2, utc),
            "dot" => TryDot(frame, p+2, utc),
            "bossHp" => TryBossHp(frame, p+2, utc),
            "mobSpawn" => ObserveEntityBridge(frame, p+2, utc, "mobSpawn"),
            "entityRemoved" => ObserveEntityBridge(frame, p+2, utc, "entityRemoved"),
            "selfInfo" => ObserveSelfIdentity(frame, p+2, utc),
            "otherInfo" => ObserveIdentity(frame, p+2, utc, "otherInfo"),
            "charLookup" => TryCharacterLookup(frame, p+2, utc),
            _ => null
        };
        if (evt is not null) { Diagnostic?.Invoke(new(utc,"parse",$"Parsed {kind}",frame.Length)); var candidates = kind == "damage" ? DescribeVarintCandidates(frame, FindPostSkillPosition(frame, p+2)) : "";
      ValidationRecord?.Invoke($"{utc:O}|tag={kind}|src={evt.SourceId}|tgt={evt.TargetId}|skill={evt.Skill}|amount={evt.Amount}|type={evt.DamageType}|candidates={candidates}|raw={Convert.ToHexString(frame)}"); yield return evt; }
        else Diagnostic?.Invoke(new(utc,"dispatch",$"Matched {kind}; no validated event emitted",frame.Length));
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
        byte flagByte=0; int adjust=0;
        if (p+1<d.Length && (p+2>=d.Length || d[p+1]==0)) { flagByte=d[p]; p+=2; adjust=1; }
        int trailer=trailing[category]-adjust*2; if (trailer<0 || p+trailer>d.Length) return null; p+=trailer;
        if (!ReadV(d, ref p, out _)) return null;
        if (!ReadV(d, ref p, out _)) return null;
        if (!ReadV(d, ref p, out var damage) || damage==0 || damage>9_000_000_000UL) return null;

        if (damage <= 4)
        {
            if (TryRecoverAlternateDamage(d, out var recoveredDamage))
                damage = recoveredDamage;
        }

        RememberCombatEntity(actor);
        RememberCombatEntity(target);
        var dtype = DecodeType((byte)damageType, flagByte);
        Diagnostic?.Invoke(new(utc,"damage-flags",$"rawType={damageType} flagByte=0x{flagByte:X2} decoded={dtype}",d.Length));
        long actorId = checked((long)actor);
        long targetId = checked((long)target);
        var actorName = ResolveName(actorId, "Actor");
        var actorClass = identities.TryGetValue(actorId, out var knownIdentity) && knownIdentity.ClassName != "Unknown"
            ? knownIdentity.ClassName : ClassFromSkill(skill);
        return new(CombatKind.Damage, actorId, actorName, targetId, ResolveName(targetId, "Target"),
            SkillName(checked((int)skill)), (long)damage, dtype, 0,0,"",0, actorClass);
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
        var actorName = ResolveName(actorId, "Actor");
        var actorClass = identities.TryGetValue(actorId, out var knownIdentity) && knownIdentity.ClassName != "Unknown"
            ? knownIdentity.ClassName : ClassFromSkill((int)skill);
        return new(damage>0?CombatKind.Damage:CombatKind.Heal, actorId,actorName,targetId,ResolveName(targetId, "Target"),
            SkillName(checked((int)skill)), damage>0?damage:(long)heal, DamageType.Dot,0,0,"",0, actorClass);
    }

    private Aion2Decoded? TryBossHp(ReadOnlySpan<byte> d, int p, DateTime utc)
    {
        if (!ReadV(d,ref p,out var entity) || entity==0) return null;
        // Layout varies; conservatively scan the remaining payload for two plausible adjacent varints.
        for(int i=p;i<d.Length;i++) { int q=i; if(!ReadV(d,ref q,out var cur)) continue; if(!ReadV(d,ref q,out var max)) continue;
            if(max>0 && cur<=max && max>=1000 && max<=long.MaxValue)
                return new(CombatKind.TargetHp,0,"",checked((long)entity),$"Target {entity}","",0,DamageType.Unknown,(long)cur,(long)max,"",0);
        }
        return null;
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
        int start = Math.Max(0, d.Length - 12);
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

    private string ResolveName(long id, string fallback)
        => identities.TryGetValue(id, out var x) ? x.Name : $"{fallback} {id}";

    private Aion2Decoded? ObserveIdentity(ReadOnlySpan<byte> d, int p, DateTime utc, string packetKind)
    {
        int start = p;
        if (!ReadV(d, ref p, out var rawId) || rawId == 0 || rawId > long.MaxValue) return null;
        long id = (long)rawId;
        string? best = null;
        int nameOffset = -1;
        if (TryReadStructuredCharacterName(d, start, out var structuredName, out nameOffset))
            best = structuredName;
        if (string.IsNullOrWhiteSpace(best) || best.Length < 3) { var failedCandidates = DescribeIdentityCandidates(d, start);
        Diagnostic?.Invoke(new(utc,"identity",$"{packetKind} id={id} no validated name idCandidates={failedCandidates}",d.Length));
        ValidationRecord?.Invoke($"{utc:O}|tag=identity|packet={packetKind}|id={id}|name=|idCandidates={failedCandidates}|raw={Convert.ToHexString(d)}"); return null; }
        identities[id] = new PlayerIdentity(best, "Unknown");
        globalPlayerNames[id] = best;
        foreach (var link in sessionToGlobal.Where(x => x.Value == id).ToArray())
        {
            identities[link.Key] = new PlayerIdentity(best, "Unknown");
            ValidationRecord?.Invoke($"{utc:O}|tag=lateGlobalSessionName|session={link.Key}|global={id}|name={best}");
        }
        Diagnostic?.Invoke(new(utc,"identity-map",$"Mapped entity {id} -> {best}",d.Length));
        var idCandidates = DescribeIdentityCandidates(d, start);
        Diagnostic?.Invoke(new(utc,"identity",$"{packetKind} id={id} name={best} idCandidates={idCandidates}",d.Length));
        ValidationRecord?.Invoke($"{utc:O}|tag=identity|packet={packetKind}|id={id}|name={best}|nameOffset={nameOffset}|idCandidates={idCandidates}|bridgeFields={DescribeBridgeFields(d,start)}|bridgeStrings={DescribeBridgeStrings(d,start)}|combatIdHits={FindCombatIdEncodings(d)}|raw={Convert.ToHexString(d)}");
        ValidationRecord?.Invoke($"{utc:O}|tag=identityMap|entity={id}|name={best}|source={packetKind}");
        return new(CombatKind.PlayerName,id,best,0,"","",0,DamageType.Unknown,0,0,"",0);
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
            if (d[i] != 0x07) continue;
            int len = d[i + 1];
            if (len < 3 || len > 24 || i + 2 + len > d.Length) continue;
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
        identities[entityId] = new PlayerIdentity(name, "Unknown");
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
            if (len < 3 || len > 24 || i + 1 + len > d.Length) continue;
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
        identities[id] = new PlayerIdentity(best, "Unknown");
        globalPlayerNames[id] = best;
        foreach (var link in sessionToGlobal.Where(x => x.Value == id).ToArray())
        {
            identities[link.Key] = new PlayerIdentity(best, "Unknown");
            ValidationRecord?.Invoke($"{utc:O}|tag=lateGlobalSessionName|session={link.Key}|global={id}|name={best}");
        }
        Diagnostic?.Invoke(new(utc,"identity-map",$"Mapped self entity {id} -> {best}",d.Length));
        ValidationRecord?.Invoke($"{utc:O}|tag=selfIdentity|entity={id}|name={best}|nameOffset={nameOffset}|raw={Convert.ToHexString(d)}");
        ValidationRecord?.Invoke($"{utc:O}|tag=identityMap|entity={id}|name={best}|source=selfInfo");
        return new(CombatKind.PlayerName, id, best, 0, "", "", 0, DamageType.Unknown, 0,0,"",0);
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
                    className = jobCode switch { 11 => "Gladiator", 12 => "Templar", 13 => "Assassin", 14 => "Ranger", 15 => "Sorcerer", 16 => "Spiritmaster", 17 => "Cleric", 18 => "Chanter", _ => "Unknown" };
                }
            }
            partyIdentities[id] = new PlayerIdentity(name, className);
            identities[id] = new PlayerIdentity(name, className);
            ValidationRecord?.Invoke($"{utc:O}|tag=partyIdentity|opcode=0x{opcode:X2}|entity={id}|name={name}|server={sid}|jobCode={jobCode}|class={className}|level={level}|combatMatch={recentCombatEntityIds.Contains(id)}");
            Diagnostic?.Invoke(new(utc,"party-identity",$"Mapped party entity {id} -> {name} class={className} job={jobCode} level={level} server={sid}",d.Length));
            if (emitted.Add(id))
                yield return new(CombatKind.PlayerName, id, name, 0, "", "", 0, DamageType.Unknown, 0,0,"",0);
        }
    }

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

            var evt = ObserveIdentity(d, i + 2, utc, kind);
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
        if (!globalPlayerNames.TryGetValue(globalId, out var name) || string.IsNullOrWhiteSpace(name)) return null;
        identities[session] = new PlayerIdentity(name, "Unknown");
        ValidationRecord?.Invoke($"{utc:O}|tag=globalSessionName|session={session}|global={globalId}|name={name}");
        return new(CombatKind.PlayerName,session,name,0,"","",0,DamageType.Unknown,0,0,"",0);
    }

    private Aion2Decoded? TryEmbeddedGlobalSessionLink(ReadOnlySpan<byte> d, DateTime utc)
    {
        for (int i = 0; i + 2 < d.Length; i++)
            if (d[i] == 0x20 && d[i + 1] == 0x36)
                return TryGlobalSessionLink(d, i, utc);
        return null;
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
    private static DamageType DecodeType(byte t, byte flags) {
        if ((flags & 0x02)!=0) return DamageType.Back;
        if ((flags & 0x01)!=0) return DamageType.Frontal;
        if (t == 3) return DamageType.Crit;
        return DamageType.Direct;
    }
}




























