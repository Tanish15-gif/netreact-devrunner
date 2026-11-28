using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NetReact.DevRunner.Cli;

internal sealed class TerminalInput : IDisposable
{
    private const uint MouseInput = 0x0010;
    private const uint QuickEdit = 0x0040;
    private const uint ExtendedFlags = 0x0080;
    private const uint VirtualTerminalInput = 0x0200;
    private readonly nint _handle;
    private readonly uint _originalMode;
    private bool _mouseTracking;
    private readonly List<ConsoleKeyInfo> _escape = [];
    private readonly Queue<ConsoleKeyInfo> _keys = new();
    private long _escapeStarted;

    public TerminalInput()
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = GetStdHandle(-10);
            if (GetConsoleMode(handle, out _originalMode) &&
                SetConsoleMode(handle, (_originalMode | MouseInput | ExtendedFlags) & ~(QuickEdit | VirtualTerminalInput)))
            {
                _handle = handle;
            }
        }

        SetMouseTracking(true);
    }

    public void SetMouseTracking(bool enabled)
    {
        enabled &= _handle != 0 || !OperatingSystem.IsWindows();
        if (enabled == _mouseTracking)
            return;

        if (_handle != 0)
        {
            var mode = (_originalMode | ExtendedFlags) & ~VirtualTerminalInput;
            mode = enabled ? (mode | MouseInput) & ~QuickEdit : (mode | QuickEdit) & ~MouseInput;
            SetConsoleMode(_handle, mode);
        }

        _escape.Clear();
        _mouseTracking = enabled;
        Console.Write(enabled ? "\x1b[?1006h\x1b[?1000h" : "\x1b[?1000l\x1b[?1006l");
    }

    public bool TryRead(out Input input)
    {
        input = default;
        if (_escape.Count > 0 && Stopwatch.GetElapsedTime(_escapeStarted) >= TimeSpan.FromMilliseconds(25))
            ReleaseKeys();
        if (_keys.TryDequeue(out var buffered))
        {
            input = new(buffered);
            return true;
        }

        if (_handle != 0)
        {
            if (!GetNumberOfConsoleInputEvents(_handle, out var count) || count == 0 ||
                !ReadConsoleInput(_handle, out var record, 1, out var read) || read == 0)
                return false;

            if (_mouseTracking && record.EventType == 2 && record.MouseFlags == 4)
            {
                var delta = unchecked((short)(record.ButtonState >> 16));
                input = new(Scroll: -3 * Math.Sign(delta) * Math.Max(1, Math.Abs((int)delta) / 120));
            }
            else if (record.EventType == 1 && record.KeyDown != 0 && record.VirtualKey <= byte.MaxValue)
            {
                var key = new ConsoleKeyInfo((char)record.Character, (ConsoleKey)record.VirtualKey,
                    (record.KeyModifiers & 16) != 0, (record.KeyModifiers & 3) != 0, (record.KeyModifiers & 12) != 0);
                if (record.VirtualKey != 0)
                {
                    input = new(key);
                    for (var i = 1; i < Math.Min((int)record.RepeatCount, 48); i++)
                        _keys.Enqueue(key);
                }
                else
                {
                    DecodeKey(key, out input);
                }
            }
            return true;
        }

        if (!Console.KeyAvailable)
            return false;
        DecodeKey(Console.ReadKey(intercept: true), out input);
        return true;
    }

    private void DecodeKey(ConsoleKeyInfo key, out Input input)
    {
        input = default;
        if (!_mouseTracking || (_escape.Count == 0 && key.KeyChar != '\x1b' &&
            (key.KeyChar != '[' || !key.Modifiers.HasFlag(ConsoleModifiers.Alt))))
        {
            input = new(key);
            return;
        }

        if (_escape.Count == 0)
        {
            _escapeStarted = Stopwatch.GetTimestamp();
            if (key.KeyChar == '[')
                _escape.Add(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false));
        }
        _escape.Add(key);
        if (_escape.Count == 1 || (_escape.Count == 2 && key.KeyChar == '[') ||
            (_escape.Count == 3 && _escape[1].KeyChar == '[' && key.KeyChar == '<'))
            return;

        if (_escape.Count > 3 && _escape[1].KeyChar == '[' && _escape[2].KeyChar == '<')
        {
            if (key.KeyChar is 'M' or 'm')
            {
                var button = new StringBuilder();
                for (var i = 3; i < _escape.Count && _escape[i].KeyChar != ';'; i++)
                    button.Append(_escape[i].KeyChar);
                if (key.KeyChar == 'M' && int.TryParse(button.ToString(), out var code))
                {
                    input = (code & 0b1100_0011) switch
                    {
                        64 => new(Scroll: -3),
                        65 => new(Scroll: 3),
                        _ => default
                    };
                }
                _escape.Clear();
                return;
            }
            if (_escape.Count < 24 && (char.IsAsciiDigit(key.KeyChar) || key.KeyChar == ';'))
                return;
        }

        ReleaseKeys();
        input = new(_keys.Dequeue());
    }

    private void ReleaseKeys()
    {
        foreach (var key in _escape)
            _keys.Enqueue(key);
        _escape.Clear();
    }

    public void Dispose()
    {
        try
        {
            if (_mouseTracking)
                Console.Write("\x1b[?1000l\x1b[?1006l");
        }
        finally
        {
            if (_handle != 0)
                SetConsoleMode(_handle, _originalMode);
        }
    }

    internal readonly record struct Input(ConsoleKeyInfo? Key = null, int Scroll = 0);

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(8)] public ushort RepeatCount;
        [FieldOffset(10)] public ushort VirtualKey;
        [FieldOffset(14)] public ushort Character;
        [FieldOffset(16)] public uint KeyModifiers;
        [FieldOffset(8)] public uint ButtonState;
        [FieldOffset(16)] public uint MouseFlags;
    }

    [DllImport("kernel32")]
    private static extern nint GetStdHandle(int id);
    [DllImport("kernel32")]
    private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32")]
    private static extern bool SetConsoleMode(nint handle, uint mode);
    [DllImport("kernel32")]
    private static extern bool GetNumberOfConsoleInputEvents(nint handle, out uint count);
    [DllImport("kernel32", EntryPoint = "ReadConsoleInputW", ExactSpelling = true)]
    private static extern bool ReadConsoleInput(nint handle, out InputRecord record, uint count, out uint read);
}
