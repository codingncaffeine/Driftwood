using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Driftwood.Core.Settings;
using SDL3;

namespace Driftwood.Client.Render;

public enum ControllerNoticeKind
{
    Connected,
    Disconnected,
}

public readonly record struct ControllerNotice(ControllerNoticeKind Kind, string Name, string Provider);

/// <summary>SDL3 gamepads, with XInput used only when SDL itself cannot be loaded or initialized.</summary>
/// <remarks>
/// <para>SDL is the primary path because it normalizes the enormous range of controllers into one
/// layout while retaining the device's own name and face-button labels. XInput is deliberately a
/// fallback rather than a second simultaneous scan: presenting the same Xbox pad twice is worse
/// than having no fallback, and XInput cannot identify more than four slots or provide a model
/// name.</para>
/// <para>Constructing this does nothing. <see cref="Start"/> is called by the host only after the
/// first frame has reached the display, preserving the startup boundary that replaced Silk's
/// measured ten-second eager joystick scan.</para>
/// </remarks>
public sealed class ControllerInput : IDisposable
{
    private sealed record SdlDevice(uint Id, nint Handle, string Name);

    private readonly Dictionary<uint, SdlDevice> _sdl = [];
    private readonly Queue<ControllerNotice> _notices = [];
    private readonly XInputFallback _xinput = new();
    private bool _started;
    private bool _sdlStarted;
    private bool _disposed;
    private uint _activeSdl;
    private int _activeXInput = -1;
    private long _xinputRumbleUntil;

    public bool Started => _started;
    public bool Available => _sdlStarted || _xinput.Available;
    public string Provider => _sdlStarted ? "SDL3" : _xinput.Available ? "XInput fallback" : "unavailable";
    public string Fault { get; private set; } = "not scanned yet";
    public double ScanMilliseconds { get; private set; }
    public string ActiveName { get; private set; } = "no controller connected";
    public int ConnectedCount => _sdlStarted ? _sdl.Count : _xinput.ConnectedCount;
    public ControllerSnapshot Current { get; private set; } = ControllerSnapshot.Empty;
    public ControllerSnapshot Previous { get; private set; } = ControllerSnapshot.Empty;

    /// <summary>True on a frame containing an intentional stick, trigger, or button gesture.</summary>
    public bool HadInputThisFrame { get; private set; }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        var timer = Stopwatch.StartNew();

