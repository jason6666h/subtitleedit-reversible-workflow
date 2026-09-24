using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Nikse.SubtitleEdit.Features.Main;

namespace Nikse.SubtitleEdit.Features.Main.Layout;

public static class SubtitleReviewIntegrationUi
{
    public static Button CreateToolbarButton(MainViewModel vm)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem { Header = "校閱目前選取字幕", Command = vm.SubtitleReviewSelectedCommand });
        menu.Items.Add(new MenuItem { Header = "校閱整份字幕", Command = vm.SubtitleReviewAllCommand });
        menu.Items.Add(new MenuItem { Header = "詞彙表", Command = vm.SubtitleReviewGlossaryCommand });
        menu.Items.Add(new MenuItem { Header = "提示詞設定", Command = vm.SubtitleReviewSettingsCommand });
        return new Button
        {
            Name = "SubtitleReviewIntegrationToolbarButton",
            Content = "AI 字幕校閱 ▾",
            Padding = new Thickness(8, 4),
            Flyout = menu,
            [AutomationProperties.NameProperty] = "AI 字幕校閱",
        };
    }
}
