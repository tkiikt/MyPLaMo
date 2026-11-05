using System.Runtime.InteropServices;
using System.Text.Json;

namespace MyPLaMo;

record Entry(string Source,string Result,string From,string To)
{
    public override string ToString()=>Source.ReplaceLineEndings(" ")[..Math.Min(60,Source.ReplaceLineEndings(" ").Length)];
}

sealed class MainWindow : Form
{
    readonly Preferences prefs=Preferences.Load();
    readonly Engine engine=new();
    readonly SemaphoreSlim engineGate = new(1);
    readonly CheckBox chrome = new() { Text = "Chromeからの翻訳を受け付ける", AutoSize = true, Anchor = AnchorStyles.Left };
    ChromeBridge? bridge;
    bool syncingChrome;
    readonly NotifyIcon tray=new();
    readonly TextBox input=Editor(false),output=Editor(true);
    readonly ComboBox from=Ui.Combo(new[]{new Lang("","自動検出")}.Concat(Lang.List).ToArray()),
        to=Ui.Combo(Lang.List), style=Ui.Combo("標準","敬体","常体");
    readonly Button translate=Ui.Button("翻訳する",true), settings=Ui.Button("モデルと設定"), swap=Ui.Button("⇄  入れ替え"),
        send=Ui.Button("元のアプリに挿入"),log=Ui.Button("履歴");
    readonly Label status=Ui.Label(""),count=Ui.Label("");
    readonly List<Entry> entries=[];
    static string LogPath=>Path.Combine(Preferences.Home,"history.json");
    CancellationTokenSource? job;
    bool quitting,capturing,modal;
    IntPtr origin;
    string inferred="";
    static TextBox Editor(bool readOnly)=>new(){Multiline=true,ReadOnly=readOnly,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BackColor=Ui.Paper,ForeColor=Ui.Ink,
        BorderStyle=BorderStyle.FixedSingle,Font=new Font("Yu Gothic UI",14),AcceptsReturn=true,PlaceholderText=readOnly?"翻訳結果":"翻訳したい文章を入力…",AccessibleName=readOnly?"訳文":"原文"};
    public MainWindow(bool startHidden)
    {
        Ui.Window(this,"MyPLaMo  •  ローカル翻訳",new Size(1020,570));MinimumSize=new Size(840,440);KeyPreview=true;
        var root=Ui.Stack(20);Controls.Add(root);
        void Add(Control c,bool fill=false){root.RowStyles.Add(fill?new RowStyle(SizeType.Percent,100):new RowStyle(SizeType.AutoSize));root.Controls.Add(c);}
        var top=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2};top.ColumnStyles.Add(new(SizeType.Percent,100));
        top.Controls.Add(Ui.Label("MyPLaMo",22,true));top.Controls.Add(Ui.Row(chrome,log,settings));Add(top);
        var panes=new TableLayoutPanel{ColumnCount=2,Dock=DockStyle.Fill};
        panes.ColumnStyles.Add(new(SizeType.Percent,50));panes.ColumnStyles.Add(new(SizeType.Percent,50));
        panes.RowStyles.Add(new(SizeType.AutoSize));panes.RowStyles.Add(new(SizeType.Percent,100));panes.RowStyles.Add(new(SizeType.AutoSize));
        to.Width=from.Width=136;style.Width=72;SelectLang(from,prefs.Source);SelectLang(to,prefs.Target);
        style.SelectedIndex=Array.IndexOf(new[]{"","polite","plain"},prefs.Style) switch {<0=>0,var n=>n};
        panes.Controls.Add(Ui.Row(Ui.Label("原文",11,true),from,swap),0,0);panes.Controls.Add(Ui.Row(Ui.Label("訳文",11,true),to,style),1,0);
        panes.Controls.Add(input,0,1);panes.Controls.Add(output,1,1);
        Button paste=Ui.Button("貼り付け"),clear=Ui.Button("クリア"),copy=Ui.Button("訳文をコピー");
        panes.Controls.Add(Ui.Row(paste,clear,count),0,2);panes.Controls.Add(Ui.Row(copy,send),1,2);Add(panes,true);
        var footer=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2,Padding=new Padding(0,10,0,0)};
        footer.ColumnStyles.Add(new(SizeType.Percent,100));footer.Controls.Add(status);footer.Controls.Add(translate);Add(footer);
        send.Visible=false; input.TextChanged+=(_,_)=>{count.Text=$"{input.TextLength:N0} / {prefs.Limit:N0}";if(job==null)output.Clear();};
        void ChangeLanguage(){if(job!=null)return;output.Clear();style.Enabled=Id(to)=="ja";prefs.Target=Id(to);prefs.Source=Id(from);prefs.Style=new[]{"","polite","plain"}[style.SelectedIndex];prefs.Save();}
        from.SelectedIndexChanged+=(_,_)=>ChangeLanguage();to.SelectedIndexChanged+=(_,_)=>ChangeLanguage();style.SelectedIndexChanged+=(_,_)=>ChangeLanguage();
        style.Enabled=Id(to)=="ja";
        swap.Click+=(_,_)=>{var oldFrom=Id(from);
            if(oldFrom=="")oldFrom=input.TextLength==0?(Id(to)=="ja"?"en":"ja"):LanguageDetection.Guess(input.Text);
            if(oldFrom==""){status.Text="入れ替えるには原文の言語を選んでください。";return;}
            var oldTo=Id(to);
            var a=input.Text;var b=output.Text;SelectLang(to,oldFrom);SelectLang(from,oldTo);input.Text=b;output.Text=a;};
        clear.Click+=(_,_)=>{input.Clear();output.Clear();};
        paste.Click+=(_,_)=>ClipboardAction(()=>input.Paste());copy.Click+=(_,_)=>ClipboardAction(()=>{if(output.TextLength>0)Clipboard.SetText(output.Text);});
        settings.Click+=(_,_)=>OpenSetup();translate.Click+=async(_,_)=>{if(job!=null)job.Cancel();else await RunTranslation();};
        send.Click+=(_,_)=>Insert();log.Click+=(_,_)=>ShowLog();
        KeyDown+=async(_,e)=>{if(e.Control&&e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;if(job==null)await RunTranslation();}else if(e.KeyCode==Keys.Escape)job?.Cancel();};
        tray.Text="MyPLaMo";tray.Icon=Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!)??SystemIcons.Application;tray.Visible=true;
        var menu=new ContextMenuStrip();menu.Items.Add("翻訳画面を開く",null,(_,_)=>Reveal());menu.Items.Add("終了",null,(_,_)=>{quitting=true;Close();});
        tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>Reveal();
        FormClosing+=(_,e)=>{if(e.CloseReason==CloseReason.UserClosing&&!quitting){e.Cancel=true;Hide();return;}job?.Cancel();if(bridge!=null)_=bridge.DisposeAsync();engine.Dispose();tray.Dispose();};
        chrome.CheckedChanged+=async(_,_)=>await ConfigureChrome();
        Shown+=(_,_)=>{RefreshReady();chrome.Checked=prefs.ChromeEnabled;if(prefs.Active==null)OpenSetup();else if(startHidden)Hide();};
        try {if(File.Exists(LogPath))entries.AddRange(JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(LogPath))??[]);}catch(Exception e) when(e is IOException or JsonException){}
        foreach(var control in new Control[]{paste,clear,from,to,swap,style,log,settings}) busyControls.Add(control);
    }
    readonly List<Control> busyControls=[];
    static string Id(ComboBox b)=>((Lang)b.SelectedItem!).Id;
    static void SelectLang(ComboBox b,string id){b.SelectedItem=b.Items.Cast<Lang>().FirstOrDefault(l=>l.Id==id)??b.Items[0];}
    void Reveal(){Show();WindowState=FormWindowState.Normal;Activate();input.Focus();}
    async void OpenSetup()
    {
        if(job!=null||modal)return;
        modal=true;
        await engineGate.WaitAsync();
        try { using var s=new Setup(prefs,engine);s.ShowDialog(this); }
        finally { engineGate.Release();modal=false;RefreshReady(); }
    }

    async Task ConfigureChrome()
    {
        if (syncingChrome) return;
        syncingChrome = true;
        chrome.Enabled = false;
        try {
            if (chrome.Checked) {
                bridge = new ChromeBridge((source, ct) => InvokeAsync(
                    token => new ValueTask<string>(TranslateForChrome(source, token)), ct));
                await bridge.Start();
            } else if (bridge != null) {
                await bridge.DisposeAsync();
                bridge = null;
            }
            prefs.ChromeEnabled = chrome.Checked;
            prefs.Save();
        } catch (Exception ex) {
            if (bridge != null) await bridge.DisposeAsync();
            bridge = null;
            chrome.Checked = false;
            prefs.ChromeEnabled = false;
            status.Text = "Chrome連携を開始できませんでした。 " + ex.GetBaseException().Message;
        } finally { syncingChrome = false; chrome.Enabled = true; }
    }

    async Task<string> TranslateForChrome(string source, CancellationToken ct)
    {
        await engineGate.WaitAsync(ct);
        try {
            ct.ThrowIfCancellationRequested();
            var model = prefs.Active;
            if (model == null || !prefs.LicenseAccepted)
                throw new IOException("MyPLaMoの「モデルと設定」でモデルの準備を完了してください。");
            await engine.Load(Path.Combine(prefs.Folder, model.file), prefs.GPU, new Progress<string>(), ct);
            return await engine.Translate(source, "", "ja", prefs.Style, ct);
        } finally { engineGate.Release(); }
    }
    void RefreshReady()
    {
        UnregisterHotKey(Handle,44);
        var m=prefs.Active;status.Text=m==null?"モデルと設定からモデルを準備してください":$"PLaMo 3 Translate　{m.id.ToUpperInvariant()}　·　{(engine.Device==""?(prefs.GPU?"GPU 優先":"CPU"):engine.Device)}";
        count.Text=$"{input.TextLength:N0} / {prefs.Limit:N0}";
        if(prefs.Shortcut.Length==1 && !RegisterHotKey(Handle,44,0x4003,prefs.Shortcut[0]))status.Text+="　ショートカット競合：設定で変更してください";
    }
    void ClipboardAction(Action action){try{action();}catch(ExternalException){status.Text="クリップボードが使用中です。もう一度お試しください。";}}
    async Task RunTranslation()
    {
        if(job!=null||modal||string.IsNullOrWhiteSpace(input.Text))return;
        if(input.TextLength>prefs.Limit){status.Text=$"入力を {prefs.Limit:N0} 文字以内に分けてください。";return;}
        if(prefs.Active is not Model m){OpenSetup();return;}
        if(Id(from)==Id(to)){status.Text="翻訳元と翻訳先に異なる言語を選んでください。";return;}
        job=new();var progress=new Progress<string>(s=>status.Text=s);var source=input.Text;
        foreach(var c in busyControls)c.Enabled=false;input.ReadOnly=true;translate.Text="キャンセル";output.Clear();
        await engineGate.WaitAsync();
        try{
            job.Token.ThrowIfCancellationRequested();
            await engine.Load(Path.Combine(prefs.Folder,m.file),prefs.GPU,progress,job.Token);
            status.Text=$"{m.id.ToUpperInvariant()}　·　{engine.Device}　翻訳中…";
            output.Text=await engine.Translate(source,Id(from),Id(to),prefs.Style,job.Token);
            inferred=Id(from)!=""?Id(from):LanguageDetection.Guess(source);
            var detected=Id(from)==""?Lang.List.FirstOrDefault(l=>l.Id==inferred)?.Name:null;
            status.Text=$"翻訳完了　·　{m.id.ToUpperInvariant()}　·　{engine.Device}"+(detected==null?"":$"　·　原文の推定：{detected}");
            if(prefs.History){entries.Insert(0,new(source,output.Text,Id(from),Id(to)));if(entries.Count>100)entries.RemoveRange(100,entries.Count-100);SaveLog();}
        }catch(OperationCanceledException){engine.Dispose();status.Text="翻訳をキャンセルしました。";}
        catch(Exception e){engine.Dispose();status.Text=e.Message;}
        finally{engineGate.Release();job.Dispose();job=null;foreach(var c in busyControls)c.Enabled=true;style.Enabled=Id(to)=="ja";input.ReadOnly=false;translate.Text="翻訳する";}
    }
    void SaveLog(){Directory.CreateDirectory(Preferences.Home);File.WriteAllText(LogPath+".tmp",JsonSerializer.Serialize(entries));File.Move(LogPath+".tmp",LogPath,true);}
    void ShowLog()
    {
        modal=true;
        try{
            using var dlg=new Form();Ui.Window(dlg,"翻訳履歴",new Size(540,360));
            var list=new ListBox{Dock=DockStyle.Fill};list.Items.AddRange(entries.ToArray());
            Button use=Ui.Button("選択した翻訳を開く"),erase=Ui.Button("履歴を全件削除");var row=Ui.Row(use,erase);row.Dock=DockStyle.Bottom;
            dlg.Controls.Add(list);dlg.Controls.Add(row);
            use.Click+=(_,_)=>{if(list.SelectedItem is Entry e){SelectLang(from,e.From);SelectLang(to,e.To);input.Text=e.Source;output.Text=e.Result;dlg.Close();}};
            erase.Click+=(_,_)=>{if(MessageBox.Show(dlg,"履歴をすべて削除しますか？","履歴",MessageBoxButtons.YesNo)==DialogResult.Yes){File.Delete(LogPath);entries.Clear();list.Items.Clear();}};
            dlg.ShowDialog(this);
        }finally{modal=false;}
    }
    [DllImport("user32")]static extern bool RegisterHotKey(IntPtr hwnd,int id,uint mods,uint key);
    [DllImport("user32")]static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    [DllImport("user32")]static extern IntPtr GetForegroundWindow();
    [DllImport("user32")]static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32")]static extern short GetAsyncKeyState(int key);
    [DllImport("user32")]static extern uint GetClipboardSequenceNumber();
    [DllImport("user32")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    uint originPid;
    protected override void WndProc(ref Message msg)
    {
        base.WndProc(ref msg);if(msg.Msg==0x312&&!modal&&!capturing&&job==null)_=CaptureSelection();
    }
    async Task CaptureSelection()
    {
        capturing=true;
        try{
            origin=GetForegroundWindow();GetWindowThreadProcessId(origin,out originPid);
            if(originPid==Environment.ProcessId){origin=IntPtr.Zero;send.Visible=false;Reveal();return;}
            var seq=GetClipboardSequenceNumber();
            using var backup=new ClipboardBackup(Handle);string selected="";
            for(int i=0;i<100 && (GetAsyncKeyState(0x11)<0||GetAsyncKeyState(0x12)<0);i++)await Task.Delay(15);
            if(GetForegroundWindow()==origin && GetAsyncKeyState(0x11)>=0 && GetAsyncKeyState(0x12)>=0){
                SendKeys.SendWait("^c");
                for(int i=0;i<30 && seq==GetClipboardSequenceNumber();i++)await Task.Delay(25);
                if(seq!=GetClipboardSequenceNumber()){
                    try{selected=Clipboard.GetText();}finally{backup.Restore(Handle);}
                }
            }
            Reveal();send.Visible=true;
            if(!string.IsNullOrWhiteSpace(selected)){input.Text=selected;await RunTranslation();}
            else status.Text="選択文字を取得できませんでした。原文を貼り付けてください。";
        }catch(Exception e){Reveal();status.Text="手動で貼り付けてください。 "+e.Message;}
        finally{capturing=false;}
    }
    async void Insert()
    {
        if(origin==IntPtr.Zero||output.TextLength==0||job!=null)return;
        if(MessageBox.Show(this,"呼び出し元の選択位置に訳文を貼り付けますか？","訳文の挿入",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
        GetWindowThreadProcessId(origin,out var pid);
        if(pid!=originPid||!SetForegroundWindow(origin)){status.Text="元の画面に戻れませんでした。訳文をコピーしてください。";return;}
        await Task.Delay(120);
        if(GetForegroundWindow()==origin)ClipboardAction(()=>{Clipboard.SetText(output.Text);SendKeys.SendWait("^v");});
    }
}




