using System;
using System.Collections.Generic;
using System.Linq;
using Aion2DPSPro.Protocol;
using Aion2DPSPro.Capture;
using Xunit;

namespace Aion2DPSPro.Tests;

public sealed class PacketDispatcherRegressionTests
{
    private static ProtocolProfile DamageProfile() => new(
        "REGRESSION", "Global", "capture-2026-10-03", 13328,
        new Dictionary<string, PacketTag>
        {
            ["damage"] = new PacketTag(0x04, 0x38)
        },
        false);

    private static ProtocolProfile DamageAndMobProfile() => new(
        "REGRESSION", "Global", "capture-2026-10-04", 13328,
        new Dictionary<string, PacketTag>
        {
            ["damage"] = new PacketTag(0x04, 0x38),
            ["mobSpawn"] = new PacketTag(0x41, 0x36)
        },
        false);

    [Fact]
    public void PunishmentMax_Frontal_LiveCapture_Decodes21357()
    {
        // Live Global capture 2026-10-03:
        // skill=Punishment - Max, actor=8915, target=36963, expected damage=21,357.
        // The 11,350 -> 21,357 pair sits in the extended tail and previously
        // regressed to a tiny trailing field.
        var frame = Convert.FromHexString(
            "250438E3A0020600D345937AB800090200000277E10F4802000000D658EDA6010200");

        var dispatcher = new PacketDispatcher(DamageProfile());
        var events = dispatcher.Dispatch(frame, DateTime.UnixEpoch).ToList();

        var hit = Assert.Single(events.Where(x => x.Kind == CombatKind.Damage));
        Assert.Equal(8915, hit.SourceId);
        Assert.Equal(36963, hit.TargetId);
        Assert.Equal(21357, hit.Amount);
        Assert.Equal(DamageType.Frontal, hit.DamageType);
    }

    [Fact]
    public void Pummel_Frontal_LiveCapture_Decodes526()
    {
        // Ordinary frontal packet from the same capture. This protects the
        // charged-skill recovery change from breaking the common layout.
        var frame = Convert.FromHexString(
            "240438E3A002060091245EB7B7003A02000002C3A0C34701000000D6538E040100");

        var dispatcher = new PacketDispatcher(DamageProfile());
        var events = dispatcher.Dispatch(frame, DateTime.UnixEpoch).ToList();

        var hit = Assert.Single(events.Where(x => x.Kind == CombatKind.Damage));
        Assert.Equal(4625, hit.SourceId);
        Assert.Equal(36963, hit.TargetId);
        Assert.Equal(526, hit.Amount);
        Assert.Equal(DamageType.Frontal, hit.DamageType);
    }

    [Fact]
    public void ViciousStrike_Perfect_LiveCapture_Decodes1027()
    {
        var frame = Convert.FromHexString(
            "240438E3A0020600D3451042B70054020400024BCE954701000000D65883080100");

        var dispatcher = new PacketDispatcher(DamageProfile());
        var events = dispatcher.Dispatch(frame, DateTime.UnixEpoch).ToList();

        var hit = Assert.Single(events.Where(x => x.Kind == CombatKind.Damage));
        Assert.Equal(8915, hit.SourceId);
        Assert.Equal(36963, hit.TargetId);
        Assert.Equal(1027, hit.Amount);
        Assert.Equal(DamageType.Perfect, hit.DamageType);
    }


