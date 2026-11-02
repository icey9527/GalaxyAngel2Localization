using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceSubEditor
{
    public interface ICommand
    {
        void Undo();
        void Redo();
        string Describe();
    }

    /// <summary>台词修改命令: 支持单条/批量/跨文件, 统一进撤销栈</summary>
    public class TextChangeCommand : ICommand
    {
        public class Change
        {
            public string File;
            public TblEntry Entry;      // 台词槽(可能为 null=当时不存在, 由命令创建)
            public TblSection Section;
            public int RowIndex;
            public string OldText, NewText;
            public int TextKey;
        }

        readonly DocumentStore store;
        public readonly List<Change> Changes = new List<Change>();
        public string Description = "修改台词";

        public TextChangeCommand(DocumentStore store) { this.store = store; }

        public void Add(string file, TblSection sec, int rowIndex, TblEntry slot, string oldText, string newText)
        {
            Changes.Add(new Change
            {
                File = file, Section = sec, RowIndex = rowIndex, Entry = slot,
                OldText = oldText, NewText = newText,
                TextKey = sec.NumericEntries.Count + rowIndex
            });
        }

        public bool IsEmpty { get { return Changes.All(c => (c.OldText ?? "") == (c.NewText ?? "")); } }

        public void Redo() { Apply(c => c.NewText); }
        public void Undo() { Apply(c => c.OldText); }

        void Apply(Func<Change, string> pick)
        {
            foreach (var c in Changes)
            {
                string val = pick(c) ?? "";
                if (c.Entry == null)
                {
                    // 撤销"新建": 文本回到空 → 删除该槽
                    if (val.Length == 0)
                    {
                        var dead = c.Section.Entries.FirstOrDefault(e => !e.IsNumeric && e.Key == c.TextKey);
                        if (dead != null) c.Section.Entries.Remove(dead);
                    }
                    else
                    {
                        var slot = c.Section.GetTextSlot(c.RowIndex, true);
                        slot.Text = val;
                        if (c.Entry == null) c.Entry = slot;   // 记住新建的槽
                    }
                }
                else if (val.Length == 0 && c.OldText.Length == 0)
                {
                    // 该槽是本次命令首次创建的空台词，撤销时应保持不存在。
                    var created = c.Section.Entries.FirstOrDefault(e => ReferenceEquals(e, c.Entry));
                    if (created != null) c.Section.Entries.Remove(created);
                    c.Entry = null;
                }
                else c.Entry.Text = val;
                store.MarkModified(c.File);
            }
            store.RaiseChanged();
        }

        public string Describe()
        {
            return Description + " ×" + Changes.Count;
        }
    }

    public class UndoManager
    {
        readonly Stack<ICommand> undoStack = new Stack<ICommand>();
        readonly Stack<ICommand> redoStack = new Stack<ICommand>();
        const int Limit = 500;

        public event Action StateChanged;
        public string LastDescription { get; private set; }

        /// <summary>执行并入栈(命令自身负责生效)</summary>
        public void Execute(ICommand cmd)
        {
            cmd.Redo();
            undoStack.Push(cmd);
            if (undoStack.Count > Limit) { var arr = undoStack.ToArray(); undoStack.Clear(); foreach (var c in arr.Reverse().Skip(1)) undoStack.Push(c); }
            redoStack.Clear();
            LastDescription = cmd.Describe();
            var h = StateChanged; if (h != null) h();
        }

        public bool CanUndo { get { return undoStack.Count > 0; } }
        public bool CanRedo { get { return redoStack.Count > 0; } }

        public void StackInfo(out int undoCount, out int redoCount)
        {
            undoCount = undoStack.Count;
            redoCount = redoStack.Count;
        }

        public void Undo()
        {
            if (undoStack.Count == 0) return;
            var cmd = undoStack.Pop();
            cmd.Undo();
            redoStack.Push(cmd);
            LastDescription = "撤销: " + cmd.Describe();
            var h = StateChanged; if (h != null) h();
        }

        public void Redo()
        {
            if (redoStack.Count == 0) return;
            var cmd = redoStack.Pop();
            cmd.Redo();
            undoStack.Push(cmd);
            LastDescription = "重做: " + cmd.Describe();
            var h = StateChanged; if (h != null) h();
        }
    }
}
