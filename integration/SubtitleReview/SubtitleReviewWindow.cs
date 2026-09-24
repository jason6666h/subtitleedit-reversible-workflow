using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Features.Shared;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;
using SubtitleReview.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main.Layout;

internal sealed class SubtitleReviewWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ReviewDocumentSnapshot _snapshot;
    private readonly ReviewSelection _selection;
    private readonly Action<Guid>? _playRow;
    private readonly SubtitleReviewSettingsStore _settingsStore;
    private readonly SubtitleReviewEngine _engine = new();
    private readonly ReviewSessionStore _sessionStore;
    private readonly string _sessionFileName;
    private readonly ReviewWorkflowState _state = new();
    private readonly List<GlossaryUiRow> _glossaryRows = [];
    private readonly List<GlossaryCandidateUiRow> _glossaryCandidates = [];

    private readonly TextBox _glossaryPath = new();
    private readonly TextBox _evidencePath = new();
    private readonly TextBox _promptTemplateText = new();
    private readonly TextBox _referenceMaterialText = new();
    private readonly TextBox _promptText = new();
    private readonly TextBox _responseText = new();
    private readonly TextBox _originalText = new();
    private readonly TextBox _aiText = new();
    private readonly TextBox _manualText = new();

    private readonly TableView _rowList = new() { SelectionMode = SelectionMode.Single };
    private readonly TableView _responseStatusTable = new() { SelectionMode = SelectionMode.Single };
    private readonly ListBox _glossaryList = new();
    private readonly ListBox _glossaryCandidateList = new();
    private readonly ListBox _reviewReferenceList = new() { MaxHeight = 120 };
    private readonly StackPanel _promptSectionButtons = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private ScrollViewer _promptSectionScroller = null!;
    private List<PromptSectionLink> _promptSections = [];
    private readonly TextBlock _protectedPhraseSummary = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _promptPosition = new();
    private readonly TextBlock _progress = new();
    private readonly TextBlock _rowWarning = new();
    private readonly TextBlock _evidenceSummary = new();
    private readonly TextBlock _bulkActionHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _mergeActionHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _changedOnly = new() { Content = "只看有修改" };
    private readonly CheckBox _highRiskOnly = new() { Content = "只看高風險" };
    private readonly Button _applyAiButton = new() { Content = "套用 AI" };
    private readonly Button _keepButton = new() { Content = "保留原文" };
    private readonly Button _manualButton = new() { Content = "採用手動文字" };
    private readonly Button _commitButton = new() { Content = "套用逐筆調整（選用）", IsEnabled = false };
    private readonly Button _applyAllAiButton = new() { Content = "一鍵套用全部 AI 結果到 SE 字幕", IsEnabled = false };
    private readonly Button _playButton = new() { Content = "播放這一句" };
    private readonly Button _mergeButton = new() { Content = "合併下一行" };
    private readonly int _initialTab;
    private readonly SplitView _workspaceSplitView = new();
    private readonly ListBox _workspaceNavigation = new() { SelectionMode = SelectionMode.Single };
    private readonly ContentControl _workspaceContent = new();
    private readonly Button _workspacePaneToggle = new() { Content = "導覽" };
    private readonly TabControl _reviewDetailTabs = new();
    private readonly SplitView _promptUtilitySplitView = new();
    private readonly TabControl _promptUtilityTabs = new();
    private readonly Button _promptReferencesButton = new() { Content = "參考資料" };
    private readonly Button _promptResultsButton = new() { Content = "產生結果" };
    private readonly SplitView _reviewUtilitySplitView = new();
    private readonly Button _reviewEvidenceButton = new() { Content = "Evidence / 進階" };
    private readonly TabControl _glossaryTabs = new();
    private Control[] _workspacePages = [];

    private Grid _headerGrid = null!;
    private Grid _promptSettingsGrid = null!;
    private Grid _promptEditorGrid = null!;
    private Control _reviewOriginalPanel = null!;
    private Control _reviewAiPanel = null!;
    private Control _reviewManualPanel = null!;
    private Grid _evidenceGrid = null!;
    private TextBlock _evidenceLabel = null!;
    private Button _evidenceBrowseButton = null!;
    private Button _evidenceClearButton = null!;
    private Grid _glossaryPathBar = null!;
    private TextBlock _glossaryPathLabel = null!;
    private Button _glossaryBrowseButton = null!;
    private Button _glossaryReloadButton = null!;

    public SubtitleReviewWindow(
        MainViewModel vm,
        ReviewDocumentSnapshot snapshot,
        ReviewSelection selection,
        Action<Guid>? playRow,
        int initialTab = 0,
        SubtitleReviewSettingsStore? settingsStore = null,
        ReviewSessionStore? sessionStore = null)
    {
        _vm = vm;
        _snapshot = snapshot;
        _selection = selection;
        _playRow = playRow;
        _settingsStore = settingsStore ?? new SubtitleReviewSettingsStore();
        _sessionStore = sessionStore ?? new ReviewSessionStore(
            Path.Combine(Se.DataFolder, "SubtitleReview", "sessions"));
        _initialTab = Math.Clamp(initialTab, 0, 3);
        _sessionFileName = ReviewSessionResume.GetSessionFileName(snapshot, selection);

        var editableRows = snapshot.Rows.Count(row => !row.IsReferenceOnly);
        var scope = selection.Rows.Count == editableRows
            ? $"整份字幕，共 {selection.Rows.Count} 列"
            : $"目前選取 {selection.Rows.Count} 列";
        Title = $"AI 字幕校閱 — {scope}";
        Width = 1380;
        Height = 900;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UiUtil.InitializeWindow(this, "SubtitleReviewWindow");

        var settings = _settingsStore.Load();
        var glossaryPath = _settingsStore.ResolveGlossaryPath(settings.GlossaryPath);
        _glossaryPath.Text = glossaryPath;
        _evidencePath.Text = settings.EvidencePath;
        _promptTemplateText.Text = settings.PromptTemplate;
        _referenceMaterialText.Text = settings.ReferenceMaterial;
        _reviewReferenceList.ItemsSource = settings.ReviewReferenceFiles ?? [];
        if (!string.Equals(settings.GlossaryPath, glossaryPath, StringComparison.OrdinalIgnoreCase))
            _settingsStore.Save(settings with { GlossaryPath = glossaryPath });
        Content = BuildLayout();
        ConfigureAccessibility();
        WireEvents();
        SizeChanged += (_, _) => ApplyResponsiveLayout(Bounds.Width);
        Opened += (_, _) =>
        {
            ApplyResponsiveLayout(Bounds.Width);
            TryResumeSession();
            if (_initialTab == 3)
                LoadGlossary();
        };
        Closed += (_, _) =>
        {
            _state.Dispose();
            TrySaveSession();
            _vm.StopSubtitleReviewPlayback();
        };
        SetStatus($"校閱範圍：{scope}。可直接產生提示詞。");
    }

    private Control BuildLayout()
    {
        var root = new Grid
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10,
        };

        _headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 10,
        };
        _workspacePaneToggle.MinWidth = 72;
        _workspacePaneToggle.Click += (_, _) => _workspaceSplitView.IsPaneOpen = !_workspaceSplitView.IsPaneOpen;
        _headerGrid.Children.Add(_workspacePaneToggle);

        var titlePanel = new StackPanel
        {
            Spacing = 1,
            Children =
            {
                new TextBlock
                {
                    Text = "AI 字幕校閱",
                    FontSize = 20,
                    FontWeight = FontWeight.Bold,
                },
                new TextBlock
                {
                    Text = Title ?? "字幕校閱工作區",
                    Opacity = 0.68,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        Grid.SetColumn(titlePanel, 1);
        _headerGrid.Children.Add(titlePanel);
        root.Children.Add(_headerGrid);

        _workspacePages =
        [
            BuildPromptTab(),
            BuildResponseTab(),
            BuildReviewTab(),
            BuildGlossaryTab(),
        ];

        _workspaceNavigation.ItemsSource = new[]
        {
            "1  提示詞與參考",
            "2  AI 回覆",
            "3  校閱與套用",
            "4  詞彙表",
        };
        _workspaceNavigation.SelectionChanged += (_, _) =>
        {
            SelectWorkspacePage(_workspaceNavigation.SelectedIndex);
        };

        var pane = new Grid
        {
            Margin = new Thickness(0, 0, 10, 0),
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8,
        };
        pane.Children.Add(new TextBlock
        {
            Text = "校閱流程",
            FontWeight = FontWeight.Bold,
            FontSize = 15,
            Margin = new Thickness(8, 6),
        });
        Grid.SetRow(_workspaceNavigation, 1);
        pane.Children.Add(_workspaceNavigation);

        _workspaceContent.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _workspaceContent.VerticalContentAlignment = VerticalAlignment.Stretch;
        _workspaceSplitView.Pane = pane;
        _workspaceSplitView.Content = _workspaceContent;
        _workspaceSplitView.OpenPaneLength = 220;
        _workspaceSplitView.CompactPaneLength = 0;
        _workspaceSplitView.DisplayMode = SplitViewDisplayMode.Inline;
        _workspaceSplitView.IsPaneOpen = true;
        _workspaceSplitView.UseLightDismissOverlayMode = true;

        Grid.SetRow(_workspaceSplitView, 1);
        root.Children.Add(_workspaceSplitView);

        _status.TextWrapping = TextWrapping.Wrap;
        _status.MinHeight = 30;
        _status.VerticalAlignment = VerticalAlignment.Center;
        var statusBar = new Border
        {
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            Child = _status,
        };
        Grid.SetRow(statusBar, 2);
        root.Children.Add(statusBar);

        _workspaceNavigation.SelectedIndex = _initialTab;
        SelectWorkspacePage(_initialTab);
        return root;
    }

    private void ApplyResponsiveLayout(double width)
    {
        var useOverlay = width < 1100;
        _workspaceSplitView.DisplayMode = useOverlay
            ? SplitViewDisplayMode.Overlay
            : SplitViewDisplayMode.Inline;
        _workspaceSplitView.IsPaneOpen = !useOverlay;
        _workspacePaneToggle.IsVisible = useOverlay;
    }

    private void SelectWorkspacePage(int index)
    {
        if (index < 0 || index >= _workspacePages.Length)
            return;

        _promptUtilitySplitView.IsPaneOpen = false;
        _reviewUtilitySplitView.IsPaneOpen = false;

        _workspaceContent.Content = _workspacePages[index];
        if (_workspaceSplitView.DisplayMode == SplitViewDisplayMode.Overlay)
            _workspaceSplitView.IsPaneOpen = false;

        if (index == 3)
            LoadGlossary();
    }

    private static void ConfigureRightUtilitySplitView(SplitView splitView, Control content, Control pane)
    {
        splitView.Content = content;
        splitView.Pane = pane;
        splitView.PanePlacement = SplitViewPanePlacement.Right;
        splitView.DisplayMode = SplitViewDisplayMode.Overlay;
        splitView.OpenPaneLength = 480;
        splitView.CompactPaneLength = 0;
        splitView.IsPaneOpen = false;
        splitView.UseLightDismissOverlayMode = true;
        splitView.HorizontalAlignment = HorizontalAlignment.Stretch;
        splitView.VerticalAlignment = VerticalAlignment.Stretch;
    }

    private static Control BuildUtilityPane(string title, Control content, SplitView owner)
    {
        var close = new Button
        {
            Content = "關閉",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => owner.IsPaneOpen = false;

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeight.Bold,
                    FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                close,
            },
        };
        Grid.SetColumn(close, 1);

        var grid = new Grid
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8,
            Children = { header, content },
        };
        Grid.SetRow(content, 1);
        return grid;
    }

    private Control BuildPromptTab()
    {
        foreach (var box in new[] { _promptTemplateText, _referenceMaterialText, _promptText })
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.HorizontalAlignment = HorizontalAlignment.Stretch;
            box.VerticalAlignment = VerticalAlignment.Stretch;
            box.VerticalContentAlignment = VerticalAlignment.Top;
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        }

        _referenceMaterialText.PlaceholderText =
            "選填：可貼上術語、背景資料或已確認的參考內容；內容再長也只會在此區塊內捲動。";
        _referenceMaterialText.MinHeight = 96;
        _referenceMaterialText.MaxHeight = 180;
        _promptText.IsReadOnly = true;
        _promptText.PlaceholderText = "完成設定後按「產生校閱提示詞」，本批結果會顯示在這裡。";
        _promptTemplateText.TextChanged += (_, _) => RefreshPromptSectionNavigation();
        RefreshPromptSectionNavigation();

        var saveTemplate = new Button { Content = "儲存提示詞設定" };
        saveTemplate.Click += (_, _) => SaveSettings();
        var resetTemplate = new Button { Content = "還原預設" };
        resetTemplate.Click += (_, _) =>
        {
            _promptTemplateText.Text = ReviewResources.PromptTemplate.Trim();
            SetStatus("已載入預設完整提示詞；按「儲存提示詞設定」後才會保存。");
        };

        var templateHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            RowSpacing = 6,
        };
        templateHeader.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "提示詞章節編輯", FontWeight = FontWeight.Bold, FontSize = 15 },
                new TextBlock
                {
                    Text = "左側點選章節即可跳到對應位置；內容仍是同一份完整提示詞並會保存到每一批。",
                    Opacity = 0.68,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        });
        var templateActions = new WrapPanel
        {
            ItemSpacing = 6,
            Children = { saveTemplate, resetTemplate },
        };
        Grid.SetRow(templateActions, 1);
        templateHeader.Children.Add(templateActions);

        _promptEditorGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*"),
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8,
        };
        _promptSectionScroller = new ScrollViewer
        {
            Content = _promptSectionButtons,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var promptNavigation = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "跳到章節",
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    _promptSectionScroller,
                },
            },
        };
        Grid.SetColumn(_promptSectionScroller, 1);
        _promptEditorGrid.Children.Add(promptNavigation);
        _promptTemplateText.MinHeight = 360;
        Grid.SetRow(_promptTemplateText, 1);
        _promptEditorGrid.Children.Add(_promptTemplateText);

        var templateCard = new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                RowSpacing = 8,
                Children =
                {
                    templateHeader,
                    _promptEditorGrid,
                },
            },
        };
        Grid.SetRow(_promptEditorGrid, 1);

        var addReferences = new Button { Content = "加入 TXT…" };
        addReferences.Click += async (_, _) => await BrowseReviewReferencesAsync();
        var clearReferences = new Button { Content = "清除 TXT" };
        clearReferences.Click += (_, _) => ClearReviewReferences();

        var referenceHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            RowSpacing = 6,
        };
        referenceHeader.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "校閱參考資料", FontWeight = FontWeight.Bold, FontSize = 15 },
                new TextBlock
                {
                    Text = "可直接貼入參考內容，也可附加多個 TXT 作為上下文參考。",
                    Opacity = 0.68,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        });
        var referenceActions = new WrapPanel
        {
            ItemSpacing = 6,
            Children = { addReferences, clearReferences },
        };
        Grid.SetRow(referenceActions, 1);
        referenceHeader.Children.Add(referenceActions);

        _reviewReferenceList.MaxHeight = 120;
        var referenceBody = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,2*,Auto,1*"),
            RowSpacing = 6,
        };
        referenceBody.Children.Add(new TextBlock
        {
            Text = "參考資料（選填，會保存）",
            FontWeight = FontWeight.SemiBold,
        });
        Grid.SetRow(_referenceMaterialText, 1);
        referenceBody.Children.Add(_referenceMaterialText);
        var txtLabel = new TextBlock
        {
            Text = "已加入的 TXT",
            FontWeight = FontWeight.SemiBold,
        };
        Grid.SetRow(txtLabel, 2);
        referenceBody.Children.Add(txtLabel);
        Grid.SetRow(_reviewReferenceList, 3);
        referenceBody.Children.Add(_reviewReferenceList);

        var referenceCard = new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*"),
                RowSpacing = 8,
                Children =
                {
                    referenceHeader,
                    referenceBody,
                },
            },
        };
        Grid.SetRow(referenceBody, 1);

        _promptSettingsGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*"),
            RowDefinitions = new RowDefinitions("*"),
        };
        _promptSettingsGrid.Children.Add(templateCard);

        var generate = new Button { Content = "產生校閱提示詞" };
        generate.Click += async (_, _) => await GeneratePromptsAsync();
        var previous = new Button { Content = "◀ 上一批" };
        previous.Click += (_, _) => ChangePrompt(-1);
        var next = new Button { Content = "下一批 ▶" };
        next.Click += (_, _) => ChangePrompt(1);
        var copy = new Button { Content = "複製目前提示詞" };
        copy.Click += async (_, _) => await CopyPromptAsync();
        var export = new Button { Content = "匯出分批 .md" };
        export.Click += async (_, _) => await ExportPromptsAsync();

        var batchActions = new WrapPanel
        {
            ItemSpacing = 6,
            LineSpacing = 6,
            Children =
            {
                generate,
                previous,
                next,
                copy,
                export,
                _promptReferencesButton,
                _promptResultsButton,
                _promptPosition,
            },
        };

        var resultCard = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            Child = _promptText,
        };
        _promptText.MinHeight = 180;

        _promptUtilityTabs.ItemsSource = new object[]
        {
            new TabItem { Header = "參考資料", Content = referenceCard },
            new TabItem { Header = "產生結果", Content = resultCard },
        };
        _promptUtilityTabs.SelectedIndex = 0;

        _promptReferencesButton.Click += (_, _) =>
        {
            _promptUtilityTabs.SelectedIndex = 0;
            _promptUtilitySplitView.IsPaneOpen = true;
        };
        _promptResultsButton.Click += (_, _) =>
        {
            _promptUtilityTabs.SelectedIndex = 1;
            _promptUtilitySplitView.IsPaneOpen = true;
        };

        var primary = new Grid
        {
            Margin = new Thickness(8),
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 6,
        };
        primary.Children.Add(batchActions);
        Grid.SetRow(_promptSettingsGrid, 1);
        primary.Children.Add(_promptSettingsGrid);

        var utilityPane = BuildUtilityPane("提示詞工具", _promptUtilityTabs, _promptUtilitySplitView);
        ConfigureRightUtilitySplitView(_promptUtilitySplitView, primary, utilityPane);
        return _promptUtilitySplitView;
    }

    private void RefreshPromptSectionNavigation()
    {
        var next = ParsePromptSections(_promptTemplateText.Text ?? string.Empty);
        var currentLabels = _promptSections.Select(section => section.Display).ToArray();
        var nextLabels = next.Select(section => section.Display).ToArray();
        var labelsChanged = !currentLabels.SequenceEqual(nextLabels, StringComparer.Ordinal);
        _promptSections = next;

        if (!labelsChanged)
            return;

        _promptSectionButtons.Children.Clear();
        foreach (var section in _promptSections)
        {
            var button = new Button
            {
                Content = section.Display,
                MinHeight = 32,
                Padding = new Thickness(10, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(button, $"跳到 {section.Display}");
            button.Click += (_, _) => JumpToPromptSection(section);
            _promptSectionButtons.Children.Add(button);
        }
    }

    private void JumpToPromptSection(PromptSectionLink section)
    {
        _promptTemplateText.Focus();
        _promptTemplateText.CaretIndex = section.Offset;
        _promptTemplateText.SelectionStart = section.Offset;
        _promptTemplateText.SelectionEnd = section.Offset;
        _promptTemplateText.ScrollToLine(section.LineIndex);

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _promptTemplateText.ScrollToLine(section.LineIndex),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private static List<PromptSectionLink> ParsePromptSections(string text)
    {
        var sections = new List<PromptSectionLink>();
        var lineStart = 0;
        var lineIndex = 0;

        while (lineStart <= text.Length)
        {
            var newline = text.IndexOf('\n', lineStart);
            var lineEnd = newline >= 0 ? newline : text.Length;
            var line = text.Substring(lineStart, lineEnd - lineStart).TrimEnd('\r');

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var heading = line[3..].Trim();
                var digitCount = 0;
                while (digitCount < heading.Length && char.IsDigit(heading[digitCount]))
                    digitCount++;

                if (digitCount > 0 && digitCount < heading.Length && heading[digitCount] == '.')
                    sections.Add(new PromptSectionLink(heading, lineStart, lineIndex));
            }

            if (newline < 0)
                break;

            lineStart = newline + 1;
            lineIndex++;
        }

        return sections;
    }

    private sealed record PromptSectionLink(string Display, int Offset, int LineIndex);

    private Control BuildResponseTab()
    {
        _responseText.AcceptsReturn = true;
        _responseText.TextWrapping = TextWrapping.Wrap;
        _responseText.HorizontalAlignment = HorizontalAlignment.Stretch;
        _responseText.VerticalAlignment = VerticalAlignment.Stretch;
        _responseText.VerticalContentAlignment = VerticalAlignment.Top;
        ScrollViewer.SetVerticalScrollBarVisibility(_responseText, ScrollBarVisibility.Auto);
        _responseStatusTable.Width = double.NaN;
        _responseStatusTable.Height = double.NaN;
        _responseStatusTable.HorizontalAlignment = HorizontalAlignment.Stretch;
        _responseStatusTable.VerticalAlignment = VerticalAlignment.Stretch;
        _responseStatusTable.Columns.AddRange(new TableViewColumn[]
        {
            MakeReviewColumn("批次", nameof(ResponseParseStatusRow.Batch), new GridLength(70)),
            MakeReviewColumn("來源", nameof(ResponseParseStatusRow.Source), new GridLength(2, GridUnitType.Star)),
            MakeReviewColumn("已解析", nameof(ResponseParseStatusRow.Parsed), new GridLength(90)),
            MakeReviewColumn("未修改", nameof(ResponseParseStatusRow.Unchanged), new GridLength(90)),
            MakeReviewColumn("已修改", nameof(ResponseParseStatusRow.Changed), new GridLength(90)),
            MakeReviewColumn("缺漏", nameof(ResponseParseStatusRow.Missing), new GridLength(2, GridUnitType.Star)),
            MakeReviewColumn("衝突", nameof(ResponseParseStatusRow.Conflicts), new GridLength(90)),
            MakeReviewColumn("未知", nameof(ResponseParseStatusRow.Unknown), new GridLength(90)),
            MakeReviewColumn("狀態", nameof(ResponseParseStatusRow.Status), new GridLength(110)),
        });
        UiUtil.ApplyTableViewRowStyle(_responseStatusTable);
        ScrollViewer.SetHorizontalScrollBarVisibility(_responseStatusTable, ScrollBarVisibility.Auto);
        TableViewExtras.AttachListNavigation(_responseStatusTable);
        _responseText.TextChanged += async (_, _) =>
        {
            if (_state.SettingResponseText)
                return;
            await DebounceResponseParseStatusAsync();
        };

        var import = new Button { Content = "一次匯入多個 .md" };
        import.Click += async (_, _) => await ImportResponseAsync();
        var preview = new Button { Content = "加入這批並更新預覽" };
        preview.Click += async (_, _) =>
        {
            AddPastedResponse();
            await PreparePreviewAsync();
        };
        var clear = new Button { Content = "清除全部回覆" };
        clear.Click += (_, _) => ClearResponses();
        var actions = new WrapPanel
        {
            ItemSpacing = 8,
            LineSpacing = 8,
            Children =
            {
                import,
                preview,
                clear,
            },
        };
        var top = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "貼上一批後按「加入這批並更新預覽」；可重複加入，也可一次匯入多個檔案。",
                    TextWrapping = TextWrapping.Wrap,
                },
                actions,
            },
        };
        var primary = new Grid
        {
            Margin = new Thickness(8),
            RowDefinitions = new RowDefinitions("Auto,3*,Auto,2*"),
            RowSpacing = 8,
        };
        primary.Children.Add(top);
        Grid.SetRow(_responseText, 1);
        primary.Children.Add(_responseText);

        var progressLabel = new TextBlock
        {
            Text = "各批解析進度",
            FontWeight = FontWeight.Bold,
        };
        Grid.SetRow(progressLabel, 2);
        primary.Children.Add(progressLabel);
        Grid.SetRow(_responseStatusTable, 3);
        primary.Children.Add(_responseStatusTable);
        return primary;
    }

    private Control BuildEvidenceBar()
    {
        _evidencePath.IsReadOnly = true;
        var browse = new Button { Content = "選擇 Evidence JSONL…" };
        browse.Click += async (_, _) => await BrowseEvidenceAsync();
        var clear = new Button { Content = "清除" };
        clear.Click += (_, _) =>
        {
            _evidencePath.Text = string.Empty;
            SaveSettings();
            SetStatus("已清除 Evidence；未提供 Evidence 不會被視為低風險。");
        };

        _evidenceGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = 6,
        };
        _evidenceLabel = new TextBlock
        {
            Text = "Evidence",
            VerticalAlignment = VerticalAlignment.Center,
        };
        _evidenceBrowseButton = browse;
        _evidenceClearButton = clear;
        _evidenceGrid.Children.Add(_evidenceLabel);
        Grid.SetRow(_evidencePath, 1);
        _evidenceGrid.Children.Add(_evidencePath);
        var evidenceActions = new WrapPanel
        {
            ItemSpacing = 6,
            LineSpacing = 6,
            Children = { _evidenceBrowseButton, _evidenceClearButton },
        };
        Grid.SetRow(evidenceActions, 2);
        _evidenceGrid.Children.Add(evidenceActions);
        return _evidenceGrid;
    }

    private Control BuildReviewTab()
    {
        _rowList.Width = double.NaN;
        _rowList.Height = double.NaN;
        _rowList.HorizontalAlignment = HorizontalAlignment.Stretch;
        _rowList.VerticalAlignment = VerticalAlignment.Stretch;
        _rowList.Columns.AddRange(new TableViewColumn[]
        {
            MakeReviewColumn("編號", nameof(ReviewUiRow.SubtitleNumber), new GridLength(70)),
            MakeReviewColumn("決定", nameof(ReviewUiRow.DecisionLabel), new GridLength(90)),
            MakeReviewColumn("原文", nameof(ReviewUiRow.OriginalText), new GridLength(2, GridUnitType.Star)),
            MakeReviewColumn("AI 校閱", nameof(ReviewUiRow.AiText), new GridLength(2, GridUnitType.Star)),
            MakeReviewColumn("檢查", nameof(ReviewUiRow.PreviewLabel), new GridLength(120)),
            MakeReviewColumn("風險", nameof(ReviewUiRow.RiskLabel), new GridLength(100)),
        });
        UiUtil.ApplyTableViewRowStyle(_rowList);
        ScrollViewer.SetHorizontalScrollBarVisibility(_rowList, ScrollBarVisibility.Auto);
        TableViewExtras.AttachListNavigation(_rowList);
        _rowList.SelectionChanged += (_, _) => ShowSelectedRow();

        var filterControls = new WrapPanel
        {
            ItemSpacing = 8,
            LineSpacing = 8,
            Children = { _progress, _changedOnly, _highRiskOnly, _reviewEvidenceButton },
        };
        var bulkActions = new WrapPanel
        {
            ItemSpacing = 8,
            LineSpacing = 8,
            Children = { _applyAllAiButton, _commitButton },
        };
        var filters = new StackPanel
        {
            Spacing = 6,
            Children = { filterControls, bulkActions, _bulkActionHint },
        };

        foreach (var box in new[] { _originalText, _aiText })
        {
            box.AcceptsReturn = true;
            box.IsReadOnly = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.MinHeight = 72;
        }
        _manualText.AcceptsReturn = true;
        _manualText.TextWrapping = TextWrapping.Wrap;
        _manualText.MinHeight = 72;
        _rowWarning.TextWrapping = TextWrapping.Wrap;
        _evidenceSummary.TextWrapping = TextWrapping.Wrap;
        var actions = new WrapPanel
        {
            ItemSpacing = 8,
            LineSpacing = 8,
            Children =
            {
                _applyAiButton,
                _keepButton,
                _manualButton,
                _playButton,
                _mergeButton,
            },
        };

        var navigation = new WrapPanel
        {
            ItemSpacing = 8,
            LineSpacing = 8,
        };
        var previous = new Button { Content = "◀ 上一筆" };
        previous.Click += (_, _) => MoveSelection(-1);
        var next = new Button { Content = "下一筆 ▶" };
        next.Click += (_, _) => MoveSelection(1);
        navigation.Children.Add(previous);
        navigation.Children.Add(next);

        _reviewOriginalPanel = new Grid
        {
            RowDefinitions = new RowDefinitions("*"),
            Children = { _originalText },
        };
        _reviewAiPanel = new Grid
        {
            RowDefinitions = new RowDefinitions("*"),
            Children = { _aiText },
        };
        _reviewManualPanel = new Grid
        {
            RowDefinitions = new RowDefinitions("*"),
            Children = { _manualText },
        };

        _reviewDetailTabs.ItemsSource = new object[]
        {
            new TabItem { Header = "原文", Content = _reviewOriginalPanel },
            new TabItem { Header = "AI 校閱", Content = _reviewAiPanel },
            new TabItem { Header = "手動修正", Content = _reviewManualPanel },
        };
        _reviewDetailTabs.SelectedIndex = 1;

        var detail = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto"),
            RowSpacing = 5,
        };
        detail.Children.Add(_rowWarning);
        Grid.SetRow(_evidenceSummary, 1);
        detail.Children.Add(_evidenceSummary);
        Grid.SetRow(_reviewDetailTabs, 2);
        detail.Children.Add(_reviewDetailTabs);
        Grid.SetRow(actions, 3);
        detail.Children.Add(actions);
        Grid.SetRow(_mergeActionHint, 4);
        detail.Children.Add(_mergeActionHint);
        Grid.SetRow(navigation, 5);
        detail.Children.Add(navigation);

        var body = new Grid
        {
            Margin = new Thickness(8),
            RowDefinitions = new RowDefinitions("Auto,3*,2*"),
            RowSpacing = 6,
        };
        body.Children.Add(filters);
        Grid.SetRow(_rowList, 1);
        body.Children.Add(_rowList);
        var detailScroller = new ScrollViewer
        {
            Content = detail,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(detailScroller, 2);
        body.Children.Add(detailScroller);

        var evidenceContent = BuildEvidenceBar();
        _reviewEvidenceButton.Click += (_, _) => _reviewUtilitySplitView.IsPaneOpen = true;
        var utilityPane = BuildUtilityPane("Evidence / 進階資料", evidenceContent, _reviewUtilitySplitView);
        ConfigureRightUtilitySplitView(_reviewUtilitySplitView, body, utilityPane);
        return _reviewUtilitySplitView;
    }

    private static TableViewColumn MakeReviewColumn(string header, string property, GridLength width) =>
        new()
        {
            Header = header,
            Binding = new Binding(property),
            Width = width,
            CellTheme = UiUtil.TableViewCellTheme,
            HeaderTheme = UiUtil.TableViewColumnHeaderTheme,
        };

    private Control BuildGlossaryTab()
    {
        _glossaryPath.IsReadOnly = true;
        _protectedPhraseSummary.TextWrapping = TextWrapping.Wrap;

        var browse = new Button { Content = "選擇 CSV…" };
        browse.Click += async (_, _) => await BrowseGlossaryAsync();
        var reload = new Button { Content = "重新載入" };
        reload.Click += (_, _) => LoadGlossary();
        var add = new Button { Content = "加入選取候選" };
        add.Click += async (_, _) => await SaveSelectedGlossaryCandidateAsync();

        _glossaryPathBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = 6,
        };
        _glossaryPathLabel = new TextBlock
        {
            Text = "詞彙表",
            VerticalAlignment = VerticalAlignment.Center,
        };
        _glossaryBrowseButton = browse;
        _glossaryReloadButton = reload;
        _glossaryPathBar.Children.Add(_glossaryPathLabel);
        Grid.SetRow(_glossaryPath, 1);
        _glossaryPathBar.Children.Add(_glossaryPath);
        var glossaryActions = new WrapPanel
        {
            ItemSpacing = 6,
            LineSpacing = 6,
            Children = { _glossaryBrowseButton, _glossaryReloadButton },
        };
        Grid.SetRow(glossaryActions, 2);
        _glossaryPathBar.Children.Add(glossaryActions);

        var currentPanel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 6,
        };
        currentPanel.Children.Add(new TextBlock
        {
            Text = "目前規則（唯讀）",
            FontWeight = FontWeight.Bold,
        });
        Grid.SetRow(_glossaryList, 1);
        currentPanel.Children.Add(_glossaryList);

        var candidateHeader = new WrapPanel
        {
            ItemSpacing = 8,
            LineSpacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "AI 建議候選（需人工確認）",
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                add,
            },
        };
        var candidatePanel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 6,
        };
        candidatePanel.Children.Add(candidateHeader);
        Grid.SetRow(_glossaryCandidateList, 1);
        candidatePanel.Children.Add(_glossaryCandidateList);

        _glossaryTabs.ItemsSource = new object[]
        {
            new TabItem { Header = "目前規則", Content = currentPanel },
            new TabItem { Header = "AI 建議候選", Content = candidatePanel },
        };
        _glossaryTabs.SelectedIndex = 0;

        var root = new Grid
        {
            Margin = new Thickness(8),
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            RowSpacing = 8,
        };
        root.Children.Add(_glossaryPathBar);
        Grid.SetRow(_protectedPhraseSummary, 1);
        root.Children.Add(_protectedPhraseSummary);
        Grid.SetRow(_glossaryTabs, 2);
        root.Children.Add(_glossaryTabs);
        return root;
    }

    private void ConfigureAccessibility()
    {
        AutomationProperties.SetName(this, Title ?? "AI 字幕校閱");
        AutomationProperties.SetName(_workspaceSplitView, "AI 字幕校閱工作區");
        AutomationProperties.SetName(_workspaceNavigation, "校閱流程導覽");
        AutomationProperties.SetName(_workspacePaneToggle, "開啟或關閉校閱流程導覽");
        AutomationProperties.SetName(_workspaceContent, "目前校閱工作內容");
        AutomationProperties.SetName(_reviewDetailTabs, "字幕內容比較");
        AutomationProperties.SetName(_glossaryTabs, "詞彙表內容");
        AutomationProperties.SetName(_promptUtilitySplitView, "提示詞工具抽屜");
        AutomationProperties.SetName(_promptUtilityTabs, "提示詞工具內容");
        AutomationProperties.SetName(_promptReferencesButton, "開啟參考資料");
        AutomationProperties.SetName(_promptResultsButton, "開啟產生結果");
        AutomationProperties.SetName(_reviewUtilitySplitView, "校閱進階工具抽屜");
        AutomationProperties.SetName(_reviewEvidenceButton, "開啟 Evidence 進階資料");
        AutomationProperties.SetName(_promptSectionButtons, "提示詞章節導覽");
        AutomationProperties.SetName(_promptTemplateText, "完整提示詞編輯");
        AutomationProperties.SetName(_promptText, "本批提示詞唯讀結果");
        AutomationProperties.SetName(_responseText, "AI 回覆輸入");
        AutomationProperties.SetName(_responseStatusTable, "AI 回覆解析進度");
        AutomationProperties.SetName(_rowList, "字幕校閱結果");
        AutomationProperties.SetName(_originalText, "原文");
        AutomationProperties.SetName(_aiText, "AI 校閱文字");
        AutomationProperties.SetName(_manualText, "手動修正文字");
        AutomationProperties.SetName(_glossaryPath, "詞彙表路徑");
        AutomationProperties.SetName(_evidencePath, "Evidence JSONL 路徑");
        AutomationProperties.SetName(_glossaryList, "現有詞彙規則");
        AutomationProperties.SetName(_glossaryCandidateList, "AI 建議新增詞彙");
        AutomationProperties.SetName(_applyAiButton, "套用 AI，Ctrl+Enter");
        AutomationProperties.SetName(_keepButton, "保留原文，Ctrl+K");
        AutomationProperties.SetName(_manualButton, "採用手動文字");
        AutomationProperties.SetName(_playButton, "播放這一句，Ctrl+P");
        AutomationProperties.SetName(_mergeButton, "合併下一行");
        AutomationProperties.SetName(_applyAllAiButton, "一鍵套用全部 AI 結果到 Subtitle Edit 字幕");
        AutomationProperties.SetName(_commitButton, "套用逐筆調整");
        AutomationProperties.SetName(_changedOnly, "只看有修改");
        AutomationProperties.SetName(_highRiskOnly, "只看高風險");
        AutomationProperties.SetName(_status, "校閱狀態");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_rowWarning, "目前字幕警告");
        AutomationProperties.SetLiveSetting(_rowWarning, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_bulkActionHint, "全部套用阻擋原因");
        AutomationProperties.SetName(_mergeActionHint, "合併阻擋原因");
    }

    private void WireEvents()
    {
        _applyAiButton.Click += (_, _) => SetDecision(ReviewDecisionKind.Apply);
        _keepButton.Click += (_, _) => SetDecision(ReviewDecisionKind.Keep);
        _manualButton.Click += (_, _) => SetDecision(ReviewDecisionKind.Manual);
        _playButton.IsVisible = _playRow is not null;
        _playButton.Click += (_, _) => PlaySelectedRow();
        _mergeButton.Click += async (_, _) => await MergeNextAsync();
        _commitButton.Click += async (_, _) => await CommitAsync();
        _applyAllAiButton.Click += async (_, _) => await ApplyAllAiResponsesAndCommitAsync();
        _changedOnly.IsCheckedChanged += (_, _) => ApplyFilters();
        _highRiskOnly.IsCheckedChanged += (_, _) => ApplyFilters();
        KeyDown += (_, e) =>
        {
            if (HandleReviewShortcut(e.Key, e.KeyModifiers))
                e.Handled = true;
        };
    }

    private bool HandleReviewShortcut(Key key, KeyModifiers modifiers)
    {
        var commandModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (modifiers != commandModifier)
            return false;

        switch (key)
        {
            case Key.Up:
                MoveSelection(-1);
                return true;
            case Key.Down:
                MoveSelection(1);
                return true;
            case Key.Enter:
                SetDecision(ReviewDecisionKind.Apply);
                return true;
            case Key.K:
                SetDecision(ReviewDecisionKind.Keep);
                return true;
            case Key.P:
                PlaySelectedRow();
                return true;
            default:
                return false;
        }
    }

    private void PlaySelectedRow()
    {
        if (_playRow is null || _rowList.SelectedItem is not ReviewUiRow row)
            return;
        _playRow(row.RowId);
    }

    private bool SaveSettings()
    {
        var current = _settingsStore.Load();
        var glossary = (_glossaryPath.Text ?? string.Empty).Trim();
        var evidence = (_evidencePath.Text ?? string.Empty).Trim();
        var promptTemplate = (_promptTemplateText.Text ?? string.Empty).Trim();
        var referenceMaterial = (_referenceMaterialText.Text ?? string.Empty).Trim();
        if (promptTemplate.Length == 0)
        {
            SetStatus("完整提示詞不可空白。可按「載入預設提示詞」恢復預設內容。");
            return false;
        }
        var changed =
            !string.Equals(current.GlossaryPath, glossary, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.EvidencePath, evidence, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.PromptTemplate, promptTemplate, StringComparison.Ordinal) ||
            !string.Equals(current.ReferenceMaterial, referenceMaterial, StringComparison.Ordinal);
        _settingsStore.Save(new SubtitleReviewSettings(
            glossary,
            current.ChunkSize,
            evidence,
            promptTemplate,
            referenceMaterial,
            current.ReviewReferenceFiles));
        _state.InvalidateFingerprints();
        var hasReviewArtifacts =
            _state.Prompts.Count > 0 ||
            _state.Rows.Count > 0 ||
            _state.ResponseDocuments.Count > 0 ||
            !string.IsNullOrWhiteSpace(_responseText.Text);
        if (changed && hasReviewArtifacts)
        {
            ResetReviewStateForNewSources("校閱設定已變更；已清除舊提示詞、AI 回覆與預覽，請重新產生提示詞。");
            return true;
        }
        SetStatus("完整提示詞、參考資料與校閱設定已儲存；之後產生每一批都會沿用。");
        return true;
    }

    private async Task BrowseReviewReferencesAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "加入先前校閱文本參考",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("文字檔 (*.txt)") { Patterns = ["*.txt"] },
                ],
            });
        if (files.Count == 0)
            return;

        try
        {
            _settingsStore.AttachReviewReferenceFiles(files.Select(file => file.Path.LocalPath));
            RefreshReviewReferenceFiles();
            ReviewReferencesChanged("已將校閱文本參考保存到 SE；匯出提示詞時會一併輸出所有 TXT。");
        }
        catch (Exception ex)
        {
            SetStatus($"加入校閱文本參考失敗：{ex.Message}");
        }
    }

    private void ClearReviewReferences()
    {
        try
        {
            var undeleted = _settingsStore.ClearReviewReferenceFiles();
            RefreshReviewReferenceFiles();
            ReviewReferencesChanged(undeleted.Count == 0
                ? "已清除全部校閱文本參考 TXT。"
                : $"已清除 TXT 附件清單；有 {undeleted.Count} 個鎖定中的 SE 副本暫時無法刪除。關閉使用中的程式後可自行清理 references 目錄。");
        }
        catch (Exception ex)
        {
            SetStatus($"清除校閱文本參考失敗：{ex.Message}");
        }
    }

    private void RefreshReviewReferenceFiles() =>
        _reviewReferenceList.ItemsSource = _settingsStore.Load().ReviewReferenceFiles ?? [];

    private void ReviewReferencesChanged(string message)
    {
        _state.InvalidateFingerprints();
        var hasReviewArtifacts = _state.Prompts.Count > 0 ||
            _state.Rows.Count > 0 ||
            _state.ResponseDocuments.Count > 0 ||
            !string.IsNullOrWhiteSpace(_responseText.Text);
        if (hasReviewArtifacts)
            ResetReviewStateForNewSources(message + " 已清除舊提示詞、AI 回覆與預覽，請重新產生提示詞。");
        else
            SetStatus(message);
    }

    private async Task BrowseEvidenceAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "選擇 ASR Evidence JSONL",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("JSONL (*.jsonl)") { Patterns = ["*.jsonl"] },
                ],
            });
        if (files.Count == 0)
            return;

        _evidencePath.Text = files[0].Path.LocalPath;
        SaveSettings();
        SetStatus("已選擇 Evidence JSONL；下次產生提示詞/解析預覽時會嚴格匹配。");
    }

    private async Task BrowseGlossaryAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "選擇詞彙表 CSV",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("CSV (*.csv)") { Patterns = ["*.csv"] },
                ],
            });
        if (files.Count == 0)
            return;

        _glossaryPath.Text = files[0].Path.LocalPath;
        SaveSettings();
        LoadGlossary();
    }

    private void LoadGlossary()
    {
        try
        {
            var path = _settingsStore.ResolveGlossaryPath(_glossaryPath.Text);
            var result = _engine.LoadGlossary(path);
            _glossaryPath.Text = result.Path;

            _glossaryRows.Clear();
            _glossaryRows.AddRange(result.Rows.Select(row => new GlossaryUiRow(
                row.Enabled,
                row.Source,
                row.Target,
                row.Category)));
            _glossaryList.ItemsSource = null;
            _glossaryList.ItemsSource = _glossaryRows;

            var protectedPhrases = result.ProtectedPhrases
                .Where(value => value.Length > 0)
                .ToArray();
            var sample = string.Join("、", protectedPhrases.Take(8));
            _protectedPhraseSummary.Text = protectedPhrases.Length == 0
                ? "保護詞：未提供"
                : $"保護詞：{protectedPhrases.Length} 筆" +
                  (sample.Length > 0 ? $"（例如：{sample}{(protectedPhrases.Length > 8 ? "…" : string.Empty)}）" : string.Empty);

            var settings = _settingsStore.Load();
            _settingsStore.Save(settings with { GlossaryPath = result.Path });
            SetStatus($"已載入詞彙表：{_glossaryRows.Count} 筆。");
        }
        catch (Exception ex)
        {
            SetStatus($"載入詞彙表失敗：{ex.Message}");
        }
    }

    private async Task SaveSelectedGlossaryCandidateAsync()
    {
        if (_glossaryCandidateList.SelectedItem is not GlossaryCandidateUiRow candidate)
        {
            SetStatus("請先選擇一筆 AI 詞彙候選。");
            return;
        }

        var confirm = await MessageBox.Show(
            this,
            "加入詞彙表",
            $"錯誤詞：{candidate.Source}\n正確詞：{candidate.Target}\n類別：{candidate.Category}\n\n" +
            "這只會加入詞彙規則，不會自動修改目前字幕。確定加入？",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            var path = _settingsStore.ResolveGlossaryPath(_glossaryPath.Text);
            var result = _engine.SaveGlossary(
                path,
                [
                    new GlossarySuggestion(
                        candidate.Source,
                        candidate.Target,
                        candidate.Category,
                        candidate.EvidenceIds,
                        candidate.Confidence,
                        candidate.Note),
                ],
                confirmed: true);
            LoadGlossary();
            if (result.AddedCount > 0)
            {
                _state.InvalidateFingerprints();
                if (_state.Prompts.Count > 0 || _state.Rows.Count > 0)
                {
                    ResetReviewStateForNewSources("詞彙表已變更；已清除舊校閱結果，請重新產生提示詞。");
                    return;
                }
            }
            SetStatus(result.AddedCount == 0
                ? "這筆詞彙規則已存在，沒有重複加入。"
                : "已加入詞彙表，並建立 .bak 備份。");
        }
        catch (Exception ex)
        {
            SetStatus($"詞彙表寫入失敗：{ex.Message}");
        }
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void EnsureFingerprints()
    {
        if (_state.BackendFingerprint is not null && _state.EngineFingerprint is not null)
            return;

        var inputs = CaptureFingerprintInputs();
        _state.BackendFingerprint = "native-csharp-v1";
        _state.EngineFingerprint = ComputeEngineFingerprint(
            inputs.GlossaryPath,
            inputs.EvidencePath,
            inputs.PromptTemplate,
            inputs.ReferenceMaterial,
            inputs.ReviewReferenceFiles);
    }

    private async Task EnsureFingerprintsAsync()
    {
        if (_state.BackendFingerprint is not null && _state.EngineFingerprint is not null)
            return;

        var inputs = CaptureFingerprintInputs();
        var engineFingerprint = await Task.Run(() => ComputeEngineFingerprint(
            inputs.GlossaryPath,
            inputs.EvidencePath,
            inputs.PromptTemplate,
            inputs.ReferenceMaterial,
            inputs.ReviewReferenceFiles));
        _state.BackendFingerprint = "native-csharp-v1";
        _state.EngineFingerprint = engineFingerprint;
    }

    private (string GlossaryPath, string? EvidencePath, string PromptTemplate,
        string ReferenceMaterial, IReadOnlyList<string> ReviewReferenceFiles) CaptureFingerprintInputs()
    {
        var glossaryPath = _settingsStore.ResolveGlossaryPath(_glossaryPath.Text);
        _glossaryPath.Text = glossaryPath;
        return (
            glossaryPath,
            EmptyToNull(_evidencePath.Text),
            (_promptTemplateText.Text ?? string.Empty).Trim(),
            (_referenceMaterialText.Text ?? string.Empty).Trim(),
            _settingsStore.ResolveReviewReferenceFiles());
    }

    private static string ComputeEngineFingerprint(
        string glossaryPath,
        string? evidencePath,
        string promptTemplate,
        string referenceMaterial,
        IReadOnlyList<string> reviewReferenceFiles)
    {
        var engine = new StringBuilder();
        engine.Append("core=").Append(SubtitleReviewEngine.GetEngineFingerprint()).AppendLine();
        if (File.Exists(glossaryPath))
            engine.Append("glossary=").Append(HashFile(glossaryPath)).AppendLine();

        var protectedPath = Path.Combine(
            Path.GetDirectoryName(glossaryPath) ?? string.Empty,
            "protected_phrases.json");
        if (File.Exists(protectedPath))
            engine.Append("protected=").Append(HashFile(protectedPath)).AppendLine();

        if (evidencePath is not null)
        {
            engine.Append("evidence_path=").Append(evidencePath).AppendLine();
            if (File.Exists(evidencePath))
                engine.Append("evidence=").Append(HashFile(evidencePath)).AppendLine();
        }

        engine.Append("prompt_template=")
            .Append(Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(promptTemplate))).ToLowerInvariant())
            .AppendLine();
        engine.Append("reference_material=")
            .Append(Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(referenceMaterial))).ToLowerInvariant())
            .AppendLine();
        foreach (var path in reviewReferenceFiles.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            engine.Append("review_reference=")
                .Append(Path.GetFileName(path))
                .Append(':')
                .Append(HashFile(path))
                .AppendLine();
        }
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(engine.ToString()))).ToLowerInvariant();
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private ReviewInputRow[] EngineRows()
    {
        var byId = _snapshot.Rows.ToDictionary(row => row.Id);
        return _selection.Rows.Select(item =>
        {
            var row = byId[item.RowId];
            return new ReviewInputRow(
                item.ReviewId,
                row.Text,
                TimeSpan.FromTicks(row.StartTicks).TotalMilliseconds,
                TimeSpan.FromTicks(row.EndTicks).TotalMilliseconds);
        }).ToArray();
    }

    private async Task GeneratePromptsAsync()
    {
        if (_state.SessionInvalidated)
        {
            SetStatus("這個校閱 session 已失效，只能檢視；請重新開啟校閱。");
            return;
        }

        try
        {
            if (!SaveSettings())
                return;
            var settings = _settingsStore.Load();
            var rows = EngineRows();
            var glossaryPath = _settingsStore.ResolveGlossaryPath(_glossaryPath.Text);
            var evidencePath = EmptyToNull(_evidencePath.Text);
            var reviewReferenceFiles = _settingsStore.ResolveReviewReferenceFiles();
            ReviewPromptResult? result = null;
            await RunBusyAsync("正在產生校閱提示詞…", async () =>
            {
                await EnsureFingerprintsAsync();
                result = await Task.Run(() => _engine.CreateReviewRequest(
                    rows,
                    settings.ChunkSize,
                    glossaryPath,
                    evidencePath,
                    settings.PromptTemplate,
                    settings.ReferenceMaterial,
                    reviewReferenceFiles.Select(path => Path.GetFileName(path)!).ToArray()));
            });

            if (result is null)
                return;
            _state.Prompts.Clear();
            _state.Prompts.AddRange(result.Prompts.Select(prompt => prompt.Markdown));
            RebuildPromptExpectedIds(settings.ChunkSize);
            UpdateResponseParseStatus();

            _state.PromptIndex = 0;
            ShowPrompt();
            TrySaveSession();
            SetStatus($"已產生 {_state.Prompts.Count} 批提示詞；原生 C# 校閱引擎已完成。");
        }
        catch (Exception ex)
        {
            SetStatus($"產生提示詞失敗：{ex.Message}");
        }
    }

    private void ChangePrompt(int delta)
    {
        if (_state.Prompts.Count == 0)
            return;
        _state.PromptIndex = Math.Clamp(_state.PromptIndex + delta, 0, _state.Prompts.Count - 1);
        ShowPrompt();
    }

    private void ShowPrompt()
    {
        _promptText.Text = _state.Prompts.Count == 0 ? string.Empty : _state.Prompts[_state.PromptIndex];
        _promptPosition.Text = _state.Prompts.Count == 0
            ? "尚未產生"
            : $"第 {_state.PromptIndex + 1} / {_state.Prompts.Count} 批";
    }

    private void RebuildPromptExpectedIds(int chunkSize)
    {
        _state.PromptExpectedIds.Clear();
        _state.PromptExpectedIds.AddRange(EngineRows()
            .Chunk(chunkSize)
            .Select(chunk => chunk.Select(row => row.ReviewId).ToArray()));
    }

    private ReviewDecisions CurrentDecisions()
    {
        var rows = _state.Rows.ToDictionary(row => row.ReviewId);
        var decisions = _selection.Rows.Select(selected =>
        {
            if (!rows.TryGetValue(selected.ReviewId, out var row))
                return new ReviewDecision(selected.ReviewId, ReviewDecisionKind.Pending);

            return new ReviewDecision(
                row.ReviewId,
                row.Decision,
                string.IsNullOrWhiteSpace(row.AiText) ? null : row.AiText,
                row.ManualText);
        }).ToArray();
        return new ReviewDecisions(_selection, decisions);
    }

    private void TrySaveSession()
    {
        if (_state.BackendFingerprint is null || _state.EngineFingerprint is null)
            return;

        try
        {
            var responses = CurrentResponses();
            var session = new ReviewSession(
                1,
                _snapshot,
                _state.BackendFingerprint,
                _state.EngineFingerprint,
                CurrentDecisions(),
                Enumerable.Range(1, _state.Prompts.Count).Select(i => $"prompt-{i}").ToArray(),
                Enumerable.Range(1, responses.Length).Select(i => $"response-{i}").ToArray(),
                DateTimeOffset.UtcNow)
            {
                PromptChunks = _state.Prompts.ToArray(),
                Responses = responses,
            };
            _sessionStore.Save(_sessionFileName, session);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetStatus($"校閱進度無法儲存：{ex.Message}");
        }
    }

    private void TryResumeSession()
    {
        if (!_sessionStore.Exists(_sessionFileName))
            return;

        try
        {
            EnsureFingerprints();
            var session = _sessionStore.Load(_sessionFileName);
            if (!ReviewSessionResume.TryRebind(
                    session,
                    _snapshot,
                    _state.BackendFingerprint!,
                    _state.EngineFingerprint!,
                    out var rebound,
                    out var reason))
            {
                ResetReviewStateForNewSources(
                    $"找到舊校閱進度，但目前環境已不同（{reason}）；已忽略舊進度，可直接重新產生提示詞。");
                return;
            }

            RestoreSessionText(rebound);

            if (rebound.Responses.Count > 0)
            {
                var glossaryPath = _settingsStore.ResolveGlossaryPath(_glossaryPath.Text);
                var result = _engine.PreparePreview(
                    EngineRows(),
                    rebound.Responses.ToArray(),
                    glossaryPath,
                    EmptyToNull(_evidencePath.Text));
                LoadPreview(result);
                ApplySessionDecisions(rebound.Decisions.Items);
            }

            SetStatus($"已恢復先前校閱進度：{_state.Rows.Count(row => row.Decision != ReviewDecisionKind.Pending)}/{_selection.Rows.Count}。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ReviewEngineException)
        {
            SetStatus($"無法恢復先前校閱進度：{ex.Message}");
        }
    }

    private void RestoreSessionText(ReviewSession session)
    {
        _state.Prompts.Clear();
        _state.Prompts.AddRange(session.PromptChunks);
        _state.PromptIndex = 0;
        ShowPrompt();
        RebuildPromptExpectedIds(_settingsStore.Load().ChunkSize);
        LoadResponseDocuments(session.Responses
            .Select((content, index) => ($"已儲存回覆_第{index + 1:000}批.md", content))
            .ToArray());
    }

    private async Task CopyPromptAsync()
    {
        if (string.IsNullOrWhiteSpace(_promptText.Text))
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        await clipboard.SetTextAsync(_promptText.Text);
        var references = _settingsStore.Load().ReviewReferenceFiles ?? [];
        SetStatus(references.Length == 0
            ? "已複製目前提示詞。"
            : $"已複製目前提示詞；送交 AI 時請一併上傳 {references.Length} 個校閱參考 TXT。");
    }

    private async Task ExportPromptsAsync()
    {
        if (_state.Prompts.Count == 0)
        {
            SetStatus("請先產生校閱提示詞，再匯出分批 Markdown 檔。");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "選擇分批提示詞匯出位置",
                AllowMultiple = false,
            });
        if (folders.Count == 0)
            return;

        try
        {
            var output = ExportPromptFiles(folders[0].Path.LocalPath);
            SetStatus($"已匯出 {_state.Prompts.Count} 個分批提示詞：{output}");
        }
        catch (Exception ex)
        {
            SetStatus($"匯出提示詞失敗：{ex.Message}");
        }
    }

    private string ExportPromptFiles(string parentDirectory)
    {
        Directory.CreateDirectory(parentDirectory);
        var output = Path.Combine(
            parentDirectory,
            $"AI校閱提示詞_{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}"[..31]);
        Directory.CreateDirectory(output);
        _settingsStore.ExportReviewReferenceFiles(output);
        for (var index = 0; index < _state.Prompts.Count; index++)
        {
            var path = Path.Combine(output, $"AI校閱提示詞_第{index + 1:000}批.md");
            File.WriteAllText(path, _state.Prompts[index], new UTF8Encoding(false));
        }
        return output;
    }

    private async Task ImportResponseAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "匯入 AI 校閱回覆",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Markdown (*.md)")
                    {
                        Patterns = ["*.md"],
                    },
                ],
            });
        if (files.Count == 0)
            return;

        try
        {
            var documents = new List<(string Name, string Content)>();
            foreach (var path in files
                         .Select(file => file.Path.LocalPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                documents.Add((
                    Path.GetFileName(path),
                    await File.ReadAllTextAsync(path, new UTF8Encoding(false, true))));
            }
            AddPastedResponse();
            await CompleteResponseImportAsync(_state.ResponseDocuments
                .Select(document => (document.Name, document.Content))
                .Concat(documents)
                .ToArray());
        }
        catch (Exception ex)
        {
            SetStatus($"匯入失敗：{ex.Message}");
        }
    }

    private async Task CompleteResponseImportAsync(IReadOnlyList<(string Name, string Content)> documents)
    {
        LoadResponseDocuments(documents);
        TrySaveSession();

        await PreparePreviewAsync();
    }

    private void LoadResponseDocuments(IReadOnlyList<(string Name, string Content)> documents)
    {
        _state.ResponseDocuments.Clear();
        _state.ResponseDocuments.AddRange(documents.Select(document =>
            new ResponseDocument(document.Name, document.Content)));
        SetResponseTextWithoutParsing(string.Empty);
        UpdateResponseParseStatus();
    }

    private void SetResponseTextWithoutParsing(string text)
    {
        _state.SettingResponseText = true;
        try
        {
            _responseText.Text = text;
        }
        finally
        {
            _state.SettingResponseText = false;
        }
    }

    private string[] CurrentResponses()
    {
        var responses = _state.ResponseDocuments.Select(document => document.Content).ToList();
        var response = _responseText.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(response))
            responses.AddRange(SplitPastedResponses(response));
        return responses.ToArray();
    }

    private bool AddPastedResponse()
    {
        var response = _responseText.Text?.Trim();
        if (string.IsNullOrWhiteSpace(response))
            return false;

        foreach (var content in SplitPastedResponses(response))
        {
            if (_state.ResponseDocuments.Any(document =>
                    string.Equals(document.Content.Trim(), content, StringComparison.Ordinal)))
                continue;
            _state.ResponseDocuments.Add(new ResponseDocument(
                $"貼上內容第{_state.ResponseDocuments.Count + 1:000}段",
                content));
        }
        SetResponseTextWithoutParsing(string.Empty);
        UpdateResponseParseStatus();
        TrySaveSession();
        return true;
    }

    private void ClearResponses()
    {
        _state.ResponseDocuments.Clear();
        SetResponseTextWithoutParsing(string.Empty);
        _state.Rows.Clear();
        _state.UnknownResponseIdCount = 0;
        ApplyFilters();
        UpdateProgress();
        UpdateResponseParseStatus();
        TrySaveSession();
        SetStatus("已清除 AI 回覆與預覽，可重新貼上或匯入。");
    }

    private static string[] SplitPastedResponses(string response)
    {
        var lines = response
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var starts = Enumerable.Range(0, lines.Length)
            .Where(index => lines[index].Trim() == "## 校閱結果")
            .ToArray();
        if (starts.Length <= 1)
            return [response.Trim()];

        return starts.Select((start, index) =>
        {
            var from = index == 0 ? 0 : start;
            var to = index + 1 < starts.Length ? starts[index + 1] : lines.Length;
            return string.Join("\n", lines[from..to]).Trim();
        }).Where(value => value.Length > 0).ToArray();
    }

    private async Task DebounceResponseParseStatusAsync()
    {
        var debounce = _state.RestartResponseParseDebounce();
        try
        {
            await Task.Delay(250, debounce.Token);
            if (!_state.OwnsResponseParseDebounce(debounce))
                return;

            var input = CaptureResponseParseStatusInput();
            var statuses = await Task.Run(
                () => BuildResponseParseStatuses(
                    input.Responses,
                    input.Names,
                    input.Batches,
                    input.AllExpected),
                debounce.Token);
            if (_state.OwnsResponseParseDebounce(debounce) && !debounce.IsCancellationRequested)
                ApplyResponseParseStatuses(statuses);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _state.CompleteResponseParseDebounce(debounce);
        }
    }

    private void UpdateResponseParseStatus()
    {
        var input = CaptureResponseParseStatusInput();
        ApplyResponseParseStatuses(BuildResponseParseStatuses(
            input.Responses,
            input.Names,
            input.Batches,
            input.AllExpected));
    }

    private (string[] Responses, string[] Names, int[][] Batches, int[] AllExpected)
        CaptureResponseParseStatusInput()
    {
        var responses = CurrentResponses();
        var batches = _state.PromptExpectedIds.Count > 0
            ? _state.PromptExpectedIds.Select(batch => batch.ToArray()).ToArray()
            : [_selection.Rows.Select(row => row.ReviewId).ToArray()];
        var names = _state.ResponseDocuments.Select(document => document.Name).ToList();
        names.AddRange(Enumerable.Range(names.Count + 1, Math.Max(0, responses.Length - names.Count))
            .Select(index => $"尚未加入的貼上內容第{index:000}段"));
        return (
            responses,
            names.ToArray(),
            batches,
            _selection.Rows.Select(row => row.ReviewId).ToArray());
    }

    private IReadOnlyList<ResponseParseStatusRow> BuildResponseParseStatuses(
        IReadOnlyList<string> responses,
        IReadOnlyList<string> names,
        IReadOnlyList<int[]> batches,
        IReadOnlyCollection<int> allExpected)
    {
        if (responses.Count == 0)
            return batches.Select((expected, index) =>
                ResponseParseStatusRow.NotImported(index + 1, expected)).ToArray();

        var statuses = new List<ResponseParseStatusRow>();
        try
        {
            var parsedSet = _engine.ParseAiResponseSet(responses, allExpected);
            var parsed = parsedSet.Combined;
            var present = parsed.Results
                .Where(result => !string.IsNullOrWhiteSpace(result.Value))
                .Select(result => result.Key)
                .ToHashSet();
            var originalById = EngineRows().ToDictionary(row => row.ReviewId, row => row.Text);
            var sourcesByBatch = batches.Select(_ => new List<string>()).ToArray();
            for (var responseIndex = 0; responseIndex < parsedSet.Documents.Count; responseIndex++)
            {
                var one = parsedSet.Documents[responseIndex];
                if (one.Error is not null)
                {
                    var source = responseIndex < names.Count ? names[responseIndex] : $"回覆 {responseIndex + 1}";
                    statuses.Add(ResponseParseStatusRow.Unparsed(source, one.Error));
                    continue;
                }

                for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
                    if (one.Results.Keys.Any(batches[batchIndex].Contains))
                        sourcesByBatch[batchIndex].Add(
                            responseIndex < names.Count ? names[responseIndex] : $"回覆 {responseIndex + 1}");
            }

            for (var index = 0; index < batches.Count; index++)
            {
                var expected = batches[index];
                var missing = expected.Where(id => !present.Contains(id)).ToArray();
                var conflicts = parsed.Conflicts.Keys.Where(expected.Contains).ToArray();
                var comparable = expected
                    .Where(id => present.Contains(id) &&
                        !conflicts.Contains(id) &&
                        originalById.ContainsKey(id))
                    .Select(id => (
                        Original: originalById[id],
                        Reviewed: parsed.Results[id]))
                    .ToArray();
                var unchanged = comparable.Count(pair => !HasSubstantiveDifference(
                    pair.Original,
                    pair.Reviewed));
                statuses.Add(new ResponseParseStatusRow(
                    (index + 1).ToString("000"),
                    sourcesByBatch[index].Count == 0 ? "—" : string.Join("、", sourcesByBatch[index]),
                    $"{expected.Length - missing.Length}/{expected.Length}",
                    unchanged.ToString(),
                    (comparable.Length - unchanged).ToString(),
                    ResponseParseStatusRow.FormatIds(missing),
                    ResponseParseStatusRow.FormatIds(conflicts),
                    "—",
                    missing.Length == 0 && conflicts.Length == 0 ? "完成" : "尚未完成"));
            }
            if (parsed.UnknownIds.Count > 0)
                statuses.Add(ResponseParseStatusRow.UnknownIds(parsed.UnknownIds));
        }
        catch (ReviewEngineException ex)
        {
            statuses.AddRange(batches.Select((expected, index) =>
                ResponseParseStatusRow.UnparsedBatch(index + 1, names, expected, ex.Message)));
        }
        return statuses;
    }

    private static bool HasSubstantiveDifference(string original, string reviewed) =>
        !string.Equals(
            RemovePunctuationAndWhitespace(original),
            RemovePunctuationAndWhitespace(reviewed),
            StringComparison.Ordinal);

    private static string RemovePunctuationAndWhitespace(string text) =>
        string.Concat(text.EnumerateRunes()
            .Where(rune => !Rune.IsWhiteSpace(rune) && !Rune.IsPunctuation(rune))
            .Select(rune => rune.ToString()));

    private void ApplyResponseParseStatuses(IReadOnlyList<ResponseParseStatusRow> statuses)
    {
        _state.ResponseStatuses.Clear();
        _state.ResponseStatuses.AddRange(statuses);
        RefreshResponseStatusTable();
    }

    private void RefreshResponseStatusTable()
    {
        _responseStatusTable.ItemsSource = null;
        _responseStatusTable.ItemsSource = _state.ResponseStatuses.ToArray();
    }

    private async Task PreparePreviewAsync(bool selectPreviewTab = false)
    {
        if (_state.SessionInvalidated)
        {
            SetStatus("這個校閱 session 已失效，只能檢視；請重新開啟校閱。");
            return;
        }

        var responses = CurrentResponses();
        if (responses.Length == 0)
        {
            SetStatus("請先貼上 AI 回覆或匯入 .md。");
            return;
        }

        try
        {
            var rows = EngineRows();
            var glossaryPath = _settingsStore.ResolveGlossaryPath(_glossaryPath.Text);
            var evidencePath = EmptyToNull(_evidencePath.Text);
            ReviewPreviewResult? result = null;
            await RunBusyAsync("正在解析 AI 回覆並建立完整預覽…", async () =>
            {
                await EnsureFingerprintsAsync();
                result = await Task.Run(() => _engine.PreparePreview(
                    rows,
                    responses,
                    glossaryPath,
                    evidencePath));
            });

            if (result is null)
                return;
            LoadPreview(result);
            UpdateResponseParseStatus();
            TrySaveSession();
            if (selectPreviewTab)
            {
                _workspaceNavigation.SelectedIndex = 2;
                SelectWorkspacePage(2);
            }
            SetStatus(result.MissingIds.Count > 0
                ? $"AI 回覆已解析；缺漏 {result.MissingIds.Count} 列，一鍵套用時會自動保留原文。"
                : "AI 回覆已解析；確認完整預覽後，可按「一鍵套用全部 AI 結果到 SE 字幕」。");
        }
        catch (Exception ex)
        {
            SetStatus($"解析 AI 回覆失敗：{ex.Message}");
        }
    }

    private async Task RunBusyAsync(string status, Func<Task> work)
    {
        if (!_state.TryBeginBusy())
            return;

        _workspaceSplitView.IsEnabled = false;
        _workspacePaneToggle.IsEnabled = false;
        RefreshActionState();
        SetStatus(status);
        try
        {
            await work();
        }
        finally
        {
            _state.EndBusy();
            _workspaceSplitView.IsEnabled = true;
            _workspacePaneToggle.IsEnabled = true;
            RefreshActionState();
        }
    }

    private void LoadPreview(ReviewPreviewResult result)
    {
        var selectionByReviewId = _selection.Rows.ToDictionary(row => row.ReviewId);
        var snapshotById = _snapshot.Rows.ToDictionary(row => row.Id);
        var missing = result.MissingIds.ToHashSet();
        _state.UnknownResponseIdCount = result.UnknownIds.Count;

        _state.Rows.Clear();
        foreach (var value in result.Rows)
        {
            if (!selectionByReviewId.TryGetValue(value.ReviewId, out var selected))
                continue;

            var snapshot = snapshotById[selected.RowId];
            var ai = value.AiReviewText;
            var missingResponse = missing.Contains(value.ReviewId) || string.IsNullOrWhiteSpace(ai);
            var changed = !missingResponse && !string.Equals(ai, snapshot.Text, StringComparison.Ordinal);
            var decision = changed ? ReviewDecisionKind.Pending : ReviewDecisionKind.Keep;
            _state.Rows.Add(new ReviewUiRow(
                value.ReviewId,
                selected.RowId,
                snapshot.Number,
                snapshot.Text,
                ai,
                value.ResponseConflict,
                missingResponse,
                changed,
                decision,
                ToUiEvidence(value)));
        }

        _glossaryCandidates.Clear();
        _glossaryCandidates.AddRange(result.GlossaryCandidates
            .Where(candidate =>
                candidate.Source.Length > 0 && candidate.Target.Length > 0)
            .Select(candidate => new GlossaryCandidateUiRow(
                candidate.Source,
                candidate.Target,
                candidate.Category,
                candidate.EvidenceIds,
                candidate.Confidence,
                candidate.Note)));
        _glossaryCandidateList.ItemsSource = null;
        _glossaryCandidateList.ItemsSource = _glossaryCandidates;

        ApplyFilters();
        UpdateProgress();
        if (result.UnknownIds.Count > 0)
            SetStatus($"AI 回覆包含未知編號：{string.Join("、", result.UnknownIds)}；不會套用。");
    }

    private void ApplySessionDecisions(IReadOnlyList<ReviewDecision> decisions)
    {
        var byId = decisions.ToDictionary(decision => decision.ReviewId);
        foreach (var row in _state.Rows)
        {
            if (!byId.TryGetValue(row.ReviewId, out var decision))
                continue;

            row.Decision = decision.Kind;
            row.ManualText = decision.ManualText;
        }

        var selected = _rowList.SelectedItem as ReviewUiRow ?? _state.Rows.FirstOrDefault();
        if (selected is not null)
            RefreshRowList(selected);
        UpdateProgress();
    }

    private static ReviewEvidence ToUiEvidence(ReviewPreviewRow row)
    {
        var packet = row.Evidence;
        var alternatives = packet.Alternatives
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select(item =>
            {
                var label = string.IsNullOrWhiteSpace(item.Engine)
                    ? "候選"
                    : item.Engine;
                return $"{label}: {item.Text}";
            })
            .ToArray();

        return new ReviewEvidence(
            row.EvidenceStatus,
            packet.Risk.Priority,
            packet.Risk.Score,
            packet.Risk.FlagCodes,
            alternatives);
    }

    private void ShowSelectedRow()
    {
        if (_rowList.SelectedItem is not ReviewUiRow row)
        {
            _originalText.Text = string.Empty;
            _aiText.Text = string.Empty;
            _manualText.Text = string.Empty;
            _evidenceSummary.Text = string.Empty;
            RefreshActionState();
            return;
        }

        _originalText.Text = row.OriginalText;
        _aiText.Text = row.AiText;
        _manualText.Text = row.ManualText ?? row.AiText;
        _evidenceSummary.Text = row.Evidence.Summary;
        _rowWarning.Text = _state.SessionInvalidated
            ? "這個舊校閱 session 已失效，只供檢視；請重新開啟校閱。"
            : row.Warning;
        RefreshActionState();
    }

    private void SetDecision(ReviewDecisionKind kind)
    {
        if (_state.SessionInvalidated)
        {
            SetStatus("這個校閱 session 已失效；請重新開啟校閱後再做決策。");
            return;
        }

        if (_rowList.SelectedItem is not ReviewUiRow row)
            return;

        if (kind == ReviewDecisionKind.Apply && !row.CanApplyAi)
        {
            SetStatus(row.Warning);
            return;
        }

        if (kind == ReviewDecisionKind.Manual)
        {
            var text = _manualText.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus("手動修正文字不可空白。");
                return;
            }
            row.ManualText = text;
        }

        row.Decision = kind;
        RefreshRowList(row);
        UpdateProgress();
            TrySaveSession();
        MoveSelection(1, pendingOnly: true);
    }

    private ReviewDecision[] BuildAllAiDecisions()
    {
        if (_state.SessionInvalidated)
            throw new InvalidOperationException("這個校閱 session 已失效；請重新開啟校閱。");
        if (_state.Rows.Count == 0)
            throw new InvalidOperationException("目前沒有可套用的 AI 校閱結果。");

        if (_state.UnknownResponseIdCount > 0)
            throw new InvalidOperationException($"AI 回覆包含 {_state.UnknownResponseIdCount} 個未知編號，無法安全寫回 SE 字幕。");

        var blocked = _state.Rows.FirstOrDefault(row =>
            row.Conflict ||
            (!row.MissingResponse && !ReviewSafety.FormatTagsMatch(row.OriginalText, row.AiText)));
        if (blocked is not null)
            throw new InvalidOperationException($"第 {blocked.SubtitleNumber} 列無法採用 AI 回覆：{blocked.Warning}");

        return _state.Rows.Select(row => row.MissingResponse
            ? new ReviewDecision(row.ReviewId, ReviewDecisionKind.Keep)
            : new ReviewDecision(row.ReviewId, ReviewDecisionKind.Apply, row.AiText))
            .ToArray();
    }

    internal int ApplyAllAiResponsesToSubtitle()
    {
        var decisions = BuildAllAiDecisions();
        var applied = _vm.ApplySubtitleReview(_snapshot, _selection, decisions);
        var decisionsById = decisions.ToDictionary(decision => decision.ReviewId);
        foreach (var row in _state.Rows)
            row.Decision = decisionsById[row.ReviewId].Kind;
        return applied;
    }

    private async Task ApplyAllAiResponsesAndCommitAsync()
    {
        try
        {
            BuildAllAiDecisions();
        }
        catch (InvalidOperationException ex)
        {
            _changedOnly.IsChecked = false;
            _highRiskOnly.IsChecked = false;
            ApplyFilters(_state.Rows.FirstOrDefault(row =>
                row.Conflict ||
                (!row.MissingResponse && !ReviewSafety.FormatTagsMatch(row.OriginalText, row.AiText))));
            SetStatus(ex.Message);
            return;
        }

        var changeCount = _state.Rows.Count(row => row.Changed);
        var missingCount = _state.Rows.Count(row => row.MissingResponse);
        if (changeCount == 0)
        {
            SetStatus(missingCount > 0
                ? $"AI 沒有可套用的修改；未回覆的 {missingCount} 列會保留原文。"
                : "AI 結果全部與原文相同，沒有需要套用的修改。");
            return;
        }

        var highRiskCount = _state.Rows.Count(row => row.Changed && row.Evidence.IsHighRisk);
        var confirm = await MessageBox.Show(
            this,
            "套用全部 AI 校閱結果",
            $"將把 {changeCount} 列 AI 校閱文字直接寫回 Subtitle Edit 主畫面的字幕。" +
            (missingCount > 0 ? $"\nAI 未回覆的 {missingCount} 列會自動保留原文。" : string.Empty) +
            (highRiskCount > 0 ? $"\n其中 {highRiskCount} 列標示為高風險。" : string.Empty) +
            "\n時間軸與字幕格式資料不會改變；可用一次 Ctrl+Z 還原。\n\n確定套用？",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            var applied = ApplyAllAiResponsesToSubtitle();
            SetStatus($"已將 {applied} 列 AI 校閱結果套用到 SE 字幕；可用 Ctrl+Z 一次還原。");
            Close();
        }
        catch (Exception ex)
        {
            SetStatus($"未套用任何結果：{ex.Message}");
        }
    }

    private ReviewUiRow[] VisibleRows() =>
        _state.Rows.Where(row =>
                (_changedOnly.IsChecked != true || row.Changed) &&
                (_highRiskOnly.IsChecked != true || row.Evidence.IsHighRisk))
            .ToArray();

    private void ApplyFilters(ReviewUiRow? preferred = null)
    {
        preferred ??= _rowList.SelectedItem as ReviewUiRow;
        var visible = VisibleRows();
        _rowList.ItemsSource = null;
        _rowList.ItemsSource = visible;
        var index = preferred is null ? -1 : Array.IndexOf(visible, preferred);
        _rowList.SelectedIndex = index >= 0 ? index : visible.Length > 0 ? 0 : -1;
        if (_rowList.SelectedIndex >= 0)
            _rowList.ScrollIntoView(_rowList.SelectedIndex);
    }

    private void MoveSelection(int delta, bool pendingOnly = false)
    {
        var visible = VisibleRows();
        if (visible.Length == 0)
            return;

        var current = _rowList.SelectedItem as ReviewUiRow;
        var start = current is null ? 0 : Math.Max(0, Array.IndexOf(visible, current));
        for (var step = 1; step <= visible.Length; step++)
        {
            var index = Math.Clamp(start + delta * step, 0, visible.Length - 1);
            if (!pendingOnly || visible[index].Decision == ReviewDecisionKind.Pending)
            {
                _rowList.SelectedItem = visible[index];
                _rowList.ScrollIntoView(index);
                return;
            }
            if (index == 0 || index == visible.Length - 1)
                break;
        }
    }

    private void RefreshRowList(ReviewUiRow selected) => ApplyFilters(selected);

    private void UpdateProgress()
    {
        var decided = _state.Rows.Count(row => row.Decision != ReviewDecisionKind.Pending);
        var changed = _state.Rows.Count(row => row.Changed);
        var blockReason = GetApplyAllBlockReason();
        _progress.Text = $"進度：{decided}/{_state.Rows.Count}｜AI 有修改：{changed} 列" +
                         (blockReason is null ? string.Empty : $"｜全部套用不可用：{blockReason}");
        RefreshActionState();
    }

    private string? GetApplyAllBlockReason()
    {
        if (_state.IsBusy)
            return "處理中";
        if (_state.SessionInvalidated)
            return "校閱 session 已失效";
        if (_state.Rows.Count == 0)
            return "尚未建立完整預覽";
        if (_state.UnknownResponseIdCount > 0)
            return $"回覆含 {_state.UnknownResponseIdCount} 個未知編號";
        var blocked = _state.Rows.FirstOrDefault(row =>
            row.Conflict ||
            (!row.MissingResponse && !ReviewSafety.FormatTagsMatch(row.OriginalText, row.AiText)));
        return blocked is null
            ? null
            : $"第 {blocked.SubtitleNumber} 列：{blocked.Warning}";
    }

    private void RefreshActionState()
    {
        var row = _rowList.SelectedItem as ReviewUiRow;
        var writable = !_state.IsBusy && !_state.SessionInvalidated;
        var bulkReason = GetApplyAllBlockReason();
        var mergeReason = GetMergeBlockReason(row);
        _applyAiButton.IsEnabled = writable && row?.CanApplyAi == true;
        _keepButton.IsEnabled = writable && row is not null;
        _manualButton.IsEnabled = writable && row is not null;
        _mergeButton.IsEnabled = mergeReason is null;
        _commitButton.IsEnabled = writable &&
                                  _state.Rows.Count > 0 &&
                                  _state.Rows.All(item => item.Decision != ReviewDecisionKind.Pending);
        _applyAllAiButton.IsEnabled = bulkReason is null;
        var missingCount = _state.Rows.Count(item => item.MissingResponse);
        _bulkActionHint.Text = bulkReason ??
            (missingCount > 0 ? $"AI 未回覆 {missingCount} 列；一鍵套用時會自動保留原文。" : string.Empty);
        _mergeActionHint.Text = mergeReason ?? string.Empty;
    }

    private string? GetMergeBlockReason(ReviewUiRow? row)
    {
        if (_state.IsBusy)
            return "處理中，暫時不能合併。";
        if (_state.SessionInvalidated)
            return "校閱 session 已失效，不能合併。";
        if (row is null)
            return "請先選擇一筆字幕。";

        var selected = _selection.Rows.FirstOrDefault(item => item.ReviewId == row.ReviewId);
        if (selected is null)
            return "找不到目前字幕列。";
        var snapshot = _snapshot.Rows.FirstOrDefault(item => item.Id == selected.RowId);
        if (snapshot is null || snapshot.Position >= _snapshot.Rows.Count)
            return "目前字幕沒有下一行可合併。";

        var next = _snapshot.Rows[snapshot.Position];
        if (next.IsReferenceOnly)
            return "下一行是參考字幕，不能合併。";
        if (string.IsNullOrWhiteSpace(next.Text))
            return "下一行是空白字幕，不能合併。";
        return null;
    }

    private async Task MergeNextAsync()
    {
        if (_state.SessionInvalidated || _rowList.SelectedItem is not ReviewUiRow row)
            return;

        SubtitleReviewMergePreview preview;
        try
        {
            preview = _vm.GetSubtitleReviewMergePreview(_snapshot, _selection, row.ReviewId);
        }
        catch (Exception ex)
        {
            SetStatus($"無法合併：{ex.Message}");
            return;
        }

        var confirm = await MessageBox.Show(
            this,
            "合併下一行",
            $"將字幕 #{preview.CurrentNumber} 與 #{preview.NextNumber} 合併。\n" +
            $"時間範圍：{FormatTime(preview.Start)} → {FormatTime(preview.End)}\n\n" +
            "合併是獨立的原生 Subtitle Edit 操作，可用 Ctrl+Z 還原。\n" +
            "合併後目前 AI 校閱 session 會改為唯讀。\n\n確定合併？",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            _vm.MergeSubtitleReviewWithNext(_snapshot, _selection, row.ReviewId);
            InvalidateSession("字幕已合併；舊校閱 session 已鎖成唯讀，請重新開啟校閱以取得新的行對應。");
        }
        catch (Exception ex)
        {
            SetStatus($"合併失敗，未修改字幕：{ex.Message}");
        }
    }

    private static string FormatTime(TimeSpan value) =>
        value.ToString(@"hh\:mm\:ss\.fff");

    private async Task CommitAsync()
    {
        if (_state.Rows.Count == 0 ||
            _state.Rows.Any(row => row.Decision == ReviewDecisionKind.Pending))
            return;

        var decisions = _state.Rows.Select(row => new ReviewDecision(
            row.ReviewId,
            row.Decision,
            string.IsNullOrWhiteSpace(row.AiText) ? null : row.AiText,
            row.ManualText)).ToArray();

        var changeCount = _state.Rows.Count(row =>
            row.Decision == ReviewDecisionKind.Apply
                ? !string.Equals(row.AiText, row.OriginalText, StringComparison.Ordinal)
                : row.Decision == ReviewDecisionKind.Manual &&
                  !string.Equals(row.ManualText, row.OriginalText, StringComparison.Ordinal));
        var highRiskCount = _state.Rows.Count(row =>
            row.Evidence.IsHighRisk &&
            row.Decision is ReviewDecisionKind.Apply or ReviewDecisionKind.Manual);
        if (changeCount == 0)
        {
            SetStatus("沒有已確認要修改的字幕。");
            return;
        }

        var confirm = await MessageBox.Show(
            this,
            "套用 AI 字幕校閱",
            $"將提交 {changeCount} 筆人工確認的修改。" +
            (highRiskCount > 0 ? $"\n其中 {highRiskCount} 筆標示為高風險。" : string.Empty) +
            "\n只修改字幕文字，不改時間軸；可用一次 Ctrl+Z 還原。\n\n確定套用？",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            var applied = _vm.ApplySubtitleReview(_snapshot, _selection, decisions);
            SetStatus($"已套用 {applied} 列字幕；可使用 Ctrl+Z 一次還原。");
            Close();
        }
        catch (Exception ex)
        {
            SetStatus($"字幕已在校閱期間變更，未套用任何結果：{ex.Message}");
        }
    }

    private void ResetReviewStateForNewSources(string message)
    {
        _state.ResetForNewSources();
        ShowPrompt();

        SetResponseTextWithoutParsing(string.Empty);
        _responseStatusTable.ItemsSource = null;
        _glossaryCandidates.Clear();
        _glossaryCandidateList.ItemsSource = null;
        _glossaryCandidateList.ItemsSource = _glossaryCandidates;

        _changedOnly.IsChecked = false;
        _highRiskOnly.IsChecked = false;
        ApplyFilters();

        ShowSelectedRow();
        UpdateProgress();
        SetStatus(message);
    }

    private void InvalidateSession(string message)
    {
        _state.SessionInvalidated = true;
        ShowSelectedRow();
        UpdateProgress();
        SetStatus(message);
    }

    private void SetStatus(string message)
    {
        _status.Text = message;
    }

    private sealed record GlossaryUiRow(
        string Enabled,
        string Source,
        string Target,
        string Category)
    {
        public override string ToString()
        {
            var disabled = string.Equals(Enabled, "0", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(Enabled, "false", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(Enabled, "off", StringComparison.OrdinalIgnoreCase);
            var state = disabled ? "停用｜" : string.Empty;
            var category = string.IsNullOrWhiteSpace(Category) ? string.Empty : $"｜{Category}";
            return $"{state}{Source} → {Target}{category}";
        }
    }

    private sealed record GlossaryCandidateUiRow(
        string Source,
        string Target,
        string Category,
        string EvidenceIds,
        string Confidence,
        string Note)
    {
        public override string ToString()
        {
            var details = new[]
            {
                string.IsNullOrWhiteSpace(Category) ? null : Category,
                string.IsNullOrWhiteSpace(Confidence) ? null : $"信心 {Confidence}",
                string.IsNullOrWhiteSpace(EvidenceIds) ? null : $"證據 {EvidenceIds}",
            }.Where(value => value is not null);
            var suffix = string.Join("｜", details);
            return suffix.Length == 0
                ? $"{Source} → {Target}"
                : $"{Source} → {Target}｜{suffix}";
        }
    }

}
