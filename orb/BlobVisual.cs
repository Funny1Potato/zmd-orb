using System;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>
/// 面板首页中心的粒子团：一批均匀撒在小球面上的点，半径随"呼吸 + 三层正弦噪声"起伏，
/// 绕竖轴缓慢自转，深浅按朝向分档上色。
///
/// 撒点用 Halton 低差异序列（2 / 3 进制）反变换到球面 —— 均匀，而且不会像等分角的螺旋那样
/// 留出条纹感。尺寸由 RBase 与那几个系数定（126 × 0.79 是常态半径），要动先量一眼。
/// 画刷按透明度预建若干档，比每点新建一支省得多（这是面板里唯一每帧都重画的东西）。
/// </summary>
sealed class BlobVisual : FrameworkElement
{
    struct P { public double X, Y, Z, Ph; }

    const int Particles = 650;
    const int Buckets = 20;        // 透明度分档数
    const double RBase = 126;      // 常态半径的基准
    const double Rate = 0.96;      // 呼吸与自转的推进速率（约 0.96/秒）

    readonly P[] _pts = new P[Particles];
    readonly Brush[] _brushes = new Brush[Buckets + 1];
    double _t;

    public BlobVisual()
    {
        var rnd = new Random();
        for (int i = 0; i < Particles; i++)
        {
            // Halton(2,3) → 球面：u 定纬度、v 定经度
            double u = Ring.Halton(i + 1, 2), v = Ring.Halton(i + 1, 3);
            double y = 1 - 2 * u;
            double ring = Math.Sqrt(Math.Max(0, 1 - y * y));
            double phi = 2 * Math.PI * v;
            _pts[i] = new P
            {
                X = ring * Math.Cos(phi), Y = y, Z = ring * Math.Sin(phi),
                Ph = rnd.NextDouble() * Math.PI * 2,      // 每个点的噪声初相
            };
        }
        _t = rnd.NextDouble() * 100;                      // 起始相位随机，免得每次开面板都一样
        for (int b = 0; b <= Buckets; b++)
        {
            var br = new SolidColorBrush(Ring.BlobRgb) { Opacity = 0.10 + 0.40 * b / Buckets };
            br.Freeze();
            _brushes[b] = br;
        }
        IsHitTestVisible = false;
    }

    public void Advance(double dt)
    {
        _t += dt * Rate;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (UiSettings.Lite) return;      // 轻量模式：面板也不画粒子团（环、数字、列表照旧）
        double side = ActualWidth > 1 ? ActualWidth : 290;
        double mid = side / 2;
        double inflate = 0.79 + 0.12 * (0.5 + 0.5 * Math.Sin(_t * 0.9));   // 呼吸
        double spin = _t * 0.12, cos = Math.Cos(spin), sin = Math.Sin(spin);
        foreach (var p in _pts)
        {
            double r = RBase * inflate + RBase * 0.09 * Puff(in p);
            double x = p.X * cos + p.Z * sin;                 // 绕竖轴自转
            double depth = (p.Z * cos - p.X * sin + 1) / 2;   // 0 = 背面，1 = 正面
            double rad = 0.7 + depth * 1.2;
            dc.DrawEllipse(_brushes[Bucket(depth)], null,
                           new Point(mid + x * r, mid + p.Y * r), rad, rad);
        }
    }

    /// <summary>三层正弦相乘当噪声：让每个点的半径无规则地起伏（纯随机会让球面看着"毛刺"）。</summary>
    double Puff(in P p) => Math.Sin(3.1 * p.X + _t * 0.7 + p.Ph)
                         * Math.Sin(2.7 * p.Y - _t * 0.5)
                         * Math.Sin(2.3 * p.Z + _t * 0.6);

    static int Bucket(double depth) =>
        (int)Math.Round(Math.Max(0, Math.Min(1, depth)) * Buckets);
}