using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace QuantScope.Web.Components;

/// <summary>線をドラッグできるグラフの元（左右の位置 0〜1 を値に直すのは各グラフ）</summary>
public abstract class ChartBase : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime Js { get; set; } = default!;

    protected ElementReference Box { get; set; }
    private IJSObjectReference? _module;
    private bool _dragging;
    private double _left;
    private double _width = 1;

    /// <summary>グラフの左右の余白（0〜1 の割合）</summary>
    protected virtual double LeftFraction => 0;

    protected abstract bool CanDrag { get; }

    protected abstract void OnDragTo(double fraction);

    protected async Task Down(PointerEventArgs e)
    {
        if (!CanDrag) return;
        _module ??= await Js.InvokeAsync<IJSObjectReference>("import", "./js/chart.js");
        var r = await _module.InvokeAsync<double[]>("rect", Box);
        _left = r[0];
        _width = Math.Max(1, r[1]);
        await _module.InvokeVoidAsync("capture", Box, e.PointerId);
        _dragging = true;
        Drag(e);
    }

    protected void Move(PointerEventArgs e)
    {
        if (_dragging) Drag(e);
    }

    protected void Up() => _dragging = false;

    private void Drag(PointerEventArgs e)
    {
        double f = (((e.ClientX - _left) / _width) - LeftFraction) / (1 - LeftFraction);
        OnDragTo(Math.Clamp(f, 0, 1));
    }

    protected static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
        GC.SuppressFinalize(this);
    }
}
