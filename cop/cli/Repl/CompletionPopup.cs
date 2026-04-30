namespace Cop.Repl;

/// <summary>
/// Renders a scrollable completion popup in the terminal using ANSI escape codes.
/// </summary>
public class CompletionPopup
{
    private readonly int _maxVisible;
    private List<string> _items = [];
    private int _selectedIndex;
    private int _scrollOffset;
    private bool _visible;

    public CompletionPopup(int maxVisible = 10)
    {
        _maxVisible = maxVisible;
    }

    public bool IsVisible => _visible;
    public int SelectedIndex => _selectedIndex;
    public string? SelectedItem => _items.Count > 0 ? _items[_selectedIndex] : null;
    public int ItemCount => _items.Count;

    public void Show(List<string> items)
    {
        if (items.Count == 0)
        {
            Hide();
            return;
        }

        _items = items;
        _selectedIndex = 0;
        _scrollOffset = 0;
        _visible = true;
    }

    public void Hide()
    {
        _visible = false;
        _items = [];
        _selectedIndex = 0;
        _scrollOffset = 0;
    }

    public void MoveUp()
    {
        if (!_visible || _items.Count == 0) return;
        _selectedIndex = (_selectedIndex - 1 + _items.Count) % _items.Count;
        EnsureVisible();
    }

    public void MoveDown()
    {
        if (!_visible || _items.Count == 0) return;
        _selectedIndex = (_selectedIndex + 1) % _items.Count;
        EnsureVisible();
    }

    public void PageUp()
    {
        if (!_visible || _items.Count == 0) return;
        _selectedIndex = Math.Max(0, _selectedIndex - _maxVisible);
        EnsureVisible();
    }

    public void PageDown()
    {
        if (!_visible || _items.Count == 0) return;
        _selectedIndex = Math.Min(_items.Count - 1, _selectedIndex + _maxVisible);
        EnsureVisible();
    }

    private void EnsureVisible()
    {
        if (_selectedIndex < _scrollOffset)
            _scrollOffset = _selectedIndex;
        else if (_selectedIndex >= _scrollOffset + _maxVisible)
            _scrollOffset = _selectedIndex - _maxVisible + 1;
    }

    /// <summary>
    /// Renders the popup below the current cursor position.
    /// Returns the ANSI string to write, and the number of lines used.
    /// </summary>
    public (string Content, int Lines) Render(int cursorCol)
    {
        if (!_visible || _items.Count == 0)
            return ("", 0);

        int visibleCount = Math.Min(_maxVisible, _items.Count);
        var sb = new System.Text.StringBuilder();

        // Save cursor, move to next line
        sb.Append("\x1b[s"); // save cursor
        sb.Append('\n');     // move down

        // Draw border top
        int maxWidth = Math.Min(_items.Max(i => i.Length) + 4, 50);
        string border = new string('─', maxWidth);
        sb.Append($"\x1b[{cursorCol + 1}G"); // position at column
        sb.Append($"\x1b[90m┌{border}┐\x1b[0m");

        for (int i = 0; i < visibleCount; i++)
        {
            sb.Append('\n');
            sb.Append($"\x1b[{cursorCol + 1}G"); // position at column

            int idx = _scrollOffset + i;
            string item = _items[idx];
            string padded = item.PadRight(maxWidth);

            if (idx == _selectedIndex)
            {
                // Highlighted item
                sb.Append($"\x1b[90m│\x1b[0m\x1b[7m {padded[..maxWidth]} \x1b[0m\x1b[90m│\x1b[0m");
            }
            else
            {
                sb.Append($"\x1b[90m│\x1b[0m {padded[..maxWidth]} \x1b[90m│\x1b[0m");
            }
        }

        // Scroll indicators
        sb.Append('\n');
        sb.Append($"\x1b[{cursorCol + 1}G");
        string bottomBorder = border;
        if (_items.Count > _maxVisible)
        {
            string indicator = $" {_selectedIndex + 1}/{_items.Count} ";
            int pad = maxWidth - indicator.Length;
            bottomBorder = new string('─', pad / 2) + indicator + new string('─', pad - pad / 2);
        }
        sb.Append($"\x1b[90m└{bottomBorder}┘\x1b[0m");

        // Restore cursor
        sb.Append("\x1b[u"); // restore cursor

        return (sb.ToString(), visibleCount + 2); // +2 for borders
    }

    /// <summary>
    /// Clears the popup from the screen (erases the lines it occupied).
    /// </summary>
    public string Clear(int linesUsed)
    {
        if (linesUsed == 0) return "";

        var sb = new System.Text.StringBuilder();
        sb.Append("\x1b[s"); // save cursor

        for (int i = 0; i < linesUsed + 1; i++)
        {
            sb.Append('\n');
            sb.Append("\x1b[2K"); // clear line
        }

        sb.Append("\x1b[u"); // restore cursor
        return sb.ToString();
    }
}
