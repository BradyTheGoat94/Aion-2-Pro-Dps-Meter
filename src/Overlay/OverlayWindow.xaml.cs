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
    string currentTheme = "Aion Blue/Red";
    string currentStyle = "Classic";
    bool clickThrough;
    bool showDetails = true;
    public OverlayWindow() { InitializeComponent(); ApplyTheme(currentTheme); ApplyOverlayStyle(currentStyle); }
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

    void OpenSettings(object sender, RoutedEventArgs e)
    {
        var w = new Window { Title="AION 2 DPS — Overlay Settings", Width=410, Height=540,
            WindowStartupLocation=WindowStartupLocation.CenterOwner, Owner=this, Background=Brush("#0A0E16"), Foreground=Brush("#F4F7FF"), ResizeMode=ResizeMode.NoResize };
        var panel = new System.Windows.Controls.StackPanel { Margin=new Thickness(20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text="OVERLAY SETTINGS", FontSize=22, FontWeight=FontWeights.Bold, Margin=new Thickness(0,0,0,16) });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text="Overlay style", Foreground=Brush("#9DB7DE") });
        var styles = new System.Windows.Controls.ComboBox { Margin=new Thickness(0,5,0,14), Height=30 };
        foreach (var n in StyleNames) styles.Items.Add(n);
        styles.SelectedItem=currentStyle;
        styles.SelectionChanged += (_,__) => { if(styles.SelectedItem is string name){ currentStyle=name; ApplyOverlayStyle(name); } };
        panel.Children.Add(styles);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text="Color theme", Foreground=Brush("#9DB7DE") });
        var themes = new System.Windows.Controls.ComboBox { Margin=new Thickness(0,5,0,14), Height=30 };
        foreach (var n in ThemeNames) themes.Items.Add(n);
        themes.SelectedItem=currentTheme;
        themes.SelectionChanged += (_,__) => { if(themes.SelectedItem is string name){ currentTheme=name; ApplyTheme(name); } };
        panel.Children.Add(themes);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text="Overlay opacity", Foreground=Brush("#9DB7DE") });
        var opacity = new System.Windows.Controls.Slider { Minimum=.35, Maximum=1, Value=Opacity, TickFrequency=.05, IsSnapToTickEnabled=true, Margin=new Thickness(0,5,0,14) };
        opacity.ValueChanged += (_,__) => Opacity=opacity.Value;
        panel.Children.Add(opacity);
        var detail = new System.Windows.Controls.CheckBox { Content="Show player detail panel", IsChecked=showDetails, Margin=new Thickness(0,4,0,8) };
        detail.Checked += (_,__) => { showDetails=true; ApplyOverlayStyle(currentStyle); };
        detail.Unchecked += (_,__) => { showDetails=false; ApplyOverlayStyle(currentStyle); };
        panel.Children.Add(detail);
        var top = new System.Windows.Controls.CheckBox { Content="Always on top", IsChecked=Topmost, Margin=new Thickness(0,4,0,8) };
        top.Checked += (_,__) => Topmost=true; top.Unchecked += (_,__) => Topmost=false; panel.Children.Add(top);
        var pass = new System.Windows.Controls.CheckBox { Content="Mouse click-through", IsChecked=clickThrough, Margin=new Thickness(0,4,0,8) };
        pass.Checked += (_,__) => ApplyClickThrough(true); pass.Unchecked += (_,__) => ApplyClickThrough(false); panel.Children.Add(pass);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text="Tip: double-click any player row for a detailed report.", Foreground=Brush("#8FB8FF"), TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,16,0,0) });
        w.Content=panel; w.ShowDialog();
    }

    static readonly string[] ThemeNames = { "Aion Blue/Red", "Neon Spectrum", "Void Purple", "Emerald Glass", "Solar Flare", "Ice Crystal" };
    static readonly string[] StyleNames = { "Classic", "Compact", "Minimal", "Glass", "Tournament" };

    void ApplyOverlayStyle(string name)
    {
        currentStyle=name;
        // Reset shared layout properties first so every selection is reversible.
        Tabs.Visibility=Visibility.Visible;
        FooterBar.Visibility=Visibility.Visible;
        HeaderBar.Visibility=Visibility.Visible;
        DetailPanel.Visibility=showDetails?Visibility.Visible:Visibility.Collapsed;
        Root.CornerRadius=new CornerRadius(9);
        Root.BorderThickness=new Thickness(1.4);
        Rows.BorderThickness=new Thickness(1);
        Rows.Margin=new Thickness(0);
        HeaderBar.Height=double.NaN;
        FooterBar.Opacity=1;
        DetailPanel.Opacity=1;

        switch(name)
        {
            case "Compact":
                Width=640; Height=430; MinWidth=520; MinHeight=320;
                DetailPanel.Visibility=Visibility.Collapsed;
                FooterBar.Visibility=Visibility.Collapsed;
                Root.CornerRadius=new CornerRadius(6);
                Root.BorderThickness=new Thickness(1);
                break;
            case "Minimal":
                Width=570; Height=360; MinWidth=480; MinHeight=280;
                Tabs.Visibility=Visibility.Collapsed;
                DetailPanel.Visibility=Visibility.Collapsed;
                FooterBar.Visibility=Visibility.Collapsed;
                Root.CornerRadius=new CornerRadius(3);
                Root.BorderThickness=new Thickness(1);
                Rows.BorderThickness=new Thickness(0);
                break;
            case "Glass":
                Width=780; Height=560; MinWidth=560; MinHeight=380;
                Root.Opacity=.88;
                Root.CornerRadius=new CornerRadius(16);
                Root.BorderThickness=new Thickness(1);
                Rows.Opacity=.92;
                DetailPanel.Opacity=.92;
                break;
            case "Tournament":
                Width=900; Height=620; MinWidth=680; MinHeight=440;
                Root.CornerRadius=new CornerRadius(0);
                Root.BorderThickness=new Thickness(2);
                Rows.BorderThickness=new Thickness(0,2,0,2);
                FooterBar.Opacity=.96;
                break;
            default:
                Width=780; Height=560; MinWidth=560; MinHeight=380;
                Root.Opacity=1;
                Rows.Opacity=1;
                break;
        }

        // Ensure opacity changes from Glass never leak into another style.
        if(name!="Glass") { Root.Opacity=1; Rows.Opacity=1; DetailPanel.Opacity=1; }
    }
    void ApplyTheme(string name)
    {
        var p = name switch {
            "Neon Spectrum" => ("#E80B1022","#FF2D55","#00E5FF","#151A36"),
            "Void Purple" => ("#ED100B22","#B65CFF","#FF4FD8","#21113B"),
            "Emerald Glass" => ("#E9081D1B","#20E3B2","#56F39A","#0E302B"),
            "Solar Flare" => ("#ED211008","#FF5A36","#FFC857","#3A170D"),
            "Ice Crystal" => ("#EB071B2D","#4CC9F0","#BDEBFF","#0B2A42"),
            _ => ("#F20A1630","#E53935","#2F80ED","#101F3D")
        };
        Root.Background=Brush(p.Item1); Root.BorderBrush=Brush(p.Item2);
        HeaderBar.Background=Brush(p.Item4); HeaderBar.BorderBrush=Brush(p.Item3);
        FooterBar.Background=Brush(p.Item4); FooterBar.BorderBrush=Brush(p.Item3);
        DetailPanel.BorderBrush=Brush(p.Item3);
    }

    void OpenSelectedReport(object sender, RoutedEventArgs e)
    {
        if (last is null || Rows.SelectedItem is not Row r) return;
        var w = new Window { Title=$"{r.Name} — Combat Report", Width=760, Height=560, Owner=this,
            WindowStartupLocation=WindowStartupLocation.CenterOwner, Background=Brush("#0A0E16"), Foreground=Brush("#F4F7FF") };
        var root = new System.Windows.Controls.Grid { Margin=new Thickness(18) };
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height=GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
        var head = new System.Windows.Controls.StackPanel();
        head.Children.Add(new System.Windows.Controls.TextBlock { Text=r.Name, FontSize=26, FontWeight=FontWeights.Bold, Foreground=r.Brush });
        head.Children.Add(new System.Windows.Controls.TextBlock { Text=$"{r.ClassName}   •   {r.Stats.Dps:N0} DPS   •   {r.Stats.Damage:N0} damage   •   {r.Stats.Share:0.0}% share   •   {r.Stats.CritPercent:0.0}% crit", Foreground=Brush("#AFC9EF"), Margin=new Thickness(0,3,0,14) });
        root.Children.Add(head);
        var tabs = new System.Windows.Controls.TabControl { Margin=new Thickness(0,4,0,0) };
        System.Windows.Controls.Grid.SetRow(tabs,1);
        var skillsTab = new System.Windows.Controls.TabItem { Header="Skills" };
        var skillGrid = new System.Windows.Controls.DataGrid { IsReadOnly=true, AutoGenerateColumns=false, Background=Brush("#101827"), Foreground=Brush("#F4F7FF"), BorderThickness=new Thickness(0) };
        skillGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header="Skill", Binding=new System.Windows.Data.Binding("Name"), Width=new System.Windows.Controls.DataGridLength(1,System.Windows.Controls.DataGridLengthUnitType.Star) });
        skillGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header="Damage", Binding=new System.Windows.Data.Binding("Damage"){StringFormat="N0"}, Width=120 });
        skillGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header="Hits", Binding=new System.Windows.Data.Binding("Hits"), Width=80 });
        skillGrid.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header="DPS", Binding=new System.Windows.Data.Binding("Dps"){StringFormat="N0"}, Width=100 });
        skillGrid.ItemsSource=last.Skills; skillsTab.Content=skillGrid; tabs.Items.Add(skillsTab);
        var eventsTab = new System.Windows.Controls.TabItem { Header="Recent Events" };
        var events = new System.Windows.Controls.ListBox { Background=Brush("#101827"), Foreground=Brush("#DDE9FF"), FontFamily=new FontFamily("Consolas") };
        events.ItemsSource=last.RecentEvents.Where(x=>x.Source==r.Name || x.SourceId.ToString()==r.Name.Replace("Actor ","")).Reverse().Select(x=>$"{x.Utc:HH:mm:ss.fff}  {x.Skill,-28} {x.Amount,10:N0}  {x.DamageType}  {x.DamageFlags}");
        eventsTab.Content=events; tabs.Items.Add(eventsTab);
        root.Children.Add(tabs); w.Content=root; w.Show();
    }
    void CloseOverlay(object s,RoutedEventArgs e)=>Hide();
    void Drag(object s,MouseButtonEventArgs e){if(e.LeftButton==MouseButtonState.Pressed)DragMove();}
    public void ApplyClickThrough(bool enabled){clickThrough=enabled;var h=new WindowInteropHelper(this).Handle;if(h==IntPtr.Zero)return;var ex=GetWindowLong(h,-20);SetWindowLong(h,-20,enabled?ex|0x20|0x08000000:ex&~0x20);}
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd,int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd,int nIndex,int dwNewLong);
    static string F(double v)=>v>=1_000_000?$"{v/1_000_000:0.00}M":v>=1_000?$"{v/1_000:0.0}K":$"{v:0}";
    sealed record Row(int Rank,string Name,string ClassName,string Dps,string Damage,string Share,Brush Brush,double BarWidth,PlayerStats Stats);
}
