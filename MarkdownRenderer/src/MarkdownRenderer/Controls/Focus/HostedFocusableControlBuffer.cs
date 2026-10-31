using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.Controls.Focus;

// These controls never cross a WinRT collection boundary. Private array
// storage avoids generating projection adapters for this focus-only path.
internal sealed class HostedFocusableControlBuffer
{
    private Control[] _items = Array.Empty<Control>();

    public int Count { get; private set; }

    public Control this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _items[index];
        }
    }

    public void Add(Control control)
    {
        if (Count == _items.Length)
        {
            int capacity = _items.Length == 0
                ? 4
                : _items.Length > Array.MaxLength / 2
                    ? Array.MaxLength
                    : _items.Length * 2;
            if (capacity <= Count)
                throw new OutOfMemoryException();
            Array.Resize(ref _items, capacity);
        }

        _items[Count++] = control;
    }

    public int IndexOf(Control control)
    {
        for (int i = 0; i < Count; i++)
        {
            if (EqualityComparer<Control>.Default.Equals(_items[i], control))
                return i;
        }

        return -1;
    }
}
