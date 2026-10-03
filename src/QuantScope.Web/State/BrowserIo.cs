using Microsoft.JSInterop;

namespace QuantScope.Web.State;

/// <summary>ブラウザーの機能（ファイルの書き出し・画像の読み込み・コピー）。ファイルはブラウザーの外に送らない。</summary>
public sealed class BrowserIo(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    private async ValueTask<IJSObjectReference> Module() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/io.js");

    public async Task DownloadAsync(string name, string mime, byte[] bytes) =>
        await (await Module()).InvokeVoidAsync("download", name, mime, bytes);

    public async Task<byte[]?> DecodeImageAsync(byte[] bytes, string type) =>
        await (await Module()).InvokeAsync<byte[]?>("decodeImage", bytes, type);

    public async Task<byte[]> EncodePngAsync(int width, int height, byte[] rgba, int maxEdge) =>
        await (await Module()).InvokeAsync<byte[]>("encodePng", width, height, rgba, maxEdge);

    public async Task<bool> OpenHtmlAsync(string html) => await (await Module()).InvokeAsync<bool>("openHtml", html);

    public async Task<bool> CopyTextAsync(string text) => await (await Module()).InvokeAsync<bool>("copyText", text);

    public async Task<string?> LoadAsync(string key) => await (await Module()).InvokeAsync<string?>("load", key);

    public async Task SaveAsync(string key, string value) => await (await Module()).InvokeVoidAsync("save", key, value);

    public async Task ListenKeysAsync<T>(DotNetObjectReference<T> target) where T : class =>
        await (await Module()).InvokeVoidAsync("listenKeys", target);

    public async Task ClickAsync(string id) => await (await Module()).InvokeVoidAsync("clickElement", id);

    public async Task<bool> IsNarrowAsync() => await (await Module()).InvokeAsync<bool>("isNarrow");

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
    }
}
