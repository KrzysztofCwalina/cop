using System.Runtime.InteropServices;
using System.Text;

namespace Cop.Repl;

/// <summary>
/// Custom line editor with readline-style editing, history, clipboard support,
/// and integrated completion popup.
/// </summary>
public class LineEditor
{
    private readonly List<string> _history = [];
    private int _historyIndex;
    private readonly CompletionPopup _popup = new();
    private readonly ReplCompleter _completer;
    private int _lastPopupLines;
    private string _prompt = "cop> ";

    public LineEditor(ReplCompleter completer)
    {
        _completer = completer;
    }

    public void SetPrompt(string prompt) => _prompt = prompt;

    /// <summary>
    /// Reads a line from the console with editing support.
    /// Returns null if Ctrl+D is pressed (EOF) or input is exhausted.
    /// </summary>
    public string? ReadLine(string prompt)
    {
        // Fallback for non-interactive (piped) input
        if (Console.IsInputRedirected)
        {
            Console.Write(prompt);
            return Console.ReadLine();
        }

        Console.Write(prompt);

        var buffer = new StringBuilder();
        int cursor = 0;
        _historyIndex = _history.Count;
        string? savedInput = null;

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            // Dismiss popup for keys that move cursor without changing text
            if (_popup.IsVisible && key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow
                or ConsoleKey.Home or ConsoleKey.End or ConsoleKey.Delete)
            {
                ClearPopup();
            }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    if (_popup.IsVisible)
                    {
                        AcceptCompletion(buffer, ref cursor, prompt);
                        ClearPopup();
                    }
                    else
                    {
                        Console.WriteLine();
                        var line = buffer.ToString();
                        if (!string.IsNullOrWhiteSpace(line))
                            AddToHistory(line);
                        return line;
                    }
                    break;

                case ConsoleKey.Escape:
                    if (_popup.IsVisible)
                    {
                        ClearPopup();
                    }
                    else
                    {
                        buffer.Clear();
                        cursor = 0;
                        RedrawLine(prompt, buffer, cursor);
                    }
                    break;

                case ConsoleKey.Tab:
                    if (_popup.IsVisible)
                    {
                        AcceptCompletion(buffer, ref cursor, prompt);
                        ClearPopup();
                    }
                    else
                    {
                        HandleTab(buffer, ref cursor, prompt);
                    }
                    break;

                case ConsoleKey.Backspace:
                    if (cursor > 0)
                    {
                        bool popupWasVisible = _popup.IsVisible;
                        buffer.Remove(cursor - 1, 1);
                        cursor--;
                        RedrawLine(prompt, buffer, cursor);
                        if (popupWasVisible)
                            RefreshPopupFilter(buffer, cursor, prompt);
                    }
                    break;

                case ConsoleKey.Delete:
                    if (cursor < buffer.Length)
                    {
                        buffer.Remove(cursor, 1);
                        RedrawLine(prompt, buffer, cursor);
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (cursor > 0)
                    {
                        cursor--;
                        Console.Write("\x1b[D");
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursor < buffer.Length)
                    {
                        cursor++;
                        Console.Write("\x1b[C");
                    }
                    break;

                case ConsoleKey.Home:
                    cursor = 0;
                    Console.Write($"\r\x1b[{prompt.Length + 1}G");
                    break;

                case ConsoleKey.End:
                    cursor = buffer.Length;
                    Console.Write($"\r\x1b[{prompt.Length + cursor + 1}G");
                    break;

                case ConsoleKey.UpArrow:
                    if (_popup.IsVisible)
                    {
                        _popup.MoveUp();
                        RenderPopup(prompt, cursor);
                    }
                    else
                    {
                        HistoryPrevious(buffer, ref cursor, prompt, ref savedInput);
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (_popup.IsVisible)
                    {
                        _popup.MoveDown();
                        RenderPopup(prompt, cursor);
                    }
                    else
                    {
                        HistoryNext(buffer, ref cursor, prompt, ref savedInput);
                    }
                    break;

                case ConsoleKey.PageUp:
                    if (_popup.IsVisible)
                    {
                        _popup.PageUp();
                        RenderPopup(prompt, cursor);
                    }
                    break;

                case ConsoleKey.PageDown:
                    if (_popup.IsVisible)
                    {
                        _popup.PageDown();
                        RenderPopup(prompt, cursor);
                    }
                    break;

                default:
                    if (key.Key == ConsoleKey.D && key.Modifiers.HasFlag(ConsoleModifiers.Control) && buffer.Length == 0)
                    {
                        Console.WriteLine();
                        return null; // EOF
                    }

                    if (HandleControlKeys(key, buffer, ref cursor, prompt))
                        break;

                    // Regular character
                    if (key.KeyChar >= 32)
                    {
                        bool popupWasVisible = _popup.IsVisible;
                        buffer.Insert(cursor, key.KeyChar);
                        cursor++;
                        RedrawLine(prompt, buffer, cursor);

                        // Auto-trigger completions on context characters
                        if (key.KeyChar == '.' || key.KeyChar == ':')
                        {
                            HandleTab(buffer, ref cursor, prompt);
                        }
                        else if (popupWasVisible)
                        {
                            RefreshPopupFilter(buffer, cursor, prompt);
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Handles Ctrl+key combinations. Returns true if handled.
    /// </summary>
    private bool HandleControlKeys(ConsoleKeyInfo key, StringBuilder buffer, ref int cursor, string prompt)
    {
        if (!key.Modifiers.HasFlag(ConsoleModifiers.Control))
            return false;

        // Ctrl+keys change buffer/cursor in non-incremental ways; dismiss popup
        if (_popup.IsVisible)
            ClearPopup();

        switch (key.Key)
        {
            case ConsoleKey.A: // Move to start
                cursor = 0;
                Console.Write($"\r\x1b[{prompt.Length + 1}G");
                return true;

            case ConsoleKey.E: // Move to end
                cursor = buffer.Length;
                Console.Write($"\r\x1b[{prompt.Length + cursor + 1}G");
                return true;

            case ConsoleKey.K: // Kill to end of line
                if (cursor < buffer.Length)
                {
                    SetClipboard(buffer.ToString(cursor, buffer.Length - cursor));
                    buffer.Remove(cursor, buffer.Length - cursor);
                    RedrawLine(prompt, buffer, cursor);
                }
                return true;

            case ConsoleKey.U: // Kill to start of line
                if (cursor > 0)
                {
                    SetClipboard(buffer.ToString(0, cursor));
                    buffer.Remove(0, cursor);
                    cursor = 0;
                    RedrawLine(prompt, buffer, cursor);
                }
                return true;

            case ConsoleKey.W: // Kill word backward
                int start = cursor;
                while (start > 0 && buffer[start - 1] == ' ') start--;
                while (start > 0 && buffer[start - 1] != ' ') start--;
                if (start < cursor)
                {
                    SetClipboard(buffer.ToString(start, cursor - start));
                    buffer.Remove(start, cursor - start);
                    cursor = start;
                    RedrawLine(prompt, buffer, cursor);
                }
                return true;

            case ConsoleKey.V: // Paste
                string? clip = GetClipboard();
                if (clip is not null)
                {
                    clip = clip.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
                    buffer.Insert(cursor, clip);
                    cursor += clip.Length;
                    RedrawLine(prompt, buffer, cursor);
                }
                return true;

            case ConsoleKey.L: // Clear screen
                Console.Write("\x1b[2J\x1b[H");
                RedrawLine(prompt, buffer, cursor);
                return true;

            default:
                return false;
        }
    }

    private void HandleTab(StringBuilder buffer, ref int cursor, string prompt)
    {
        var input = buffer.ToString();
        var (candidates, replacementStart) = _completer.GetCompletions(input, cursor);

        if (candidates.Count == 0)
        {
            ClearPopup();
            return;
        }

        if (candidates.Count == 1)
        {
            ApplyCompletion(buffer, ref cursor, candidates[0], replacementStart, prompt);
            ClearPopup();
            return;
        }

        // Multiple candidates — show popup
        _popup.Show(candidates);
        RenderPopup(prompt, cursor);
    }

    private void RefreshPopupFilter(StringBuilder buffer, int cursor, string prompt)
    {
        var input = buffer.ToString();
        var (candidates, _) = _completer.GetCompletions(input, cursor);

        if (candidates.Count == 0)
        {
            ClearPopup();
            return;
        }

        if (candidates.Count == 1)
        {
            // Auto-accept when only one candidate remains
            _popup.UpdateItems(candidates);
            RenderPopup(prompt, cursor);
            return;
        }

        _popup.UpdateItems(candidates);
        RenderPopup(prompt, cursor);
    }

    private void AcceptCompletion(StringBuilder buffer, ref int cursor, string prompt)
    {
        var selected = _popup.SelectedItem;
        if (selected is null) return;

        var input = buffer.ToString();
        var (_, replacementStart) = _completer.GetCompletions(input, cursor);
        ApplyCompletion(buffer, ref cursor, selected, replacementStart, prompt);
    }

    private void ApplyCompletion(StringBuilder buffer, ref int cursor, string completion, int replacementStart, string prompt)
    {
        int removeLen = cursor - replacementStart;
        buffer.Remove(replacementStart, removeLen);
        buffer.Insert(replacementStart, completion);
        cursor = replacementStart + completion.Length;
        RedrawLine(prompt, buffer, cursor);
    }

    private void RenderPopup(string prompt, int cursor)
    {
        if (_lastPopupLines > 0)
            Console.Write(_popup.Clear(_lastPopupLines));

        var (content, lines) = _popup.Render(0);
        _lastPopupLines = lines;

        if (!string.IsNullOrEmpty(content))
            Console.Write(content);
    }

    private void ClearPopup()
    {
        if (_lastPopupLines > 0)
        {
            Console.Write(_popup.Clear(_lastPopupLines));
            _lastPopupLines = 0;
        }
        _popup.Hide();
    }

    private void HistoryPrevious(StringBuilder buffer, ref int cursor, string prompt, ref string? savedInput)
    {
        if (_history.Count == 0 || _historyIndex <= 0) return;

        if (_historyIndex == _history.Count)
            savedInput = buffer.ToString();

        _historyIndex--;
        buffer.Clear();
        buffer.Append(_history[_historyIndex]);
        cursor = buffer.Length;
        RedrawLine(prompt, buffer, cursor);
    }

    private void HistoryNext(StringBuilder buffer, ref int cursor, string prompt, ref string? savedInput)
    {
        if (_historyIndex >= _history.Count) return;

        _historyIndex++;
        buffer.Clear();
        if (_historyIndex == _history.Count)
            buffer.Append(savedInput ?? "");
        else
            buffer.Append(_history[_historyIndex]);
        cursor = buffer.Length;
        RedrawLine(prompt, buffer, cursor);
    }

    private void AddToHistory(string line)
    {
        if (_history.Count > 0 && _history[^1] == line) return;
        _history.Add(line);
    }

    private static void RedrawLine(string prompt, StringBuilder buffer, int cursor)
    {
        Console.Write($"\r\x1b[2K{prompt}{buffer}");
        Console.Write($"\r\x1b[{prompt.Length + cursor + 1}G");
    }

    private static void SetClipboard(string text)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-noprofile -command \"Set-Clipboard -Value $input\"")
                {
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.StandardInput.Write(text);
                p?.StandardInput.Close();
                p?.WaitForExit(2000);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("pbcopy")
                {
                    RedirectStandardInput = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.StandardInput.Write(text);
                p?.StandardInput.Close();
                p?.WaitForExit(1000);
            }
        }
        catch { /* clipboard not available */ }
    }

    private static string? GetClipboard()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-noprofile -command Get-Clipboard")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                var result = p?.StandardOutput.ReadToEnd()?.TrimEnd();
                p?.WaitForExit(2000);
                return result;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("pbpaste")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                var result = p?.StandardOutput.ReadToEnd();
                p?.WaitForExit(1000);
                return result;
            }
        }
        catch { /* clipboard not available */ }
        return null;
    }
}
