using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using YukkuriMovieMaker.Commons;

namespace MouseMotion;

/// <summary>「軌跡」プロパティに表示するドラッグ用キャンバスを作る属性。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MouseMotionEditorAttribute : PropertyEditorAttribute2
{
    public MouseMotionEditorAttribute()
    {
        PropertyEditorSize = PropertyEditorSize.FullWidth;
        MinHeight = 220;
    }

    public override FrameworkElement Create() => new MouseMotionEditor();

    public override void SetBindings(FrameworkElement control, ItemProperty[] itemProperties)
        => ((MouseMotionEditor)control).SetItemProperties(itemProperties);

    public override void ClearBindings(FrameworkElement control)
        => ((MouseMotionEditor)control).ClearItemProperties();
}

/// <summary>
/// 画面(1920x1080 相当)を縮小して表示した枠。
/// 枠内を左ボタンでドラッグすると、その軌跡と速さの変化を記録し、
/// 離した時点で、軌跡と速さの変化(実際のイージング)を記録して書き込む。
/// 手ぶれ補正をかけた軌跡と、実際に使われるイージングの曲線も表示する。
/// </summary>
public sealed class MouseMotionEditor : UserControl, IPropertyEditorControl
{
    private const double ViewW = 320, ViewH = 180;
    private const double LogicalW = 1920, LogicalH = 1080;
    private const double KX = LogicalW / ViewW;   // 画面px / 表示px
    private const double KY = LogicalH / ViewH;
    private const double PreviewSeconds = 1.5;
    private const double DotRadius = 7;
    private const double CurveW = 320, CurveH = 72, CurveMargin = 8;
    private const int CurvePoints = 64;

    private static readonly PropertyInfo EasingTypeProperty =
        typeof(MouseMotionEffect).GetProperty(nameof(MouseMotionEffect.EasingType));
    private static readonly PropertyInfo EasingModeProperty =
        typeof(MouseMotionEffect).GetProperty(nameof(MouseMotionEffect.EasingMode));
    private static readonly PropertyInfo EasingSourceProperty =
        typeof(MouseMotionEffect).GetProperty(nameof(MouseMotionEffect.EasingSource));
    private static readonly PropertyInfo PlaybackProperty =
        typeof(MouseMotionEffect).GetProperty(nameof(MouseMotionEffect.Playback));
    private static readonly PropertyInfo StabilizationProperty =
        typeof(MouseMotionEffect).GetProperty(nameof(MouseMotionEffect.Stabilization));

    private const string VersionTag = "v2.2";

    public event EventHandler BeginEdit;
    public event EventHandler EndEdit;

    private readonly Canvas canvas;
    private readonly Polyline pathLine;
    private readonly Polyline rawLine;
    private readonly Polyline liveLine;
    private readonly Canvas curveCanvas;
    private readonly Polyline curveLine;
    private readonly Ellipse startMark;
    private readonly Ellipse endMark;
    private readonly Ellipse dot;
    private readonly TextBlock hint;
    private readonly ComboBox sourceBox;
    private readonly ComboBox stabilizeBox;
    private readonly ComboBox playbackBox;
    private bool syncing;

    private readonly List<MotionSample> samples = new();
    private readonly Stopwatch recordWatch = new();
    private readonly Stopwatch previewWatch = new();
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    private ItemProperty[] itemProperties = Array.Empty<ItemProperty>();
    private MouseMotionEffect effect;
    private bool recording;
    private Point lastPoint;

    private MotionModel previewModel = MotionModel.Empty;
    private double previewSeconds = PreviewSeconds;

