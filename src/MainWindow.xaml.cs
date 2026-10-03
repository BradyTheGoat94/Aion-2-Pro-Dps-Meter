using System.Windows;
using System.Windows.Threading;
using Aion2DPSPro.Capture;
using Aion2DPSPro.Overlay;
using Aion2DPSPro.Protocol;

namespace Aion2DPSPro;

public partial class MainWindow : Window
{
    readonly CombatEngine engine = new();
              readonly Dictionary<long, string> liveEntityNames = new();
              readonly Dictionary<long, string> liveEntityClasses = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly OverlayWindow overlay = new();
    readonly LiveCaptureAdapter capture;
    readonly List<OverlayWindow> extraOverlays = new();
    long packets;
    long decoderDiagnostics;
    long recognized;
    long parsedEvents;
              long duplicates;
              long damageEvents, healEvents, hpEvents, otherEvents;
              long acceptedDamageEvents;
              long snapshotId;
              string activeFlow = "Waiting...";
              readonly Queue<string> eventInspector = new();
    string captureStatus = "Starting live capture...";

    public MainWindow()
    {
        InitializeComponent();
        engine.PreviewMode = false;
        var profilePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Protocol", "Profiles", "global-current.json");
        ProtocolProfile profile;
        try { profile = ProtocolProfile.Load(profilePath); }
        catch { profile = ProtocolProfile.SafeGlobalScaffold(); }

        var decoder = new CurrentClientDecoder(profile);
                  var validationDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aion2DPSPro", "Validation");
                  System.IO.Directory.CreateDirectory(validationDir);
                  var validationPath = System.IO.Path.Combine(validationDir, $"combat-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                  int validationLines = 0;
                  decoder.ValidationRecord += s => { if (validationLines >= 500) return; System.IO.File.AppendAllText(validationPath, s + Environment.NewLine); validationLines++; Dispatcher.Invoke(() => ValidationFile.Text = validationPath); };
        decoder.Diagnostic += d => Dispatcher.Invoke(() =>
        {
            decoderDiagnostics++;
            if (d.Stage == "parse") recognized++;
            LastDecoder.Text = $"[{d.Stage}] {d.Message} ({d.Bytes} bytes)";
            RenderDiagnostics(decoder.ProfileId);
        });

        capture = new LiveCaptureAdapter(decoder);
        capture.PacketCaptured += () => Dispatcher.Invoke(() => { packets++; RenderDiagnostics(decoder.ProfileId); });
        capture.DuplicateSuppressed += () => Dispatcher.Invoke(() => { duplicates++; RenderDiagnostics(decoder.ProfileId); });
                  capture.FlowLocked += s => Dispatcher.Invoke(() => { activeFlow = s; RenderDiagnostics(decoder.ProfileId); });
                  capture.EventReceived += e => Dispatcher.Invoke(() =>
        {
            parsedEvents++;
                      LatestEvent.Text = $"kind={e.Kind} amount={e.Amount} src={e.SourceId} tgt={e.TargetId} skill={e.Skill} type={e.DamageType}";
                      LastEvent.Text = $"kind={e.Kind} src={e.SourceId} tgt={e.TargetId} skill={e.Skill} amount={e.Amount} type={e.DamageType}";
                      if (e.Kind == CombatKind.Damage) damageEvents++;
                      else if (e.Kind == CombatKind.Heal) healEvents++;
                      else if (e.Kind == CombatKind.TargetHp) hpEvents++;
                      else otherEvents++;
                                  var before = engine.Snapshot();
                                            if (e.Kind == CombatKind.PlayerName && e.SourceId > 0 && !string.IsNullOrWhiteSpace(e.Source))
                          liveEntityNames[e.SourceId] = e.Source;
                      if ((e.Kind == CombatKind.Damage || e.Kind == CombatKind.Heal) && e.SourceId > 0 && liveEntityNames.TryGetValue(e.SourceId, out var resolvedSourceName))
                          e = e with { Source = resolvedSourceName };
                                            if ((e.Kind == CombatKind.Damage || e.Kind == CombatKind.Heal) && e.SourceId > 0)
                      {
                          if (!string.IsNullOrWhiteSpace(e.SourceClass) && e.SourceClass != "Unknown")
                              liveEntityClasses[e.SourceId] = e.SourceClass;
                          else if (liveEntityClasses.TryGetValue(e.SourceId, out var validatedSourceClass))
                              e = e with { SourceClass = validatedSourceClass };
                      }
                      engine.Apply(e);
                      var after = engine.Snapshot();
                      if (e.Kind == CombatKind.Damage && after.FightDamage > before.FightDamage) acceptedDamageEvents++;
                      var accepted = e.Kind != CombatKind.Damage || after.FightDamage > before.FightDamage;
                      var delta = after.FightDamage - before.FightDamage;
                      var line = $"{e.Utc:HH:mm:ss.fff} {e.Kind} src={e.SourceId} tgt={e.TargetId} skill={e.Skill} amount={e.Amount} type={e.DamageType} engineDelta={delta} {(accepted ? "ACCEPT" : "NO-DAMAGE-CHANGE")}";
                      eventInspector.Enqueue(line);
                      while (eventInspector.Count > 12) eventInspector.Dequeue();
                      EventInspector.Text = string.Join(Environment.NewLine, eventInspector.Reverse());
            RenderDiagnostics(decoder.ProfileId);
        });
        capture.StatusChanged += s => Dispatcher.Invoke(() =>
        {
            captureStatus = s;
            RenderDiagnostics(decoder.ProfileId);
        });

        timer.Tick += (_, _) => Render();
        timer.Start();
        capture.Start();
        RenderDiagnostics(decoder.ProfileId);
    }

    void RenderDiagnostics(string profileId)
    {
        Status.Text = captureStatus;
        PacketCount.Text = packets.ToString("N0");
        DiagnosticCount.Text = decoderDiagnostics.ToString("N0");
        RecognizedCount.Text = recognized.ToString("N0");
        EventCount.Text = parsedEvents.ToString("N0");
                  AcceptedDamageCount.Text = acceptedDamageEvents.ToString("N0");
                  DuplicateCount.Text = duplicates.ToString("N0");
                  DamageEventCount.Text = damageEvents.ToString("N0");
                  HealEventCount.Text = healEvents.ToString("N0");
                  HpEventCount.Text = hpEvents.ToString("N0");
                  OtherEventCount.Text = otherEvents.ToString("N0");
                  ActiveFlow.Text = activeFlow;
        Profile.Text = profileId;
    }

    void OpenOverlay(object sender, RoutedEventArgs e)
    {
        if (!overlay.IsVisible) overlay.Show();
        overlay.Activate();
    }

    void Reset(object sender, RoutedEventArgs e) => engine.ResetFight();

    void Render()
    {
        var s = engine.Snapshot();
                  snapshotId++;
                  SnapshotState.Text = $"Snapshot #{snapshotId:N0} | FightDamage={s.FightDamage:N0} | FightDPS={s.FightDps:0.##}";
        Dps.Text = $"{s.FightDps:0} DPS";
        Damage.Text = $"{s.FightDamage:N0} damage";
        Target.Text = s.Target is null ? "No target" :
            $"{s.Target.Name}  {s.Target.Percent:0.0}%  {s.Target.CurrentHp:N0}/{s.Target.MaxHp:N0}";
        Grid.ItemsSource = s.Players.Select((p,i) => new {
            Rank=i+1, Name=ApplyLiveEntityNames(p.Name), Dps=p.Dps.ToString("N0"), Damage=p.Damage.ToString("N0"), Share=$"{p.Share:0.0}%"
        }).ToList();
        overlay.Render(s);
    }

    protected override void OnClosed(EventArgs e)
    {
        timer.Stop();
        capture.Dispose();
        overlay.Close();
        foreach (var w in extraOverlays) w.Close();
        base.OnClosed(e);
    }

              private string ApplyLiveEntityNames(string label)
              {
                  if (string.IsNullOrWhiteSpace(label)) return label;
                  const string prefix = "Actor ";
                  if (!label.StartsWith(prefix, StringComparison.Ordinal)) return label;
                  if (!long.TryParse(label.Substring(prefix.Length), out var id)) return label;
                  return liveEntityNames.TryGetValue(id, out var name) ? name : label;
              }
}










