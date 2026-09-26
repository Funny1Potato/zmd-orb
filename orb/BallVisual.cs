using System;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>
/// 电量环 + 中心粒子团。几何、配色、动画参数全部对应 frontend/ring.js（同一套 zmd 口径）。
/// 不参与命中测试：球的可点区域由外层 Grid 统一处理。
/// </summary>
sealed class BallVisual : FrameworkElement
{
    struct P { public double X, Y, Z; }

    const int Buckets = 20;                  // 粒子透明度分档：避免每帧新建 120 个画刷

    readonly P[] _pts = new P[Ring.Particles];
    readonly double[] _phase = new double[Ring.Particles];
    readonly Brush[] _blobBrush = new Brush[Buckets + 1];

    readonly Geometry _decoLeftUnderG, _decoLeftG, _decoRightUnderG, _decoRightG, _glowClip;
    readonly Pen _decoUnder, _decoLeftPen, _decoRightPen, _railPen, _trackPen;
    readonly Pen[] _arcPen = new Pen[3], _glowNear = new Pen[3], _glowMid = new Pen[3], _glowFar = new Pen[3];
    readonly Brush _discBrush;

    Geometry? _arc;
    double _arcPct = double.NaN;
    double _pct;
    double _t, _squish = 1, _amp = 1;

