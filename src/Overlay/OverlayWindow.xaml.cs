using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Aion2DPSPro;

namespace Aion2DPSPro.Overlay;
public partial class OverlayWindow : Window
{
    public CombatEngine? Engine {get;set;}
    public Func<MeterSegment,MeterCategory,MeterSnapshot>? SnapshotProvider {get;set;}
    public Func<string[]>? HistoryProvider {get;set;}
    public Func<string,Task<MeterSnapshot?>>? HistoryLoader {get;set;}
    MeterSnapshot? last;
    MeterSnapshot? historicalSnapshot;
    string? historicalPath;
    bool loadingHistory;
    long? selectedActor;
    string SelectedName(long id)=>last?.Players.FirstOrDefault(p=>p.ActorId==id)?.Name??$"Actor {id}";
    string currentTheme = "Aion Blue/Red";
    string currentStyle = "Classic Dashboard";
    bool clickThrough;
    bool showDetails = true;
    public OverlayWindow() { InitializeComponent(); CategoryPicker.ItemsSource=new[]{"Damage","Healing","Damage Taken","Deaths","Buffs","Debuffs","Interrupts","Dispels"}; CategoryPicker.SelectedIndex=0; FightHistory.ItemsSource=new[]{new HistoryChoice("Saved fights ▾",null)}; FightHistory.SelectedIndex=0; ApplyTheme(currentTheme); ApplyOverlayStyle(currentStyle); LoadPreferences(); }
    static readonly Dictionary<string,string> Colors = new(StringComparer.OrdinalIgnoreCase) {
        ["Gladiator"]="#E65353", ["Templar"]="#E8903D", ["Assassin"]="#C45CFF", ["Ranger"]="#F2C94C",
        ["Sorcerer"]="#4DA3FF", ["Spiritmaster"]="#48C9D8", ["Cleric"]="#6DDB72", ["Chanter"]="#D6DCE8", ["Brawler"]="#FF7A45", ["Unknown"]="#AAB6CC" };
    public void Render(MeterSnapshot s) {
        var category=(MeterCategory)Math.Clamp(Tabs.SelectedIndex,0,7);
        if(historicalSnapshot!=null)
        {
            s=HistoricalCategory(historicalSnapshot,category);
        }
        else
        {
            s=SnapshotProvider?.Invoke((MeterSegment)Math.Clamp(Segment.SelectedIndex,0,2),category)??s;
        }
        selectedActor=(Rows.SelectedItem as Row)?.Stats.ActorId??selectedActor;
        last=s; PreviewBadge.Visibility=s.PreviewMode?Visibility.Visible:Visibility.Collapsed;
        Timer.Text=TimeSpan.FromSeconds(s.FightSeconds).ToString(@"mm\:ss"); GroupDps.Text=$"GROUP {F(s.Players.Sum(p=>p.Dps))} {s.MetricLabel}";
        BossHp.Value=s.Target?.Percent??0; TargetName.Text=s.Target?.Name??"No target";
        TargetHp.Text=s.Target is null?"":$"{s.Target.Percent:0.0}%   {F(s.Target.CurrentHp)} / {F(s.Target.MaxHp)}";
        StatusText.Text=s.PreviewMode?"SIMULATED DATA":$"{(s.InFight?"CURRENT":"COMPLETED")} • PROTOCOL UNVERIFIED";
        var max=Math.Max(1,s.Players.FirstOrDefault()?.Dps??1);
        Rows.ItemsSource=s.Players.Select((p,i)=>new Row(i+1,p.Name,p.ClassName,F(p.Dps),F(p.Damage),$"{p.Share:0.0}%",Brush(p.ClassName),Math.Max(0,150*p.Dps/max),Math.Clamp(100*p.Dps/max,0,100),p)).ToList();
        Rows.SelectedItem=Rows.Items.Cast<Row>().FirstOrDefault(r=>r.Stats.ActorId==selectedActor);
        EmptyRows.Visibility=s.Players.Count==0?Visibility.Visible:Visibility.Collapsed;
        if(Rows.SelectedItem==null){SelectedPlayer.Text="Select a player";SelectedMeta.Text="";}
        UpdateSkills();
    }

    static MeterSnapshot HistoricalCategory(MeterSnapshot saved,MeterCategory category)
    {
        if(category==MeterCategory.Damage || !saved.Categories.TryGetValue(category,out var data))
            return saved with {Category=category};
        return saved with {Players=data.Players,Skills=data.Skills,Category=category,MetricLabel=data.MetricLabel};
    }