        try
        {
            // SDL uses its internal joystick worker by default on Windows. Saying it before init
            // makes that contract explicit and keeps slow wireless queries off Driftwood's frame.
            SDL.SetHint("SDL_JOYSTICK_THREAD", "1");
            if (!SDL.InitSubSystem(SDL.InitFlags.Gamepad))
                throw new InvalidOperationException(SDL.GetError());

            _sdlStarted = true;
            Fault = "";
            foreach (var id in SDL.GetGamepads(out _) ?? []) OpenSdl(id, announce: true);
            PickFirstSdl();
        }
        catch (Exception error)
        {
            // Initialization can succeed and enumeration can still fail on one damaged device.
            // Tear the partial SDL path down before falling back so Provider and Update never
            // claim SDL while polling a half-open device table.
            foreach (var device in _sdl.Values)
                try { SDL.CloseGamepad(device.Handle); } catch (Exception) { }
            _sdl.Clear();
            if (_sdlStarted)
                try { SDL.QuitSubSystem(SDL.InitFlags.Gamepad); } catch (Exception) { }
            _sdlStarted = false;
            _activeSdl = 0;

            Fault = $"SDL3 could not start: {error.GetBaseException().Message}";
            try
            {
                _xinput.Start(_notices);
                if (_xinput.Available) Fault += "; using XInput fallback";
                PickXInput();
            }
            catch (Exception fallback)
            {
                Fault += $"; XInput could not start: {fallback.GetBaseException().Message}";
            }
        }
        finally
        {
            timer.Stop();
            ScanMilliseconds = timer.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>Refreshes hot-plug state and publishes the most recently used connected pad.</summary>
    public void Update()
    {
        HadInputThisFrame = false;
        Previous = Current;
        if (!_started || _disposed)
        {
            Current = ControllerSnapshot.Empty;
            return;
        }

        if (!_sdlStarted)
        {
            _xinput.Update(_notices);
            if (_xinputRumbleUntil > 0 && Stopwatch.GetTimestamp() >= _xinputRumbleUntil)
            {
                if (_activeXInput >= 0) _xinput.Rumble(_activeXInput, 0, 0);
                _xinputRumbleUntil = 0;
            }
            if (_activeXInput < 0 || !_xinput.IsConnected(_activeXInput)) PickXInput();

            for (var i = 0; i < XInputFallback.MaximumControllers; i++)
            {
                if (!_xinput.IsConnected(i)) continue;
                var sample = _xinput.Read(i);
                if (Meaningful(sample, i == _activeXInput ? Previous : ControllerSnapshot.Empty))
                {
                    if (_activeXInput != i) Previous = ControllerSnapshot.Empty;
                    _activeXInput = i;
                    ActiveName = _xinput.Name(i);
                    HadInputThisFrame = true;
                }
            }

            Current = _activeXInput >= 0 ? _xinput.Read(_activeXInput) : ControllerSnapshot.Empty;
            HadInputThisFrame |= Meaningful(Current, Previous);
            return;
        }

        while (SDL.PollEvent(out var e))
        {
            var type = (SDL.EventType)e.Type;
            switch (type)
            {
                case SDL.EventType.GamepadAdded:
                    OpenSdl(e.GDevice.Which, announce: true);
                    break;
                case SDL.EventType.GamepadRemoved:
                    CloseSdl(e.GDevice.Which, announce: true);
                    break;
                case SDL.EventType.GamepadRemapped:
                    ReopenSdl(e.GDevice.Which);
                    break;
            }
        }

        SDL.UpdateGamepads();
        if (_activeSdl == 0 || !_sdl.ContainsKey(_activeSdl)) PickFirstSdl();

        foreach (var device in _sdl.Values)
        {
            var sample = ReadSdl(device);
            var before = device.Id == _activeSdl ? Previous : ControllerSnapshot.Empty;
            if (!Meaningful(sample, before)) continue;

            if (_activeSdl != device.Id) Previous = ControllerSnapshot.Empty;
            _activeSdl = device.Id;
            ActiveName = device.Name;
            HadInputThisFrame = true;
        }

        Current = _activeSdl != 0 && _sdl.TryGetValue(_activeSdl, out var active)
            ? ReadSdl(active)
            : ControllerSnapshot.Empty;
        HadInputThisFrame |= Meaningful(Current, Previous);
    }

    public bool Held(ControllerControl control) => Current.Held(control, ControllerTuning.TriggerThreshold);

    public bool Pressed(ControllerControl control) => Held(control)
        && !Previous.Held(control, ControllerTuning.TriggerThreshold);

    public bool Released(ControllerControl control) => !Held(control)
        && Previous.Held(control, ControllerTuning.TriggerThreshold);

    public bool Held(ControllerAction action, ControllerBindings bindings) => Held(bindings.Control(action));

    public bool Pressed(ControllerAction action, ControllerBindings bindings) => Pressed(bindings.Control(action));

    public bool Released(ControllerAction action, ControllerBindings bindings) => Released(bindings.Control(action));

    public ControllerControl FirstPressedControl()
    {
        foreach (var control in Enum.GetValues<ControllerControl>())
            if (Pressed(control)) return control;
        return ControllerControl.None;
    }

    public bool TryTakeNotice(out ControllerNotice notice)
    {
        if (_notices.Count == 0)
        {
            notice = default;
            return false;
        }

        notice = _notices.Dequeue();
        return true;
    }

    /// <summary>Device-aware label for the settings screen and live footer.</summary>
    public string Label(ControllerControl control)
    {
        if (_sdlStarted && _activeSdl != 0 && _sdl.TryGetValue(_activeSdl, out var device))
            return SdlLabel(device.Handle, control);

        return control switch
        {
            ControllerControl.South => "A",
            ControllerControl.East => "B",
            ControllerControl.West => "X",
            ControllerControl.North => "Y",
            ControllerControl.LeftShoulder => "LB",
            ControllerControl.RightShoulder => "RB",
            ControllerControl.LeftTrigger => "LT",
            ControllerControl.RightTrigger => "RT",
            ControllerControl.LeftStick => "LS press",
            ControllerControl.RightStick => "RS press",
            ControllerControl.Start => "menu",
            ControllerControl.Back => "view",
            _ => ControllerBindings.Name(control),
        };
    }

    public void Rumble(float low, float high, int milliseconds, int strengthPercent)
    {
        if (milliseconds <= 0 || strengthPercent <= 0 || !Current.Connected) return;
        var scale = Math.Clamp(strengthPercent, 0, 100) / 100f;
        var lo = (ushort)MathF.Round(Math.Clamp(low, 0f, 1f) * scale * ushort.MaxValue);
        var hi = (ushort)MathF.Round(Math.Clamp(high, 0f, 1f) * scale * ushort.MaxValue);

        try
        {
            if (_sdlStarted && _activeSdl != 0 && _sdl.TryGetValue(_activeSdl, out var device))
                SDL.RumbleGamepad(device.Handle, lo, hi, (uint)milliseconds);
            else if (_activeXInput >= 0)
            {
                _xinput.Rumble(_activeXInput, lo, hi);
                _xinputRumbleUntil = Stopwatch.GetTimestamp()
                    + (long)(Stopwatch.Frequency * (milliseconds / 1000.0));
            }
        }
        catch (Exception) { }
    }

    private void OpenSdl(uint id, bool announce)
    {
        if (id == 0 || _sdl.ContainsKey(id)) return;
        var handle = SDL.OpenGamepad(id);
        if (handle == 0) return;

        var name = CleanName(SDL.GetGamepadName(handle), $"controller {id}");
        var device = new SdlDevice(id, handle, name);
        _sdl[id] = device;
        if (_activeSdl == 0)
        {
            _activeSdl = id;
            ActiveName = name;
            Previous = ControllerSnapshot.Empty;
        }
        if (announce) _notices.Enqueue(new ControllerNotice(ControllerNoticeKind.Connected, name, "SDL3"));
    }

    private void CloseSdl(uint id, bool announce)
    {
        if (!_sdl.Remove(id, out var device)) return;
        try { SDL.CloseGamepad(device.Handle); } catch (Exception) { }
        if (announce) _notices.Enqueue(new ControllerNotice(ControllerNoticeKind.Disconnected, device.Name, "SDL3"));

        if (_activeSdl == id)
        {
            _activeSdl = 0;
            Current = ControllerSnapshot.Empty;
            Previous = ControllerSnapshot.Empty;
            PickFirstSdl();
        }
    }

    private void ReopenSdl(uint id)
    {
        var active = _activeSdl == id;
        CloseSdl(id, announce: false);
        OpenSdl(id, announce: false);
        if (active && _sdl.ContainsKey(id)) _activeSdl = id;
    }

    private void PickFirstSdl()
    {
        if (_sdl.Count == 0)
        {
            _activeSdl = 0;
            ActiveName = "no controller connected";
            Current = ControllerSnapshot.Empty;
            return;
        }

        var first = _sdl.Values.First();
        _activeSdl = first.Id;
        ActiveName = first.Name;
        Previous = ControllerSnapshot.Empty;
    }

    private void PickXInput()
    {
        _activeXInput = -1;
        for (var i = 0; i < XInputFallback.MaximumControllers; i++)
        {
            if (!_xinput.IsConnected(i)) continue;
            _activeXInput = i;
            ActiveName = _xinput.Name(i);
            Previous = ControllerSnapshot.Empty;
            return;
        }

        ActiveName = "no controller connected";
        Current = ControllerSnapshot.Empty;
    }

    private static ControllerSnapshot ReadSdl(SdlDevice device)
    {
        var buttons = 0UL;
        foreach (var control in DigitalControls)
            if (SDL.GetGamepadButton(device.Handle, ToSdlButton(control)))
                buttons |= ControllerSnapshot.Bit(control);

        return new ControllerSnapshot(
            true,
            new Vector2(Axis(SDL.GetGamepadAxis(device.Handle, SDL.GamepadAxis.LeftX)),
                       -Axis(SDL.GetGamepadAxis(device.Handle, SDL.GamepadAxis.LeftY))),
            new Vector2(Axis(SDL.GetGamepadAxis(device.Handle, SDL.GamepadAxis.RightX)),
                        Axis(SDL.GetGamepadAxis(device.Handle, SDL.GamepadAxis.RightY))),
            Trigger(SDL.GetGamepadAxis(device.Handle, SDL.GamepadAxis.LeftTrigger)),
            Trigger(SDL.GetGamepadAxis(device.Handle, SDL.GamepadAxis.RightTrigger)),
            buttons);
    }

    private static bool Meaningful(ControllerSnapshot now, ControllerSnapshot before)
    {
        if (!now.Connected) return false;
        if (now.Buttons != before.Buttons) return true;
        if (now.LeftTrigger >= ControllerTuning.TriggerThreshold
            != (before.LeftTrigger >= ControllerTuning.TriggerThreshold)) return true;
        if (now.RightTrigger >= ControllerTuning.TriggerThreshold
            != (before.RightTrigger >= ControllerTuning.TriggerThreshold)) return true;
        return now.Move.LengthSquared() >= 0.35f * 0.35f || now.Look.LengthSquared() >= 0.35f * 0.35f;
    }

    private static float Axis(short value) => value >= 0 ? value / 32767f : value / 32768f;
    private static float Trigger(short value) => Math.Clamp(value / 32767f, 0f, 1f);

    private static readonly ControllerControl[] DigitalControls =
    [
        ControllerControl.South, ControllerControl.East, ControllerControl.West, ControllerControl.North,
        ControllerControl.Back, ControllerControl.Start, ControllerControl.LeftStick, ControllerControl.RightStick,
        ControllerControl.LeftShoulder, ControllerControl.RightShoulder,
        ControllerControl.DPadUp, ControllerControl.DPadDown, ControllerControl.DPadLeft, ControllerControl.DPadRight,
    ];

    private static SDL.GamepadButton ToSdlButton(ControllerControl control) => control switch
    {
        ControllerControl.South => SDL.GamepadButton.South,
        ControllerControl.East => SDL.GamepadButton.East,
        ControllerControl.West => SDL.GamepadButton.West,
        ControllerControl.North => SDL.GamepadButton.North,
        ControllerControl.Back => SDL.GamepadButton.Back,
        ControllerControl.Start => SDL.GamepadButton.Start,
        ControllerControl.LeftStick => SDL.GamepadButton.LeftStick,
        ControllerControl.RightStick => SDL.GamepadButton.RightStick,
        ControllerControl.LeftShoulder => SDL.GamepadButton.LeftShoulder,
        ControllerControl.RightShoulder => SDL.GamepadButton.RightShoulder,
        ControllerControl.DPadUp => SDL.GamepadButton.DPadUp,
        ControllerControl.DPadDown => SDL.GamepadButton.DPadDown,
        ControllerControl.DPadLeft => SDL.GamepadButton.DPadLeft,
        _ => SDL.GamepadButton.DPadRight,
    };

    private static string SdlLabel(nint handle, ControllerControl control)
    {
        if (control is ControllerControl.South or ControllerControl.East
            or ControllerControl.West or ControllerControl.North)
        {
            var label = SDL.GetGamepadButtonLabel(handle, ToSdlButton(control));
            return label switch
            {
                SDL.GamepadButtonLabel.A => "A",
                SDL.GamepadButtonLabel.B => "B",
                SDL.GamepadButtonLabel.X => "X",
                SDL.GamepadButtonLabel.Y => "Y",
                SDL.GamepadButtonLabel.Cross => "cross",
                SDL.GamepadButtonLabel.Circle => "circle",
                SDL.GamepadButtonLabel.Square => "square",
                SDL.GamepadButtonLabel.Triangle => "triangle",
                _ => ControllerBindings.Name(control),
            };
        }

        var type = SDL.GetGamepadType(handle);
        var playStation = type is SDL.GamepadType.PS3 or SDL.GamepadType.PS4 or SDL.GamepadType.PS5;
        var nintendo = type is SDL.GamepadType.NintendoSwitchPro
            or SDL.GamepadType.NintendoSwitchJoyconLeft
            or SDL.GamepadType.NintendoSwitchJoyconRight
            or SDL.GamepadType.NintendoSwitchJoyconPair;

        return control switch
        {
            ControllerControl.LeftShoulder => playStation ? "L1" : nintendo ? "L" : "LB",
            ControllerControl.RightShoulder => playStation ? "R1" : nintendo ? "R" : "RB",
            ControllerControl.LeftTrigger => playStation ? "L2" : nintendo ? "ZL" : "LT",
            ControllerControl.RightTrigger => playStation ? "R2" : nintendo ? "ZR" : "RT",
            ControllerControl.LeftStick => playStation ? "L3" : "LS press",
            ControllerControl.RightStick => playStation ? "R3" : "RS press",
            ControllerControl.Start => playStation ? "options" : "menu / start",
            ControllerControl.Back => playStation ? "create / share" : "view / back",
            _ => ControllerBindings.Name(control),
        };
    }

    private static string CleanName(string? name, string fallback)
    {
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        return name.Length <= 96 ? name : name[..96];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var device in _sdl.Values)
        {
            try { SDL.RumbleGamepad(device.Handle, 0, 0, 0); } catch (Exception) { }
            try { SDL.CloseGamepad(device.Handle); } catch (Exception) { }
        }
        _sdl.Clear();

        if (_sdlStarted)
        {
            try { SDL.QuitSubSystem(SDL.InitFlags.Gamepad); } catch (Exception) { }
        }

        _xinput.Dispose();
    }

