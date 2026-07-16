using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using InfoPanel.Drawing;
using InfoPanel.Models;
using SkiaSharp;

namespace InfoPanel.Designer
{
    /// <summary>
    /// Direct-manipulation designer surface: renders the live profile through the
    /// Skia lease under a zoom/pan viewport, draws selection adorners in view space,
    /// and drives a small interaction state machine (select/move/resize/marquee/pan).
    /// </summary>
    public sealed class DesignCanvas : Control
    {
        private enum InteractionState { Idle, MovingItems, ResizingItem, Marquee, Panning }

        private DesignerSession? _session;
        private DispatcherTimer? _renderTimer;

        // Viewport (world = profile pixels; view = control logical pixels)
        private float _zoom = 1f;
        private SKPoint _pan = new(40, 40);

        // Interaction
        private InteractionState _state = InteractionState.Idle;
        private SKPoint _pressWorld;
        private Point _pressView;
        private SKPoint _lastWorld;
        private Point _lastView;
        private SKRect _marqueeWorld;
        private int _resizeHandle = -1;
        private DisplayItem? _resizeItem;
        private bool _spaceDown;
        private bool _fitPending;

        private const float MinZoom = 0.1f;
        private const float MaxZoom = 8f;
        private const double HandleSize = 8;
        private static readonly SKColor WorkspaceColor = new(0x1A, 0x1A, 0x1A);
        private static readonly SKColor AccentColor = new(0x14, 0xB8, 0xA6);

        public DesignCanvas()
        {
            Focusable = true;
            ClipToBounds = true;
        }

        public DesignerSession? Session
        {
            get => _session;
            set
            {
                _session = value;
                if (value != null)
                {
                    // keep re-fitting through layout passes until the user takes over —
                    // the first fit often runs before splitters settle the final bounds
                    _fitPending = true;
                    ZoomToFit();
                }

                InvalidateVisual();
            }
        }

        public float Zoom => _zoom;
        public event EventHandler? ZoomChanged;

        /// <summary>Raised whenever pan, zoom, or control size changes — hosts sync scrollbars from this.</summary>
        public event EventHandler? ViewportChanged;

        public SKPoint Pan
        {
            get => _pan;
            set
            {
                _pan = value;
                _fitPending = false;
                ViewportChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }
        }

        public bool SnapToGrid { get; set; } = true;
        public int GridSpacing { get; set; } = 20;

