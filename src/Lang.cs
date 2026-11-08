using System.Globalization;

namespace MyPLaMo;

record Lang(string Id, string Name)
{
    public override string ToString() => Name;
    public static Lang[] List = "ja en zh-hans zh-hant ko ar az be bg bn ca ceb de es et eu fa fr gu he hi hr hu hy id it jv ka kk km lt lv ml mr ms my ne nl pa pl ru si sk sl sr sw ta te th tl ur uz vi".Split(' ').Select(code => new Lang(code, code switch {
        "ja" => "日本語", "en" => "英語", "zh-hans" => "中国語（簡体字）", "zh-hant" => "中国語（繁体字）", "ko" => "韓国語",
        _ => CultureInfo.GetCultureInfo(code).NativeName
    })).ToArray();
}
