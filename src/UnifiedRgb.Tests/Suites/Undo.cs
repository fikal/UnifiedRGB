using UnifiedRgb.Core;

namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| Undo and redo for both editors: the stack itself, the design |
| snapshots it actually carries, and gesture grouping.         |
|                                                              |
| The stack stores the state BEFORE each change, which is the  |
| detail every bug in this area has come back to, so the first |
| section walks the whole contract in one go: stepping back    |
| and forward, abandoning the future on a fresh edit, the      |
| bounded history dropping its OLDEST entry, and the Changed   |
| event the view binds its enabled states to.                  |
|                                                              |
| The other two sections are the stack in the shape the app    |
| uses it. A design snapshot is one JSON string, so it has to  |
| survive a round trip untouched; and a drag is one undo step  |
| no matter how many mouse moves it took, which is the         |
| reported bug that put the gesture API on the stack in the    |
| first place.                                                 |
\*-----------------------------------------------------------*/
static class UndoSuite
{
    public static void Run(Harness t)
    {
        t.Section("UndoStack (#f3)");
        {
            var h = new UndoStack<string>(capacity: 3);
            t.Check(!h.CanUndo && !h.CanRedo, "undo: empty to start");
            t.Check(h.Undo("now") == null, "undo: nothing to undo returns null");
            t.Check(h.Redo("now") == null, "undo: nothing to redo returns null");

            // Push records the state BEFORE each change; the argument to Undo is the
            // state as it is now, so redo can come back to it.
            h.Push("a");            // about to go a -> b
            h.Push("b");            // about to go b -> c
            t.Check(h.CanUndo && !h.CanRedo, "undo: pushes are undoable, nothing to redo yet");

            t.Equal("b", h.Undo("c"), "undo: steps back one");
            t.Equal("a", h.Undo("b"), "undo: steps back again");
            t.Check(!h.CanUndo && h.CanRedo, "undo: exhausted, redo available");
            t.Check(h.Undo("a") == null, "undo: past the start returns null");

            t.Equal("b", h.Redo("a"), "redo: steps forward one");
            t.Equal("c", h.Redo("b"), "redo: steps forward again");
            t.Check(!h.CanRedo && h.CanUndo, "redo: exhausted, undo available");

            // A new edit after undoing abandons the future.
            h.Undo("c");
            t.Check(h.CanRedo, "undo: redo exists after stepping back");
            h.Push("x");
            t.Check(!h.CanRedo, "undo: a new edit clears redo");

            // Capacity drops the OLDEST entry, so recent history always survives.
            var cap = new UndoStack<int>(capacity: 3);
            for (int i = 1; i <= 5; i++) cap.Push(i);
            t.Equal(3, cap.Count, "undo: bounded at capacity");
            t.Equal(5, cap.Undo(6), "undo: newest entry kept");
            t.Equal(4, cap.Undo(5), "undo: second newest kept");
            t.Equal(3, cap.Undo(4), "undo: third newest kept");
            t.Check(cap.Undo(3) == 0, "undo: the oldest two fell off");

            var cleared = new UndoStack<string>();
            cleared.Push("a"); cleared.Undo("b");
            cleared.Clear();
            t.Check(!cleared.CanUndo && !cleared.CanRedo, "undo: Clear empties both sides");

            // The view binds to CanUndo/CanRedo, so changes have to be announced.
            int fired = 0;
            var watched = new UndoStack<string>();
            watched.Changed += () => fired++;
            watched.Push("a"); watched.Undo("b"); watched.Redo("a"); watched.Clear();
            t.Equal(4, fired, "undo: every mutation raises Changed");
        }

        t.Section("UndoStack round-trips a design snapshot (#f3)");
        {
            // What the designer actually stores: whole-design JSON.
            string before = "{\"BgX\":10,\"BgY\":20,\"Elements\":[]}";
            string after = "{\"BgX\":99,\"BgY\":20,\"Elements\":[]}";
            var h = new UndoStack<string>();
            h.Push(before);
            t.Equal(before, h.Undo(after), "undo: a design snapshot comes back intact");
            t.Equal(after, h.Redo(before), "redo: the newer design comes back intact");
        }

        t.Section("UndoStack: a drag is one undo step (#f3)");
        {
            // The reported bug: a drag that pauses part way through (lining an element
            // up against a snap guide) used to split into several undo steps, so one
            // Ctrl+Z only walked back part of the move. Mouse down to mouse up is one
            // entry, however many moves and however long it takes.
            var h = new UndoStack<string>();
            h.BeginGesture("x=0");
            t.Check(h.InGesture, "gesture: mouse down opens a gesture");
            for (int x = 1; x <= 40; x++) h.GestureEdit();     // every mouse move
            h.EndGesture();
            t.Equal(1, h.Count, "gesture: forty moves are one undo step");
            t.Equal("x=0", h.Undo("x=40"), "gesture: undo returns to where the drag began");

            // A click that selects but moves nothing must not leave a dead step behind,
            // because Push clears redo and the user would silently lose their redo.
            var sel = new UndoStack<string>();
            sel.Push("a");
            sel.Undo("b");                                     // redo now holds "b"
            t.Check(sel.CanRedo, "gesture: redo available before the click");
            sel.BeginGesture("a");
            sel.EndGesture();                                  // no GestureEdit: nothing moved
            t.Equal(0, sel.Count, "gesture: a click that moves nothing records nothing");
            t.Check(sel.CanRedo, "gesture: a click that moves nothing keeps redo alive");

            // Two separate drags are two separate steps.
            var two = new UndoStack<string>();
            two.BeginGesture("p0"); two.GestureEdit(); two.GestureEdit(); two.EndGesture();
            two.BeginGesture("p1"); two.GestureEdit(); two.EndGesture();
            t.Equal(2, two.Count, "gesture: each drag is its own step");
            t.Equal("p1", two.Undo("p2"), "gesture: undo walks back one drag at a time");
            t.Equal("p0", two.Undo("p1"), "gesture: and then the one before it");

            // Edits outside a gesture are the caller's business, not the stack's.
            var loose = new UndoStack<string>();
            loose.GestureEdit();
            t.Equal(0, loose.Count, "gesture: an edit with no gesture open records nothing");

            // Capture lost to an Alt+Tab ends the gesture; the next drag is new.
            var lost = new UndoStack<string>();
            lost.BeginGesture("a"); lost.GestureEdit();
            lost.EndGesture();
            t.Check(!lost.InGesture, "gesture: EndGesture closes it");
            lost.BeginGesture("b"); lost.GestureEdit(); lost.EndGesture();
            t.Equal(2, lost.Count, "gesture: a drag after a lost capture is its own step");
        }
    }
}
