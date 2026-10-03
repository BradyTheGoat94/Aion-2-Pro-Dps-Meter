using Aion2DPSPro;
using Aion2DPSPro.Protocol;
using Aion2DPSPro.Storage;

int checks=0;
void Equal(double actual,double expected,string label) {checks++;if(Math.Abs(actual-expected)>.00001)throw new Exception($"{label}: expected {expected}, got {actual}");}
void True(bool actual,string label) {checks++;if(!actual)throw new Exception(label);}
var t=new DateTime(2026,10,3,0,0,0,DateTimeKind.Utc);
var now=t;
var e=new CombatEngine(()=>now);
CombatEvent Hit(int seconds,long id,long amount,string name="Player",bool boss=false)=>new(t.AddSeconds(seconds),CombatKind.Damage,id,name,99,"Boss","Strike",amount,IsBoss:boss);
e.Apply(Hit(0,1,100));e.Apply(Hit(10,1,200));
Equal(e.Snapshot().FightDamage,300,"damage sum");Equal(e.Snapshot().FightDps,30,"encounter DPS");
now=t.AddSeconds(20);Equal(e.Snapshot().FightDps,30,"DPS frozen during idle");
e.Apply(new(t.AddSeconds(2),CombatKind.BuffApply,1,"Player",1,"Player",Effect:"Power",Stacks:1));
e.Apply(new(t.AddSeconds(6),CombatKind.BuffRemove,1,"Player",1,"Player",Effect:"Power"));
// Late timestamps must not reset encounter activity.
now=t.AddSeconds(41);True(!e.Snapshot().InFight,"timeout completes");Equal(e.Snapshot().FightDamage,300,"completed preserved");Equal(e.History.Count,1,"single completion");
e.Apply(Hit(50,2,400,"Player"));Equal(e.Snapshot(MeterSegment.Previous,MeterCategory.Damage).FightDamage,300,"previous fight");
Equal(e.Snapshot(MeterSegment.Overall,MeterCategory.Damage).FightDamage,700,"overall sum");
e.Apply(new(t.AddSeconds(51),CombatKind.Heal,2,"Player",1,"Player","Heal",50));Equal(e.Snapshot(MeterSegment.Current,MeterCategory.Healing).Players.Single().Damage,50,"healing independent");
Equal(e.Snapshot(MeterSegment.Current,MeterCategory.DamageTaken).Players.Single().Damage,400,"damage taken independent");
e.Apply(new(t.AddSeconds(52),CombatKind.Death,TargetId:2));Equal(e.Snapshot(MeterSegment.Current,MeterCategory.Deaths).Players.Single().Damage,1,"death target");
e.Apply(new(t.AddSeconds(53),CombatKind.Interrupt,2));Equal(e.Snapshot(MeterSegment.Current,MeterCategory.Interrupts).Players.Single().Damage,1,"interrupt count");
e.Apply(new(t.AddSeconds(54),CombatKind.Dispel,2));Equal(e.Snapshot(MeterSegment.Current,MeterCategory.Dispels).Players.Single().Damage,1,"dispel count");
var b=new CombatEngine(()=>t.AddSeconds(20));b.Apply(Hit(0,1,100));b.Apply(new(t.AddSeconds(2),CombatKind.BuffApply,1,"Player",1,"Player",Effect:"Power",Stacks:2));b.Apply(new(t.AddSeconds(6),CombatKind.BuffRemove,1,"Player",1,"Player",Effect:"Power"));b.Apply(Hit(10,1,100));
Equal(b.Snapshot().Buffs.Single().Uptime,40,"buff actual fight denominator");
b.Apply(Hit(11,2,300,"Player"));Equal(b.Snapshot().Players.Count,2,"same names remain separate");Equal(b.Snapshot().Skills.Count,2,"skills per actor");
b.Apply(new(t.AddSeconds(12),CombatKind.PlayerName,1,"Resolved",SourceClass:"Cleric"));True(b.Snapshot().Players.Any(p=>p.Name=="Resolved"&&p.ClassName=="Cleric"),"late identity");
b.Apply(new(t.AddSeconds(13),CombatKind.Despawn,1));b.Apply(Hit(14,1,50,"Replacement"));True(b.Snapshot().Players.Any(p=>p.Name=="Resolved"&&p.Damage==200),"reused ID history preserved");True(b.Snapshot().Players.Any(p=>p.Name=="Replacement"&&p.Damage==50),"reused ID new actor");
var pet=new CombatEngine(()=>t);pet.Apply(new(t,CombatKind.PlayerName,1,"Owner"));pet.Apply(new(t,CombatKind.Ownership,5,OwnerId:1));pet.Apply(Hit(0,5,90,"Summon"));True(pet.Snapshot().Players.Single().Name=="Owner","explicit pet ownership");
var bossEngine=new CombatEngine(()=>now);bossEngine.Apply(Hit(0,1,100,boss:true));now=t.AddSeconds(60);True(bossEngine.Snapshot().InFight,"boss downtime retained");bossEngine.Apply(new(t.AddSeconds(61),CombatKind.TargetHp,TargetId:99,CurrentHp:0,MaxHp:1000,IsBoss:true));True(!bossEngine.Snapshot().InFight,"boss death completes");
var tcp=new TcpStreamReassembler();
Equal(tcp.Push("a",100,new byte[]{1,2,3},t).SelectMany(x=>x).Count(),3,"initial TCP");
Equal(tcp.Push("a",102,new byte[]{3,4,5},t).SelectMany(x=>x).Count(),2,"overlap keeps new suffix");
Equal(tcp.Push("a",102,new byte[]{3,4,5},t).Count,0,"duplicate discarded");
Equal(tcp.Push("a",108,new byte[]{9},t).Count,0,"out of order waits");Equal(tcp.Push("a",105,new byte[]{6,7,8},t).SelectMany(x=>x).Count(),4,"gap filled flushes");
int resets=0;tcp.StreamReset+=_=>resets++;tcp.Push("a",120,new byte[]{1},t);tcp.Push("a",121,new byte[]{2},t.AddSeconds(6));True(resets>0,"gap recovery reset");
Equal(tcp.Push("wrap",uint.MaxValue-1,new byte[]{1,2,3},t).Count,1,"sequence wrap first");Equal(tcp.Push("wrap",1,new byte[]{4},t).Count,1,"sequence wrap continuation");
var profile=new ProtocolProfile("fixture","Global","fixture",13328,new Dictionary<string,PacketTag>{{"damage",new(4,56)},{"entityRemoved",new(33,141)}});
// Observed frame regressions, with logged values independently encoded here. These are NOT a ground-truth fight total.
var observed=new[]{("210438E3A00204008D3540B7B70009020B95C34701000000D658E7020100",359L),("210438E3A00204008D3540B7B70009021595C34702000000D658E8020200",360L),("240438E3A00224008D3540B7B70009021595C34703000000D65888030203030300",392L),("280438E3A00226008D351042B7000B020800004BCE954701000000D658D50F031414140100",2005L)};
foreach(var (hex,amount) in observed)
{
 var decoder=new CurrentClientDecoder(profile);var evt=decoder.Decode(Convert.FromHexString(hex),t).Single(x=>x.Kind==CombatKind.Damage);Equal(evt.Amount,amount,"observed damage replay");Equal(evt.SourceId,6797,"observed actor");Equal(evt.TargetId,36963,"observed target");
 var split=new CurrentClientDecoder(profile);var bytes=Convert.FromHexString(hex);True(!split.DecodeStream("in",bytes[..8],t).Any(),"split frame waits");Equal(split.DecodeStream("in",bytes[8..],t).Single(x=>x.Kind==CombatKind.Damage).Amount,amount,"split frame replay");
}
var directions=new CurrentClientDecoder(profile);var f=Convert.FromHexString(observed[0].Item1);directions.DecodeStream("in",f[..8],t).ToArray();directions.DecodeStream("out",f[..5],t).ToArray();Equal(directions.DecodeStream("in",f[8..],t).Single().Amount,359,"directions isolated");
var removal=new CurrentClientDecoder(profile).Decode(Convert.FromHexString("0B218DED8A010000"),t).Single();True(removal.Kind==CombatKind.Despawn,"observed removal");Equal(removal.SourceId,17773,"observed removal ID");
Equal(Lz4BlockDecoder.Decompress(new byte[]{0,0,0},new byte[10]),-1,"bad LZ4 rejects");
var folder=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));try {var store=new FightStore(folder);await store.SaveAsync(b.Snapshot());var saved=await store.LoadAsync(store.List().Single());Equal(saved!.FightDamage,b.Snapshot().FightDamage,"history roundtrip");Equal(saved.Skills.Count,b.Snapshot().Skills.Count,"history skill roundtrip");}finally{Directory.Delete(folder,true);}
var reuseRow=b.Snapshot().Players.Single(p=>p.Name=="Replacement");Equal(b.Snapshot().RecentEvents.Where(x=>x.Kind==CombatKind.Damage&&x.SourceId==reuseRow.ActorId).Sum(x=>x.Amount),50,"reused actor report events isolated");
var savedSnapshot=e.Snapshot();e.ResetFight();True(e.History.Last().Categories[MeterCategory.Healing].Players.Count>0,"completed history retains healing dataset");
// Sanitized identity prefixes observed in the new log: no private names or inventory data.
byte[] Frame(string bodyHex)
{
 var body=Convert.FromHexString(bodyHex);var length=new List<byte>();uint v=(uint)body.Length+4;
 do {byte part=(byte)(v&127);v>>=7;length.Add((byte)(part|(v>0?128:0)));}while(v>0);
 return length.Concat(body).ToArray();
}
var identityProfile=profile with {Tags=new Dictionary<string,PacketTag>(profile.Tags) {{"selfInfo",new(51,54)},{"otherInfo",new(69,54)}}};
var identityDecoder=new CurrentClientDecoder(identityProfile);
var self=identityDecoder.Decode(Frame("3336FD235F81C1283708546573744865726F000000"),t).Single(x=>x.Kind==CombatKind.PlayerName);
True(self.Source=="TestHero"&&self.SourceId==4605,"observed self identity prefix");
var ally=identityDecoder.Decode(Frame("4536A2210120A401070854657374416C6C7913000000"),t).Last(x=>x.Kind==CombatKind.PlayerName);
True(ally.Source=="TestAlly"&&ally.SourceId==4258,"observed other identity prefix");
var bridge=new Aion2DPSPro.Capture.CaptureIdentityBridge();string scope="adapter|local|server";
bridge.Observe(scope,new(t,CombatKind.PlayerName,self.SourceId,self.Source));bridge.Observe(scope,new(t,CombatKind.PlayerName,ally.SourceId,ally.Source));
var bridged=new CombatEngine(()=>t);
foreach(var identity in bridge.Identities(scope,t))bridged.Apply(identity);
bridged.Apply(bridge.Resolve(scope,Hit(0,4605,100,"Actor 4605")));bridged.Apply(bridge.Resolve(scope,Hit(0,4258,50,"Actor 4258")));
True(bridged.Snapshot().Players.Any(p=>p.Name=="TestHero"&&p.EntityId==4605),"pre-lock self identity reaches combat rows");
True(bridged.Snapshot().Players.Any(p=>p.Name=="TestAlly"&&p.EntityId==4258),"sibling identity reaches exact actor");
True(bridge.Resolve("adapter|local|different-server",Hit(0,4605,100,"Actor 4605")).Source=="Actor 4605","identity scope isolation");
bridge.Observe(scope,new(t,CombatKind.Despawn,4605));True(bridge.Resolve(scope,Hit(0,4605,100,"Actor 4605")).Source=="Actor 4605","bridge removes stale identity");
True(bridge.Identities(scope,t.AddMinutes(11)).Count==0,"identity cache expires");
var reportEngine=new CombatEngine(()=>t);reportEngine.Apply(new(t,CombatKind.Damage,1,"Tester",2,"Dummy","One",100,DamageFlags:DamageFlags.Critical|DamageFlags.Back|DamageFlags.Perfect));reportEngine.Apply(new(t.AddSeconds(1),CombatKind.Heal,1,"Tester",1,"Tester","Mend",50));
var full=reportEngine.Snapshot(MeterSegment.Current,MeterCategory.Damage,true);Equal(full.Categories[MeterCategory.Healing].Skills.Single().Damage,50,"report retains healing skills");Equal(full.Skills.Single().FlagHits[DamageFlags.Perfect],1,"perfect counts");Equal(full.Targets.Single().Damage,100,"actor target totals");
reportEngine.ResetFight();reportEngine.Apply(Hit(10,1,999));Equal(reportEngine.SnapshotEncounter(full.EncounterId)!.FightDamage,100,"open report stays on its encounter");
Console.WriteLine($"PASS: {checks} regression assertions");
