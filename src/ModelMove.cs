namespace MyPLaMo;
static class ModelMove
{
    public static async Task CopyVerified(Model model,string from,string to,IProgress<string> progress,CancellationToken ct)
    {
        var source=Path.Combine(from,model.file);
        var target=Path.Combine(to,model.file);
        if(string.Equals(Path.GetFullPath(source),Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase))
            throw new IOException("別の保存先を選んでください。");
        Directory.CreateDirectory(to);
        // Never overwrite a file or resume fragment belonging to a separate download.
        if(File.Exists(target)){await model.Prepare(to,progress,ct);return;}
        if(new DriveInfo(Path.GetPathRoot(to)!).AvailableFreeSpace<model.bytes)throw new IOException("移動先の空き容量が足りません。");
        var temp=Path.Combine(to,model.file+"."+Guid.NewGuid().ToString("N")+".tmp");
        try{
            progress.Report("モデルをコピーしています…");
            await using(var input=File.OpenRead(source))
            await using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,true))
                await input.CopyToAsync(output,ct);
            progress.Report("コピーしたモデルを検証中…");
            await using(var input=File.OpenRead(temp)){
                var hash=Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input,ct));
                if(!model.AcceptsHash(hash))throw new IOException("検証に失敗しました。元のモデルを保持しました。");
            }
            File.Move(temp,target);
        }finally{if(File.Exists(temp)) File.Delete(temp);}
    }
}
