using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aion2DPSPro;
using Aion2DPSPro.Overlay;

internal static class Program
{
 [STAThread]
 static void Main()
 {
  var app=new Application();
  var window=new OverlayWindow();
  void Invoke(string name,string value)=>typeof(OverlayWindow).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,new object[]{value});
  string? previous=null;
  foreach(var theme in new[]{"Aion Blue/Red","Neon Spectrum","Void Purple","Emerald Glass","Solar Flare","Ice Crystal"})
  {
   Invoke("ApplyTheme",theme);
   string color=((SolidColorBrush)((Border)window.FindName("Root")).Background).Color.ToString();
   if(color==previous)throw new Exception($"Theme did not change: {theme}");previous=color;
  }
  foreach(var style in new[]{"Bars Only","Raid Compact","Glass Cards","Tournament","Classic Dashboard"})Invoke("ApplyOverlayStyle",style);
  if(((FrameworkElement)window.FindName("ToolbarGrid")).Visibility!=Visibility.Visible)throw new Exception("Classic did not restore toolbar");
  var t=DateTime.UtcNow;var engine=new CombatEngine(()=>t);
  engine.Apply(new(t,CombatKind.Damage,1,"Player",2,"Target","Strike",100));
  window.SnapshotProvider=(segment,category)=>engine.Snapshot(segment,category);
  var tabs=(TabControl)window.FindName("Tabs");var segment=(ComboBox)window.FindName("Segment");
  for(int s=0;s<3;s++)for(int c=0;c<8;c++){segment.SelectedIndex=s;tabs.SelectedIndex=c;window.Render(engine.Snapshot());}
  tabs.SelectedIndex=0;segment.SelectedIndex=0;window.Render(engine.Snapshot());
  if(((ListView)window.FindName("Rows")).Items.Count!=1)throw new Exception("Damage rows missing");
  tabs.SelectedIndex=1;window.Render(engine.Snapshot());
  if(((ListView)window.FindName("Rows")).Items.Count!=0)throw new Exception("Healing incorrectly reused damage rows");
  // Integration: identity-only socket and combat socket, same exact entity and endpoint scope.
  var captureEngine=new CombatEngine(()=>t);
  var identityProfile=new Aion2DPSPro.Protocol.ProtocolProfile("test","test","test",13328,new Dictionary<string,Aion2DPSPro.Protocol.PacketTag>{{"selfInfo",new(51,54)},{"damage",new(4,56)}});
  using(var adapter=new Aion2DPSPro.Capture.LiveCaptureAdapter(new Aion2DPSPro.Protocol.CurrentClientDecoder(identityProfile)))
  {
   adapter.EventReceived+=captureEngine.Apply;
   var process=typeof(Aion2DPSPro.Capture.LiveCaptureAdapter).GetMethod("Process",BindingFlags.NonPublic|BindingFlags.Instance)!;
   void Packet(ushort localPort,string hex)
   {
    var ip=new PacketDotNet.IPv4Packet(System.Net.IPAddress.Parse("10.0.0.2"),System.Net.IPAddress.Parse("10.0.0.1"));
    var tcp=new PacketDotNet.TcpPacket(13328,localPort) {SequenceNumber=100,PayloadData=Convert.FromHexString(hex)};
    ip.PayloadPacket=tcp;process.Invoke(adapter,new object[]{"test-adapter",ip,t});
   }
   Packet(50001,"193336FD235F81C1283708546573744865726F000000");
   Packet(50002,"210438E3A0020400FD2340B7B70009020B95C34701000000D658E7020100");
   if(captureEngine.Snapshot().Players.Single().Name!="TestHero")throw new Exception("Identity-only connection did not resolve combat name");
   adapter.Dispose();adapter.Completion.GetAwaiter().GetResult();
  }
  Console.WriteLine("PASS: separate identity/combat sockets resolve exact actor name");
  var reportEngine=new CombatEngine(()=>t.AddSeconds(15));
  reportEngine.Apply(new(t,CombatKind.Damage,1,"TestHero",2,"Training Scarecrow","Punishing Strike",18000,SourceClass:"Templar",DamageFlags:DamageFlags.Critical|DamageFlags.Perfect));
  reportEngine.Apply(new(t.AddSeconds(4),CombatKind.Damage,1,"TestHero",2,"Training Scarecrow","Desperate Strike",12000,SourceClass:"Templar",DamageFlags:DamageFlags.Back));
  reportEngine.Apply(new(t.AddSeconds(8),CombatKind.Damage,1,"TestHero",2,"Training Scarecrow","Pummel",9000,SourceClass:"Templar"));
  reportEngine.Apply(new(t.AddSeconds(10),CombatKind.Heal,1,"TestHero",1,"TestHero","Mend",3500,SourceClass:"Templar"));
  var report=new CombatReportWindow(()=>reportEngine.Snapshot(MeterSegment.Current,MeterCategory.Damage,true),1);
  report.Show();report.UpdateLayout();
  if(((DataGrid)report.FindName("SkillGrid")).Items.Count!=3)throw new Exception("Report damage skills missing");
  if(((DataGrid)report.FindName("TargetGrid")).Items.Count!=1)throw new Exception("Report targets missing");
  var reportCategories=(ComboBox)report.FindName("Category");reportCategories.SelectedItem=MeterCategory.Healing;report.Refresh();
  if(((DataGrid)report.FindName("SkillGrid")).Items.Count!=1)throw new Exception("Report healing skills missing");
  reportEngine.Apply(new(t.AddSeconds(11),CombatKind.Heal,1,"TestHero",1,"TestHero","Second heal",500));report.Refresh();
  if(((DataGrid)report.FindName("SkillGrid")).Items.Count!=2)throw new Exception("Report live refresh missing");
  ((CheckBox)report.FindName("Pause")).IsChecked=true;
  reportEngine.Apply(new(t.AddSeconds(12),CombatKind.Heal,1,"TestHero",1,"TestHero","Third heal",500));report.Refresh(true);
  if(((DataGrid)report.FindName("SkillGrid")).Items.Count!=2)throw new Exception("Report pause changed data");
  ((CheckBox)report.FindName("Pause")).IsChecked=false;reportCategories.SelectedItem=MeterCategory.Damage;report.Refresh();report.UpdateLayout();
  var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)report.ActualWidth,(int)report.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(report);
  var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
  using(var file=System.IO.File.Create("report-preview.png"))encoder.Save(file);
  report.Close();
  Console.WriteLine("PASS: modern report data, categories, targets, live refresh and pause");
  window.Close();Console.WriteLine("PASS: WPF themes, styles, segment/category switching");app.Shutdown();
 }
}
