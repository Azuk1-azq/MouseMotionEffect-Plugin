using System.ComponentModel.DataAnnotations;
using System.Numerics;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;

namespace MouseMotion;

/// <summary>
/// 設定内のキャンバスをマウスでドラッグするだけで、
/// 軌跡(移動)とイージングを自動生成してアイテムを動かす映像エフェクト。
/// イージングは「記録そのまま」と「プリセット」を切り替えられ、手ぶれ補正もかけられる。
/// 動きは「タイムライン終了まで」(アイテム全体に引き伸ばす)か「通常速度」(記録した時間のまま)で再生できる。
/// </summary>
[VideoEffect("マウス移動", new[] { VideoEffectCategories.Animation },
    new[] { "移動", "イージング", "ドラッグ", "マウス", "mouse", "easing", "move" }, false, false)]
public sealed class MouseMotionEffect : VideoEffectBase
{
    public override string Label => "マウス移動";

    [Display(GroupName = "マウス移動", Name = "軌跡",
        Description = "枠内をマウスでドラッグして軌跡を記録します。ドラッグの速さの変化も一緒に記録されます。")]
    [MouseMotionEditor]
    public string PathData { get => pathData; set => Set(ref pathData, value); }
    private string pathData = "";

    [Display(GroupName = "マウス移動", Name = "再生速度",
        Description = "タイムライン終了まで: 動きをアイテムの長さ全体に引き伸ばします。\n通常速度: ドラッグにかかった時間のまま動き、終わったら終点で止まります。")]
    [EnumComboBox]
    public MotionPlayback Playback { get => playback; set => Set(ref playback, value); }
    private MotionPlayback playback = MotionPlayback.FitItem;

    [Display(GroupName = "マウス移動", Name = "手ぶれ補正",
        Description = "ドラッグ中の手の震えを取り除いて、軌跡と速さをなめらかにします。強いほど細かな動きが消えます。")]
    [EnumComboBox]
    public StabilizationLevel Stabilization { get => stabilization; set => Set(ref stabilization, value); }
    private StabilizationLevel stabilization = StabilizationLevel.Off;

    [Display(GroupName = "マウス移動", Name = "イージング方式",
        Description = "記録そのまま: 実際にドラッグしたときの速さの変化をそのまま使います。\nプリセット: 下の「イージング」「イージングの向き」を使います。")]
    [EnumComboBox]
    public MotionEasingSource EasingSource { get => easingSource; set => Set(ref easingSource, value); }
    private MotionEasingSource easingSource = MotionEasingSource.Recorded;

    [Display(GroupName = "マウス移動", Name = "イージング",
        Description = "「イージング方式」がプリセットのときに使われます。ドラッグから自動で決まり、手動でも変更できます。")]
    [EnumComboBox]
    public EasingType EasingType { get => easingType; set => Set(ref easingType, value); }
    private EasingType easingType = EasingType.Linear;

    [Display(GroupName = "マウス移動", Name = "イージングの向き",
        Description = "「イージング方式」がプリセットのときに使われます。")]
    [EnumComboBox]
    public EasingMode EasingMode { get => easingMode; set => Set(ref easingMode, value); }
    private EasingMode easingMode = EasingMode.In;

    [Display(GroupName = "マウス移動", Name = "上下を反転",
        Description = "動きの上下が逆になる場合にオンにします。")]
    [ToggleSlider]
    public bool InvertY { get => invertY; set => Set(ref invertY, value); }
    private bool invertY;

    // ---- 描画側から使う ----

    private sealed class ModelCache
    {
        public readonly string Data;
        public readonly StabilizationLevel Level;
        public readonly MotionModel Model;
        public ModelCache(string data, StabilizationLevel level, MotionModel model)
        {
            Data = data;
            Level = level;
            Model = model;
        }
    }

    private ModelCache cache;

    /// <summary>記録と手ぶれ補正の設定から求めた動き。同じ入力ならキャッシュを返す。</summary>
    internal MotionModel GetModel()
    {
        var snapshot = cache;
        var data = pathData ?? "";
        var level = stabilization;
        if (snapshot != null && snapshot.Level == level && snapshot.Data == data) return snapshot.Model;

        var model = MotionBuilder.Build(data, level);
        cache = new ModelCache(data, level, model);
        return model;
    }

    /// <summary>アイテム内の時刻 t(0..1)における、軌跡の進行割合(0..1、Back などは範囲外もあり)。</summary>
    internal double Progress(MotionModel model, double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (easingSource == MotionEasingSource.Recorded && model.HasTiming)
            return model.TimingAt(t);
        return Easing.GetValue(easingType, easingMode, t);
    }

    /// <summary>アイテム内の進行 t(0..1)における、開始位置からの変位(論理px)。</summary>
    /// <param name="t">アイテム内の進行(0..1)</param>
    /// <param name="elapsedMs">アイテム開始からの経過時間(ミリ秒)</param>
    internal Vector2 Evaluate(double t, double elapsedMs)
    {
        var model = GetModel();
        if (model.Path.IsEmpty) return Vector2.Zero;
        if (playback == MotionPlayback.Realtime && model.DurationMs >= 1)
            t = Math.Clamp(elapsedMs / model.DurationMs, 0, 1);
        var d = model.Path.DisplacementAt(Progress(model, t));
        return invertY ? new Vector2(d.X, -d.Y) : d;
    }

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new MouseMotionProcessor(this);

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
        => Enumerable.Empty<string>();

    protected override IEnumerable<IAnimatable> GetAnimatables() => Array.Empty<IAnimatable>();
}

/// <summary>
/// 画像には手を加えず、描画位置(Draw)だけを動かすエフェクト処理。
/// </summary>
internal sealed class MouseMotionProcessor : IVideoEffectProcessor
{
    private readonly MouseMotionEffect effect;
    private ID2D1Image input;

    public MouseMotionProcessor(MouseMotionEffect effect) => this.effect = effect;

    public ID2D1Image Output => input;

    public void SetInput(ID2D1Image input) => this.input = input;

    public void ClearInput() => input = null;

    public DrawDescription Update(EffectDescription effectDescription)
    {
        int length = effectDescription.ItemDuration.Frame;
        int frame = effectDescription.ItemPosition.Frame;
        double t = length <= 1 ? 1.0 : Math.Clamp(frame / (double)(length - 1), 0.0, 1.0);

        int fps = effectDescription.FPS;
        double elapsedMs = fps > 0 ? frame * 1000.0 / fps : 0;
        var move = effect.Evaluate(t, elapsedMs);
        var draw = effectDescription.DrawDescription;
        return draw with { Draw = draw.Draw + new Vector3(move.X, move.Y, 0f) };
    }

    public void Dispose()
    {
        // input は上流が所有しているので破棄しない
    }
}
