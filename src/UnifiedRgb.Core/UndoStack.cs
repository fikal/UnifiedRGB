namespace UnifiedRgb.Core;

/// <summary>Undo/redo over whole-state snapshots.
///
/// Snapshots rather than commands: an LCD design is a few KB of JSON, the
/// editor mutates it from a dozen places (drag, grip, add, delete, every
/// property box), and a command per edit would be a dozen chances to forget
/// one. Push the state BEFORE a change and the history is right by
/// construction.
///
/// Bounded, because "undo everything since launch" is not worth unbounded
/// memory: the oldest entry falls off once Capacity is reached.</summary>
public sealed class UndoStack<T>
{
    readonly List<T> _undo = new();
    readonly List<T> _redo = new();

    public UndoStack(int capacity = 50) => Capacity = Math.Max(1, capacity);

    public int Capacity { get; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int Count => _undo.Count;

    /// <summary>Raised whenever CanUndo/CanRedo may have changed, so a view can
    /// re-evaluate its buttons without polling.</summary>
    public event Action? Changed;

    /// <summary>Record the state as it was BEFORE the change being made now.
    /// Redo is dropped: the future stops being reachable the moment history
    /// takes a different branch.</summary>
    public void Push(T before)
    {
        _undo.Add(before);
        if (_undo.Count > Capacity) _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>Step back. Hand in the CURRENT state so redo can return to it.
    /// Null when there is nothing to undo.</summary>
    public T? Undo(T current)
    {
        if (_undo.Count == 0) return default;
        var prev = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(current);
        Changed?.Invoke();
        return prev;
    }

    /// <summary>Step forward again, current state going back onto the undo
    /// side. Null when there is nothing to redo.</summary>
    public T? Redo(T current)
    {
        if (_redo.Count == 0) return default;
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(current);
        if (_undo.Count > Capacity) _undo.RemoveAt(0);
        Changed?.Invoke();
        return next;
    }

    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0) return;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}
