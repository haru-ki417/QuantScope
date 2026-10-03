using System.Diagnostics;
using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;

namespace QuantScope.Core.Pipeline;

/// <summary>手順を順に実行した結果。States[0] が入力、States[i + 1] が i 番目の手順のあと。</summary>
public sealed class PipelineRun
{
    internal PipelineRun(Raster input, Calibration calibration, IReadOnlyList<string> signatures, IReadOnlyList<PipelineState> states, IReadOnlyList<StepOutcome> outcomes)
    {
        Input = input;
        Calibration = calibration;
        Signatures = signatures;
        States = states;
        Outcomes = outcomes;
    }

    public Raster Input { get; }
    public Calibration Calibration { get; }
    internal IReadOnlyList<string> Signatures { get; }
    public IReadOnlyList<PipelineState> States { get; }
    public IReadOnlyList<StepOutcome> Outcomes { get; }
    public PipelineState Final => States[^1];

    /// <summary>i 番目の手順のあとの状態（-1 なら入力）</summary>
    public PipelineState After(int index) => States[Math.Clamp(index + 1, 0, States.Count - 1)];
}

public static class PipelineRunner
{
    /// <summary>
    /// 手順を順に実行する。previous を渡すと、入力・縮尺が同じで、先頭から変わっていない手順の結果を使い回す
    /// （値を 1 つ変えたときに、その手順から先だけを計算し直す）。
    /// 使えない手順（マスクがないのにマスクの処理など）は飛ばし、理由を Outcome に残す。
    /// </summary>
    public static PipelineRun Run(Raster input, Calibration calibration, IReadOnlyList<Step> steps, PipelineRun? previous = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(steps);
        var sigs = steps.Select(s => s.Signature()).ToList();
        var states = new List<PipelineState> { PipelineState.Start(input, calibration) };
        var outcomes = new List<StepOutcome>();

        int reuse = 0;
        if (previous is not null && ReferenceEquals(previous.Input, input) && previous.Calibration == calibration)
        {
            while (reuse < sigs.Count && reuse < previous.Signatures.Count && sigs[reuse] == previous.Signatures[reuse]) reuse++;
            for (int i = 0; i < reuse; i++)
            {
                states.Add(previous.States[i + 1]);
                outcomes.Add(previous.Outcomes[i]);
            }
        }

        for (int i = reuse; i < steps.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var step = steps[i];
            var state = states[^1];
            if (!step.Enabled)
            {
                states.Add(state);
                outcomes.Add(new StepOutcome(false, "オフ", null, TimeSpan.Zero));
                continue;
            }
            var def = StepCatalog.Get(step.Kind);
            var ctx = new StepContext(cancel);
            var sw = Stopwatch.StartNew();
            try
            {
                var next = def.Apply(state, step, ctx);
                states.Add(next);
                outcomes.Add(new StepOutcome(true, ctx.Info, ctx.Warning, sw.Elapsed));
            }
            catch (StepNotApplicableException ex)
            {
                states.Add(state);
                outcomes.Add(new StepOutcome(false, null, ex.Message, sw.Elapsed));
            }
            catch (ArgumentException ex)
            {
                states.Add(state);
                outcomes.Add(new StepOutcome(false, null, ex.Message, sw.Elapsed));
            }
        }
        return new PipelineRun(input, calibration, sigs, states, outcomes);
    }

    /// <summary>
    /// 最後の状態のマスクを計測する。マスクがなければ null（二値化の手順がない）。
    /// region は解析する範囲、exclude は手で除く粒の点（どちらも今の画像の座標）。
    /// </summary>
    public static AnalysisResult? Analyze(PipelineState state, MeasureSettings settings, Roi? region = null, IReadOnlyList<PointD>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);
        if (state.Mask is null) return null;
        var source = settings.IntensityFromOriginal ? state.Original : state.Image;
        // 白黒にした画像などは大きさが同じ。切り抜いた場合も Original は同じ形に揃えてある
        if (source.Width != state.Mask.Width || source.Height != state.Mask.Height) source = state.Image;
        var measured = IntensityChannels.Extract(source, settings.Channel, out bool fallback);
        var regionMask = region?.ToMask(state.Mask.Width, state.Mask.Height);
        var options = settings.ToOptions() with
        {
            ExcludePoints = exclude ?? [],
            IntensityLabel = fallback ? "明るさ" : IntensityChannels.Title(settings.Channel),
        };
        return ParticleAnalyzer.Analyze(state.Mask, measured, state.Calibration, options, regionMask);
    }
}
