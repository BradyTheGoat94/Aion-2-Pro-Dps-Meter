using System.Windows;
using System.ComponentModel;
using System.Windows.Threading;
using Aion2DPSPro.Capture;
using Aion2DPSPro.Overlay;
using Aion2DPSPro.Protocol;
using Aion2DPSPro.Storage;

namespace Aion2DPSPro;

public partial class MainWindow : Window
{
    readonly CombatEngine engine=new();
    readonly DispatcherTimer timer=new() {Interval=TimeSpan.FromMilliseconds(250)};
    readonly OverlayWindow overlay=new();
    readonly LiveCaptureAdapter capture;
    readonly object diagnosticsGate=new();
    readonly Queue<string> inspector=new();
    long packets,decoderDiagnostics,recognized,parsedEvents,duplicates,damageEvents,healEvents,hpEvents,otherEvents,acceptedDamageEvents,snapshotId;
    string activeFlow="Waiting...",captureStatus="Starting capture...",lastDecoder="",lastEvent="",profileId="",validationPath="",historyError="";
    Task saveTail=Task.CompletedTask;
    readonly FightStore store=new();
    public MainWindow()
    {
        InitializeComponent();
        overlay.Engine=engine;
        overlay.SnapshotProvider=(segment,category)=>engine.Snapshot(segment,category);
        overlay.HistoryProvider=()=>store.List().ToArray();
        overlay.HistoryLoader=path=>store.LoadAsync(path);
        var path=System.IO.Path.Combine(AppContext.BaseDirectory,"Protocol","Profiles","global-current.json");
        ProtocolProfile profile;
        try {profile=ProtocolProfile.Load(path);} catch {profile=ProtocolProfile.SafeGlobalScaffold();}
        var decoder=new CurrentClientDecoder(profile); profileId=decoder.ProfileId;
        var validationDir=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Aion2DPSPro","Validation");
        System.IO.Directory.CreateDirectory(validationDir);
        validationPath=System.IO.Path.Combine(validationDir,$"combat-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var validationCounts=new Dictionary<string,int>();
        void RecordValidation(string line)
        {
            var tag=line.Split('|').FirstOrDefault(x=>x.StartsWith("tag="))??"tag=other";
            string bucket=tag is "tag=identityLifecycle" or "tag=entityBridge" or "tag=mobIdentity" or "tag=targetHpCandidate"?"verbose":
                tag is "tag=damage" or "tag=dot"?"combat":tag=="tag=damageFlags"?"flags":tag.Contains("Identity",StringComparison.OrdinalIgnoreCase)||tag=="tag=identityMap"?"identity":"other";
            int limit=bucket=="combat"?600:bucket=="verbose"?50:bucket=="flags"?100:200;
            if(validationCounts.GetValueOrDefault(bucket)>=limit)return;
            try {System.IO.File.AppendAllText(validationPath,line+Environment.NewLine);validationCounts[bucket]=validationCounts.GetValueOrDefault(bucket)+1;}
            catch(Exception ex) {lock(diagnosticsGate)captureStatus=$"Validation logging failed: {ex.Message}";}
        }
        decoder.ValidationRecord+=RecordValidation;
        decoder.Diagnostic += d => {lock(diagnosticsGate) {decoderDiagnostics++;if(d.Stage=="parse")recognized++;lastDecoder=$"[{d.Stage}] {d.Message} ({d.Bytes} bytes)";}};
        engine.EncounterCompleted += snapshot =>
        {
            // Serialize writes, preserving ordering and reporting failures without stopping capture.
            lock(diagnosticsGate) saveTail=saveTail.ContinueWith(async _=>
            {
                try {await store.SaveAsync(snapshot);} catch(Exception ex) {lock(diagnosticsGate) historyError=$"History save failed: {ex.Message}";}
            }).Unwrap();
        };
        capture=new LiveCaptureAdapter(decoder);
        capture.ValidationRecord+=RecordValidation;
        capture.PacketCaptured += ()=>Interlocked.Increment(ref packets);
        capture.DuplicateSuppressed += ()=>Interlocked.Increment(ref duplicates);
        capture.FlowLocked += s=> {lock(diagnosticsGate)activeFlow=s;};
        capture.ConnectionReset += ()=>engine.Apply(new(DateTime.UtcNow,CombatKind.Zone));
        capture.StatusChanged += s=> {lock(diagnosticsGate)captureStatus=s;};
        capture.EventReceived += e=>
        {
            engine.Apply(e);
            lock(diagnosticsGate)
            {
                parsedEvents++;
                lastEvent=$"kind={e.Kind} amount={e.Amount} src={e.SourceId} tgt={e.TargetId} skill={e.Skill} type={e.DamageType}";
                if(e.Kind==CombatKind.Damage) {damageEvents++;if(e.Amount>0)acceptedDamageEvents++;}
                else if(e.Kind==CombatKind.Heal)healEvents++;
                else if(e.Kind==CombatKind.TargetHp)hpEvents++;
                else otherEvents++;
                inspector.Enqueue($"{e.Utc:HH:mm:ss.fff} {lastEvent}");while(inspector.Count>12)inspector.Dequeue();
            }
        };
        timer.Tick += (_,_)=>Render(); timer.Start(); capture.Start();
    }
    void OpenOverlay(object sender,RoutedEventArgs e) {if(!overlay.IsVisible)overlay.Show();overlay.Activate();}
    void Reset(object sender,RoutedEventArgs e)=>engine.ResetFight();
    void Render()
    {
        var s=engine.Snapshot();
        snapshotId++; SnapshotState.Text=$"Snapshot #{snapshotId:N0} | FightDamage={s.FightDamage:N0} | FightDPS={s.FightDps:0.##}";
        Dps.Text=$"{s.FightDps:0} DPS"; Damage.Text=$"{s.FightDamage:N0} damage";
        Target.Text=s.Target is null?"No target":$"{s.Target.Name} {s.Target.Percent:0.0}% {s.Target.CurrentHp:N0}/{s.Target.MaxHp:N0}";
        Grid.ItemsSource=s.Players.Select((p,i)=>new {Rank=i+1,p.Name,Dps=p.Dps.ToString("N0"),Damage=p.Damage.ToString("N0"),Share=$"{p.Share:0.0}%"}).ToArray();
        overlay.Render(s);
        lock(diagnosticsGate)
        {
            Status.Text=$"{captureStatus} | {capture.Health}"+(historyError.Length>0?$" | {historyError}":"");
            PacketCount.Text=Interlocked.Read(ref packets).ToString("N0"); DuplicateCount.Text=Interlocked.Read(ref duplicates).ToString("N0");
            DiagnosticCount.Text=decoderDiagnostics.ToString("N0"); RecognizedCount.Text=recognized.ToString("N0"); EventCount.Text=parsedEvents.ToString("N0");
            AcceptedDamageCount.Text=acceptedDamageEvents.ToString("N0"); DamageEventCount.Text=damageEvents.ToString("N0"); HealEventCount.Text=healEvents.ToString("N0");
            HpEventCount.Text=hpEvents.ToString("N0"); OtherEventCount.Text=otherEvents.ToString("N0"); ActiveFlow.Text=activeFlow; Profile.Text=profileId;
            LastDecoder.Text=lastDecoder; LastEvent.Text=LatestEvent.Text=lastEvent; EventInspector.Text=string.Join(Environment.NewLine,inspector.Reverse()); ValidationFile.Text=validationPath;
        }
    }
    bool allowClose,closing;
    protected override async void OnClosing(CancelEventArgs e)
    {
        if(allowClose) {base.OnClosing(e);return;}
        e.Cancel=true;base.OnClosing(e);
        if(closing)return;
        closing=true;timer.Stop();capture.Dispose();
        try
        {
            await capture.Completion;
            engine.ResetFight();
            Task pending;lock(diagnosticsGate)pending=saveTail;
            await pending;
            if(historyError.Length>0)MessageBox.Show(this,historyError,"History was not saved");
        }
        catch(Exception ex) {MessageBox.Show(this,ex.Message,"Unable to finish saving history");}
        overlay.Close();allowClose=true;Close();
    }
}