    /// <summary>Interop assumptions that can be proved without a controller attached.</summary>
    public static List<string> SelfTest(out string detail)
    {
        var faults = new List<string>();
        var mapped = new HashSet<SDL.GamepadButton>();
        var bits = new HashSet<ulong>();

        foreach (var control in DigitalControls)
        {
            var button = ToSdlButton(control);
            if (button == SDL.GamepadButton.Invalid)
                faults.Add($"{control} maps to SDL's invalid button");
            if (!mapped.Add(button)) faults.Add($"two controls map to SDL {button}");

            var bit = ControllerSnapshot.Bit(control);
            if (bit == 0 || !bits.Add(bit)) faults.Add($"{control} has no unique held-state bit");
        }

        var gamepadBytes = Marshal.SizeOf<XInputFallback.XInputGamepad>();
        var stateBytes = Marshal.SizeOf<XInputFallback.XInputState>();
        var vibrationBytes = Marshal.SizeOf<XInputFallback.XInputVibration>();
        var capabilitiesBytes = Marshal.SizeOf<XInputFallback.XInputCapabilities>();
        if (gamepadBytes != 12) faults.Add($"XINPUT_GAMEPAD is {gamepadBytes} bytes, not 12");
        if (stateBytes != 16) faults.Add($"XINPUT_STATE is {stateBytes} bytes, not 16");
        if (vibrationBytes != 4) faults.Add($"XINPUT_VIBRATION is {vibrationBytes} bytes, not 4");
        if (capabilitiesBytes != 20) faults.Add($"XINPUT_CAPABILITIES is {capabilitiesBytes} bytes, not 20");

        var xinput = XInputFallback.Snapshot(new XInputFallback.XInputGamepad
        {
            Buttons = 0xF3FF,
            LeftTrigger = 255,
            RightTrigger = 255,
            LeftThumbX = short.MaxValue,
            LeftThumbY = short.MinValue,
            RightThumbX = short.MinValue,
            RightThumbY = short.MaxValue,
        });
        foreach (var control in DigitalControls)
            if (!xinput.Held(control)) faults.Add($"XInput did not map {control}");
        if (!xinput.Held(ControllerControl.LeftTrigger) || !xinput.Held(ControllerControl.RightTrigger))
            faults.Add("XInput did not map both analogue triggers");
        if (xinput.Move.X < 0.99f || xinput.Move.Y > -0.99f
            || xinput.Look.X > -0.99f || xinput.Look.Y > -0.99f)
            faults.Add($"XInput axis signs are wrong: move {xinput.Move}, look {xinput.Look}");

        detail = $"{mapped.Count} SDL and XInput buttons mapped one-to-one; XInput ABI "
            + $"{gamepadBytes}/{stateBytes}/{vibrationBytes}/{capabilitiesBytes} bytes";
        return faults;
    }

