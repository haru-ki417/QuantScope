using QuantScope.Core.Reporting;

namespace QuantScope.Web.State;

/// <summary>解析レポート（Core の HTML を、ブラウザーで作った画像つきで）</summary>
public static class Reports
{
    public static string AppVersion { get; } =
        typeof(Reports).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "";

    public static async Task<string?> BuildAsync(Workspace ws, BrowserIo io)
    {
        ArgumentNullException.ThrowIfNull(ws);
        ArgumentNullException.ThrowIfNull(io);
        if (ws.Analysis is not { } a || ws.Run is not { } run) return null;
        var st = run.Final;
        var orig = st.Original.Width == st.Image.Width && st.Original.Height == st.Image.Height ? st.Original : st.Image;
        byte[] originalPng = await io.EncodePngAsync(orig.Width, orig.Height, Frames.Image(orig, ws.ColorMap), 1600);
        byte[] overlayPng = await io.EncodePngAsync(orig.Width, orig.Height, Frames.Composite(st, a, ws.ColorMap, ws.ColorPerObject, Math.Max(ws.OverlayOpacity, 0.6)), 1600);
        return AnalysisReport.BuildHtml(new ReportInput
        {
            Recipe = ws.Recipe.Clone(),
            Result = a,
            Calibration = ws.Calibration,
            FileName = ws.FileName,
            ImageDescription = ws.ImageDescription,
            ImageWidth = st.Image.Width,
            ImageHeight = st.Image.Height,
            Outcomes = run.Outcomes,
            RegionText = ws.Roi is null ? null : ws.RoiText,
            AppVersion = AppVersion + "（Web 版）",
            OriginalPng = originalPng,
            OverlayPng = overlayPng,
        });
    }
}
