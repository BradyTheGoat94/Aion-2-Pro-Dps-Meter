using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Aion2DPSPro;

namespace Aion2DPSPro.Overlay;
public partial class OverlayWindow : Window
{
    MeterSnapshot? last;
    public OverlayWindow() { InitializeComponent(); }
    static readonly Dictionary<string,string> Colors = new(StringComparer.OrdinalIgnoreCase) {
        ["Gladiator"]="#E65353", ["Templar"]="#E8903D", ["Assassin"]="#C45CFF", ["Ranger"]="#F2C94C",
        ["Sorcerer"]="#4DA3FF", ["Spiritmaster"]="#48C9D8", ["Cleric"]="#6DDB72", ["Chanter"]="#D6DCE8", ["Brawler"]="#FF7A45", ["Unknown"]="#AAB6CC" };
    public void Render(MeterSnapshot s) {
        last=s; PreviewBadge.Visibility=s.PreviewMode?Visibility.Visible:Visibility.Collapsed;
        Timer.Text=TimeSpan.FromSeconds(s.FightSeconds).ToString(@"mm\:ss"); GroupDps.Text=$"GROUP {F(s.FightDps)} DPS";
        BossHp.Value=s.Target?.Percent??0; TargetName.Text=s.Target?.Name??"No target";
        TargetHp.Text=s.Target is null?"":$"{s.Target.Percent:0.0}%   {F(s.Target.CurrentHp)} / {F(s.Target.MaxHp)}";
        StatusText.Text=s.PreviewMode?"SIMULATED DATA":"LIVE DATA";
        var max=Math.Max(1,s.Players.FirstOrDefault()?.Dps??1);
        Rows.ItemsSource=s.Players.Select((p,i)=>new Row(i+1,p.Name,p.ClassName,F(p.Dps),F(p.Damage),$"{p.Share:0.0}%",Brush(p.ClassName),Math.Max(4,150*p.Dps/max),p)).ToList();
        Skills.ItemsSource=s.Skills.Take(7).Select(x=>new {x.Name,Damage=F(x.Damage)}).ToList();
    }
    static Brush Brush(string c)=>new SolidColorBrush((Color)ColorConverter.ConvertFromString(Colors.TryGetValue(c,out var v)?v:Colors["Unknown"]));
    void PlayerSelected(object s,System.Windows.Controls.SelectionChangedEventArgs e){ if(Rows.SelectedItem is Row r){SelectedPlayer.Text=r.Name;SelectedPlayer.Foreground=r.Brush;SelectedMeta.Text=$"{r.ClassName}  •  {r.Stats.Dps:N0} DPS  •  {r.Stats.Damage:N0} damage  •  {r.Stats.CritPercent:0.0}% crit";} }
    void ToggleOptions(object s,RoutedEventArgs e)=>ApplyClickThrough(false);
    void CloseOverlay(object s,RoutedEventArgs e)=>Hide();
    void Drag(object s,MouseButtonEventArgs e){if(e.LeftButton==MouseButtonState.Pressed)DragMove();}
    public void ApplyClickThrough(bool enabled){var h=new WindowInteropHelper(this).Handle;if(h==IntPtr.Zero)return;var ex=GetWindowLong(h,-20);SetWindowLong(h,-20,enabled?ex|0x20|0x08000000:ex&~0x20);}
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd,int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd,int nIndex,int dwNewLong);
    static string F(double v)=>v>=1_000_000?$"{v/1_000_000:0.00}M":v>=1_000?$"{v/1_000:0.0}K":$"{v:0}";
    sealed record Row(int Rank,string Name,string ClassName,string Dps,string Damage,string Share,Brush Brush,double BarWidth,PlayerStats Stats);
}
