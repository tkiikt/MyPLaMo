using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace MyPLaMo;

public sealed partial class ModelCardViewModel : ObservableObject
{
    internal Model Model { get; }
    public string Id => Model.id;
    public string Title => $"PLaMo 3 Translate {Id.ToUpperInvariant()} · GGUF";
    public string SizeText => Model.bytes < 1e9 ? $"{Model.bytes / 1e6:0.0} MB" : $"{Model.bytes / 1e9:0.00} GB";
    public string Note => Id == "31b" ? "大容量モデル：32 GB以上のメモリを推奨。空きメモリによっては動作が遅くなります。" : Id == "2b" ? "おすすめ · 日常の翻訳に" : "";
    public Visibility NoteVisibility => Note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string ActionId => "ActivateModel_" + Id;
    public string DeleteId => "DeleteModel_" + Id;
    public string DeleteName => Id.ToUpperInvariant() + "モデルを削除";
    public string ActionName => Id.ToUpperInvariant() + " " + ActionLabel;
    string actionLabel = "ダウンロード", glyph = "\uE7B8";
    bool canActivate, canDelete;
    public string ActionLabel { get => actionLabel; private set { if (SetProperty(ref actionLabel, value)) OnPropertyChanged(nameof(ActionName)); } }
    public string Glyph { get => glyph; private set => SetProperty(ref glyph, value); }
    public bool CanActivate { get => canActivate; private set => SetProperty(ref canActivate, value); }
    public bool CanDelete { get => canDelete; private set => SetProperty(ref canDelete, value); }

    internal ModelCardViewModel(Model model) => Model = model;
    internal void Refresh(Preferences prefs, bool consent, bool busy)
    {
        var path = Path.Combine(prefs.Folder, Model.file);
        bool exists = File.Exists(path), fragment = File.Exists(path + ".part");
        bool selected = exists && prefs.ModelId == Id;
        Glyph = selected ? "\uE73E" : "\uE7B8";
        ActionLabel = selected ? "使用中" : exists ? "使用する" : fragment ? "取得を再開" : "ダウンロード";
        CanActivate = consent && !busy && !selected;
        CanDelete = !busy && (exists || fragment);
    }
}
