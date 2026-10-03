namespace QuantScope.Core.Pipeline;

/// <summary>
/// 元に戻す・やり直すための、レシピの記録（JSON）。
/// スライダーを動かし続けたときのように、同じ値への続けての変更は 1 つにまとめる。
/// </summary>
public sealed class UndoHistory
{
    private const int Limit = 200;
    private readonly List<string> _items = [];
    private int _index = -1;
    private string? _lastKey;
    private DateTime _lastAt;

    public bool CanUndo => _index > 0;
    public bool CanRedo => _index >= 0 && _index < _items.Count - 1;

    public void Push(string state, string? coalesceKey)
    {
        if (_index >= 0 && _items[_index] == state) return;
        // やり直しの分は捨てる
        if (_index < _items.Count - 1) _items.RemoveRange(_index + 1, _items.Count - _index - 1);
        bool merge = coalesceKey is not null && coalesceKey == _lastKey && (DateTime.UtcNow - _lastAt).TotalMilliseconds < 1500 && _index > 0;
        if (merge)
        {
            _items[_index] = state;
        }
        else
        {
            _items.Add(state);
            if (_items.Count > Limit) _items.RemoveAt(0);
            _index = _items.Count - 1;
        }
        _lastKey = coalesceKey;
        _lastAt = DateTime.UtcNow;
    }

    public string? Undo()
    {
        if (!CanUndo) return null;
        _lastKey = null;
        return _items[--_index];
    }

    public string? Redo()
    {
        if (!CanRedo) return null;
        _lastKey = null;
        return _items[++_index];
    }

    public void Reset(string state)
    {
        _items.Clear();
        _items.Add(state);
        _index = 0;
        _lastKey = null;
    }
}
