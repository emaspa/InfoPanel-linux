using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using System;

namespace InfoPanel.Views.Controls
{
    public class SkiaCanvas : Control
    {
        public Action<SKCanvas, int, int>? RenderAction { get; set; }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (RenderAction == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
            context.Custom(new SkiaDrawOp(new Rect(0, 0, Bounds.Width, Bounds.Height), RenderAction));
        }

        private class SkiaDrawOp : ICustomDrawOperation
        {
            private readonly Rect _bounds;
            private readonly Action<SKCanvas, int, int> _renderAction;

            public SkiaDrawOp(Rect bounds, Action<SKCanvas, int, int> renderAction)
            {
                _bounds = bounds;
                _renderAction = renderAction;
            }

            public Rect Bounds => _bounds;

            public bool HitTest(Point p) => _bounds.Contains(p);

            public bool Equals(ICustomDrawOperation? other) => false;

            public void Render(ImmediateDrawingContext context)
            {
                var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (leaseFeature == null) return;

                using var lease = leaseFeature.Lease();
                _renderAction(lease.SkCanvas, (int)_bounds.Width, (int)_bounds.Height);
            }

            public void Dispose() { }
        }
    }
}
