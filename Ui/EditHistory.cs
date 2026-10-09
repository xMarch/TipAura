// Bounded undo/redo stacks of immutable states. The caller pushes the state before each change and
// passes the current state when undoing or redoing, so it can be returned to by the opposite step.
internal sealed class EditHistory<T>(int capacity)
{
    private readonly LinkedList<T> _undo = new();
    private readonly Stack<T> _redo = new();

    internal bool CanUndo => _undo.Count > 0;
    internal bool CanRedo => _redo.Count > 0;
    internal int UndoCount => _undo.Count;

    // A new change makes the redo branch unreachable; past the capacity the oldest state is dropped.
    internal void Push(T state)
    {
        _redo.Clear();
        _undo.AddLast(state);
        while (_undo.Count > capacity) _undo.RemoveFirst();
    }

    internal bool TryUndo(T current, out T state)
    {
        if (_undo.Last is not { } last) { state = current; return false; }
        _undo.RemoveLast();
        _redo.Push(current);
        state = last.Value;
        return true;
    }

    internal bool TryRedo(T current, out T state)
    {
        if (!_redo.TryPop(out state!)) { state = current; return false; }
        _undo.AddLast(current);
        return true;
    }

    internal void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