    /// <summary>Small Windows fallback; it intentionally never runs beside SDL.</summary>
    private sealed class XInputFallback : IDisposable
    {
        public const int MaximumControllers = 4;
        private const uint Success = 0;
        private const uint DeviceNotConnected = 1167;
        private nint _library;
        private GetStateDelegate? _getState;
        private GetCapabilitiesDelegate? _getCapabilities;
        private SetStateDelegate? _setState;
        private readonly bool[] _connected = new bool[MaximumControllers];
        private readonly string[] _names = new string[MaximumControllers];
        private long _nextScan;

        public bool Available => _getState is not null;
        public int ConnectedCount => _connected.Count(value => value);

        public void Start(Queue<ControllerNotice> notices)
        {
            foreach (var dll in (string[])["xinput1_4.dll", "xinput9_1_0.dll", "xinput1_3.dll"])
                if (NativeLibrary.TryLoad(dll, out _library)) break;
            if (_library == 0) return;

            _getState = Export<GetStateDelegate>("XInputGetState");
            _getCapabilities = Export<GetCapabilitiesDelegate>("XInputGetCapabilities");
            _setState = Export<SetStateDelegate>("XInputSetState");
            Scan(notices, announce: true);
        }

        public void Update(Queue<ControllerNotice> notices)
        {
            if (!Available || Stopwatch.GetTimestamp() < _nextScan) return;
            Scan(notices, announce: true);
        }

