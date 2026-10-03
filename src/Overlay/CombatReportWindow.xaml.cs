using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Aion2DPSPro.Overlay;

public partial class CombatReportWindow : Window
{
    private readonly Func<MeterSnapshot> provider;
    private readonly long actorId;
    private readonly DispatcherTimer timer=new() {Interval=TimeSpan.FromMilliseconds(500)};
    private MeterSnapshot? displayed;
    private bool ready;
    public CombatReportWindow(Func<MeterSnapshot> provider,long actorId,MeterCategory category=MeterCategory.Damage)
    {
        this.provider=provider;this.actorId=actorId;
        InitializeComponent();
        Category.ItemsSource=Enum.GetValues<MeterCategory>();Category.SelectedItem=category;ready=true;
        timer.Tick+=(_,_)=> {if(Pause.IsChecked!=true)Refresh();};
        Loaded+=(_,_)=> {Refresh();timer.Start();};Closed+=(_,_)=>timer.Stop();
        Refresh();
    }
    private void CategoryChanged(object sender,SelectionChangedEventArgs e) {if(ready)Refresh(Pause.IsChecked==true);}
    private void PauseChanged(object sender,RoutedEventArgs e) {if(ready)Refresh(Pause.IsChecked==true);}
    public void Refresh(bool useDisplayed=false)
    {
        if(!ready)return;
        var s=useDisplayed&&displayed!=null?displayed:provider();displayed=s;
        var category=Category.SelectedItem is MeterCategory selected?selected:MeterCategory.Damage;
        CategorySnapshot Dataset(MeterCategory c)=>s.Categories.TryGetValue(c,out var data)?data:c==s.Category?new(s.Players,s.Skills,s.MetricLabel):new(Array.Empty<PlayerStats>(),Array.Empty<SkillStats>(),"COUNT");
        PlayerStats? Actor(MeterCategory c)=>Dataset(c).Players.FirstOrDefault(x=>x.ActorId==actorId);
        var stats=Actor(category);
        var identity=stats??Enum.GetValues<MeterCategory>().Select(Actor).FirstOrDefault(x=>x!=null);
        PlayerName.Text=identity?.Name??$"Actor {actorId}";
        Title=$"{PlayerName.Text} — Combat analysis";
        PlayerContext.Text=$"{identity?.ClassName??"Unknown"}  •  Entity {identity?.EntityId??actorId}  •  {category}";
        FightContext.Text=$"{TimeSpan.FromSeconds(s.FightSeconds):mm\\:ss} encounter  •  {s.StartedUtc?.ToLocalTime():HH:mm:ss}";
        LiveState.Text=Pause.IsChecked==true?"PAUSED":s.InFight?"LIVE / 0.5s":"COMPLETED";
        TargetContext.Text=s.Target?.Name??"No target data";
        bool rate=category is MeterCategory.Damage or MeterCategory.Healing or MeterCategory.DamageTaken;
        string metric=Dataset(category).MetricLabel;
        AmountLabel.Text=$"TOTAL {category.ToString().ToUpperInvariant()}";
        AmountValue.Text=(stats?.Damage??0).ToString("N0");
        RateLabel.Text=rate?$"ENCOUNTER {metric}":"EVENT COUNT / SECONDS";
        RateValue.Text=(stats?.Dps??0).ToString(rate?"N1":"N0");
        ShareValue.Text=$"{stats?.Share??0:0.0}%";HitsValue.Text=(stats?.Hits??0).ToString("N0");CritValue.Text=rate?$"{stats?.CritPercent??0:0.0}%":"—";
        ActiveValue.Text=category is MeterCategory.Damage or MeterCategory.Healing?(stats?.ActiveDps??0).ToString("N1"):"—";
        Overview.Text=$"Damage {(Actor(MeterCategory.Damage)?.Damage??0):N0}   •   Healing {(Actor(MeterCategory.Healing)?.Damage??0):N0}   •   Damage taken {(Actor(MeterCategory.DamageTaken)?.Damage??0):N0}   •   Deaths {(Actor(MeterCategory.Deaths)?.Damage??0):N0}   •   Interrupts {(Actor(MeterCategory.Interrupts)?.Damage??0):N0}   •   Dispels {(Actor(MeterCategory.Dispels)?.Damage??0):N0}";
        var skills=Dataset(category).Skills.Where(x=>x.ActorId==actorId).Select(x=>new SkillRow(x.Name,x.Damage,x.Dps,x.Share,x.Hits,x.Crits,x.CritPercent,x.Average,x.MinHit,x.MaxHit,
            x.FlagHits.GetValueOrDefault(DamageFlags.Perfect),x.FlagHits.GetValueOrDefault(DamageFlags.Back),x.FlagHits.GetValueOrDefault(DamageFlags.Frontal),x.FlagHits.GetValueOrDefault(DamageFlags.Double),x.FlagHits.GetValueOrDefault(DamageFlags.Parry),x.FlagHits.GetValueOrDefault(DamageFlags.MultiHit))).ToArray();
        SkillGrid.ItemsSource=skills;SkillEmpty.Visibility=skills.Length==0?Visibility.Visible:Visibility.Collapsed;
        var targets=s.Targets.Where(x=>x.ActorId==actorId).OrderByDescending(x=>x.Damage).ToArray();TargetGrid.ItemsSource=targets;TargetEmpty.Visibility=targets.Length==0?Visibility.Visible:Visibility.Collapsed;
        string Name(long id)=>Enum.GetValues<MeterCategory>().SelectMany(c=>Dataset(c).Players).FirstOrDefault(x=>x.ActorId==id)?.Name??$"Actor {id}";
        var effects=s.Buffs.Where(x=>x.TargetId==actorId).Select(x=>new {x.Name,Kind=x.IsDebuff?"Debuff":"Buff",Source=Name(x.SourceId),x.Uptime,x.ActiveSeconds,x.MaxStacks}).ToArray();
        EffectGrid.ItemsSource=effects;EffectEmpty.Visibility=effects.Length==0?Visibility.Visible:Visibility.Collapsed;
        EventGrid.ItemsSource=s.RecentEvents.Where(x=>x.SourceId==actorId||x.TargetId==actorId).Reverse().Select(x=>new {Time=x.Utc.ToLocalTime().ToString("HH:mm:ss.fff"),Direction=x.SourceId==actorId?"Outgoing":"Incoming",Kind=x.Kind.ToString(),Skill=string.IsNullOrWhiteSpace(x.Skill)?x.Effect:x.Skill,x.Amount,Flags=x.DamageFlags==DamageFlags.None?x.DamageType.ToString():x.DamageFlags.ToString()}).ToArray();
        Footnote.Text=$"{s.EndReason}  Protocol unverified. Empty counts can mean no events or unsupported live opcodes. *Active rate estimates activity with intervals capped at 5s. Event feed retains the latest 2,000 encounter events; skill and target totals cover all accepted events.".Trim();
    }
    public sealed record SkillRow(string Name,long Damage,double Dps,double Share,long Hits,long Crits,double CritPercent,double Average,long MinHit,long MaxHit,long Perfect,long Back,long Front,long Double,long Parry,long Multi);
}
