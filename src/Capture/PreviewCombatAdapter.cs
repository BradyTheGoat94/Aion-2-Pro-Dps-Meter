namespace Aion2DPSPro.Capture;
public sealed class PreviewCombatAdapter
{
    readonly Random rng=new(42); readonly string[] names={"Shadowblade","Ironfang","Kaelthas","Vexx","Lyria","Selene","Drax","Aurelia"};
    readonly string[] classes={"Assassin","Gladiator","Sorcerer","Ranger","Cleric","Spiritmaster","Chanter","Templar"};
    readonly string[] skills={"Burst Skill","Combo Strike","Aether Skill","Basic Attack","Damage over Time"}; long boss=18_772_000;
    public IEnumerable<CombatEvent> Tick(){var now=DateTime.UtcNow; for(int i=0;i<names.Length;i++){var baseDmg=i switch{0=>7200,1=>6500,2=>6800,3=>5600,4=>3600,5=>5100,6=>3200,_=>2800}; var amount=(long)(baseDmg*(.70+rng.NextDouble()*.65)); var crit=rng.NextDouble()<.27; if(crit)amount=(long)(amount*1.55); boss=Math.Max(0,boss-amount); yield return new CombatEvent(now,CombatKind.Damage,i+1,names[i],9001,"Training Colossus",skills[rng.Next(skills.Length)],amount,crit?DamageType.Crit:DamageType.Direct,boss,18_772_000,SourceClass:classes[i]);} yield return new CombatEvent(now,CombatKind.TargetHp,TargetId:9001,Target:"Training Colossus",CurrentHp:boss,MaxHp:18_772_000);}
}
