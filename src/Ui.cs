using System.Globalization;
namespace MyPLaMo;

static class Ui
{
    public static Color Ink=Color.FromArgb(30,49,55), Teal=Color.FromArgb(16,99,95), Canvas=Color.FromArgb(246,249,248);
    public static Color Paper=Color.White;
    public static void SetDark()
    {
        Ink=Color.FromArgb(226,236,234);Teal=Color.FromArgb(37,117,111);Canvas=Color.FromArgb(29,37,39);Paper=Color.FromArgb(40,51,53);
#pragma warning disable WFO5001
        Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001
    }
    public static void Window(Form form,string title,Size size)
    {
        form.Text=title;form.Font=new Font("Yu Gothic UI",10);
        form.ForeColor=Ink;form.BackColor=Canvas;form.ClientSize=size;
        form.AutoScaleMode=AutoScaleMode.Dpi;form.StartPosition=FormStartPosition.CenterScreen;
    }
    public static Label Label(string text,int size=10,bool bold=false)=>new(){Text=text,AutoSize=true,Font=new Font("Yu Gothic UI",size,bold?FontStyle.Bold:FontStyle.Regular),Margin=new Padding(4,7,8,5)};
    public static Button Button(string title,bool primary=false)
    {
        var b=new Button{Text=title,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(10,3,10,3),FlatStyle=FlatStyle.Flat,BackColor=primary?Teal:Paper,ForeColor=primary?Color.White:Ink,Cursor=Cursors.Hand};
        b.FlatAppearance.BorderColor=primary?Teal:Color.FromArgb(193,209,205);return b;
    }
    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var r=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};r.Controls.AddRange(controls);return r;
    }
    public static TableLayoutPanel Stack(int pad=16)=>new(){Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1,Padding=new Padding(pad),GrowStyle=TableLayoutPanelGrowStyle.AddRows};
    public static ComboBox Combo(params object[] items)
    {
        var b=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=150,Margin=new Padding(4,7,4,5)};
        b.Items.AddRange(items); b.SelectedIndex=0;return b;
    }
}
record Lang(string Id,string Name)
{
    public override string ToString()=>Name;
    public static Lang[] List = "ja en zh-hans zh-hant ko ar az be bg bn ca ceb de es et eu fa fr gu he hi hr hu hy id it jv ka kk km lt lv ml mr ms my ne nl pa pl ru si sk sl sr sw ta te th tl ur uz vi".Split(' ').Select(code=>new Lang(code,code switch {
        "ja"=>"日本語","en"=>"英語","zh-hans"=>"中国語（簡体字）","zh-hant"=>"中国語（繁体字）","ko"=>"韓国語",
        _=>CultureInfo.GetCultureInfo(code).NativeName})).ToArray();
}

