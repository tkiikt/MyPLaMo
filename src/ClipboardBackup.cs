using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
namespace MyPLaMo;

sealed class ClipboardBackup : IDisposable
{
    readonly List<(uint Format,IntPtr Handle)> items=[];
    [DllImport("user32")]static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32")]static extern bool CloseClipboard();
    [DllImport("user32")]static extern bool EmptyClipboard();
    [DllImport("user32")]static extern uint EnumClipboardFormats(uint previous);
    [DllImport("user32")]static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32")]static extern IntPtr SetClipboardData(uint format,IntPtr handle);
    [DllImport("ole32")]static extern IntPtr OleDuplicateData(IntPtr handle,ushort format,uint flags);
    [DllImport("ole32")]static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    static void Open(IntPtr owner)
    {
        for(int i=0;i<10;i++){if(OpenClipboard(owner))return;Thread.Sleep(10);}
        throw new IOException("クリップボードが使用中です。");
    }
    public ClipboardBackup(IntPtr owner)
    {
        Open(owner);
        try{
            for(uint fmt=EnumClipboardFormats(0);fmt!=0;fmt=EnumClipboardFormats(fmt)){
                var orig=GetClipboardData(fmt);
                var copy=orig==IntPtr.Zero?IntPtr.Zero:OleDuplicateData(orig,(ushort)fmt,0);
                if(copy==IntPtr.Zero)throw new IOException("クリップボードの元データを退避できませんでした。");
                items.Add((fmt,copy));
            }
        }catch{Dispose();throw;}finally{CloseClipboard();}
    }
    public void Restore(IntPtr owner)
    {
        Open(owner);
        try{
            if(!EmptyClipboard())throw new IOException("クリップボードを復元できませんでした。");
            while(items.Count>0){
                var (fmt,handle)=items[0];
                if(SetClipboardData(fmt,handle)==IntPtr.Zero)throw new IOException("クリップボード形式を復元できませんでした。");
                items.RemoveAt(0); // ownership transferred to Windows
            }
        }finally{CloseClipboard();}
    }
    public void Dispose()
    {
        foreach(var (fmt,handle) in items){
            var medium=new STGMEDIUM{unionmember=handle,tymed=fmt switch{
                2 or 9 or 0x82=>TYMED.TYMED_GDI,3 or 0x83=>TYMED.TYMED_MFPICT,14 or 0x8e=>TYMED.TYMED_ENHMF,_=>TYMED.TYMED_HGLOBAL}};
            ReleaseStgMedium(ref medium);
        }
        items.Clear();
    }
}