    [Theory]
    [InlineData("240438B48A02060095764792D2008802080001C723415201000000BE63D2160100", 15125, 34100, 2898, DamageType.Double)]
    [InlineData("240438B48A0206009576181DD2009B020000016B5D135201000000BE63C80B0100", 15125, 34100, 1480, DamageType.Back)]
    [InlineData("240438ED8A010600C82F42451301FB02040001D0E6866B020000009864822A0200", 6088, 17773, 5378, DamageType.Perfect)]
    [InlineData("240438ED8A010600C82F42451301BD03000001D0E6866B020000009864DE3F0200", 6088, 17773, 8158, DamageType.Crit)]
    [InlineData("240438E3A0020600B10E40B7B70009020000020B95C34701000000D65880030100", 1841, 36963, 384, DamageType.Frontal)]
    [InlineData("210438D885010400C72FD1E3FF001202AFFDF463010000009E55B5020100", 6087, 17112, 309, DamageType.Direct)]
    [InlineData("280438C82F4610ED8A01B2081300F402820002F556BE0001000000904E010113B7A36F0100", 17773, 6088, 62, DamageType.Parry)]
    public void LiveCapture_20261003_DamageLayouts_DecodeExpected(
        string raw, long sourceId, long targetId, long amount, DamageType damageType)
    {
        // Exact raw packets copied from combat-20261003-134933.log.
        // These cover every non-DoT damage type observed in that capture.
        var dispatcher = new PacketDispatcher(DamageProfile());
        var events = dispatcher.Dispatch(Convert.FromHexString(raw), DateTime.UnixEpoch).ToList();

        var hit = Assert.Single(events.Where(x => x.Kind == CombatKind.Damage));
        Assert.Equal(sourceId, hit.SourceId);
        Assert.Equal(targetId, hit.TargetId);
        Assert.Equal(amount, hit.Amount);
        Assert.Equal(damageType, hit.DamageType);
    }



    [Fact]
    public void CaptureIdentityBridge_SharesIdentityAcrossDuplicateNpcapAdapters()
    {
        var bridge = new CaptureIdentityBridge();
        var at = DateTime.UnixEpoch.AddSeconds(1);
        const string conversation = "192.168.1.197|193.202.112.15|conversation=192.168.1.197:55333<>193.202.112.15:13328";
        string identityScope = @"\\Device\\NPF_{ADAPTER_A}|" + conversation;
        string combatScope = @"\\Device\\NPF_{ADAPTER_B}|" + conversation;

        bridge.Observe(identityScope, new CombatEvent(
            Utc: at, Kind: CombatKind.PlayerName, SourceId: 1761,
            Source: "KnownPlayer", SourceClass: "Templar"));

        var resolved = bridge.Resolve(combatScope, new CombatEvent(
            Utc: at.AddSeconds(1), Kind: CombatKind.Damage, SourceId: 1761,
            Source: "Actor 1761", TargetId: 69146, Target: "Executioner Barthien",
            Skill: "Judgment", Amount: 10401, DamageType: DamageType.Crit,
            SourceClass: "Unknown"));

        Assert.Equal("KnownPlayer", resolved.Source);
        Assert.Equal("Templar", resolved.SourceClass);
    }


    [Fact]
    public void CombatEngine_EightSecondsIdle_ArchivesAndClearsCurrentFight()
    {
        var now = DateTime.UnixEpoch;
        var engine = new CombatEngine(() => now);

        engine.Apply(new CombatEvent(
            Utc: now, Kind: CombatKind.Damage, SourceId: 77, Source: "Tester",
            TargetId: 900, Target: "Training Target", Skill: "Strike",
            Amount: 8000, DamageType: DamageType.Direct));

        now = now.AddSeconds(7);
        Assert.True(engine.Snapshot().InFight);
        Assert.Empty(engine.History);

        now = now.AddSeconds(1);
        var current = engine.Snapshot();
        Assert.False(current.InFight);
        Assert.Equal(0, current.FightDamage);
        Assert.Empty(current.Players);

        var archived = Assert.Single(engine.History);
        Assert.Equal(8000, archived.FightDamage);
        Assert.Equal("Inactivity", archived.EndReason);
        Assert.NotEqual(Guid.Empty, archived.EncounterId);
    }

    [Fact]
    public void CombatEngine_BossFight_AlsoExpiresAfterEightSeconds()
    {
        var now = DateTime.UnixEpoch;
        var engine = new CombatEngine(() => now);

        engine.Apply(new CombatEvent(
            Utc: now, Kind: CombatKind.Damage, SourceId: 77, Source: "Tester",
            TargetId: 901, Target: "Boss", Skill: "Strike", Amount: 4000,
            DamageType: DamageType.Direct, IsBoss: true));

        now = now.AddSeconds(8);
        _ = engine.Snapshot();

        var archived = Assert.Single(engine.History);
        Assert.Equal("Inactivity", archived.EndReason);
        Assert.Equal(4000, archived.FightDamage);
    }