        private void Scan(Queue<ControllerNotice> notices, bool announce)
        {
            _nextScan = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            for (var i = 0; i < MaximumControllers; i++)
            {
                var before = _connected[i];
                _connected[i] = _getState!((uint)i, out _) == Success;
                if (_connected[i]) _names[i] = DeviceName(i);

                if (!announce || before == _connected[i]) continue;
                notices.Enqueue(new ControllerNotice(
                    _connected[i] ? ControllerNoticeKind.Connected : ControllerNoticeKind.Disconnected,
                    _names[i].Length > 0 ? _names[i] : $"Xbox controller {i + 1}",
                    "XInput fallback"));
            }
        }

        public bool IsConnected(int index) => index is >= 0 and < MaximumControllers && _connected[index];
        public string Name(int index) => IsConnected(index) ? _names[index] : "no controller connected";

        public ControllerSnapshot Read(int index)
        {
            if (!IsConnected(index) || _getState!((uint)index, out var state) != Success)
                return ControllerSnapshot.Empty;

            return Snapshot(state.Gamepad);
        }

        public static ControllerSnapshot Snapshot(XInputGamepad pad)
        {
            var buttons = 0UL;
            Add(ControllerControl.DPadUp, 0x0001);
            Add(ControllerControl.DPadDown, 0x0002);
            Add(ControllerControl.DPadLeft, 0x0004);
            Add(ControllerControl.DPadRight, 0x0008);
            Add(ControllerControl.Start, 0x0010);
            Add(ControllerControl.Back, 0x0020);
            Add(ControllerControl.LeftStick, 0x0040);
            Add(ControllerControl.RightStick, 0x0080);
            Add(ControllerControl.LeftShoulder, 0x0100);
            Add(ControllerControl.RightShoulder, 0x0200);
            Add(ControllerControl.South, 0x1000);
            Add(ControllerControl.East, 0x2000);
            Add(ControllerControl.West, 0x4000);
            Add(ControllerControl.North, 0x8000);

            return new ControllerSnapshot(
                true,
                new Vector2(Axis(pad.LeftThumbX), Axis(pad.LeftThumbY)),
                new Vector2(Axis(pad.RightThumbX), -Axis(pad.RightThumbY)),
                pad.LeftTrigger / 255f,
                pad.RightTrigger / 255f,
                buttons);

            void Add(ControllerControl control, ushort mask)
            {
                if ((pad.Buttons & mask) != 0) buttons |= ControllerSnapshot.Bit(control);
            }
        }

