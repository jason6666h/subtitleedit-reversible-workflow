using AudioWorkflow;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Media;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main.Layout;

public sealed class DualTimelinePanel : Grid
{
    private readonly MainViewModel _vm;
    private readonly OriginalAxisTrack _original=new(),_edited=new();
    private readonly Grid _originalGroup;
    private readonly TextBlock _status=new(){FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis};
    private readonly TextBlock _playing=new(){FontSize=12,VerticalAlignment=VerticalAlignment.Center};
    private readonly Grid _tracks=new(){RowDefinitions=new RowDefinitions("0,0,*"),ClipToBounds=true};
    private readonly CheckBox _compare=new(){Content="顯示原軌比對",Margin=new Thickness(4),MinHeight=32};
    private readonly DispatcherTimer _timer;
    private WavePeakData2? _other;
    private string? _key;
    private bool _loading;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _comparisonLoad;
    private DateTime _nextMetadata;
    private Control? _positionSource;
    private double _start,_step=.02;
    private double _lastPosition=-1;
    private bool? _lastEnabled,_lastReadOnly;
    public event Action? CloseRequested;
    internal void ShowOriginalComparison()=>_compare.IsChecked=true;
    public DualTimelinePanel(MainViewModel vm)
    {
        _vm=vm;RowDefinitions=new RowDefinitions("Auto,*,Auto");ClipToBounds=true;
        _originalGroup=TrackGroup("原時間軸 · 唯讀聽校",_original,Brushes.LightSkyBlue);
        var editedGroup=TrackGroup("修改後時間軸 · 橘＝已刪除　紫＝AU 修音　黃＝待接回",_edited,Brushes.LightGreen);
        Grid.SetRow(editedGroup,2);_tracks.Children.Add(_originalGroup);_tracks.Children.Add(editedGroup);
        var splitter=new GridSplitter{Height=8,ResizeDirection=GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext,HorizontalAlignment=HorizontalAlignment.Stretch,Background=Brushes.DimGray};
        Grid.SetRow(splitter,1);_tracks.Children.Add(splitter);
        var zoomIn=Button("放大",()=>{_step=Math.Max(.001,_step/1.5);});
        var zoomOut=Button("縮小",()=>{_step=Math.Min(60,_step*1.5);});
        var left=Button("向前",()=>_start=Math.Max(0,_start-ViewSeconds()/2));
        var right=Button("向後",()=>_start+=ViewSeconds()/2);
        var reload=Button("重載波形",()=>{if(!_loading)_key=null;if(vm.AudioVisualizer?.WavePeaks==null)vm.AudioVisualizerOnGenerateWaveformRequested(null,EventArgs.Empty);});
        var compact=new CheckBox{Content="緊湊高度",MinHeight=32,Margin=new Thickness(4)};
        compact.IsCheckedChanged+=(_,_)=>{_tracks.MaxHeight=compact.IsChecked==true?160:double.PositiveInfinity;};
        var close=Button("收起比對",()=>CloseRequested?.Invoke());
        var toolbar=new WrapPanel{Orientation=Orientation.Horizontal,Children={close,_compare,zoomIn,zoomOut,left,right,compact,reload,_playing}};
        Children.Add(toolbar);Grid.SetRow(_tracks,1);Children.Add(_tracks);
        Grid.SetRow(_status,2);Children.Add(_status);
        _status.Text="單擊任一橘／紫／黃色塊後按 Delete，可移除該標記並恢復波形；拖曳空白處則選取新範圍。";
        _compare.IsCheckedChanged+=(_,_)=>ApplyVisibility();ApplyVisibility();
        _original.SeekRequested+=async t=>await PlayAsync(true,t);
        _edited.SeekRequested+=async t=>await PlayAsync(false,t);
        _edited.RangeRequested+=(a,b)=>vm.SelectOriginalAxisRange(a,b);
        _edited.EditRegionRequested+=vm.SelectAudioEditRegion;
        // Playback position is driven by SE's native AudioVisualizer property event. This timer
        // is deliberately slow and only maintains metadata, read-only state and cached wave views.
        _timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(250)};
        _timer.Tick+=(_,_)=>Tick();
        AttachedToVisualTree+=(_,_)=>
        {
            _lifetime=new();ObservePositionSource();_timer.Start();
            // Run after the first layout so ViewSeconds uses the real track width. This makes the
            // current position visible before Play is pressed instead of waiting for mpv to move.
            Dispatcher.UIThread.Post(()=>{if(_lifetime!=null){_lastPosition=-1;Tick();}},DispatcherPriority.Loaded);
        };
        DetachedFromVisualTree+=(_,_)=>{_timer.Stop();StopObservingPosition();CancelComparisonLoad();_lifetime?.Cancel();_lifetime?.Dispose();_lifetime=null;};
    }
    private static Button Button(string text,Action action)
    {
        var b=new Button{Content=text,MinHeight=32,Margin=new Thickness(4)};b.Click+=(_,_)=>action();return b;
    }
    private static Grid TrackGroup(string title,Control track,IBrush color)
    {
        var g=new Grid{RowDefinitions=new RowDefinitions("Auto,*"),Background=Brush.Parse("#20252B"),ClipToBounds=true};
        g.Children.Add(new TextBlock{Text=title,Foreground=color,FontSize=12,Margin=new Thickness(8,4)});
        Grid.SetRow(track,1);g.Children.Add(track);return g;
    }
    private void ApplyVisibility()
    {
        bool show=_compare.IsChecked==true;
        _originalGroup.IsVisible=show;
        _tracks.RowDefinitions[0].Height=show?new GridLength(1,GridUnitType.Star):new GridLength(0);
        _tracks.RowDefinitions[1].Height=new GridLength(show?8:0);
        _tracks.RowDefinitions[2].Height=new GridLength(1,GridUnitType.Star);
    }
    private double ViewSeconds()=>Math.Max(1,_edited.Bounds.Width)*_step;
    private async Task PlayAsync(bool original,double t)
    {
        if(_vm.IsAudioTimelineBusy) { _status.Text="音訊處理中，請稍候再切換。";return; }
        _status.Text="正在切換聽校音軌…";
        try { await _vm.PlayOriginalAxisAsync(original,t);_status.Text="單擊空白處播放；點色塊後按 Delete 取消最近操作；Esc 清除。"; }
        catch(Exception e){_status.Text=e.Message;}
    }
    private void ObservePositionSource()
    {
        var source=_vm.AudioVisualizer;
        if(ReferenceEquals(source,_positionSource))return;
        StopObservingPosition();
        _positionSource=source;
        if(_positionSource!=null)_positionSource.PropertyChanged+=PositionSourceChanged;
    }
    private void StopObservingPosition()
    {
        if(_positionSource!=null)_positionSource.PropertyChanged-=PositionSourceChanged;
        _positionSource=null;
    }
    private void PositionSourceChanged(object? sender,AvaloniaPropertyChangedEventArgs e)
    {
        if(e.Property!=Nikse.SubtitleEdit.Controls.AudioVisualizerControl.AudioVisualizer.CurrentVideoPositionSecondsProperty ||
           _lifetime==null || _vm.IsAudioTimelineBusy)return;
        UpdatePlaybackCursor(_vm.AudioVisualizer?.CurrentVideoPositionSeconds??0);
    }
    private void UpdatePlaybackCursor(double position)
    {
        var map=_vm.DisplayAxisMap;bool original=_vm.IsOriginalAudioTimeline;
        var sourcePosition=original?position:map?.ToOriginal(position)??position;
        var firstPosition=_lastPosition<0;
        var moving=!firstPosition && Math.Abs(sourcePosition-_lastPosition)>.002;
        if(moving && _loading)CancelComparisonLoad();
        bool movedView=false;
        if((firstPosition||moving) && (sourcePosition<_start || sourcePosition>_start+ViewSeconds()*.92))
        {
            _start=Math.Max(0,sourcePosition-ViewSeconds()*.1);movedView=true;
        }
        _lastPosition=sourcePosition;
        if(movedView)RefreshTrackViews();
        if(_compare.IsChecked==true)_original.SetCursor(sourcePosition,original);
        _edited.SetCursor(sourcePosition,!original);
    }
    private void RefreshTrackViews()
    {
        var visualizer=_vm.AudioVisualizer;var map=_vm.DisplayAxisMap;bool original=_vm.IsOriginalAudioTimeline;
        var live=visualizer?.WavePeaks;
        if(_compare.IsChecked==true)_original.SetView(original?live:_other,null,false,_start,_step);
        _edited.SetView(original?_other:live,map,true,_start,_step,_vm.DisplayAudioEditRegions);
        _edited.AllowSelection=!original&&!_vm.IsAudioTimelineBusy;
    }
    private void Tick()
    {
        if(_lifetime==null)return;
        try
        {
            ObservePositionSource();
            var enabled=!_vm.IsAudioTimelineBusy;
            if(_lastEnabled!=enabled){_lastEnabled=enabled;_original.IsEnabled=_edited.IsEnabled=enabled;}
            if(!enabled)return;
            var visualizer=_vm.AudioVisualizer;bool original=_vm.IsOriginalAudioTimeline;
            var readOnly=original||Se.Settings.General.LockTimeCodes;
            if(visualizer!=null && _lastReadOnly!=readOnly){_lastReadOnly=readOnly;visualizer.IsReadOnly=readOnly;}
            RefreshTrackViews();
            UpdatePlaybackCursor(visualizer?.CurrentVideoPositionSeconds??0);
            if(DateTime.UtcNow<_nextMetadata)return;
            _nextMetadata=DateTime.UtcNow.AddMilliseconds(250);
            var player=_vm.GetVideoPlayerControl();
            var text=$"目前：{(original?"原軌":"修改軌")}　{player?.VideoPlayer.Speed??1:0.##}×";
            if(_playing.Text!=text)_playing.Text=text;
            if(player?.IsPlaying==true)
            {
                CancelComparisonLoad();
                if(_other==null && (_compare.IsChecked==true || original))_status.Text="播放中優先保持流暢；暫停後自動準備另一軌波形。";
            }
            else if(_compare.IsChecked==true || original)_ = LoadOtherAsync(_lifetime.Token);
        }
        catch(Exception e){_status.Text="時間軸："+e.Message;}
    }
    private void CancelComparisonLoad()
    {
        try{_comparisonLoad?.Cancel();}catch(ObjectDisposedException){}
    }
    private async Task LoadOtherAsync(CancellationToken token)
    {
        if(_loading)return;
        _loading=true;
        using var load=CancellationTokenSource.CreateLinkedTokenSource(token);
        _comparisonLoad=load;
        try
        {
            token=load.Token;
            var directory=_vm.ComparisonRevisionDirectory();
            if(directory==null){_other=null;_key=null;_status.Text="請先建立／開啟可逆專案；舊版未保存的原始內容無法補回。";return;}
            if(_key==directory)return;
            _key=directory;_other=null;
            var snapshot=await _vm.ReadComparisonRevisionAsync(directory,token);
            var peaks=await Task.Run(async()=>
            {
                using var guard=await _vm.OpenCachedAudioReadGuardAsync(snapshot.Path,snapshot.Hash,token);
                var cache=Path.Combine(Se.WaveformsFolder,"comparison-"+snapshot.Hash);
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                if(File.Exists(cache+".peaks.wav"))return WavePeakData2.FromDisk(cache+".peaks.wav");
                // Normalize every comparison source to a small mono preview. Feeding a multi-hour
                // 48 kHz/24-bit WAV directly to the peak generator caused CPU and disk contention.
                var source=cache+"-"+Guid.NewGuid().ToString("N")+".wav";
                try
                {
                    await FfmpegService.RunAsync(ToolPathResolver.Resolve(null,"ffmpeg",AppContext.BaseDirectory),["-nostdin","-v","error","-n","-i",snapshot.Path,"-map","0:a:0","-vn","-ac","1","-ar","8000","-c:a","pcm_s16le",source],token);
                    using var generator=new WavePeakGenerator2(source);
                    if(!generator.IsSupported)throw new InvalidDataException("原軌波形格式不支援。");
                    token.ThrowIfCancellationRequested();return generator.GeneratePeaks(0,cache+".peaks.wav");
                }
                finally{try{File.Delete(source);}catch(IOException){}catch(UnauthorizedAccessException){}}
            },token);
            if(!token.IsCancellationRequested && _vm.ComparisonRevisionDirectory()==directory){_other=peaks;_status.Text="兩軌以原始時間對齊；點選任一橘／紫／黃色塊後按 Delete，可移除該標記並恢復波形。";}
        }
        catch(OperationCanceledException){_key=null;}
        catch(Exception e){_status.Text="比對載入失敗，可按重載比對："+e.Message;}
        finally{if(ReferenceEquals(_comparisonLoad,load))_comparisonLoad=null;_loading=false;}
    }
}
