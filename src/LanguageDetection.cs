using System.Runtime.InteropServices;
namespace MyPLaMo;

// Windows Extended Linguistic Services supplies a local language hint for swapping panes.
// PLaMo itself handles automatic source language in the translation prompt.
static class LanguageDetection
{
    [StructLayout(LayoutKind.Sequential)]
    struct Options
    {
        public nuint Size;
        public IntPtr Category,InputLanguage,OutputLanguage,InputScript,OutputScript,InputType,OutputType,Guid;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Bag
    {
        public nuint Size;
        public IntPtr Ranges;
        public uint Count;
        public IntPtr ServiceData;
        public uint ServiceBytes;
        public IntPtr CallerData;
        public uint CallerBytes;
        public IntPtr Context;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Range
    {
        public uint Start,End;
        public IntPtr Description;
        public uint DescriptionLength;
        public IntPtr Data;
        public uint DataBytes;
        public IntPtr ContentType,Actions;
        public uint ActionCount;
        public IntPtr ActionNames;
    }
    [DllImport("elscore")]static extern int MappingGetServices(ref Options options,out IntPtr services,out uint count);
    [DllImport("elscore")]static extern int MappingFreeServices(IntPtr services);
    [DllImport("elscore",CharSet=CharSet.Unicode)]static extern int MappingRecognizeText(IntPtr service,string text,uint length,uint start,IntPtr options,ref Bag bag);
    [DllImport("elscore")]static extern int MappingFreePropertyBag(ref Bag bag);
    public static string Guess(string text)
    {
        var id=Marshal.AllocHGlobal(16);
        try{
            Marshal.StructureToPtr(new Guid("cf7e00b1-909b-4d95-a8f4-611f7c377702"),id,false);
            var opt=new Options{Size=(nuint)Marshal.SizeOf<Options>(),Guid=id,Flags=2};
            if(MappingGetServices(ref opt,out var services,out var count)<0)return "";
            try{
                var bag=new Bag{Size=(nuint)Marshal.SizeOf<Bag>()};
                if(count==0 || MappingRecognizeText(services,text,(uint)text.Length,0,IntPtr.Zero,ref bag)<0)return "";
                try{
                    if(bag.Count==0)return "";
                    var range=Marshal.PtrToStructure<Range>(bag.Ranges);
                    var tag=Marshal.PtrToStringUni(range.Data,(int)range.DataBytes/2)?.Split('\0')[0].ToLowerInvariant()??"";
                    if(tag.StartsWith("zh"))return tag.Contains("hant")||tag.Contains("tw")||tag.Contains("hk")?"zh-hant":"zh-hans";
                    return Lang.List.FirstOrDefault(l=>l.Id==tag.Split('-')[0])?.Id??"";
                }finally{MappingFreePropertyBag(ref bag);}
            }finally{MappingFreeServices(services);}
        }finally{Marshal.FreeHGlobal(id);}
    }
}
