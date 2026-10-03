using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuantScope.Core.Analysis;

namespace QuantScope.Core.Ai;

/// <summary>AI に送る内容。画像（PNG）と、行った手順の名前・計測のまとめだけ。ファイル名や画像の付帯情報は送らない。</summary>
public sealed record DescribeRequest(byte[] Png, IReadOnlyList<string> StepTitles, AnalysisSummary? Summary, string? Question);

public sealed class AiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 画像の説明を、OpenAI の API（Chat Completions、画像入力に対応したモデル）に書いてもらう。
/// キーは使う人が設定画面で入れる（アプリには入っていない）。
/// </summary>
public sealed class ImageDescriber
{
    public const string DefaultModel = "gpt-4o-mini";
    public static readonly Uri Endpoint = new("https://api.openai.com/v1/chat/completions");

    // 日本語をそのまま書く（\uXXXX にしない）。どちらも正しい JSON
    private static readonly JsonSerializerOptions PayloadJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;

    public ImageDescriber(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    public static string SystemPrompt =>
        "あなたは画像解析を学ぶ学生・研究者を手伝うアシスタントです。渡された画像について、日本語で答えてください。" +
        "写っているもの・見どころ・画像解析で注意すべき点（照明のむら、ノイズ、くっついた対象など）を、短く具体的に説明します。" +
        "医療画像であっても診断はせず、所見の可能性を述べるときは「可能性」とはっきり書き、専門家の確認が必要だと添えてください。" +
        "計測の数値が添えられていれば、その解釈と、より正確に測るための手順の工夫も提案してください。";

    /// <summary>送る JSON（テストで中身を確かめられるよう分けてある）</summary>
    public static string BuildPayload(DescribeRequest request, string model)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = new StringBuilder();
        text.Append(string.IsNullOrWhiteSpace(request.Question) ? "この画像を説明してください。" : request.Question.Trim());
        if (request.StepTitles.Count > 0) text.Append("\n\n行った処理: ").Append(string.Join(" → ", request.StepTitles));
        if (request.Summary is { } s)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n計測のまとめ: 数 {s.Count}、平均の面積 {s.MeanArea:0.##} {s.AreaUnit}、占有率 {s.AreaFraction:0.##}%、平均の円形度 {s.MeanCircularity:0.###}");
        }
        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim(),
            ["max_tokens"] = 700,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = text.ToString() },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + Convert.ToBase64String(request.Png), ["detail"] = "auto" },
                        },
                    },
                },
            },
        };
        return payload.ToJsonString(PayloadJson);
    }

    /// <summary>返ってきた JSON から文章を取り出す</summary>
    public static string ParseResponse(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            string? content = node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(content)) throw new AiException("AI から説明が返ってきませんでした。");
            return content.Trim();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException or InvalidOperationException or FormatException)
        {
            throw new AiException("AI の返事を読めませんでした。", ex);
        }
    }

    public async Task<string> DescribeAsync(DescribeRequest request, string apiKey, string model, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new AiException("API キーが設定されていません。「設定」から入れてください。");
        using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(BuildPayload(request, model), Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(msg, cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AiException("インターネットにつながらないか、AI のサーバーに届きませんでした。", ex);
        }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            throw new AiException("AI の返事が時間内に来ませんでした。少し待ってからもう一度試してください。", ex);
        }
        using (res)
        {
            string body = await res.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            if (res.IsSuccessStatusCode) return ParseResponse(body);
            throw new AiException(ErrorMessage(res.StatusCode, body));
        }
    }

    /// <summary>失敗したときの、分かりやすい説明（キーそのものは含めない）</summary>
    internal static string ErrorMessage(HttpStatusCode status, string body)
    {
        string? detail = null;
        try
        {
            detail = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }
        string head = status switch
        {
            HttpStatusCode.Unauthorized => "API キーが正しくないか、無効になっています。「設定」で確かめてください。",
            HttpStatusCode.TooManyRequests => "使える量の上限に達したか、短い時間に送りすぎました。しばらく待つか、OpenAI の利用状況を確かめてください。",
            HttpStatusCode.NotFound => "指定したモデルが使えません。「設定」でモデル名を確かめてください。",
            HttpStatusCode.BadRequest => "AI が受け付けない内容でした。",
            _ => $"AI のサーバーでエラーが起きました（{(int)status}）。",
        };
        if (!string.IsNullOrWhiteSpace(detail)) head += "\n" + Redact(detail);
        return head;
    }

    /// <summary>エラーの文にキーの一部が含まれていても、画面に出さない</summary>
    internal static string Redact(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"sk-[A-Za-z0-9_\-\*]{4,}", "sk-…");
}
