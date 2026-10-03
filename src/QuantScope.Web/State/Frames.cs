using QuantScope.Core.Analysis;
using QuantScope.Core.Imaging;
using QuantScope.Core.Pipeline;
using QuantScope.Core.Rendering;

namespace QuantScope.Web.State;

/// <summary>画面に描くための色（RGBA）を作る</summary>
public static class Frames
{
    /// <summary>BGRA（Windows の並び）を RGBA（ブラウザーの並び）にする</summary>
    public static byte[] ToRgba(byte[] bgra)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return bgra;
    }

    public static byte[] Image(Raster img, ColorMap map) => ToRgba(DisplayRenderer.ToBgra(img, map));

    /// <summary>計測した粒の重ね合わせ（色分け・陽性と陰性・手で除いた粒）。BGRA のまま返す</summary>
    public static byte[] ResultOverlayBgra(AnalysisResult a, bool perObject, int selected = 0)
    {
        ArgumentNullException.ThrowIfNull(a);
        var coloring = perObject ? OverlayColoring.PerObject : a.Summary.Positive is not null ? OverlayColoring.Classified : OverlayColoring.Uniform;
        var bytes = DisplayRenderer.LabelOverlay(a.Labels, coloring, selected, coloring == OverlayColoring.Classified ? DisplayRenderer.PositiveById(a.Particles) : null);
        if (a.Excluded is { } ex) DisplayRenderer.AddExcluded(bytes, ex);
        return bytes;
    }

    /// <summary>表示している状態の重ね合わせ（マスクがなければ null）</summary>
    public static byte[]? Overlay(Workspace ws)
    {
        ArgumentNullException.ThrowIfNull(ws);
        var st = ws.ViewState;
        if (st?.Mask is null) return null;
        var bgra = ws.OverlayUsesLabels
            ? ResultOverlayBgra(ws.Analysis!, ws.ColorPerObject, ws.SelectedParticle?.Id ?? 0)
            : DisplayRenderer.MaskOverlay(st.Mask);
        return ToRgba(bgra);
    }

    /// <summary>画像に重ね合わせを合成（保存・レポート用）。RGBA を返す</summary>
    public static byte[] Composite(PipelineState state, AnalysisResult? analysis, ColorMap map, bool perObject, double opacity)
    {
        ArgumentNullException.ThrowIfNull(state);
        var baseImg = state.Original.Width == state.Image.Width && state.Original.Height == state.Image.Height ? state.Original : state.Image;
        var baseBytes = DisplayRenderer.ToBgra(baseImg, map);
        if (state.Mask is null) return ToRgba(baseBytes);
        var over = analysis is not null && analysis.Labels.Width == state.Mask.Width
            ? ResultOverlayBgra(analysis, perObject)
            : DisplayRenderer.MaskOverlay(state.Mask);
        return ToRgba(DisplayRenderer.Compose(baseBytes, over, opacity));
    }
}