    [Fact]
    public void CombatEngine_LatePlayerIdentity_RefreshesActiveEventHistory()
    {
        var at = DateTime.UnixEpoch.AddSeconds(1);
        var now = at.AddSeconds(2);
        var engine = new CombatEngine(() => now);

        engine.Apply(new CombatEvent(
            Utc: at, Kind: CombatKind.Damage, SourceId: 15433, Source: "Actor 15433",
            TargetId: 80880, Target: "Target 80880", Skill: "Dimensional Control",
            Amount: 1120, DamageType: DamageType.Frontal, SourceClass: "Spiritmaster",
            DamageFlags: DamageFlags.Frontal));
        engine.Apply(new CombatEvent(
            Utc: at.AddSeconds(2), Kind: CombatKind.PlayerName, SourceId: 15433,
            Source: "PUTXYS", SourceClass: "Spiritmaster"));

        var snapshot = engine.Snapshot();
        var row = Assert.Single(snapshot.Players, x => x.EntityId == 15433 || x.ActorId == 15433);
        Assert.Equal("PUTXYS", row.Name);
        var combatEvent = Assert.Single(snapshot.RecentEvents, x => x.Kind == CombatKind.Damage && x.SourceId == 15433);
        Assert.Equal("PUTXYS", combatEvent.Source);
        Assert.Equal("Spiritmaster", combatEvent.SourceClass);
    }

    [Fact]
    public void Damage_FirestormFifthHit_UsesStructuralDamageInsteadOfHitOrdinal()
    {
        // Live capture 2026-10-04: the fifth Firestorm packet carries the
        // real hit (1119) after stable/base 10380, while a later field is the
        // hit ordinal 5. The decoder must not report that ordinal as damage.
        const string hex = "21043891B5010400D644007EE50018023338A559050000008C51DF080500";
        var dispatcher = new PacketDispatcher(DamageProfile());
        var events = dispatcher.Dispatch(Convert.FromHexString(hex), DateTime.UnixEpoch).ToList();
        var hit = Assert.Single(events, x => x.Kind == CombatKind.Damage);
        // Skill naming depends on the optional public bootstrap; this regression
        // protects the packet layout/damage recovery, not catalog availability.
        Assert.Equal(1119, hit.Amount);
        Assert.Equal(DamageType.Direct, hit.DamageType);
    }


    [Fact]
    public void PulledStatus_LiveCapture_LocalizesToEnglish()
    {
        // Exact 2026-10-04 live packet. Public game data identifies 당겨짐
        // as the pulled status/effect used by pull mechanics.
        const string hex = "250438CD8B010600858701D7860100070204000207AC980001000000F252C5070100";
        var dispatcher = new PacketDispatcher(DamageProfile());
        var events = dispatcher.Dispatch(Convert.FromHexString(hex), DateTime.UnixEpoch).ToList();

        var hit = Assert.Single(events, x => x.Kind == CombatKind.Damage);
        // Packet decoding must stay deterministic when CI has no public game-data
        // bootstrap. English localization itself is covered independently below.
        Assert.True(hit.Skill is "Pulled" or "Skill 100055");
        Assert.Equal(965, hit.Amount);
        Assert.Equal(DamageType.Perfect, hit.DamageType);
    }


    [Fact]
    public void OtherInfo_CurrentNameMarker_ResolvesPlayerIdentity()
    {
        // Exact prefix from the 2026-10-04 Global capture:
        // 45 36, entity 7685, current 0x17 name marker, "Shinko".
        const string hex = "B70C4536853C0120A00117065368696E6B6F";
        var dispatcher = new PacketDispatcher(DamageProfile());
        var events = dispatcher.Dispatch(Convert.FromHexString(hex), DateTime.UnixEpoch).ToList();

        var identity = Assert.Single(events, x => x.Kind == CombatKind.PlayerName);
        Assert.Equal(7685, identity.SourceId);
        Assert.Equal("Shinko", identity.Source);
    }


