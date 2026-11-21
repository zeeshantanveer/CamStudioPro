namespace CamStudio.Editor.Model;

/// <summary>Undo/redo as whole-project JSON snapshots. Small projects make this cheap and bulletproof.</summary>
public sealed class History
{
    private const int Limit = 100;
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private string? _coalesceKey;
    private long _coalesceAt;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Records the state before a change. Repeated changes with the same <paramref name="key"/> within
    /// a second (e.g. dragging a slider) collapse into one undo step.
    /// </summary>
    public void Record(EditorProject before, string? key = null)
    {
        var now = Environment.TickCount64;
        if (key != null && key == _coalesceKey && now - _coalesceAt < 1000)
        {
            _coalesceAt = now;
            return;
        }
        _coalesceKey = key;
        _coalesceAt = now;
        _undo.Add(before.ToJson());
        if (_undo.Count > Limit)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    public EditorProject? Undo(EditorProject current)
    {
        if (_undo.Count == 0) return null;
        _redo.Add(current.ToJson());
        var json = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _coalesceKey = null;
        return EditorProject.FromJson(json);
    }

    public EditorProject? Redo(EditorProject current)
    {
        if (_redo.Count == 0) return null;
        _undo.Add(current.ToJson());
        var json = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _coalesceKey = null;
        return EditorProject.FromJson(json);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _coalesceKey = null;
    }

    /// <summary>Ends slider coalescing, so the next change becomes its own step.</summary>
    public void Break() => _coalesceKey = null;
}
