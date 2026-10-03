using System.Net;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using QuantScope.Core.Ai;
using QuantScope.Core.Analysis;
using QuantScope.Core.Dicom;

namespace QuantScope.Tests;

public class IoTests
{
    private static DicomDataset Ct(short[] raw, int w, int h, string photometric = "MONOCHROME2")
    {
        var ds = new DicomDataset
        {
            { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID() },
            { DicomTag.Modality, "CT" },
            { DicomTag.PatientName, "Test^Patient" },
            { DicomTag.PhotometricInterpretation, photometric },
            { DicomTag.Rows, (ushort)h },
            { DicomTag.Columns, (ushort)w },
            { DicomTag.BitsAllocated, (ushort)16 },
            { DicomTag.BitsStored, (ushort)16 },
            { DicomTag.HighBit, (ushort)15 },
            { DicomTag.PixelRepresentation, (ushort)1 },
            { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.RescaleSlope, 1.0m },
            { DicomTag.RescaleIntercept, -1024m },
            { DicomTag.PixelSpacing, 0.7m, 0.7m },
        };
        var bytes = new byte[raw.Length * 2];
        Buffer.BlockCopy(raw, 0, bytes, 0, bytes.Length);
        var pd = DicomPixelData.Create(ds, true);
        pd.AddFrame(new MemoryByteBuffer(bytes));
        return ds;
    }

    [Fact]
    public void DICOMはHUに直し_画素間隔を縮尺にし_患者名を説明に含めない()
    {
        var ds = Ct([0, 1024, 2048, 1064], 2, 2);
        string path = Path.Combine(Path.GetTempPath(), $"qs-{Guid.NewGuid():N}.dcm");
        try
        {
            new DicomFile(ds).Save(path);
            var img = DicomImageLoader.Load(path);
            Assert.Equal([-1024f, 0, 1024, 40], img.Raster.Data);
            Assert.Equal(0.7, img.Calibration!.UnitsPerPixel, 9);
            Assert.Equal("mm", img.Calibration.Unit);
            Assert.DoesNotContain("Test", img.Description, StringComparison.Ordinal);
            Assert.Contains("CT", img.Description, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MONOCHROME1は明るいほど大きい値にそろえる()
    {
        var img = DicomImageLoader.FromDataset(Ct([0, 100], 2, 1, "MONOCHROME1"));
        Assert.True(img.Raster.Data[0] > img.Raster.Data[1]);
    }

    [Fact]
    public void DICOMでないファイルは理由を添えて断る()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qs-{Guid.NewGuid():N}.dcm");
        try
        {
            File.WriteAllText(path, "hello");
            Assert.Throws<InvalidDataException>(() => DicomImageLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AIに送る内容は画像と手順と数値だけ()
    {
        var req = new DescribeRequest([1, 2, 3], ["ぼかし（ガウス）", "二値化（しきい値）"], new AnalysisSummary { Count = 12, MeanArea = 3.5, AreaUnit = "µm²" }, null);
        string json = ImageDescriber.BuildPayload(req, "");
        Assert.Contains("\"model\":\"gpt-4o-mini\"", json, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,AQID", json, StringComparison.Ordinal);
        Assert.Contains("数 12", json, StringComparison.Ordinal);
        Assert.Contains("診断はせず", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AIの返事から文章を取り出す()
    {
        Assert.Equal("説明です", ImageDescriber.ParseResponse("{\"choices\":[{\"message\":{\"content\":\" 説明です \"}}]}"));
        Assert.Throws<AiException>(() => ImageDescriber.ParseResponse("{\"choices\":[]}"));
        Assert.Throws<AiException>(() => ImageDescriber.ParseResponse("<html>"));
    }

    [Fact]
    public async Task キーが違うときは分かりやすく伝え_キーそのものは出さない()
    {
        var handler = new FakeHandler(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Incorrect API key provided: sk-proj-abcd1234efgh.\"}}");
        var ai = new ImageDescriber(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<AiException>(() => ai.DescribeAsync(new DescribeRequest([1], [], null, null), "sk-proj-abcd1234efgh", "", TestContext.Current.CancellationToken));
        Assert.Contains("API キーが正しくない", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcd1234", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Bearer sk-proj-abcd1234efgh", handler.LastAuthorization);
        Assert.Equal(ImageDescriber.Endpoint, handler.LastUri);
    }

    [Fact]
    public async Task キーがなければ送らない()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var ai = new ImageDescriber(new HttpClient(handler));
        await Assert.ThrowsAsync<AiException>(() => ai.DescribeAsync(new DescribeRequest([1], [], null, null), " ", "", TestContext.Current.CancellationToken));
        Assert.Null(handler.LastUri);
    }

    [Fact]
    public async Task うまくいけば説明を返す()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"核が写っています\"}}]}");
        var ai = new ImageDescriber(new HttpClient(handler));
        string text = await ai.DescribeAsync(new DescribeRequest([1], [], null, "何が写っていますか"), "sk-test", "gpt-4o", TestContext.Current.CancellationToken);
        Assert.Equal("核が写っています", text);
        Assert.Contains("\"model\":\"gpt-4o\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("何が写っていますか", handler.LastBody, StringComparison.Ordinal);
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastAuthorization { get; private set; }
        public Uri? LastUri { get; private set; }
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
