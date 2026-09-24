using AudioWorkflow;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.Input;
using Nikse.SubtitleEdit.Features.Shared;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private string[] CleanupActiveFiles()=>_undoRedoManager.UndoList.Concat(_undoRedoManager.RedoList)
        .Select(e=>e.SynchronousAudio?.FileName).Append(_videoFileName).Where(p=>!string.IsNullOrEmpty(p)).Cast<string>().ToArray();
    private static CleanupRoot[] CleanupRoots()=>PortablePaths.Current is { } paths?
        WorkCleanupCatalog.PortableRoots(paths):[new CleanupRoot(PortablePaths.WorkRoot,"目前版本")];
    [RelayCommand]
    private async Task SynchronousAudioCleanup()
    {
        if(Window==null || IsAudioTimelineBusy)return;
        var owner=Window;
        var list=new ListBox{SelectionMode=SelectionMode.Multiple};
        var status=new TextBlock{Text="正在依字幕檔名列出舊專案…",TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        var clean=new Button{Content="將選取專案移至資源回收筒…",IsEnabled=false,Margin=new Thickness(4),MinHeight=36};
        var close=new Button{Content="關閉",Margin=new Thickness(4),MinHeight=36};
        var content=new Grid{RowDefinitions=new RowDefinitions("Auto,*,Auto"),Margin=new Thickness(16)};
        content.Children.Add(status);Grid.SetRow(list,1);content.Children.Add(list);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Children={clean,close}};Grid.SetRow(actions,2);content.Children.Add(actions);
        var dialog=new Window{Title="依檔名清理舊專案",Width=820,Height=480,MinWidth=600,MinHeight=320,Content=content,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        close.Click+=(_,_)=>dialog.Close();
        list.SelectionChanged+=(_,_)=>clean.IsEnabled=list.SelectedItems?.Count>0;
        dialog.Opened+=async(_,_)=>
        {
            try { var active=CleanupActiveFiles();var directory=_synchronousAudioDirectory;
                list.ItemsSource=await Task.Run(()=>WorkCleanupCatalog.Scan(CleanupRoots(),directory,active));
                status.Text="列出本版 Data/AudioWork 與所有舊版 Imported-* 專案，依字幕檔名排序。整個專案（含大型音檔、字幕與舊 AU 資料）一起移至資源回收筒，不拆散版本。標記保留者不能清理；舊版匯入專案會標記注意。回收筒清空前可還原。";
            } catch(Exception e){status.Text="無法安全掃描："+e.Message;}
        };
        clean.Click+=async(_,_)=>
        {
            var chosen=list.SelectedItems?.Cast<CleanupProject>().ToArray()??[];
            if(chosen.Length==0)return;
            if(chosen.Any(p=>p.ProtectedReason!=null)){status.Text="選取包含受保護專案，請取消勾選標記保留者。";return;}
            var warnings=chosen.Where(p=>p.Warning!=null).Select(p=>$"[{p.SourceLabel}] {p.Name}：{p.Warning}").ToArray();
            var warningText=warnings.Length==0?"":$"\n\n注意：\n{string.Join("\n",warnings)}";
            var result=await MessageBox.Show(dialog,"確認清理舊專案",$"移至 Windows 資源回收筒：\n{string.Join("\n",chosen.Select(p=>$"[{p.SourceLabel}] {p.Name}"))}\n共 {chosen.Sum(p=>p.Bytes)/1073741824d:N2} GiB。\n專案完整歷史將從工作區移出；不會清空回收筒。{warningText}",MessageBoxButtons.YesNo,MessageBoxIcon.Question);
            if(result!=MessageBoxResult.Yes)return;
            clean.IsEnabled=false;
            try
            {
                if(IsAudioTimelineBusy)throw new InvalidOperationException("請先完成音訊作業。");
                var active=CleanupActiveFiles();var directory=_synchronousAudioDirectory;
                await Task.Run(()=>WorkCleanupCatalog.ValidateSelections(chosen,CleanupRoots(),directory,active));
                foreach(var p in chosen)
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(p.Directory,Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                status.Text="已移至資源回收筒，可由 Windows 資源回收筒還原。清空前不一定釋放磁碟空間。";
                list.ItemsSource=await Task.Run(()=>WorkCleanupCatalog.Scan(CleanupRoots(),directory,active));
            }
            catch(Exception e){status.Text="清理停止；已移出的項目可從資源回收筒還原："+e.Message;}
        };
        await dialog.ShowDialog(owner);
    }
}