    async void RefreshFightHistory(object sender,EventArgs e)
    {
        if(loadingHistory || HistoryProvider==null || HistoryLoader==null)return;
        loadingHistory=true;
        try
        {
            var choices=new List<HistoryChoice> {new("Saved fights ▾",null)};
            foreach(var path in HistoryProvider().Take(50))
            {
                try
                {
                    var saved=await HistoryLoader(path);
                    if(saved==null)continue;
                    var target=string.IsNullOrWhiteSpace(saved.Target?.Name)?"Encounter":saved.Target!.Name;
                    var when=saved.StartedUtc?.ToLocalTime().ToString("MM/dd HH:mm")??"Unknown time";
                    var duration=TimeSpan.FromSeconds(saved.FightSeconds).ToString(@"mm\:ss");
                    choices.Add(new HistoryChoice($"{when} • {target} • {duration}",path));
                }
                catch { }
            }
            FightHistory.ItemsSource=choices;
            FightHistory.SelectedIndex=historicalPath==null?0:Math.Max(0,choices.FindIndex(x=>x.Path==historicalPath));
        }
        finally {loadingHistory=false;}
    }

    async void FightHistoryChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(loadingHistory || FightHistory.SelectedItem is not HistoryChoice choice)return;
        if(choice.Path==null)
        {
            historicalSnapshot=null;
            historicalPath=null;
            if(last!=null)Render(last);
            return;
        }
        if(HistoryLoader==null)return;
        try
        {
            var saved=await HistoryLoader(choice.Path);
            if(saved==null)return;
            historicalSnapshot=saved;
            historicalPath=choice.Path;
            selectedActor=null;
            Render(saved);
        }
        catch(Exception ex) {MessageBox.Show(this,ex.Message,"Unable to read fight history");}
    }

    void SegmentChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(Segment==null)return;
        historicalSnapshot=null;
        historicalPath=null;
        if(FightHistory!=null && FightHistory.SelectedIndex>0)FightHistory.SelectedIndex=0;
        if(last!=null)Render(last);
    }

    static Brush Brush(string c)=>new SolidColorBrush((Color)ColorConverter.ConvertFromString(c.StartsWith("#",StringComparison.Ordinal)?c:Colors.TryGetValue(c,out var v)?v:Colors["Unknown"]));
    void UpdateSkills()
    {
        if(last==null)return;
        Skills.ItemsSource=last.Skills.Where(x=>x.ActorId==selectedActor).Take(7).Select(x=>new {x.Name,Damage=F(x.Damage)}).ToArray();
    }
    void PlayerSelected(object s,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(Rows.SelectedItem is Row r) {selectedActor=r.Stats.ActorId;SelectedPlayer.Text=r.Name;SelectedPlayer.Foreground=r.Brush;SelectedMeta.Text=$"{r.ClassName} • {r.Stats.Dps:N0} {last?.MetricLabel} • {r.Stats.Damage:N0} total • {r.Stats.CritPercent:0.0}% crit";UpdateSkills();}
    }

    void OpenSettings(object sender, RoutedEventArgs e)
    {
        var w = new Window { Title="AION 2 DPS — Overlay Settings", Width=450, Height=700,
            WindowStartupLocation=WindowStartupLocation.CenterOwner, Owner=this, Background=Brush("#0A0E16"), Foreground=Brush("#F4F7FF"), ResizeMode=ResizeMode.NoResize };
        w.Resources.MergedDictionaries.Add(Resources);
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
        if(Engine!=null)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock {Text="End fight after inactivity (seconds)"});
            var timeout=new System.Windows.Controls.Slider {Minimum=8,Maximum=120,Value=Engine.InactivityTimeout.TotalSeconds,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(0,5,0,8)};
            timeout.ValueChanged += (_,__)=> { var value=TimeSpan.FromSeconds(timeout.Value); Engine.InactivityTimeout=value; Engine.BossInactivityTimeout=value; };panel.Children.Add(timeout);
            panel.Children.Add(new System.Windows.Controls.TextBlock {Text="Default is 8 seconds. The same inactivity timeout applies to normal and boss fights.",TextWrapping=TextWrapping.Wrap,FontSize=11});
            var finish=new System.Windows.Controls.Button {Content="Finish current fight",Margin=new Thickness(0,8,0,0)};
            finish.Click += (_,__)=>Engine.ResetFight();panel.Children.Add(finish);
        }
        var historyButton=new System.Windows.Controls.Button {Content="Saved fight history",Margin=new Thickness(0,8,0,0)};
        historyButton.Click += async (_,__)=>
        {
            try
            {
                var list=new System.Windows.Controls.ListBox {ItemsSource=HistoryProvider?.Invoke()??Array.Empty<string>()};
                var historyWindow=new Window {Title="Saved fights — double-click to open",Width=850,Height=500,Owner=this,Content=list};
                list.MouseDoubleClick += async (_,__)=>
                {
                    if(list.SelectedItem is not string file || HistoryLoader==null)return;
                    try {var saved=await HistoryLoader(file);if(saved!=null)
                    {
                        var savedTabs=new System.Windows.Controls.TabControl();
                        foreach(var category in saved.Categories)
                        {
                            var grid=new System.Windows.Controls.DataGrid {IsReadOnly=true,ItemsSource=category.Value.Players};
                            savedTabs.Items.Add(new System.Windows.Controls.TabItem {Header=category.Key.ToString(),Content=grid});
                        }
                        if(savedTabs.Items.Count==0)savedTabs.Items.Add(new System.Windows.Controls.TabItem {Header="Damage",Content=new System.Windows.Controls.DataGrid {IsReadOnly=true,ItemsSource=saved.Players}});
                        new Window {Title=$"Fight {saved.StartedUtc} — {saved.EndReason}",Width=850,Height=500,Owner=historyWindow,Content=savedTabs}.Show();
                    }}
                    catch(Exception ex) {MessageBox.Show(historyWindow,ex.Message,"Unable to read fight history");}
                };
                historyWindow.Show(); await Task.CompletedTask;
            }
            catch(Exception ex) {MessageBox.Show(this,ex.Message,"Unable to open fight history");}
        };
        panel.Children.Add(historyButton);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text="Tip: double-click any player row for a detailed report.", Foreground=Brush("#8FB8FF"), TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,16,0,0) });
        w.Content=new System.Windows.Controls.ScrollViewer {Content=panel,VerticalScrollBarVisibility=System.Windows.Controls.ScrollBarVisibility.Auto}; w.ShowDialog(); SavePreferences();
    }

    static readonly string[] ThemeNames = { "Aion Blue/Red", "Neon Spectrum", "Void Purple", "Emerald Glass", "Solar Flare", "Ice Crystal" };
    static readonly string[] StyleNames = { "Classic Dashboard", "Details Inspired", "Kagerou Inspired", "Bars Only", "Raid Compact", "Glass Cards", "Tournament" };

    void ApplyOverlayStyle(string name)
    {
        currentStyle=name;
        ((System.Windows.Controls.TextBlock)PreviewBadge.Child).Text=name is "Details Inspired" or "Kagerou Inspired"?"PREVIEW":"SIMULATED PREVIEW";
        Rows.ItemTemplate=(DataTemplate)Resources["ClassicRows"];
        CategoryTabs.Visibility=Visibility.Visible; CategoryPicker.Visibility=Visibility.Collapsed;
        // Full reset: every preset starts from the same known layout.
        Width=780; Height=560; MinWidth=560; MinHeight=380;
        MainGrid.RowDefinitions[0].Height=new GridLength(42);
        MainGrid.RowDefinitions[1].Height=new GridLength(42);
        MainGrid.RowDefinitions[2].Height=new GridLength(1,GridUnitType.Star);
        MainGrid.RowDefinitions[3].Height=new GridLength(66);
        HeaderBar.Visibility=Visibility.Visible;
        ToolbarGrid.Visibility=Visibility.Visible;
        FooterBar.Visibility=Visibility.Visible;
        DetailPanel.Visibility=showDetails?Visibility.Visible:Visibility.Collapsed;
        ContentGrid.ColumnDefinitions[0].Width=new GridLength(1.7,GridUnitType.Star);
        ContentGrid.ColumnDefinitions[1].Width=new GridLength(8);
        ContentGrid.ColumnDefinitions[2].Width=new GridLength(1,GridUnitType.Star);
        Root.CornerRadius=new CornerRadius(9);
        Root.BorderThickness=new Thickness(1.4);
        Root.Opacity=1;
        Rows.Opacity=1;
        Rows.BorderThickness=new Thickness(1);
        Rows.Margin=new Thickness(0);
        DetailPanel.Opacity=1;
        HeaderBar.CornerRadius=new CornerRadius(8,8,0,0);
        FooterBar.CornerRadius=new CornerRadius(0,0,8,8);

        switch(name)
        {
            case "Details Inspired":
            case "Kagerou Inspired":
                bool compact=name=="Kagerou Inspired";
                MinWidth=420; MinHeight=260; Width=compact?500:580; Height=compact?455:390;
                Rows.ItemTemplate=(DataTemplate)Resources[compact?"KagerouRows":"DetailsRows"];
                CategoryTabs.Visibility=Visibility.Collapsed; CategoryPicker.Visibility=Visibility.Visible;
                CategoryPicker.SelectedIndex=Math.Max(0,Tabs.SelectedIndex);
                MainGrid.RowDefinitions[0].Height=new GridLength(36);
                MainGrid.RowDefinitions[1].Height=new GridLength(38);
                MainGrid.RowDefinitions[3].Height=new GridLength(0);
                FooterBar.Visibility=Visibility.Collapsed; DetailPanel.Visibility=Visibility.Collapsed;
                ContentGrid.ColumnDefinitions[0].Width=new GridLength(1,GridUnitType.Star);
                ContentGrid.ColumnDefinitions[1].Width=new GridLength(0);
                ContentGrid.ColumnDefinitions[2].Width=new GridLength(0);
                Root.CornerRadius=new CornerRadius(compact?8:2); Root.BorderThickness=new Thickness(1);
                Rows.BorderThickness=new Thickness(0);
                break;
            case "Bars Only":
                Width=540; Height=330; MinWidth=420; MinHeight=240;
                MainGrid.RowDefinitions[0].Height=new GridLength(34);
                MainGrid.RowDefinitions[1].Height=new GridLength(0);
                MainGrid.RowDefinitions[3].Height=new GridLength(0);
                ToolbarGrid.Visibility=Visibility.Collapsed;
                FooterBar.Visibility=Visibility.Collapsed;
                DetailPanel.Visibility=Visibility.Collapsed;
                ContentGrid.ColumnDefinitions[0].Width=new GridLength(1,GridUnitType.Star);
                ContentGrid.ColumnDefinitions[1].Width=new GridLength(0);
                ContentGrid.ColumnDefinitions[2].Width=new GridLength(0);
                Root.CornerRadius=new CornerRadius(4);
                Root.BorderThickness=new Thickness(1);
                Rows.BorderThickness=new Thickness(0);
                break;
            case "Raid Compact":
                Width=650; Height=410; MinWidth=500; MinHeight=300;
                MainGrid.RowDefinitions[0].Height=new GridLength(36);
                MainGrid.RowDefinitions[1].Height=new GridLength(34);
                MainGrid.RowDefinitions[3].Height=new GridLength(38);
                DetailPanel.Visibility=Visibility.Collapsed;
                ContentGrid.ColumnDefinitions[0].Width=new GridLength(1,GridUnitType.Star);
                ContentGrid.ColumnDefinitions[1].Width=new GridLength(0);
                ContentGrid.ColumnDefinitions[2].Width=new GridLength(0);
                Root.CornerRadius=new CornerRadius(2);
                Root.BorderThickness=new Thickness(1);
                Rows.BorderThickness=new Thickness(0,1,0,1);
                break;
            case "Glass Cards":
                Width=840; Height=590; MinWidth=600; MinHeight=400;
                Root.Opacity=.82;
                Root.CornerRadius=new CornerRadius(20);
                Root.BorderThickness=new Thickness(1);
                Rows.Opacity=.90;
                Rows.Margin=new Thickness(5);
                DetailPanel.Opacity=.90;
                HeaderBar.CornerRadius=new CornerRadius(19,19,8,8);
                FooterBar.CornerRadius=new CornerRadius(8,8,19,19);
                break;
            case "Tournament":
                Width=960; Height=640; MinWidth=720; MinHeight=460;
                MainGrid.RowDefinitions[0].Height=new GridLength(52);
                MainGrid.RowDefinitions[1].Height=new GridLength(46);
                MainGrid.RowDefinitions[3].Height=new GridLength(72);
                ContentGrid.ColumnDefinitions[0].Width=new GridLength(2.2,GridUnitType.Star);
                ContentGrid.ColumnDefinitions[2].Width=new GridLength(.8,GridUnitType.Star);
                Root.CornerRadius=new CornerRadius(0);
                Root.BorderThickness=new Thickness(3,1,3,1);
                HeaderBar.CornerRadius=new CornerRadius(0);
                FooterBar.CornerRadius=new CornerRadius(0);
                Rows.BorderThickness=new Thickness(0,2,0,2);
                break;
        }
        if(!showDetails && DetailPanel.Visibility==Visibility.Collapsed) {
            ContentGrid.ColumnDefinitions[0].Width=new GridLength(1,GridUnitType.Star);
            ContentGrid.ColumnDefinitions[1].Width=new GridLength(0);ContentGrid.ColumnDefinitions[2].Width=new GridLength(0);
        }
        ApplyTheme(currentTheme);
    }
    void ApplyTheme(string name)
    {
        currentTheme=name;
        var p = name switch {
            "Neon Spectrum" => ("#E80B1022","#FF2D55","#00E5FF","#151A36","#00E5FF","#FF6B8A","#101A31"),
            "Void Purple" => ("#ED100B22","#B65CFF","#FF4FD8","#21113B","#D9A3FF","#FF82E5","#1A102D"),
            "Emerald Glass" => ("#E9081D1B","#20E3B2","#56F39A","#0E302B","#7CFFD8","#A4FFBE","#0B2522"),
            "Solar Flare" => ("#ED211008","#FF5A36","#FFC857","#3A170D","#FFD27A","#FF8066","#2D120A"),
            "Ice Crystal" => ("#EB071B2D","#4CC9F0","#BDEBFF","#0B2A42","#CFF4FF","#78D8FF","#091F31"),
            _ => ("#F20A1630","#E53935","#2F80ED","#101F3D","#8FB8FF","#FF4D5A","#0E1C35")
        };
        var bg=Brush(p.Item1); var accent=Brush(p.Item2); var secondary=Brush(p.Item3);
        var chrome=Brush(p.Item4); var primaryText=Brush(p.Item5); var hot=Brush(p.Item6); var control=Brush(p.Item7);
        if(currentStyle=="Kagerou Inspired") {bg=BrushWithOpacity(p.Item1,.62);chrome=BrushWithOpacity(p.Item4,.78);}
        Root.Background=bg; Root.BorderBrush=accent;
        HeaderBar.Background=chrome; HeaderBar.BorderBrush=secondary;
        FooterBar.Background=chrome; FooterBar.BorderBrush=secondary;
        Rows.Background=BrushWithOpacity(p.Item4,currentStyle=="Kagerou Inspired"?.15:.62); Rows.BorderBrush=secondary;
        DetailPanel.Background=BrushWithOpacity(p.Item4, .72); DetailPanel.BorderBrush=secondary;
        TitleAion.Foreground=primaryText; TitleDps.Foreground=hot;
        GroupDps.Foreground=primaryText; StatusText.Foreground=primaryText;
        TargetHp.Foreground=hot; BossHp.Foreground=accent; BossHp.Background=control;
        Segment.Background=control; Segment.Foreground=Brush("#F4F7FF");
        foreach(var item in Tabs.Items.OfType<System.Windows.Controls.TabItem>()) {
            item.Background=control; item.BorderBrush=secondary; item.Foreground=primaryText;
        }
        foreach(var button in FindVisualChildren<System.Windows.Controls.Button>(this)) {
            button.Background=control; button.BorderBrush=secondary; button.Foreground=Brush("#F4F7FF");
        }
    }

    static Brush BrushWithOpacity(string hex, double opacity)
    {
        var color=(Color)ColorConverter.ConvertFromString(hex);
        return new SolidColorBrush(Color.FromArgb((byte)(255*opacity),color.R,color.G,color.B));
    }

    static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is null) yield break;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is T match) yield return match;
            foreach(var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    void OpenSelectedReport(object sender, RoutedEventArgs e)
    {
        if(last is null || Rows.SelectedItem is not Row r)return;
        var segment=(MeterSegment)Math.Clamp(Segment.SelectedIndex,0,2);
        var frozen=historicalSnapshot??Engine?.Snapshot(segment,MeterCategory.Damage,true)??last;
        Func<MeterSnapshot> provider=()=>historicalSnapshot!=null?frozen:Engine is null?frozen:segment==MeterSegment.Overall?Engine.Snapshot(segment,MeterCategory.Damage,true):Engine.SnapshotEncounter(frozen.EncounterId)??frozen;
        var report=new CombatReportWindow(provider,r.Stats.ActorId,(MeterCategory)Math.Clamp(Tabs.SelectedIndex,0,7)) {Owner=this};
        report.Show();
    }
    static string PreferencesPath=>System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Aion2DPSPro","overlay-settings.json");
    public sealed record OverlayPreferences(string Style,string Theme,double Opacity,bool Details,bool Topmost,double Width,double Height,double Left,double Top);
    void LoadPreferences()
    {
        try {
            if(!System.IO.File.Exists(PreferencesPath))return;
            var p=System.Text.Json.JsonSerializer.Deserialize<OverlayPreferences>(System.IO.File.ReadAllText(PreferencesPath));
            if(p==null)return;
            showDetails=p.Details;
            if(ThemeNames.Contains(p.Theme))currentTheme=p.Theme;
            ApplyOverlayStyle(StyleNames.Contains(p.Style)?p.Style:currentStyle);
            if(double.IsFinite(p.Opacity))Opacity=Math.Clamp(p.Opacity,.35,1);
            Topmost=p.Topmost;
            if(double.IsFinite(p.Width))Width=Math.Clamp(p.Width,MinWidth,Math.Max(MinWidth,SystemParameters.VirtualScreenWidth));
            if(double.IsFinite(p.Height))Height=Math.Clamp(p.Height,MinHeight,Math.Max(MinHeight,SystemParameters.VirtualScreenHeight));
            if(double.IsFinite(p.Left))Left=Math.Clamp(p.Left,SystemParameters.VirtualScreenLeft,Math.Max(SystemParameters.VirtualScreenLeft,SystemParameters.VirtualScreenLeft+SystemParameters.VirtualScreenWidth-Width));
            if(double.IsFinite(p.Top))Top=Math.Clamp(p.Top,SystemParameters.VirtualScreenTop,Math.Max(SystemParameters.VirtualScreenTop,SystemParameters.VirtualScreenTop+SystemParameters.VirtualScreenHeight-Height));
        }
        catch(Exception ex) when(ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }
    void SavePreferences()
    {
        try {
            var path=PreferencesPath;System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var p=new OverlayPreferences(currentStyle,currentTheme,Opacity,showDetails,Topmost,Width,Height,double.IsFinite(Left)?Left:0,double.IsFinite(Top)?Top:0);
            var temp=path+".tmp";System.IO.File.WriteAllText(temp,System.Text.Json.JsonSerializer.Serialize(p));System.IO.File.Move(temp,path,true);
        }
        catch(Exception ex) when(ex is System.IO.IOException or UnauthorizedAccessException) {StatusText.Text="Unable to save overlay settings";}
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e){SavePreferences();base.OnClosing(e);}
    void CategoryChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e) {
        if(Tabs==null||CategoryPicker.SelectedIndex<0)return;
        Tabs.SelectedIndex=CategoryPicker.SelectedIndex;
        if(last!=null)Render(last);
    }
    void CloseOverlay(object s,RoutedEventArgs e){SavePreferences();Hide();}
    void Drag(object s,MouseButtonEventArgs e){
        var origin=e.OriginalSource as DependencyObject;
        while(origin!=null && origin!=HeaderBar){if(origin is System.Windows.Controls.Primitives.ButtonBase)return;origin=VisualTreeHelper.GetParent(origin);}
        if(e.LeftButton==MouseButtonState.Pressed){DragMove();e.Handled=true;}
    }
    public void ApplyClickThrough(bool enabled){clickThrough=enabled;var h=new WindowInteropHelper(this).Handle;if(h==IntPtr.Zero)return;var ex=GetWindowLong(h,-20);SetWindowLong(h,-20,enabled?ex|0x20:ex&~0x20);}
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd,int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd,int nIndex,int dwNewLong);
    static string F(double v)=>v>=1_000_000?$"{v/1_000_000:0.00}M":v>=1_000?$"{v/1_000:0.0}K":$"{v:0}";
    sealed record HistoryChoice(string Label,string? Path);
    sealed record Row(int Rank,string Name,string ClassName,string Dps,string Damage,string Share,Brush Brush,double BarWidth,double Relative,PlayerStats Stats);
}

