using Microsoft.AspNetCore.Components;
using QuantScope.Web.State;

namespace QuantScope.Web.Components;

/// <summary>作業の状態が変わったら描き直す部品の元</summary>
public abstract class WsComponent : ComponentBase, IDisposable
{
    [Inject] protected Workspace Ws { get; set; } = default!;

    protected override void OnInitialized() => Ws.Changed += OnChanged;

    private void OnChanged() => InvokeAsync(StateHasChanged);

    protected static string Fmt(double v) => Workspace.Fmt(v);

    public void Dispose()
    {
        Ws.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
