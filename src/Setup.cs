using System.Diagnostics;
using Microsoft.VisualBasic.Devices;
using Microsoft.Win32;

namespace MyPLaMo;

sealed class Setup : Form
{
    readonly Preferences prefs;
    readonly Engine engine;
    readonly ComboBox model=Ui.Combo(Model.All);
    readonly TextBox folder=new(){Width=390};
    readonly CheckBox gpu=new(){Text="利用可能な GPU を優先する",AutoSize=true};
    readonly CheckBox consent=new(){Text="PLaMo Community License に同意する",AutoSize=true};
    readonly CheckBox history=new(){Text="翻訳履歴をこのPCに保存",AutoSize=true};
    readonly CheckBox startup=new(){Text="サインイン時に常駐",AutoSize=true};
    readonly ComboBox hotkey=Ui.Combo("T","Y","J","無効");
    readonly NumericUpDown limit=new(){Minimum=100,Maximum=4000,Increment=100,Width=75};
    readonly Label state=Ui.Label("");
    readonly Button apply=Ui.Button("準備して使う",true),cancel=Ui.Button("閉じる");
    readonly TableLayoutPanel options=Ui.Stack(0);
    CancellationTokenSource? job;
    public Setup(Preferences p,Engine engine)
    {
        prefs=p;this.engine=engine;Ui.Window(this,"MyPLaMo  •  モデルと設定",new Size(770,725));
        MinimumSize=new Size(620,480);
        Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var root=Ui.Stack(18);root.AutoSize=false;Controls.Add(root);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var ram=new ComputerInfo().TotalPhysicalMemory/1073741824.0;
        var recommended=ram>=8 ? Model.All[1] : Model.All[0];
        var processor=Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0","ProcessorNameString","CPU")?.ToString()?.Trim();
        root.Controls.Add(Ui.Label("モデルと設定",19,true));
        var scroll=new Panel{Dock=DockStyle.Fill,AutoScroll=true};
        options.Dock=DockStyle.Top;options.AutoSizeMode=AutoSizeMode.GrowAndShrink;
        scroll.Controls.Add(options);root.Controls.Add(scroll);
        options.Controls.Add(Ui.Label($"モデルを選択　　推奨 {recommended.id.ToUpperInvariant()}",12,true));
        model.Width=220;model.SelectedItem=Model.All.FirstOrDefault(m=>m.id==p.ModelId)??recommended;
        gpu.Checked=p.GPU;options.Controls.Add(Ui.Row(model,gpu));
        options.Controls.Add(Ui.Label($"{processor}　/　RAM {ram:0} GB",9));
        var graphics=Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000","DriverDesc","") as string;
        if(!string.IsNullOrWhiteSpace(graphics)) options.Controls.Add(Ui.Label(graphics,9));
        options.Controls.Add(Ui.Label("モデルの保存先",12,true));
        var browse=Ui.Button("参照…");folder.Text=p.Folder;
        browse.Click+=(_,_)=>{using var dialog=new FolderBrowserDialog{InitialDirectory=folder.Text};if(dialog.ShowDialog(this)==DialogResult.OK)folder.Text=dialog.SelectedPath;};
        options.Controls.Add(Ui.Row(folder,browse));
        var available=Ui.Label("");var delete=Ui.Button("選択モデルを削除");
        var move=Ui.Button("モデルを移動…");
        options.Controls.Add(Ui.Row(available,move,delete));
        move.Click+=async(_,_)=>{
            var m=(Model)model.SelectedItem!;string from=folder.Text;
            using var pick=new FolderBrowserDialog{Description="移動先のフォルダ",UseDescriptionForTitle=true};
            if(pick.ShowDialog(this)!=DialogResult.OK)return;
            var dest=pick.SelectedPath;
            options.Enabled=apply.Enabled=false;cancel.Text="中断";job=new();
            try{
                engine.Dispose();
                await ModelMove.CopyVerified(m,from,dest,new Progress<string>(s=>state.Text=s),job.Token);
                if(m.id==prefs.ModelId && Path.GetFullPath(from).Equals(Path.GetFullPath(prefs.Folder),StringComparison.OrdinalIgnoreCase)){
                    prefs.Folder=dest;prefs.Save();
                }
                File.Delete(Path.Combine(from,m.file));folder.Text=dest;state.Text="移動しました。";
            }catch(OperationCanceledException){state.Text="移動を中断しました。";}
            catch(Exception ex){state.Text=ex.Message;}
            finally{job.Dispose();job=null;options.Enabled=apply.Enabled=true;cancel.Text="閉じる";Inspect();}
        };
        void Inspect()
        {
            try{var m=(Model)model.SelectedItem!; var path=Path.Combine(folder.Text,m.file);
                var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder.Text))!);
                available.Text=$"{(File.Exists(path)?"取得済み":"未取得")}　空き {drive.AvailableFreeSpace/1e9:0.0} GB";
                move.Enabled=File.Exists(path);delete.Enabled=move.Enabled||File.Exists(path+".part");}
            catch{available.Text="フォルダを指定してください";delete.Enabled=false;}
        }
        model.SelectedIndexChanged+=(_,_)=>Inspect();folder.TextChanged+=(_,_)=>Inspect();Inspect();
        delete.Click+=(_,_)=>{
            var m=(Model)model.SelectedItem!;var path=Path.Combine(folder.Text,m.file);
            if(MessageBox.Show(this,path+" と対応する中断ファイルを削除しますか？","モデル削除",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
            try{engine.Dispose(); File.Delete(path);File.Delete(path+".part");Inspect();}catch(Exception ex){state.Text=ex.Message;}
        };
        var license=new LinkLabel{Text="PFN 公式ライセンス（日本語）",AutoSize=true};
        license.LinkClicked+=(_,_)=>Process.Start(new ProcessStartInfo("https://plamo.preferredai.jp/info/plamo-community-license-ja"){UseShellExecute=true});
        consent.Checked=p.LicenseAccepted;options.Controls.Add(license);options.Controls.Add(consent);
        options.Controls.Add(Ui.Label("操作と保存",12,true));
        hotkey.Width=75;hotkey.SelectedItem=p.Shortcut;limit.Value=Math.Clamp(p.Limit,100,4000);
        options.Controls.Add(Ui.Row(Ui.Label("呼び出し　Ctrl + Alt +"),hotkey,Ui.Label("入力上限（文字）"),limit));
        history.Checked=p.History;
        string link=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),"MyPLaMo.lnk");
        startup.Checked=File.Exists(link);
        var dark=new CheckBox{Text="ダークテーマ（次回起動時）",Checked=p.Dark,AutoSize=true};
        options.Controls.Add(Ui.Row(history,startup,dark));
        state.MaximumSize=new Size(650,0);
        root.Controls.Add(state);root.Controls.Add(Ui.Row(apply,cancel));
        cancel.Click+=(_,_)=>{if(job!=null)job.Cancel();else Close();};
        FormClosing+=(_,e)=>{if(job!=null){e.Cancel=true;job.Cancel();}};
        apply.Click+=async(_,_)=>{
            if(!consent.Checked){state.Text="ライセンスを確認して同意を選択してください。";return;}
            var m=(Model)model.SelectedItem!;var dir=folder.Text.Trim();
            options.Enabled=apply.Enabled=false;cancel.Text="中断";state.Text="準備中…";
            job=new();var progress=new Progress<string>(s=>state.Text=s);
            try{
                await m.Prepare(dir,progress,job.Token);
                await engine.Load(Path.Combine(dir,m.file),gpu.Checked,progress,job.Token);
                state.Text="テスト翻訳中…";
                await engine.Translate("Hello.","en","ja","",job.Token);
                prefs.Folder=dir;prefs.ModelId=m.id;prefs.GPU=gpu.Checked;prefs.LicenseAccepted=true;
                prefs.History=history.Checked;prefs.Shortcut=(string)hotkey.SelectedItem!;prefs.Limit=(int)limit.Value;
                if(startup.Checked){
                    dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                    dynamic shortcut=shell.CreateShortcut(link);
                    shortcut.TargetPath=Environment.ProcessPath!;shortcut.Arguments="--tray";shortcut.Save();
                }else if(File.Exists(link))File.Delete(link);
                prefs.Dark=dark.Checked;prefs.Save();DialogResult=DialogResult.OK;
            }catch(OperationCanceledException){engine.Dispose();state.Text="中断しました。再実行すると取得を再開します。";}
            catch(Exception ex){state.Text=ex.Message;}
            finally{job.Dispose();job=null;options.Enabled=apply.Enabled=true;cancel.Text="閉じる";Inspect();}
            if(DialogResult==DialogResult.OK)Close();
        };
    }
}
