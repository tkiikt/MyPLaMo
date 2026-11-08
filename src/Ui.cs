using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace MyPLaMo;

static class Ui
{
    public static void CopyText(string text)
    {
        var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); Clipboard.Flush();
    }

    public static async Task<string?> PickFolder(Window owner, string path, string title)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(owner.AppWindow.Id) {
            Title = title, CommitButtonText = "このフォルダーを選択", SuggestedStartFolder = path
        };
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