    public BallVisual()
    {
        var rnd = new Random();
        for (int i = 0; i < Ring.Particles; i++)      // 斐波那契球面均匀撒点
        {
            double y = 1 - i / (double)(Ring.Particles - 1) * 2;
            double r = Math.Sqrt(Math.Max(0, 1 - y * y));
            double th = i * 2.39996;
            _pts[i] = new P { X = Math.Cos(th) * r, Y = y, Z = Math.Sin(th) * r };
            _phase[i] = rnd.NextDouble() * Math.PI * 2;
        }
        _t = rnd.NextDouble() * 100;

        for (int b = 0; b <= Buckets; b++)
        {
            var br = new SolidColorBrush(Ring.BlobRgb) { Opacity = 0.10 + 0.40 * b / Buckets };
            br.Freeze();
            _blobBrush[b] = br;
        }

        /* 描边沿用 ring.js 的做法：在底色下面再画一道更粗的同形描边。
           装饰弧两端是平头，所以描边得朝两端各外延 capExt（＝描边厚度折算成的角度），
           端面才会被平直地描上而不是靠圆头包住。 */
        double ow = Ring.OutlineW;
        double capExt = ow * 180 / (Math.PI * Ring.RDeco);
        _decoLeftUnderG = Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, 270 - capExt, 360 + capExt);
        _decoLeftG = Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, 270, 360);
        _decoRightUnderG = Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, 90 - capExt, 180 + capExt);
        _decoRightG = Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, 90, 180);

        _decoUnder = Pen(Ring.Outline, Ring.WDeco + 2 * ow);
        _decoLeftPen = Pen(Ring.DecoLeft, Ring.WDeco, 0.95);
        _decoRightPen = Pen(Ring.DecoRight, Ring.WDeco, 0.95);
        _railPen = Pen(Ring.Outline, Ring.WTrack + 2 * ow);   // 衬底 = 轨道宽 + 两侧描边，形成均匀一圈描边
        _trackPen = Pen(Ring.Track, Ring.WTrack);
        _discBrush = Freeze(new SolidColorBrush(Ring.Disc));

        var colors = new[] { Ring.ArcLow, Ring.ArcMid, Ring.ArcHigh };
        for (int i = 0; i < 3; i++)
        {
            _arcPen[i] = Pen(colors[i], Ring.WTrack, 1.0, round: true);
            // 原 SVG 是 drop-shadow(0 0 7px rgba(255,224,70,.5))：三层递减宽度的半透明描边近似这圈辉光
            _glowNear[i] = Pen(colors[i], Ring.WTrack + 4, 0.26, round: true);
            _glowMid[i] = Pen(colors[i], Ring.WTrack + 8, 0.13, round: true);
            _glowFar[i] = Pen(colors[i], Ring.WTrack + 12, 0.06, round: true);
        }

        /* 辉光要裁掉主环内缘以内的部分：最外层描边是 52±9.25，会糊到内盘(43)与主环之间
           那道透明缝上（实测在红底上留下 7% 的黄）。裁到主环内缘(=不透明衬底的起点)，
           剪影正好落在衬底边缘上，看不出硬边。 */
        var clip = new GeometryGroup { FillRule = FillRule.EvenOdd };
        clip.Children.Add(new RectangleGeometry(new Rect(0, 0, Ring.Size, Ring.Size)));
        clip.Children.Add(new EllipseGeometry(new Point(Ring.Cx, Ring.Cy), 46.9, 46.9));
        clip.Freeze();
        _glowClip = clip;

        IsHitTestVisible = false;
    }

    /// <summary>环的进度（0~100）。</summary>
    public double Pct
    {
        get => _pct;
        set
        {
            double v = value;
            if (Math.Abs(v - _pct) < 0.0001) return;
            _pct = v;
            InvalidateVisual();
        }
    }

    /// <summary>累计绘制帧数（开销观测用）。</summary>
    public long Frames { get; private set; }

    /// <summary>整理动画：粒子团被"吸"进中心再回弹（1 = 常态）。</summary>
    public void SetBreathe(double squish, double amp)
    {
        _squish = squish;
        _amp = amp;
    }

    /// <summary>推进一帧。</summary>
    public void Advance(double dt)
    {
        _t += dt;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        Frames++;

        dc.DrawGeometry(null, _decoUnder, _decoLeftUnderG);
        dc.DrawGeometry(null, _decoLeftPen, _decoLeftG);
        dc.DrawGeometry(null, _decoUnder, _decoRightUnderG);
        dc.DrawGeometry(null, _decoRightPen, _decoRightG);

        var c = new Point(Ring.Cx, Ring.Cy);
        dc.DrawEllipse(null, _railPen, c, Ring.R, Ring.R);
        dc.DrawEllipse(null, _trackPen, c, Ring.R, Ring.R);
        dc.DrawEllipse(_discBrush, null, c, Ring.RDisc, Ring.RDisc);

        EnsureArc();
        if (_arc != null)
        {
            int ci = _arcPct < 70 ? 0 : _arcPct < 88 ? 1 : 2;
            dc.PushClip(_glowClip);
            dc.DrawGeometry(null, _glowFar[ci], _arc);
            dc.DrawGeometry(null, _glowMid[ci], _arc);
            dc.DrawGeometry(null, _glowNear[ci], _arc);
            dc.Pop();
            dc.DrawGeometry(null, _arcPen[ci], _arc);
        }

        DrawBlob(dc);
    }

    /// <summary>扫到极值时留 0.5° 以免弧退化；进度几乎没变就不重建几何。</summary>
    void EnsureArc()
    {
        double p = Math.Max(0, Math.Min(100, _pct));
        if (!double.IsNaN(_arcPct) && Math.Abs(p - _arcPct) < 0.05) return;
        _arcPct = p;
        _arc = Ring.Arc(Ring.Cx, Ring.Cy, Ring.R, 0, Math.Max(0.5, Math.Min(359.5, p * 3.6)));
    }

    void DrawBlob(DrawingContext dc)
    {
        double breath = 0.5 + 0.5 * Math.Sin(_t * 0.9);
        double rot = _t * 0.12, cs = Math.Cos(rot), sn = Math.Sin(rot);
        for (int i = 0; i < _pts.Length; i++)
        {
            var p = _pts[i];
            double n = Math.Sin(3.1 * p.X + _t * 0.7 + _phase[i])
                     * Math.Sin(2.7 * p.Y - _t * 0.5)
                     * Math.Sin(2.3 * p.Z + _t * 0.6);
            double r = Ring.RDisc * (0.79 + 0.12 * breath * _amp + 0.09 * n) * _squish;
            double x = p.X * cs + p.Z * sn;
            double z = -p.X * sn + p.Z * cs;
            double depth = (z + 1) / 2;
            int b = (int)Math.Round(depth * Buckets);
            double rad = 0.6 + depth;
            dc.DrawEllipse(_blobBrush[b], null, new Point(Ring.Cx + x * r, Ring.Cy + p.Y * r), rad, rad);
        }
    }

    static Pen Pen(Color c, double w, double opacity = 1.0, bool round = false)
    {
        var brush = new SolidColorBrush(c) { Opacity = opacity };
        var pen = new Pen(brush, w);
        if (round) pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
        pen.Freeze();
        return pen;
    }

    static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}