    public MouseMotionEditor()
    {
        canvas = new Canvas
        {
            Width = ViewW,
            Height = ViewH,
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            Cursor = Cursors.Cross,
            ClipToBounds = true,
            SnapsToDevicePixels = true,
        };

        var gridBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        canvas.Children.Add(new Line { X1 = 0, Y1 = ViewH / 2, X2 = ViewW, Y2 = ViewH / 2, Stroke = gridBrush, StrokeThickness = 1, IsHitTestVisible = false });
        canvas.Children.Add(new Line { X1 = ViewW / 2, Y1 = 0, X2 = ViewW / 2, Y2 = ViewH, Stroke = gridBrush, StrokeThickness = 1, IsHitTestVisible = false });

        pathLine = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        rawLine = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0xB0, 0xB0, 0xB0)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
            Visibility = Visibility.Hidden,
        };
        liveLine = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        startMark = CreateMark(Color.FromRgb(0x66, 0xBB, 0x6A), 4);
        endMark = CreateMark(Color.FromRgb(0xEF, 0x53, 0x50), 4);
        dot = CreateMark(Colors.White, DotRadius);
        dot.Stroke = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
        dot.StrokeThickness = 2;

        canvas.Children.Add(rawLine);
        canvas.Children.Add(pathLine);
        canvas.Children.Add(liveLine);
        canvas.Children.Add(startMark);
        canvas.Children.Add(endMark);
        canvas.Children.Add(dot);
        PlaceDot(new Point(ViewW / 2, ViewH / 2));

        canvas.MouseLeftButtonDown += OnMouseDown;
        canvas.MouseMove += OnMouseMove;
        canvas.MouseLeftButtonUp += OnMouseUp;
        canvas.LostMouseCapture += OnLostCapture;

        var playButton = new Button { Content = "▶ 再生", Margin = new Thickness(0, 6, 6, 0), Padding = new Thickness(10, 2, 10, 2) };
        var clearButton = new Button { Content = "クリア", Margin = new Thickness(0, 6, 6, 0), Padding = new Thickness(10, 2, 10, 2) };
        var refitButton = new Button
        {
            Content = "プリセット再判定",
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(10, 2, 10, 2),
            ToolTip = "いまの記録(手ぶれ補正後)に最も近いプリセットを選び直します",
        };
        playButton.Click += (_, _) => StartPreview();
        clearButton.Click += (_, _) => ClearPath();
        refitButton.Click += (_, _) => RefitPreset();

        hint = new TextBlock
        {
            Text = "枠内をドラッグして軌跡を記録",
            Opacity = 0.7,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        sourceBox = new ComboBox { MinWidth = 110, Margin = new Thickness(0, 0, 12, 0) };
        sourceBox.Items.Add(new ComboBoxItem { Content = "記録そのまま", Tag = MotionEasingSource.Recorded });
        sourceBox.Items.Add(new ComboBoxItem { Content = "プリセット", Tag = MotionEasingSource.Preset });
        sourceBox.SelectionChanged += (_, _) =>
        {
            if (!syncing && sourceBox.SelectedItem is ComboBoxItem { Tag: MotionEasingSource v })
                WriteProperty(EasingSourceProperty, v);
        };

        stabilizeBox = new ComboBox { MinWidth = 70 };
        foreach (var (label, level) in new[]
        {
            ("オフ", StabilizationLevel.Off), ("弱", StabilizationLevel.Weak), ("標準", StabilizationLevel.Normal),
            ("強", StabilizationLevel.Strong), ("最強", StabilizationLevel.Max),
        })
            stabilizeBox.Items.Add(new ComboBoxItem { Content = label, Tag = level });
        stabilizeBox.SelectionChanged += (_, _) =>
        {
            if (!syncing && stabilizeBox.SelectedItem is ComboBoxItem { Tag: StabilizationLevel v })
                WriteProperty(StabilizationProperty, v);
        };

        playbackBox = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 0, 12, 0) };
        playbackBox.Items.Add(new ComboBoxItem { Content = "タイムライン終了まで", Tag = MotionPlayback.FitItem });
        playbackBox.Items.Add(new ComboBoxItem { Content = "通常速度(記録した時間)", Tag = MotionPlayback.Realtime });
        playbackBox.SelectionChanged += (_, _) =>
        {
            if (!syncing && playbackBox.SelectedItem is ComboBoxItem { Tag: MotionPlayback v })
                WriteProperty(PlaybackProperty, v);
        };

        var options = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        options.Children.Add(new TextBlock { Text = "再生速度", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        options.Children.Add(playbackBox);
        options.Children.Add(new TextBlock { Text = "イージング方式", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        options.Children.Add(sourceBox);
        options.Children.Add(new TextBlock { Text = "手ぶれ補正", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        options.Children.Add(stabilizeBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(playButton);
        buttons.Children.Add(clearButton);
        buttons.Children.Add(refitButton);

        var frame = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Child = canvas,
            },
        };

        // 実際に使われるイージング(横: 時間、縦: 進み具合)
        curveCanvas = new Canvas
        {
            Width = CurveW,
            Height = CurveH,
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            ClipToBounds = true,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
        };
        curveCanvas.Children.Add(new Line
        {
            X1 = CurveMargin, Y1 = CurveH - CurveMargin, X2 = CurveW - CurveMargin, Y2 = CurveMargin,
            Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
        });
        curveLine = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        };
        curveCanvas.Children.Add(curveLine);

        var curveFrame = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                Child = curveCanvas,
            },
        };

        var root = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
        root.Children.Add(frame);
        root.Children.Add(curveFrame);
        root.Children.Add(options);
        root.Children.Add(buttons);
        root.Children.Add(hint);
        Content = root;

        previewTimer.Tick += OnPreviewTick;
        Unloaded += (_, _) => StopPreview();
    }

    public void SetFocus() => Focus();

    // ---- バインド ----

    internal void SetItemProperties(ItemProperty[] props)
    {
        ClearItemProperties();
        itemProperties = props ?? Array.Empty<ItemProperty>();
        effect = itemProperties.Length > 0 ? itemProperties[0].PropertyOwner as MouseMotionEffect : null;
        if (effect != null) effect.PropertyChanged += OnEffectPropertyChanged;
        Refresh();
    }

    internal void ClearItemProperties()
    {
        StopPreview();
        if (effect != null) effect.PropertyChanged -= OnEffectPropertyChanged;
        effect = null;
        itemProperties = Array.Empty<ItemProperty>();
    }

    private void OnEffectPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MouseMotionEffect.PathData) &&
            e.PropertyName != nameof(MouseMotionEffect.Stabilization) &&
            e.PropertyName != nameof(MouseMotionEffect.EasingSource) &&
            e.PropertyName != nameof(MouseMotionEffect.Playback) &&
            e.PropertyName != nameof(MouseMotionEffect.EasingType) &&
            e.PropertyName != nameof(MouseMotionEffect.EasingMode)) return;
        if (Dispatcher.CheckAccess()) { if (!recording) Refresh(); }
        else Dispatcher.BeginInvoke(new Action(() => { if (!recording) Refresh(); }));
    }

    // ---- 記録 ----

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        StopPreview();
        recording = true;
        samples.Clear();
        recordWatch.Restart();

        var p = ClampToView(e.GetPosition(canvas));
        lastPoint = p;
        samples.Add(ToSample(0, p));

        pathLine.Visibility = Visibility.Hidden;
        rawLine.Visibility = Visibility.Hidden;
        startMark.Visibility = Visibility.Hidden;
        endMark.Visibility = Visibility.Hidden;
        liveLine.Points = new PointCollection { p };
        PlaceDot(p);

        canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!recording) return;
        var p = ClampToView(e.GetPosition(canvas));
        if (p == lastPoint) return;
        lastPoint = p;
        samples.Add(ToSample(recordWatch.Elapsed.TotalMilliseconds, p));
        liveLine.Points.Add(p);
        PlaceDot(p);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (recording) FinishRecording();
    }

    private void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (recording) FinishRecording();
    }

    private void FinishRecording()
    {
        recording = false;
        canvas.ReleaseMouseCapture();
        liveLine.Points = new PointCollection();

        // 保存形式にしてから読み直して判定する(あとで読み込まれる内容と同じ結果になるように)
        var data = MotionRecording.Serialize(samples);
        var model = MotionBuilder.Build(data, effect?.Stabilization ?? StabilizationLevel.Off);
        if (!model.Path.IsEmpty)
            Write(data, model.FitType, model.FitMode);

        Refresh();
    }

    private void ClearPath()
    {
        StopPreview();
        Write("", null, null);
        Refresh();
    }

    private void WriteProperty(PropertyInfo property, object value)
    {
        if (itemProperties.Length == 0) return;

        BeginEdit?.Invoke(this, EventArgs.Empty);
        try
        {
            foreach (var ip in itemProperties)
                ip.SetValue(property, value);
        }
        finally
        {
            EndEdit?.Invoke(this, EventArgs.Empty);
        }
        Refresh();
    }

    private void SyncOptions()
    {
        if (effect == null) return;
        syncing = true;
        try
        {
            foreach (ComboBoxItem item in sourceBox.Items)
                if (item.Tag is MotionEasingSource v && v == effect.EasingSource) sourceBox.SelectedItem = item;
            foreach (ComboBoxItem item in playbackBox.Items)
                if (item.Tag is MotionPlayback v && v == effect.Playback) playbackBox.SelectedItem = item;
            foreach (ComboBoxItem item in stabilizeBox.Items)
                if (item.Tag is StabilizationLevel v && v == effect.Stabilization) stabilizeBox.SelectedItem = item;
        }
        finally
        {
            syncing = false;
        }
    }

    private void Write(string data, EasingType? type, EasingMode? mode)
    {
        if (itemProperties.Length == 0) return;

        BeginEdit?.Invoke(this, EventArgs.Empty);
        try
        {
            foreach (var ip in itemProperties)
            {
                if (data != null) ip.SetValue(data);
                if (type is { } t) ip.SetValue(EasingTypeProperty, t);
                if (mode is { } m) ip.SetValue(EasingModeProperty, m);
            }
        }
        finally
        {
            EndEdit?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- 表示 ----

    private void RefitPreset()
    {
        if (effect == null) return;
        StopPreview();
        var model = effect.GetModel();
        if (model.Path.IsEmpty || !model.HasTiming) return;
        Write(null, model.FitType, model.FitMode);
        Refresh();
    }

    private void Refresh()
    {
        SyncOptions();
        var model = effect?.GetModel() ?? MotionModel.Empty;
        var path = model.Path;

        if (path.IsEmpty)
        {
            pathLine.Visibility = Visibility.Hidden;
            rawLine.Visibility = Visibility.Hidden;
            startMark.Visibility = Visibility.Hidden;
            endMark.Visibility = Visibility.Hidden;
            curveLine.Visibility = Visibility.Hidden;
            hint.Text = $"枠内をドラッグして軌跡を記録 [{VersionTag}]";
            PlaceDot(new Point(ViewW / 2, ViewH / 2));
            return;
        }

        var points = new PointCollection();
        foreach (var p in path.Points) points.Add(ToView(p));
        pathLine.Points = points;
        pathLine.Visibility = Visibility.Visible;

        // 手ぶれ補正をかけているときは、補正前の軌跡を薄く重ねる
        bool stabilized = effect.Stabilization != StabilizationLevel.Off;
        if (stabilized && model.RawPoints.Count >= 2)
        {
            var raw = new PointCollection();
            foreach (var p in model.RawPoints) raw.Add(ToView(p));
            rawLine.Points = raw;
            rawLine.Visibility = Visibility.Visible;
        }
        else
        {
            rawLine.Visibility = Visibility.Hidden;
        }

        PlaceMark(startMark, ToView(path.Points[0]));
        PlaceMark(endMark, ToView(path.Points[^1]));
        startMark.Visibility = Visibility.Visible;
        endMark.Visibility = Visibility.Visible;

        PlaceDot(ToView(path.Points[0]));

        DrawCurve(model);

        bool recorded = effect.EasingSource == MotionEasingSource.Recorded && model.HasTiming;
        string easing = recorded
            ? "記録そのまま"
            : $"プリセット {effect.EasingType} / {effect.EasingMode}";
        if (effect.EasingSource == MotionEasingSource.Recorded && !model.HasTiming)
            easing += "(この記録には速さの情報がありません)";
        string info = model.HasTiming ? "速さ: 記録あり" : "速さ: 記録なし";
        hint.Text = $"使用中: {easing} / {info} [{VersionTag}]";
    }

    private void DrawCurve(MotionModel model)
    {
        var line = new PointCollection();
        for (int i = 0; i < CurvePoints; i++)
        {
            double t = i / (double)(CurvePoints - 1);
            double p = effect.Progress(model, t);
            double x = CurveMargin + t * (CurveW - CurveMargin * 2);
            double y = CurveH - CurveMargin - p * (CurveH - CurveMargin * 2);
            line.Add(new Point(x, y));
        }
        curveLine.Points = line;
        curveLine.Visibility = Visibility.Visible;
    }

    private static string LevelName(StabilizationLevel level) => level switch
    {
        StabilizationLevel.Weak => "弱",
        StabilizationLevel.Normal => "標準",
        StabilizationLevel.Strong => "強",
        StabilizationLevel.Max => "最強",
        _ => "オフ",
    };

    private void StartPreview()
    {
        if (effect == null) return;
        previewModel = effect.GetModel();
        if (previewModel.Path.IsEmpty) return;
        previewSeconds = effect.Playback == MotionPlayback.Realtime && previewModel.DurationMs >= 1
            ? previewModel.DurationMs / 1000.0
            : PreviewSeconds;
        previewWatch.Restart();
        previewTimer.Start();
    }

    private void OnPreviewTick(object sender, EventArgs e)
    {
        if (effect == null) { StopPreview(); return; }

        double t = Math.Min(previewWatch.Elapsed.TotalSeconds / previewSeconds, 1.0);
        double p = effect.Progress(previewModel, t);
        PlaceDot(ToView(previewModel.Path.PositionAt(p)));
        if (t >= 1.0) StopPreview();
    }

    private void StopPreview()
    {
        previewTimer.Stop();
    }

    // ---- 座標変換・図形 ----

    private static Point ClampToView(Point p)
        => new(Math.Clamp(p.X, 0, ViewW), Math.Clamp(p.Y, 0, ViewH));

    /// <summary>表示座標 → 論理座標(画面中心が原点)</summary>
    private static MotionSample ToSample(double timeMs, Point p)
        => new(timeMs, new Vector2((float)((p.X - ViewW / 2) * KX), (float)((p.Y - ViewH / 2) * KY)));

    private static Point ToView(Vector2 logical)
        => new(logical.X / KX + ViewW / 2, logical.Y / KY + ViewH / 2);

    private static Ellipse CreateMark(Color color, double radius) => new()
    {
        Width = radius * 2,
        Height = radius * 2,
        Fill = new SolidColorBrush(color),
        IsHitTestVisible = false,
    };

    private static void PlaceMark(Ellipse mark, Point p)
    {
        Canvas.SetLeft(mark, p.X - mark.Width / 2);
        Canvas.SetTop(mark, p.Y - mark.Height / 2);
    }

    private void PlaceDot(Point p) => PlaceMark(dot, p);
}
