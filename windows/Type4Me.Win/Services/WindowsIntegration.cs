using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.CoreAudioApi;
using Type4Me.Win.Core;

namespace Type4Me.Win.Services;

public sealed class AudioCaptureService : IDisposable
{
    private WasapiCapture? _capture;
    private readonly object _lock = new();
    private readonly MemoryStream _chunkBuffer = new();
    private bool _disposed;

    public event Action<byte[]>? AudioChunkReady;
    public event Action<float>? AudioLevel;

    public void Start()
    {
        ThrowIfDisposed();
        Stop();
        _capture = new WasapiCapture();
        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    public void Stop()
    {
        if (_capture is null) return;
        _capture.DataAvailable -= OnDataAvailable;
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
        _capture = null;
        FlushChunkBuffer();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture is null || e.BytesRecorded <= 0) return;
        var converted = ConvertToPcm16Mono16k(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray(), _capture.WaveFormat);
        if (converted.Length == 0) return;

        AudioLevel?.Invoke(CalculateLevel(converted));
        lock (_lock)
        {
            _chunkBuffer.Write(converted);
            while (_chunkBuffer.Length >= 6400)
            {
                var all = _chunkBuffer.ToArray();
                var chunk = all[..6400];
                var remainder = all[6400..];
                _chunkBuffer.SetLength(0);
                _chunkBuffer.Write(remainder);
                AudioChunkReady?.Invoke(chunk);
            }
        }
    }

    private void FlushChunkBuffer()
    {
        lock (_lock)
        {
            if (_chunkBuffer.Length > 0)
            {
                AudioChunkReady?.Invoke(_chunkBuffer.ToArray());
                _chunkBuffer.SetLength(0);
            }
        }
    }

    public static byte[] ConvertToPcm16Mono16k(byte[] source, WaveFormat sourceFormat)
    {
        using var raw = new RawSourceWaveStream(source, 0, source.Length, sourceFormat);
        ISampleProvider sampleProvider = raw.ToSampleProvider();
        if (sourceFormat.Channels > 1)
        {
            sampleProvider = new StereoToMonoSampleProvider(sampleProvider);
        }
        if (sampleProvider.WaveFormat.SampleRate != 16000)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 16000);
        }

        var samples = new float[source.Length / Math.Max(1, sourceFormat.BlockAlign) * 2 + 4096];
        var read = sampleProvider.Read(samples, 0, samples.Length);
        var output = new byte[read * 2];
        for (var i = 0; i < read; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            var value = (short)(clamped * short.MaxValue);
            output[i * 2] = (byte)(value & 0xFF);
            output[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return output;
    }

    private static float CalculateLevel(byte[] pcm)
    {
        if (pcm.Length < 2) return 0;
        double sum = 0;
        var count = pcm.Length / 2;
        for (var i = 0; i < pcm.Length; i += 2)
        {
            var sample = BitConverter.ToInt16(pcm, i) / 32768.0;
            sum += sample * sample;
        }
        return (float)Math.Min(1.0, Math.Sqrt(sum / count) * 4);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _chunkBuffer.Dispose();
    }
}