        public void Rumble(int index, ushort low, ushort high)
        {
            if (!IsConnected(index) || _setState is null) return;
            var vibration = new XInputVibration { LeftMotorSpeed = low, RightMotorSpeed = high };
            _setState((uint)index, ref vibration);
        }

        private string DeviceName(int index)
        {
            if (_getCapabilities is null || _getCapabilities((uint)index, 0, out var caps) != Success)
                return $"Xbox controller {index + 1}";

            var kind = caps.SubType switch
            {
                0x02 => "Xbox wheel",
                0x03 => "Xbox arcade stick",
                0x04 => "Xbox flight stick",
                0x05 => "Xbox dance pad",
                0x06 or 0x07 or 0x0B => "Xbox guitar",
                0x08 => "Xbox drum kit",
                0x13 => "Xbox arcade pad",
                _ => "Xbox controller",
            };
            return $"{kind} {index + 1}";
        }

        private T? Export<T>(string name) where T : Delegate =>
            NativeLibrary.TryGetExport(_library, name, out var address)
                ? Marshal.GetDelegateForFunctionPointer<T>(address)
                : null;

        public void Dispose()
        {
            if (_setState is not null)
                for (var i = 0; i < MaximumControllers; i++)
                    if (_connected[i])
                    {
                        var stopped = default(XInputVibration);
                        _setState((uint)i, ref stopped);
                    }
            if (_library != 0) NativeLibrary.Free(_library);
            _library = 0;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint GetStateDelegate(uint index, out XInputState state);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint GetCapabilitiesDelegate(uint index, uint flags, out XInputCapabilities capabilities);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint SetStateDelegate(uint index, ref XInputVibration vibration);

        [StructLayout(LayoutKind.Sequential)]
        internal struct XInputState
        {
            public uint PacketNumber;
            public XInputGamepad Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct XInputGamepad
        {
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short LeftThumbX;
            public short LeftThumbY;
            public short RightThumbX;
            public short RightThumbY;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct XInputVibration
        {
            public ushort LeftMotorSpeed;
            public ushort RightMotorSpeed;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct XInputCapabilities
        {
            public byte Type;
            public byte SubType;
            public ushort Flags;
            public XInputGamepad Gamepad;
            public XInputVibration Vibration;
        }
    }
}