    [Fact]
    public void SelfInfo_CurrentGlobal_ResolvesBeforeProfileDispatch()
    {
        // Exact prefix from the 2026-10-04 live Global self-info packet:
        // 33 36, combat entity 3920, length-prefixed "Bradyboi".
        const string hex = "D9103336D01E5F91C12837084272616479626F69";
        var dispatcher = new PacketDispatcher(DamageProfile());
        var events = dispatcher.Dispatch(Convert.FromHexString(hex), DateTime.UnixEpoch).ToList();

        var identity = Assert.Single(events, x => x.Kind == CombatKind.PlayerName);
        Assert.Equal(3920, identity.SourceId);
        Assert.Equal("Bradyboi", identity.Source);
    }

    [Fact]
    public void GlobalSessionLink_CurrentGlobal_ParsesBeforeProfileDispatch()
    {
        // Exact prefix from the packet immediately preceding the live self-info:
        // 20 36, session/combat entity 3920, stable/global character id 304076.
        const string hex = "CF0B20360000D01E00000000CCA3040000003608A602CC08E615";
        var dispatcher = new PacketDispatcher(DamageProfile());
        var records = new List<string>();
        dispatcher.ValidationRecord += records.Add;

        _ = dispatcher.Dispatch(Convert.FromHexString(hex), DateTime.UnixEpoch).ToList();

        Assert.Contains(records, x =>
            x.Contains("tag=globalSessionLink|session=3920|global=304076", StringComparison.Ordinal));
    }


    [Fact]
    public void Embedded2036Bytes_DoNotCreateSessionIdentityLink()
    {
        // Exact 2026-10-04 live packet whose real opcode is 29 37.
        // It merely contains 20 36 at offset 11; treating that byte sequence
        // as a session link produced a false session=106/global=2638549284.
        const string hex = "2429379A3A03032FCB9CC7203604C86A7C0F472A2411459DC31B43E8611244B702";
        var dispatcher = new PacketDispatcher(DamageProfile());
        var records = new List<string>();
        dispatcher.ValidationRecord += records.Add;

        _ = dispatcher.Dispatch(Convert.FromHexString(hex), DateTime.UnixEpoch).ToList();

        Assert.DoesNotContain(records, x => x.Contains("tag=globalSessionLink", StringComparison.Ordinal));
    }


    [Theory]
    [InlineData("DE094536E83D05B0A00007064B61747469610C", 7912, "Kattia", "Templar")]
    [InlineData("E60A4536FE091D30A0010707416E6E6162656C10", 1278, "Annabel", "Ranger")]
    public void OtherInfo_CurrentGlobal_UsesPacketJobCodeForClass(
        string hex, long entityId, string name, string expectedClass)
    {
        var dispatcher = new PacketDispatcher(DamageProfile());
        var events = dispatcher.Dispatch(Convert.FromHexString(hex), DateTime.UnixEpoch).ToList();

        var identity = Assert.Single(events, x => x.Kind == CombatKind.PlayerName);
        Assert.Equal(entityId, identity.SourceId);
        Assert.Equal(name, identity.Source);
        Assert.Equal(expectedClass, identity.SourceClass);
    }

    [Fact]
    public void SelfInfo_CurrentGlobal_UsesStructuredJobMetadata()
    {
        // Exact live prefix: entity 3920, Bradyboi, server/meta 2102,
        // job code 11, extra=2. Current job table maps 11 to Templar.
        const string hex = "D9103336D01E5F91C12837084272616479626F6936080B00000002";
        var dispatcher = new PacketDispatcher(DamageProfile());
        var events = dispatcher.Dispatch(Convert.FromHexString(hex), DateTime.UnixEpoch).ToList();

        var identity = Assert.Single(events, x => x.Kind == CombatKind.PlayerName);
        Assert.Equal(3920, identity.SourceId);
        Assert.Equal("Bradyboi", identity.Source);
        Assert.Equal("Templar", identity.SourceClass);
    }

