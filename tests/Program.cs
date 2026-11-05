using MyPLaMo;
using System.Diagnostics;
var directory=Path.GetFullPath(args[0]);
using var engine=new Engine();
var progress=new Progress<string>(Console.WriteLine);
foreach(var model in Model.All.Where(m=>args.Skip(1).Contains(m.id)))
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(45));
    await model.Prepare(directory,progress,timeout.Token);
    foreach(bool gpu in model.id=="0.8b"?new[]{true,false}:new[]{true})
    {
        var clock=Stopwatch.StartNew();
        await engine.Load(Path.Combine(directory,model.file),gpu,progress,timeout.Token);
        var load=clock.Elapsed;
        var ja=await engine.Translate("The sky is blue.","","ja","polite",timeout.Token);
        var en=await engine.Translate("空は青いです。","ja","en","",timeout.Token);
        Console.WriteLine($"{model.id} {engine.Device} load={load.TotalSeconds:F2}s translation={(clock.Elapsed-load).TotalSeconds:F2}s {ja} / {en}");
        if(!ja.Contains('青')||!en.Contains("blue",StringComparison.OrdinalIgnoreCase))throw new Exception("Unexpected translation");
    }
}