        // ---- lifecycle ----

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _renderTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(1000.0 / RenderContext.TargetFrameRate, 15))
            };
            _renderTimer.Tick += (_, _) => InvalidateVisual();
            _renderTimer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _renderTimer?.Stop();
            _renderTimer = null;
        }

        // ---- viewport helpers ----

        private SKPoint ViewToWorld(Point view) => new(
            ((float)view.X - _pan.X) / _zoom,
            ((float)view.Y - _pan.Y) / _zoom);

        private Point WorldToView(SKPoint world) => new(
            world.X * _zoom + _pan.X,
            world.Y * _zoom + _pan.Y);

        private SKRect WorldToViewRect(SKRect world) => new(
            world.Left * _zoom + _pan.X,
            world.Top * _zoom + _pan.Y,
            world.Right * _zoom + _pan.X,
            world.Bottom * _zoom + _pan.Y);

        public void ZoomToFit()
        {
            if (_session == null || Bounds.Width <= 0)
            {
                return;
            }

            var margin = 48f;
            var scaleX = ((float)Bounds.Width - margin * 2) / _session.Profile.Width;
            var scaleY = ((float)Bounds.Height - margin * 2) / _session.Profile.Height;
            _zoom = Math.Clamp(Math.Min(scaleX, scaleY), MinZoom, MaxZoom);
            _pan = new SKPoint(
                ((float)Bounds.Width - _session.Profile.Width * _zoom) / 2,
                ((float)Bounds.Height - _session.Profile.Height * _zoom) / 2);
            ZoomChanged?.Invoke(this, EventArgs.Empty);
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }

        public void SetZoom(float zoom, Point? anchorView = null)
        {
            _fitPending = false;
            var anchor = anchorView ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
            var worldAnchor = ViewToWorld(anchor);
            _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
            _pan = new SKPoint(
                (float)anchor.X - worldAnchor.X * _zoom,
                (float)anchor.Y - worldAnchor.Y * _zoom);
            ZoomChanged?.Invoke(this, EventArgs.Empty);
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (_fitPending)
            {
                ZoomToFit();
            }

            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }

        // ---- rendering ----

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
            context.Custom(new DrawOp(new Rect(0, 0, Bounds.Width, Bounds.Height), this));
        }

        private void RenderCanvas(SKCanvas canvas)
        {
            canvas.Clear(WorkspaceColor);

            var session = _session;
            if (session == null) return;

            var profile = session.Profile;

            // profile surface (drop shadow + content), world space
            canvas.Save();
            canvas.Translate(_pan.X, _pan.Y);
            canvas.Scale(_zoom);

            using (var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(120), ImageFilter = SKImageFilter.CreateBlur(8 / _zoom, 8 / _zoom) })
            {
                canvas.DrawRect(new SKRect(0, 0, profile.Width, profile.Height), shadow);
            }

            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, profile.Width, profile.Height));
            // no using: SkiaGraphics.Dispose() would dispose the leased canvas
            var skiaGraphics = new SkiaGraphics(canvas, profile.FontScale);
            PanelDraw.Run(profile, skiaGraphics, preview: true, cacheHint: $"DESIGNER-{profile.Guid}");
            canvas.Restore();

            // grid overlay (world space, 1px view-width lines)
            if (SnapToGrid && GridSpacing > 1 && _zoom * GridSpacing > 4)
            {
                using var gridPaint = new SKPaint { Color = SKColors.Gray.WithAlpha(40), StrokeWidth = 1 / _zoom };
                for (int x = GridSpacing; x < profile.Width; x += GridSpacing)
                {
                    canvas.DrawLine(x, 0, x, profile.Height, gridPaint);
                }

                for (int y = GridSpacing; y < profile.Height; y += GridSpacing)
                {
                    canvas.DrawLine(0, y, profile.Width, y, gridPaint);
                }
            }

            canvas.Restore();

            // adorners, view space
            using var outline = new SKPaint { Color = AccentColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
            using var handleFill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
            using var handleStroke = new SKPaint { Color = AccentColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };

            foreach (var item in session.Selection)
            {
                var viewRect = WorldToViewRect(item.EvaluateBounds());
                canvas.DrawRect(viewRect, outline);
            }

            if (session.Selection.Count == 1 && ItemGeometry.IsResizable(session.Selection[0]) && !session.Selection[0].IsLocked)
            {
                foreach (var handle in GetHandleRects(WorldToViewRect(session.Selection[0].EvaluateBounds())))
                {
                    canvas.DrawRect(handle, handleFill);
                    canvas.DrawRect(handle, handleStroke);
                }
            }

            if (_state == InteractionState.Marquee)
            {
                var viewRect = WorldToViewRect(_marqueeWorld);
                using var marqueeFill = new SKPaint { Color = AccentColor.WithAlpha(40), Style = SKPaintStyle.Fill };
                canvas.DrawRect(viewRect, marqueeFill);
                canvas.DrawRect(viewRect, outline);
            }
        }

        private static SKRect[] GetHandleRects(SKRect viewRect)
        {
            var h = (float)HandleSize / 2;
            float midX = viewRect.MidX, midY = viewRect.MidY;
            return
            [
                SKRect.Create(viewRect.Left - h, viewRect.Top - h, 2 * h, 2 * h),      // 0 NW
                SKRect.Create(midX - h, viewRect.Top - h, 2 * h, 2 * h),               // 1 N
                SKRect.Create(viewRect.Right - h, viewRect.Top - h, 2 * h, 2 * h),     // 2 NE
                SKRect.Create(viewRect.Right - h, midY - h, 2 * h, 2 * h),             // 3 E
                SKRect.Create(viewRect.Right - h, viewRect.Bottom - h, 2 * h, 2 * h),  // 4 SE
                SKRect.Create(midX - h, viewRect.Bottom - h, 2 * h, 2 * h),            // 5 S
                SKRect.Create(viewRect.Left - h, viewRect.Bottom - h, 2 * h, 2 * h),   // 6 SW
                SKRect.Create(viewRect.Left - h, midY - h, 2 * h, 2 * h),              // 7 W
            ];
        }

        private int HitTestHandles(Point view)
        {
            var session = _session;
            if (session == null || session.Selection.Count != 1) return -1;
            var item = session.Selection[0];
            if (!ItemGeometry.IsResizable(item) || item.IsLocked) return -1;

            var handles = GetHandleRects(WorldToViewRect(item.EvaluateBounds()));
            var p = new SKPoint((float)view.X, (float)view.Y);
            for (int i = 0; i < handles.Length; i++)
            {
                var inflated = handles[i];
                inflated.Inflate(4, 4);
                if (inflated.Contains(p)) return i;
            }

            return -1;
        }

        // ---- input ----

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            _fitPending = false;

            var session = _session;
            if (session == null) return;

            var view = e.GetPosition(this);
            var world = ViewToWorld(view);
            var props = e.GetCurrentPoint(this).Properties;

            _pressView = view;
            _pressWorld = world;
            _lastView = view;
            _lastWorld = world;

            if (props.IsMiddleButtonPressed || (_spaceDown && props.IsLeftButtonPressed))
            {
                _state = InteractionState.Panning;
                e.Pointer.Capture(this);
                return;
            }

            if (!props.IsLeftButtonPressed) return;

            var additive = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            // resize handles first (view-space)
            var handle = HitTestHandles(view);
            if (handle >= 0)
            {
                _state = InteractionState.ResizingItem;
                _resizeHandle = handle;
                _resizeItem = session.Selection[0];
                session.BeginGesture();
                e.Pointer.Capture(this);
                return;
            }

            var hit = session.HitTest(world);
            if (hit != null)
            {
                if (!session.Selection.Contains(hit))
                {
                    session.Select(hit, additive);
                }
                else if (additive)
                {
                    session.Select(hit, additive: true); // toggle off
                    InvalidateVisual();
                    return;
                }

                if (!hit.IsLocked)
                {
                    _state = InteractionState.MovingItems;
                    session.BeginGesture();
                    e.Pointer.Capture(this);
                }

                InvalidateVisual();
                return;
            }

            // empty space: marquee
            if (!additive)
            {
                session.ClearSelection();
            }

            _state = InteractionState.Marquee;
            _marqueeWorld = new SKRect(world.X, world.Y, world.X, world.Y);
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            var session = _session;
            if (session == null) return;

            var view = e.GetPosition(this);
            var world = ViewToWorld(view);

            switch (_state)
            {
                case InteractionState.Panning:
                    Pan = new SKPoint(
                        _pan.X + (float)(view.X - _lastView.X),
                        _pan.Y + (float)(view.Y - _lastView.Y));
                    break;

                case InteractionState.MovingItems:
                {
                    var snap = SnapToGrid && !e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                    var totalDx = world.X - _pressWorld.X;
                    var totalDy = world.Y - _pressWorld.Y;

                    // apply as absolute offsets from gesture start so snapping stays stable
                    session.CancelGestureVisualOnly();
                    var dx = (int)Math.Round(totalDx);
                    var dy = (int)Math.Round(totalDy);
                    if (snap && GridSpacing > 1)
                    {
                        session.MoveSelectionSnapped(dx, dy, GridSpacing);
                    }
                    else
                    {
                        session.MoveSelectionBy(dx, dy);
                    }

                    InvalidateVisual();
                    break;
                }

                case InteractionState.ResizingItem when _resizeItem != null:
                {
                    ApplyResize(_resizeItem, world);
                    InvalidateVisual();
                    break;
                }

                case InteractionState.Marquee:
                    _marqueeWorld = new SKRect(
                        Math.Min(_pressWorld.X, world.X),
                        Math.Min(_pressWorld.Y, world.Y),
                        Math.Max(_pressWorld.X, world.X),
                        Math.Max(_pressWorld.Y, world.Y));
                    InvalidateVisual();
                    break;
            }

            _lastView = view;
            _lastWorld = world;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            var session = _session;
            if (session == null) return;

            switch (_state)
            {
                case InteractionState.MovingItems:
                    session.EndGesture("Move");
                    break;
                case InteractionState.ResizingItem:
                    session.EndGesture("Resize");
                    _resizeHandle = -1;
                    _resizeItem = null;
                    break;
                case InteractionState.Marquee:
                    if (_marqueeWorld.Width > 2 || _marqueeWorld.Height > 2)
                    {
                        session.SelectInRect(_marqueeWorld);
                    }

                    break;
            }

            _state = InteractionState.Idle;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var factor = e.Delta.Y > 0 ? 1.15f : 1 / 1.15f;
                SetZoom(_zoom * factor, e.GetPosition(this));
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                Pan = new SKPoint(_pan.X + (float)e.Delta.Y * 40, _pan.Y);
            }
            else
            {
                Pan = new SKPoint(_pan.X + (float)e.Delta.X * 40, _pan.Y + (float)e.Delta.Y * 40);
            }

            e.Handled = true;
        }

        private void ApplyResize(DisplayItem item, SKPoint world)
        {
            // Resize against the gesture-start geometry captured by BeginGesture
            var start = _session!.GestureStartOf(item);
            if (start == null) return;

            var (sx, sy, sw, sh) = start.Value;
            var bounds = item.EvaluateBounds();

            // effective starting size for auto-sized (0) dimensions
            var effW = sw > 0 ? sw : (int)bounds.Width;
            var effH = sh > 0 ? sh : (int)bounds.Height;

            int dx = (int)Math.Round(world.X - _pressWorld.X);
            int dy = (int)Math.Round(world.Y - _pressWorld.Y);

            int newX = sx, newY = sy, newW = effW, newH = effH;

            // handles: 0 NW, 1 N, 2 NE, 3 E, 4 SE, 5 S, 6 SW, 7 W
            if (_resizeHandle is 0 or 6 or 7) { newX = sx + dx; newW = effW - dx; }
            if (_resizeHandle is 2 or 3 or 4) { newW = effW + dx; }
            if (_resizeHandle is 0 or 1 or 2) { newY = sy + dy; newH = effH - dy; }
            if (_resizeHandle is 4 or 5 or 6) { newH = effH + dy; }

            newW = Math.Max(newW, 8);
            newH = Math.Max(newH, 8);

            item.X = newX;
            item.Y = newY;
            ItemGeometry.SetSize(item, newW, newH);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            var session = _session;
            if (session == null) return;

            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

            switch (e.Key)
            {
                case Key.Space:
                    _spaceDown = true;
                    break;
                case Key.Left: session.Nudge(-step, 0); e.Handled = true; break;
                case Key.Right: session.Nudge(step, 0); e.Handled = true; break;
                case Key.Up: session.Nudge(0, -step); e.Handled = true; break;
                case Key.Down: session.Nudge(0, step); e.Handled = true; break;
                case Key.Delete or Key.Back: session.DeleteSelection(); e.Handled = true; break;
                case Key.Escape:
                    if (_state != InteractionState.Idle)
                    {
                        session.CancelGesture();
                        _state = InteractionState.Idle;
                    }
                    else
                    {
                        session.ClearSelection();
                    }

                    e.Handled = true;
                    break;
                case Key.A when ctrl: session.SelectAll(); e.Handled = true; break;
                case Key.D when ctrl: session.Duplicate(); e.Handled = true; break;
                case Key.Z when ctrl: session.Undo.Undo(); e.Handled = true; break;
                case Key.Y when ctrl: session.Undo.Redo(); e.Handled = true; break;
                case Key.S when ctrl: session.SaveNow(); e.Handled = true; break;
                case Key.C when ctrl: CopyToClipboard(); e.Handled = true; break;
                case Key.V when ctrl: PasteFromClipboard(); e.Handled = true; break;
                case Key.PageUp: session.PushBy(ctrl ? int.MaxValue / 2 : 1); e.Handled = true; break;
                case Key.PageDown: session.PushBy(ctrl ? -int.MaxValue / 2 : -1); e.Handled = true; break;
                case Key.D0 when ctrl: ZoomToFit(); e.Handled = true; break;
                case Key.D1 when ctrl: SetZoom(1f); e.Handled = true; break;
            }

            InvalidateVisual();
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.Key == Key.Space)
            {
                _spaceDown = false;
            }
        }

        private async void CopyToClipboard()
        {
            var xml = _session?.CopySelectionToXml();
            if (xml != null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(xml);
            }
        }

        private async void PasteFromClipboard()
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                var text = await clipboard.TryGetTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    _session?.PasteFromXml(text);
                    InvalidateVisual();
                }
            }
        }

        // ---- draw op ----

        private sealed class DrawOp(Rect bounds, DesignCanvas owner) : ICustomDrawOperation
        {
            public Rect Bounds => bounds;
            public bool HitTest(Point p) => bounds.Contains(p);
            public bool Equals(ICustomDrawOperation? other) => false;

            public void Render(ImmediateDrawingContext context)
            {
                var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (leaseFeature == null) return;

                using var lease = leaseFeature.Lease();
                owner.RenderCanvas(lease.SkCanvas);
            }

            public void Dispose() { }
        }
    }
}