public sealed record HotkeyBinding(ProcessingMode Mode, Action<ProcessingMode, IntPtr> Start, Action<ProcessingMode> Stop, Action Abort);

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const int LLKHF_EXTENDED = 0x01;
    private const int LLKHF_UP = 0x80;
    private const uint MAPVK_VSC_TO_VK_EX = 0x03;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private List<HotkeyBinding> _bindings = [];
    private readonly HashSet<Guid> _activeHoldModes = [];
    private readonly Dictionary<Guid, HotkeyBinding> _activeHoldBindings = [];
    private readonly HashSet<int> _pressedKeys = [];
    private readonly object _stateGate = new();
    private readonly System.Threading.Timer _holdWatchdog;

    public GlobalHotkeyService()
    {
        _proc = HookCallback;
        _holdWatchdog = new System.Threading.Timer(CheckActiveHoldKeys, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50));
    }

    public void Register(IReadOnlyList<HotkeyBinding> bindings)
    {
        _bindings = bindings.ToList();
        if (_hook == IntPtr.Zero)
        {
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule!;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vkCode = NormalizeVirtualKey((int)info.vkCode, info.scanCode, info.flags);
            var isDown = (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN) && (info.flags & LLKHF_UP) == 0;
            var isUp = wParam == WM_KEYUP || wParam == WM_SYSKEYUP || (info.flags & LLKHF_UP) != 0;
            var wasPressed = _pressedKeys.Contains(vkCode);

            if (isDown && vkCode == NativeVirtualKeys.Escape)
            {
                foreach (var binding in _bindings) binding.Abort();
                return 1;
            }

            if (isDown)
            {
                _pressedKeys.Add(vkCode);
            }

            var suppress = false;
            foreach (var binding in _bindings)
            {
                if (binding.Mode.HotkeyStyle == HotkeyStyle.Hold)
                {
                    if (isUp && _activeHoldModes.Contains(binding.Mode.Id) &&
                        (KeyMatches(binding.Mode.HotkeyVirtualKey, vkCode) || IsConfiguredModifierKey(vkCode, binding.Mode.HotkeyModifiers)))
                    {
                        StopHoldMode(binding);
                        suppress = ShouldSuppressHotkey(binding.Mode);
                        break;
                    }

                    if (isDown &&
                        !wasPressed &&
                        KeyMatches(binding.Mode.HotkeyVirtualKey, vkCode) &&
                        ModifiersMatch(binding.Mode.HotkeyModifiers, binding.Mode.HotkeyVirtualKey) &&
                        StartHoldMode(binding))
                    {
                        binding.Start(binding.Mode, GetForegroundWindow());
                        suppress = ShouldSuppressHotkey(binding.Mode);
                        break;
                    }
                    continue;
                }

                if (binding.Mode.HotkeyStyle == HotkeyStyle.Toggle &&
                    isDown &&
                    !wasPressed &&
                    KeyMatches(binding.Mode.HotkeyVirtualKey, vkCode) &&
                    ModifiersMatch(binding.Mode.HotkeyModifiers, binding.Mode.HotkeyVirtualKey))
                {
                    binding.Start(binding.Mode, GetForegroundWindow());
                    suppress = ShouldSuppressHotkey(binding.Mode);
                    break;
                }
            }

            if (isUp)
            {
                _pressedKeys.Remove(vkCode);
                RemoveGenericEquivalent(vkCode);
            }

            if (suppress)
            {
                return 1;
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool ModifiersMatch(uint expected, int primaryVirtualKey)
    {
        if (expected == 0 && IsModifierVirtualKey(primaryVirtualKey))
        {
            return true;
        }

        var actual = 0u;
        if (IsPressedModifier(NativeHotkeyModifiers.Control)) actual |= NativeHotkeyModifiers.Control;
        if (IsPressedModifier(NativeHotkeyModifiers.Shift)) actual |= NativeHotkeyModifiers.Shift;
        if (IsPressedModifier(NativeHotkeyModifiers.Alt)) actual |= NativeHotkeyModifiers.Alt;
        if (IsPressedModifier(NativeHotkeyModifiers.Win)) actual |= NativeHotkeyModifiers.Win;
        return actual == expected;
    }

    private bool IsPressedModifier(uint modifier) => modifier switch
    {
        NativeHotkeyModifiers.Control => _pressedKeys.Contains(NativeVirtualKeys.Control) ||
                                         _pressedKeys.Contains(NativeVirtualKeys.LControl) ||
                                         _pressedKeys.Contains(NativeVirtualKeys.RControl),
        NativeHotkeyModifiers.Shift => _pressedKeys.Contains(NativeVirtualKeys.Shift) ||
                                       _pressedKeys.Contains(NativeVirtualKeys.LShift) ||
                                       _pressedKeys.Contains(NativeVirtualKeys.RShift),
        NativeHotkeyModifiers.Alt => _pressedKeys.Contains(NativeVirtualKeys.Menu) ||
                                     _pressedKeys.Contains(NativeVirtualKeys.LMenu) ||
                                     _pressedKeys.Contains(NativeVirtualKeys.RMenu),
        NativeHotkeyModifiers.Win => _pressedKeys.Contains(NativeVirtualKeys.LWin) ||
                                     _pressedKeys.Contains(NativeVirtualKeys.RWin),
        _ => false
    };

    private static bool IsConfiguredModifierKey(int virtualKey, uint modifiers)
    {
        return ((virtualKey == NativeVirtualKeys.Control || virtualKey == NativeVirtualKeys.LControl || virtualKey == NativeVirtualKeys.RControl) && (modifiers & NativeHotkeyModifiers.Control) != 0) ||
               ((virtualKey == NativeVirtualKeys.Shift || virtualKey == NativeVirtualKeys.LShift || virtualKey == NativeVirtualKeys.RShift) && (modifiers & NativeHotkeyModifiers.Shift) != 0) ||
               ((virtualKey == NativeVirtualKeys.Menu || virtualKey == NativeVirtualKeys.LMenu || virtualKey == NativeVirtualKeys.RMenu) && (modifiers & NativeHotkeyModifiers.Alt) != 0) ||
               ((virtualKey == NativeVirtualKeys.LWin || virtualKey == NativeVirtualKeys.RWin) && (modifiers & NativeHotkeyModifiers.Win) != 0);
    }

    private static bool KeyMatches(int configuredVirtualKey, int observedVirtualKey)
    {
        if (configuredVirtualKey == observedVirtualKey)
        {
            return true;
        }

        return configuredVirtualKey switch
        {
            NativeVirtualKeys.Control => observedVirtualKey is NativeVirtualKeys.LControl or NativeVirtualKeys.RControl,
            NativeVirtualKeys.LControl or NativeVirtualKeys.RControl => observedVirtualKey == NativeVirtualKeys.Control,
            NativeVirtualKeys.Shift => observedVirtualKey is NativeVirtualKeys.LShift or NativeVirtualKeys.RShift,
            NativeVirtualKeys.LShift or NativeVirtualKeys.RShift => observedVirtualKey == NativeVirtualKeys.Shift,
            NativeVirtualKeys.Menu => observedVirtualKey is NativeVirtualKeys.LMenu or NativeVirtualKeys.RMenu,
            NativeVirtualKeys.LMenu or NativeVirtualKeys.RMenu => observedVirtualKey == NativeVirtualKeys.Menu,
            _ => false
        };
    }

    private void RemoveGenericEquivalent(int virtualKey)
    {
        switch (virtualKey)
        {
            case NativeVirtualKeys.LControl or NativeVirtualKeys.RControl:
                _pressedKeys.Remove(NativeVirtualKeys.Control);
                break;
            case NativeVirtualKeys.LShift or NativeVirtualKeys.RShift:
                _pressedKeys.Remove(NativeVirtualKeys.Shift);
                break;
            case NativeVirtualKeys.LMenu or NativeVirtualKeys.RMenu:
                _pressedKeys.Remove(NativeVirtualKeys.Menu);
                break;
        }
    }

    private bool StartHoldMode(HotkeyBinding binding)
    {
        lock (_stateGate)
        {
            if (!_activeHoldModes.Add(binding.Mode.Id))
            {
                return false;
            }
            _activeHoldBindings[binding.Mode.Id] = binding;
            return true;
        }
    }

    private void StopHoldMode(HotkeyBinding binding)
    {
        var shouldStop = false;
        lock (_stateGate)
        {
            if (_activeHoldModes.Remove(binding.Mode.Id))
            {
                _activeHoldBindings.Remove(binding.Mode.Id);
                shouldStop = true;
            }
        }

        if (shouldStop)
        {
            binding.Stop(binding.Mode);
        }
    }

    private void CheckActiveHoldKeys(object? state)
    {
        List<HotkeyBinding> staleBindings;
        lock (_stateGate)
        {
            staleBindings = _activeHoldBindings.Values
                .Where(binding => !IsPhysicalHoldStillDown(binding.Mode))
                .ToList();
        }

        foreach (var binding in staleBindings)
        {
            StopHoldMode(binding);
        }
    }

    private static bool IsPhysicalHoldStillDown(ProcessingMode mode)
    {
        if (!IsPhysicalKeyDown(mode.HotkeyVirtualKey))
        {
            return false;
        }

        return HasPhysicalModifier(mode.HotkeyModifiers, NativeHotkeyModifiers.Control, NativeVirtualKeys.Control, NativeVirtualKeys.LControl, NativeVirtualKeys.RControl) &&
               HasPhysicalModifier(mode.HotkeyModifiers, NativeHotkeyModifiers.Shift, NativeVirtualKeys.Shift, NativeVirtualKeys.LShift, NativeVirtualKeys.RShift) &&
               HasPhysicalModifier(mode.HotkeyModifiers, NativeHotkeyModifiers.Alt, NativeVirtualKeys.Menu, NativeVirtualKeys.LMenu, NativeVirtualKeys.RMenu) &&
               HasPhysicalModifier(mode.HotkeyModifiers, NativeHotkeyModifiers.Win, NativeVirtualKeys.LWin, NativeVirtualKeys.RWin);
    }

    private static bool HasPhysicalModifier(uint expectedModifiers, uint modifierFlag, params int[] virtualKeys) =>
        (expectedModifiers & modifierFlag) == 0 || virtualKeys.Any(IsPhysicalKeyDown);

    private static bool IsPhysicalKeyDown(int virtualKey)
    {
        if (IsKeyDown(virtualKey))
        {
            return true;
        }

        return virtualKey switch
        {
            NativeVirtualKeys.LControl or NativeVirtualKeys.RControl => IsKeyDown(NativeVirtualKeys.Control),
            NativeVirtualKeys.LShift or NativeVirtualKeys.RShift => IsKeyDown(NativeVirtualKeys.Shift),
            NativeVirtualKeys.LMenu or NativeVirtualKeys.RMenu => IsKeyDown(NativeVirtualKeys.Menu),
            _ => false
        };
    }

    private static bool ShouldSuppressHotkey(ProcessingMode mode) =>
        !(mode.HotkeyModifiers == 0 && IsModifierVirtualKey(mode.HotkeyVirtualKey));

    private static int NormalizeVirtualKey(int virtualKey, uint scanCode, uint flags)
    {
        var extended = (flags & LLKHF_EXTENDED) != 0;
        return virtualKey switch
        {
            NativeVirtualKeys.Control => extended ? NativeVirtualKeys.RControl : NativeVirtualKeys.LControl,
            NativeVirtualKeys.Menu => extended ? NativeVirtualKeys.RMenu : NativeVirtualKeys.LMenu,
            NativeVirtualKeys.Shift => NormalizeShift(scanCode),
            _ => virtualKey
        };
    }

    private static int NormalizeShift(uint scanCode)
    {
        var mapped = MapVirtualKey(scanCode, MAPVK_VSC_TO_VK_EX);
        return mapped is NativeVirtualKeys.LShift or NativeVirtualKeys.RShift ? (int)mapped : NativeVirtualKeys.Shift;
    }

    private static bool IsModifierVirtualKey(int virtualKey) =>
        virtualKey is NativeVirtualKeys.Control or NativeVirtualKeys.LControl or NativeVirtualKeys.RControl or
            NativeVirtualKeys.Shift or NativeVirtualKeys.LShift or NativeVirtualKeys.RShift or
            NativeVirtualKeys.Menu or NativeVirtualKeys.LMenu or NativeVirtualKeys.RMenu or
            NativeVirtualKeys.LWin or NativeVirtualKeys.RWin;

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        _holdWatchdog.Dispose();
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}

public sealed class TextInjectionService
{
    private readonly Dispatcher _dispatcher;
    private IntPtr _targetWindow = IntPtr.Zero;

    public TextInjectionService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task<InjectionOutcome> InjectAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return InjectionOutcome.Inserted;

        return await _dispatcher.InvokeAsync(async () =>
        {
            System.Windows.IDataObject? previous = null;
            var inserted = false;
            try
            {
                await WaitForModifierKeysReleased(cancellationToken);
                previous = System.Windows.Clipboard.GetDataObject();
                System.Windows.Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText);
                await Task.Delay(100, cancellationToken);
                ReleaseModifierKeys();
                await Task.Delay(40, cancellationToken);
                RestoreTargetWindow();
                await Task.Delay(120, cancellationToken);
                if (!SendPaste() && !SendUnicodeText(text))
                {
                    return InjectionOutcome.CopiedToClipboard;
                }
                inserted = true;
                await Task.Delay(700, CancellationToken.None);
                TryRestoreClipboard(previous);
                return InjectionOutcome.Inserted;
            }
            catch
            {
                if (inserted)
                {
                    TryRestoreClipboard(previous);
                    return InjectionOutcome.Inserted;
                }

                RestoreTargetWindow();
                await Task.Delay(120, CancellationToken.None);
                ReleaseModifierKeys();
                if (SendUnicodeText(text))
                {
                    return InjectionOutcome.Inserted;
                }

                System.Windows.Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText);
                return InjectionOutcome.CopiedToClipboard;
            }
        }).Task.Unwrap();
    }

    private static void TryRestoreClipboard(System.Windows.IDataObject? previous)
    {
        if (previous is null)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetDataObject(previous, true);
        }
        catch
        {
        }
    }

    public PromptContext CapturePromptContext(IntPtr targetWindow)
    {
        try
        {
            _targetWindow = targetWindow != IntPtr.Zero ? targetWindow : GetForegroundWindow();
            var text = _dispatcher.Invoke(() => System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "");
            return new PromptContext("", text);
        }
        catch
        {
            return PromptContext.Empty;
        }
    }

    private static bool SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        foreach (var ch in text)
        {
            var down = UnicodeInput(ch, keyUp: false);
            var up = UnicodeInput(ch, keyUp: true);
            var sent = SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
            if (sent != 2)
            {
                return false;
            }
            Thread.Sleep(1);
        }

        return true;
    }

    private static async Task WaitForModifierKeysReleased(CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline && AnyModifierKeyDown())
        {
            await Task.Delay(25, cancellationToken);
        }
    }

    private static bool AnyModifierKeyDown() =>
        IsKeyDown(NativeVirtualKeys.Control) ||
        IsKeyDown(NativeVirtualKeys.LControl) ||
        IsKeyDown(NativeVirtualKeys.RControl) ||
        IsKeyDown(NativeVirtualKeys.Shift) ||
        IsKeyDown(NativeVirtualKeys.LShift) ||
        IsKeyDown(NativeVirtualKeys.RShift) ||
        IsKeyDown(NativeVirtualKeys.Menu) ||
        IsKeyDown(NativeVirtualKeys.LMenu) ||
        IsKeyDown(NativeVirtualKeys.RMenu) ||
        IsKeyDown(NativeVirtualKeys.LWin) ||
        IsKeyDown(NativeVirtualKeys.RWin);

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static void ReleaseModifierKeys()
    {
        var inputs = new[]
        {
            KeyboardInput(NativeVirtualKeys.V, true),
            KeyboardInput(NativeVirtualKeys.Control, true),
            KeyboardInput(NativeVirtualKeys.LControl, true),
            KeyboardInput(NativeVirtualKeys.RControl, true),
            KeyboardInput(NativeVirtualKeys.Shift, true),
            KeyboardInput(NativeVirtualKeys.LShift, true),
            KeyboardInput(NativeVirtualKeys.RShift, true),
            KeyboardInput(NativeVirtualKeys.Menu, true),
            KeyboardInput(NativeVirtualKeys.LMenu, true),
            KeyboardInput(NativeVirtualKeys.RMenu, true),
            KeyboardInput(NativeVirtualKeys.LWin, true),
            KeyboardInput(NativeVirtualKeys.RWin, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private void RestoreTargetWindow()
    {
        if (_targetWindow == IntPtr.Zero || !IsWindow(_targetWindow))
        {
            return;
        }

        var currentForeground = GetForegroundWindow();
        if (currentForeground == _targetWindow)
        {
            return;
        }

        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(currentForeground, out _);
        var targetThread = GetWindowThreadProcessId(_targetWindow, out _);

        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            AttachThreadInput(currentThread, foregroundThread, true);
        }
        if (targetThread != 0 && targetThread != currentThread)
        {
            AttachThreadInput(currentThread, targetThread, true);
        }

        try
        {
            ShowWindow(_targetWindow, 5);
            TapAltForForegroundPermission();
            SetForegroundWindow(_targetWindow);
            BringWindowToTop(_targetWindow);
        }
        finally
        {
            if (targetThread != 0 && targetThread != currentThread)
            {
                AttachThreadInput(currentThread, targetThread, false);
            }
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private static void TapAltForForegroundPermission()
    {
        var inputs = new[]
        {
            KeyboardInput(NativeVirtualKeys.Menu, false),
            KeyboardInput(NativeVirtualKeys.Menu, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static bool SendPaste()
    {
        return SendCtrlV();
    }

    private static bool SendCtrlV()
    {
        var inputs = new[]
        {
            KeyboardInput(NativeVirtualKeys.Control, false),
            KeyboardInput(NativeVirtualKeys.V, false),
            KeyboardInput(NativeVirtualKeys.V, true),
            KeyboardInput(NativeVirtualKeys.Control, true)
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    private static INPUT KeyboardInput(int virtualKey, bool keyUp) => new()
    {
        type = 1,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = (ushort)virtualKey,
                dwFlags = keyUp ? 0x0002u : 0
            }
        }
    };

    private static INPUT UnicodeInput(char character, bool keyUp) => new()
    {
        type = 1,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = character,
                dwFlags = 0x0004u | (keyUp ? 0x0002u : 0)
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
