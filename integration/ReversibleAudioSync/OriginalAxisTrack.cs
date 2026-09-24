using AudioWorkflow;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Nikse.SubtitleEdit.Logic.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Nikse.SubtitleEdit.Features.Main.Layout;

/// <summary>Display-only original-coordinate waveform. Editing is translated to SE's native selection.</summary>
public sealed class OriginalAxisTrack : Grid
{
    private readonly WaveLayer _wave=new();
    private readonly CursorLayer _cursor=new();
    private Point? _down;
    private double _position;
    private OriginalAxisMap? _selectionMap;
    private AudioEditRegion[] _regions=[];
    private AudioEditRegion? _selectedRegion;
    public event Action<double>? SeekRequested;
    public event Action<double,double>? RangeRequested;
    public event Action<AudioEditRegion?>? EditRegionRequested;
    public bool AllowSelection { get; set; }
    public double Start { get; private set; }
    public double SecondsPerPixel { get; private set; }=0.02;
    public OriginalAxisTrack()
    {
        ClipToBounds=true;Focusable=true;MinHeight=64;Background=Brushes.Transparent;Cursor=new Cursor(StandardCursorType.Cross);
        GotFocus+=(_,_)=>{_cursor.Focused=true;_cursor.InvalidateVisual();};
        LostFocus+=(_,_)=>{_cursor.Focused=false;_cursor.InvalidateVisual();};
        PointerEntered+=(_,_)=>{_cursor.Hover=true;_cursor.InvalidateVisual();};
        PointerExited+=(_,_)=>{_cursor.Hover=false;_cursor.InvalidateVisual();};
        Children.Add(_wave);Children.Add(_cursor);
        PointerPressed+=(_,e)=> { if(!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)return;Focus();_down=e.GetPosition(this);e.Pointer.Capture(this);e.Handled=true; };
        PointerMoved+=(_,e)=> { if(_down is {} p && AllowSelection) { _cursor.Selection=(p.X,e.GetPosition(this).X);_cursor.InvalidateVisual(); } };
        PointerReleased+=(_,e)=>
        {
            if(_down is not {} p)return;
            var q=e.GetPosition(this);_down=null;e.Pointer.Capture(null);
            if(AllowSelection && Math.Abs(q.X-p.X)>4)RangeRequested?.Invoke(Time(p.X),Time(q.X));
            else
            {
                _cursor.Selection=null;
                var hit=HitRegion(Time(q.X));
                _selectedRegion=hit;_cursor.SelectedRegion=hit;EditRegionRequested?.Invoke(hit);
                if(hit==null)SeekRequested?.Invoke(Time(q.X));
            }
            _cursor.InvalidateVisual();e.Handled=true;
        };
        KeyDown+=(_,e)=>
        {
            if(e.Key==Key.Escape) { _cursor.Selection=null;_selectedRegion=null;_cursor.SelectedRegion=null;EditRegionRequested?.Invoke(null);RangeRequested?.Invoke(0,0);_cursor.InvalidateVisual();e.Handled=true; }
            else if(e.Key is Key.Enter or Key.Space or Key.Left or Key.Right)
            {
                SeekRequested?.Invoke(Math.Max(0,_position+(e.Key==Key.Left?-1:e.Key==Key.Right?1:0)));e.Handled=true;
            }
        };
    }
    private AudioEditRegion? HitRegion(double seconds)
    {
        var tolerance=Math.Max(SecondsPerPixel*4,.015);
        return _regions.Where(r=>seconds>=r.StartSeconds-tolerance && seconds<=r.EndSeconds+tolerance)
            .OrderBy(r=>r.EndSeconds-r.StartSeconds).FirstOrDefault();
    }
    private double Time(double x)=>Math.Max(0,Start+Math.Clamp(x,0,Bounds.Width)*SecondsPerPixel);
    public void SetView(WavePeakData2? peaks,OriginalAxisMap? map,bool edited,double start,double secondsPerPixel,
        IReadOnlyList<AudioEditRegion>? regions=null)
    {
        if(!ReferenceEquals(_selectionMap,map)||Start!=start||SecondsPerPixel!=secondsPerPixel){_cursor.Selection=null;_cursor.InvalidateVisual();}
        var next=regions?.ToArray()??[];
        if(!next.SequenceEqual(_regions)){_regions=next;_wave.Regions=next;_wave.InvalidateView();}
        if(_selectedRegion!=null && !_regions.Contains(_selectedRegion))
        {
            _selectedRegion=null;_cursor.SelectedRegion=null;
        }
        _selectionMap=map;Start=start;SecondsPerPixel=secondsPerPixel;
        _wave.Update(peaks,map,edited,start,secondsPerPixel);
        _cursor.Start=start;_cursor.Step=secondsPerPixel;
    }
    public void SetCursor(double seconds,bool active)
    {
        _position=seconds;var x=(seconds-Start)/SecondsPerPixel;
        // Keep the overlay in phase with SE's native ~60 fps playhead. At zoomed-out scales a
        // frame can be only 0.16 px, so the old 0.75 px gate visibly reduced motion to ~12 fps.
        if(Math.Abs(x-_cursor.X)<0.1 && _cursor.Active==active)return;
        _cursor.X=x;_cursor.Active=active;_cursor.InvalidateVisual();
    }
    private sealed class CursorLayer : Control
    {
        public double X=-1,Start,Step=.02;public bool Active,Focused,Hover;public (double A,double B)? Selection;
        public AudioEditRegion? SelectedRegion;
        public CursorLayer(){IsHitTestVisible=false;}
        public override void Render(DrawingContext c)
        {
            if(Focused||Hover)c.DrawRectangle(null,new Pen(Focused?Brushes.LightSkyBlue:Brushes.Gray,Focused?2:1),new Rect(Bounds.Size).Deflate(1));
            if(Selection is {} s)c.DrawRectangle(new SolidColorBrush(Color.FromArgb(65,120,180,230)),null,new Rect(Math.Min(s.A,s.B),24,Math.Abs(s.B-s.A),Math.Max(0,Bounds.Height-24)));
            if(SelectedRegion is {} region && Step>0)
            {
                var x=(region.StartSeconds-Start)/Step;var width=(region.EndSeconds-region.StartSeconds)/Step;
                c.DrawRectangle(null,new Pen(Brushes.White,2),new Rect(x,24,Math.Max(2,width),Math.Max(0,Bounds.Height-24)).Intersect(new Rect(Bounds.Size)));
            }
            if(X>=0 && X<=Bounds.Width)c.DrawLine(new Pen(Active?Brushes.Orange:Brushes.LightGray,Active?2:1),new Point(X,0),new Point(X,Bounds.Height));
        }
    }
    private sealed class WaveLayer : Control
    {
        private WavePeakData2? _peaks;private OriginalAxisMap? _map;private bool _edited;private double _start,_step;
        private DrawingGroup? _drawing;private Size _size;
        public IReadOnlyList<AudioEditRegion> Regions=[];
        public WaveLayer(){IsHitTestVisible=false;}
        public void InvalidateView(){_drawing=null;InvalidateVisual();}
        public void Update(WavePeakData2? peaks,OriginalAxisMap? map,bool edited,double start,double step)
        {
            if(ReferenceEquals(peaks,_peaks)&&ReferenceEquals(map,_map)&&edited==_edited&&start==_start&&step==_step)return;
            _peaks=peaks;_map=map;_edited=edited;_start=start;_step=step;_drawing=null;InvalidateVisual();
        }
        public override void Render(DrawingContext context)
        {
            if(_drawing==null || _size!=Bounds.Size)
            {
                _size=Bounds.Size;_drawing=new DrawingGroup();
                using var c=_drawing.Open();
                c.DrawRectangle(Brush.Parse("#20252B"),null,new Rect(Bounds.Size));
                var h=Math.Max(0,Bounds.Height-24);var middle=24+h/2;
                foreach(var region in Regions)
                {
                    var x=(region.StartSeconds-_start)/_step;
                    var width=(region.EndSeconds-region.StartSeconds)/_step;
                    var rect=new Rect(x,24,Math.Max(2,width),h).Intersect(new Rect(Bounds.Size));
                    if(rect.Width<=0||rect.Height<=0)continue;
                    var fill=region.Kind switch
                    {
                        AudioEditRegionKind.Deleted=>new SolidColorBrush(Color.FromArgb(92,244,164,96)),
                        AudioEditRegionKind.AuditionRepair=>new SolidColorBrush(Color.FromArgb(92,190,120,255)),
                        _=>new SolidColorBrush(Color.FromArgb(108,255,215,64)),
                    };
                    c.DrawRectangle(fill,null,rect);
                    var line=region.Kind==AudioEditRegionKind.Deleted?Brushes.SandyBrown:
                        region.Kind==AudioEditRegionKind.AuditionRepair?Brushes.MediumPurple:Brushes.Gold;
                    for(var stripe=rect.X-rect.Height;stripe<rect.Right;stripe+=8)
                        c.DrawLine(new Pen(line,1),new Point(stripe,rect.Bottom),new Point(stripe+rect.Height,rect.Y));
                }
                var pen=new Pen(_edited?Brushes.LightGreen:Brushes.LightSkyBlue,1);
                if(_peaks!=null && _step>0)
                {
                    var samples=_peaks.Peaks;
                    for(int x=0;x<(int)Bounds.Width;x++)
                    {
                        var seconds=_start+x*_step;bool removed=false;
                        var source=_edited&&_map!=null?_map.ToEdited(seconds,out removed):seconds;
                        if(removed)
                        {
                            if(x%6<2)c.DrawLine(new Pen(Brushes.SandyBrown,1),new Point(x,24),new Point(x,Bounds.Height));
                            continue;
                        }
                        int index=(int)Math.Floor(source*_peaks.SampleRate);
                        if(index<0 || index>=samples.Count)continue;
                        int max=0,min=0;
                        // Bounded work at each screen pixel, independent of media duration.
                        int count=Math.Clamp((int)Math.Ceiling(_step*_peaks.SampleRate),1,8);
                        for(int j=0;j<count && index+j<samples.Count;j++){max=Math.Max(max,samples[index+j].Max);min=Math.Min(min,samples[index+j].Min);}
                        var scale=h/2/Math.Max(1,_peaks.HighestPeak);
                        c.DrawLine(pen,new Point(x,middle-max*scale),new Point(x,middle-min*scale));
                    }
                }
                var interval=Math.Max(1,Math.Pow(10,Math.Floor(Math.Log10(Math.Max(.001,_step*110)))));
                while(interval/_step<85)interval*=2;
                for(double t=Math.Ceiling(_start/interval)*interval;t<_start+Bounds.Width*_step;t+=interval)
                {
                    var x=(t-_start)/_step;
                    c.DrawLine(new Pen(Brushes.DimGray,1),new Point(x,22),new Point(x,Bounds.Height));
                    c.DrawText(new FormattedText(TimeSpan.FromSeconds(t).ToString(t>=3600?@"hh\:mm\:ss":@"mm\:ss"),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,Typeface.Default,12,Brushes.Gainsboro),new Point(x+3,3));
                }
                if(_peaks==null)c.DrawText(new FormattedText("波形準備中／請載入音訊",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface.Default,12,Brushes.Gainsboro),new Point(12,30));
            }
            _drawing.Draw(context);
        }
    }
}
