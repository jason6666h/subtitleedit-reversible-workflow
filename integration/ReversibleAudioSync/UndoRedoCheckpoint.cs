using System.Linq;

namespace Nikse.SubtitleEdit.Logic.UndoRedo;

/// <summary>
/// Opaque, detached copy of both history stacks for rollback within one manager.
/// Does not capture the live document, media, timer state, or persistent recovery files.
/// </summary>
public sealed class UndoRedoCheckpoint
{
    private readonly UndoRedoItem[] _undo;
    private readonly UndoRedoItem[] _redo;
    internal object Owner { get; }

    internal UndoRedoCheckpoint(object owner, UndoRedoItem[] undo, UndoRedoItem[] redo)
    {
        Owner = owner;
        _undo = CloneItems(undo);
        _redo = CloneItems(redo);
    }

    internal (UndoRedoItem[] Undo, UndoRedoItem[] Redo) CopyStacks() =>
        (CloneItems(_undo), CloneItems(_redo));

    private static UndoRedoItem[] CloneItems(UndoRedoItem[] items) =>
        items.Select(item => UndoRedoItem.Clone(item)!).ToArray();
}
