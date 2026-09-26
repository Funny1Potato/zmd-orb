using System;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>
/// 圆环中心的粒子团（290px）。数值照搬 zmd-manager 的 drawBlob()：
/// 650 个斐波那契球面点、半径 R = 126×(0.79 + 0.12×呼吸 + 0.09×三层正弦噪声)、
/// 点半径 0.7+depth×1.2、透明度 0.10+depth×0.40、颜色 rgb(96,96,92)。
/// 参考按每帧 +0.016 推进（≈0.96/秒），这里按真实时间推进同样速率。
/// </summary>
sealed class BlobVisual : FrameworkElement
{
    struct P { public double X, Y, Z, Ph; }

    const int Particles = 650;
    const int Buckets = 20;
    const double RBase = 126;

    readonly P[] _pts = new P[Particles];
    readonly Brush[] _brushes = new Brush[Buckets + 1];
    double _t;

    public BlobVisual()
    {
        var rnd = new Random();
        for (int i = 0; i < Particles; i++)
        {
            double y = 1 - i / (double)(Particles - 1) * 2;
            double r = Math.Sqrt(Math.Max(0, 1 - y * y));
            double th = i * 2.39996;
            _pts[i] = new P { X = Math.Cos(th) * r, Y = y, Z = Math.Sin(th) * r,
                              Ph = rnd.NextDouble() * Math.PI * 2 };
        }
        _t = rnd.NextDouble() * 100;
        for (int b = 0; b <= Buckets; b++)
        {
            var br = new SolidColorBrush(Color.FromRgb(96, 96, 92))
            { Opacity = 0.10 + 0.40 * b / Buckets };
            br.Freeze();
            _brushes[b] = br;
        }
        IsHitTestVisible = false;
    }

    public void Advance(double dt)
    {
        _t += dt * 0.96;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = ActualWidth > 1 ? ActualWidth : 290;
        double c = s / 2;
        double breath = 0.5 + 0.5 * Math.Sin(_t * 0.9);
        double rot = _t * 0.12, cs = Math.Cos(rot), sn = Math.Sin(rot);
        for (int i = 0; i < _pts.Length; i++)
        {
            var p = _pts[i];
            double n = Math.Sin(3.1 * p.X + _t * 0.7 + p.Ph)
                     * Math.Sin(2.7 * p.Y - _t * 0.5)
                     * Math.Sin(2.3 * p.Z + _t * 0.6);
            double r = RBase * (0.79 + 0.12 * breath + 0.09 * n);
            double x = p.X * cs + p.Z * sn;
            double z = -p.X * sn + p.Z * cs;
            double depth = (z + 1) / 2;
            int b = (int)Math.Round(Math.Max(0, Math.Min(1, depth)) * Buckets);
            double rad = 0.7 + depth * 1.2;
            dc.DrawEllipse(_brushes[b], null, new Point(c + x * r, c + p.Y * r), rad, rad);
        }
    }
}