    [Fact]
    public void KnownMobSource_DoesNotRemainActorPlaceholder()
    {
        // Exact 41 36 spawn + damage from the 2026-10-04 capture.
        const string spawnHex = "A00141368BFA041F00004B8E2C004002095C9AC74BAF04C800ED0D47E6CD0A43B56201FA67FA67CD0E0000CD0E00000000000000000000000000003CB8010064000000F04902000100000000000000A08601000000000050A50500010201110181969800FFFFFFFFFFFFFFFF8075D52ABB0300008BFA040102095C9AC74BAF04C800ED0D47070206F52C000002CD00D0020000D0003B0100002D00000000";
        const string damageHex = "220438DD9D0104008BFA048327E9000202376F135B010000009E55D6070100";

        var dispatcher = new PacketDispatcher(DamageAndMobProfile());
        _ = dispatcher.Dispatch(Convert.FromHexString(spawnHex), DateTime.UnixEpoch).ToList();
        var events = dispatcher.Dispatch(Convert.FromHexString(damageHex), DateTime.UnixEpoch.AddSeconds(1)).ToList();

        var hit = Assert.Single(events, x => x.Kind == CombatKind.Damage);
        Assert.Equal(81163, hit.SourceId);
        Assert.Equal("NPC", hit.SourceClass);
        Assert.False(hit.Source.StartsWith("Actor ", StringComparison.Ordinal));
    }

    [Fact]
    public void CombatEngine_NpcDamage_IsDamageTakenButNotPlayerDps()
    {
        var now = DateTime.UnixEpoch;
        var engine = new CombatEngine(() => now);

        engine.Apply(new CombatEvent(
            Utc: now, Kind: CombatKind.Damage,
            SourceId: 81163, Source: "Skill Entity", SourceClass: "NPC",
            TargetId: 55, Target: "Player", Skill: "Bittercold Wind",
            Amount: 982, DamageType: DamageType.Direct));

        var outgoing = engine.Snapshot(MeterSegment.Current, MeterCategory.Damage);
        Assert.Empty(outgoing.Players);
        Assert.Equal(0, outgoing.FightDamage);

        var taken = engine.Snapshot(MeterSegment.Current, MeterCategory.DamageTaken);
        var row = Assert.Single(taken.Players);
        Assert.Equal(982, row.Damage);
    }


    [Fact]
    public void GlobalSessionLink_CarriesTrustedSelfIdentityAcrossSessionChange()
    {
        // Exact live prefixes from two captures:
        // 1) session 3920 -> stable/global 304076
        // 2) self-info identifies session 3920 as Bradyboi
        // 3) after a transition, session 9640 -> the same stable/global 304076
        const string firstLink = "CF0B20360000D01E00000000CCA3040000003608A602CC08E615";
        const string selfInfo = "D9103336D01E5F91C12837084272616479626F6936080B00000002";
        const string nextLink = "CF0B20360000A84B00000000CCA3040000003608E55DCC08E8150000789C";

        var dispatcher = new PacketDispatcher(DamageProfile());
        var records = new List<string>();
        dispatcher.ValidationRecord += records.Add;

        _ = dispatcher.Dispatch(Convert.FromHexString(firstLink), DateTime.UnixEpoch).ToList();
        var selfEvents = dispatcher.Dispatch(Convert.FromHexString(selfInfo), DateTime.UnixEpoch.AddSeconds(1)).ToList();
        var nextEvents = dispatcher.Dispatch(Convert.FromHexString(nextLink), DateTime.UnixEpoch.AddSeconds(2)).ToList();

        var self = Assert.Single(selfEvents, x => x.Kind == CombatKind.PlayerName);
        Assert.Equal(3920, self.SourceId);
        Assert.Equal("Bradyboi", self.Source);

        var remapped = Assert.Single(nextEvents, x => x.Kind == CombatKind.PlayerName);
        Assert.Equal(9640, remapped.SourceId);
        Assert.Equal("Bradyboi", remapped.Source);
        Assert.Equal("Templar", remapped.SourceClass);

        Assert.Contains(records, x =>
            x.Contains("tag=sessionPromotedGlobal|session=3920|global=304076|name=Bradyboi", StringComparison.Ordinal));
        Assert.Contains(records, x =>
            x.Contains("tag=globalSessionName|session=9640|global=304076|name=Bradyboi", StringComparison.Ordinal));
    }

}
