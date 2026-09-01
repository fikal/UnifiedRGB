using System.Runtime.InteropServices;

namespace UnifiedRgb.Core.Native;

/// <summary>Minimal NvAPI wrapper (nvapi64.dll ships with the NVIDIA driver).
/// Functions are resolved through nvapi_QueryInterface by their well-known ids
/// (same ones OpenRGB uses). Provides GPU enumeration and the I2C access used
/// to reach the board's RGB controller (port 1, non-DDC). No elevation needed.</summary>
public static class NvApi
{
    const uint ID_Initialize = 0x0150E828;
    const uint ID_EnumPhysicalGPUs = 0xE5AC921F;
    const uint ID_GPU_GetFullName = 0xCEEE8E9F;
    const uint ID_I2CWriteEx = 0x283AC65A;
    const uint ID_I2CReadEx = 0x4D7B0709;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitializeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumPhysicalGPUsFn(IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetFullNameFn(IntPtr handle, System.Text.StringBuilder name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int I2CExFn(IntPtr handle, ref NvI2cInfoV3 info, ref uint unknown);

    static InitializeFn? _init;
    static EnumPhysicalGPUsFn? _enum;
    static GetFullNameFn? _name;
    static I2CExFn? _write;
    static I2CExFn? _read;
    static bool _ready;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    struct NvI2cInfoV3
    {
        public uint Version;          // (3 << 16) | sizeof == 0x30040
        public uint DisplayMask;
        public byte IsDdcPort;
        public byte DevAddress;       // 7-bit address << 1
        public IntPtr RegAddress;
        public uint RegAddrSize;
        public IntPtr Data;
        public uint Size;
        public uint Speed;            // deprecated field, 0xFFFF
        public uint SpeedKhz;         // 0 = default
        public byte PortId;           // RGB controllers hang off port 1
        public uint IsPortIdSet;
    }

    static T? Resolve<T>(uint id) where T : Delegate
    {
        var p = QueryInterface(id);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    public static bool TryInit()
    {
        if (_ready) return true;
        try
        {
            _init = Resolve<InitializeFn>(ID_Initialize);
            _enum = Resolve<EnumPhysicalGPUsFn>(ID_EnumPhysicalGPUs);
            _name = Resolve<GetFullNameFn>(ID_GPU_GetFullName);
            _write = Resolve<I2CExFn>(ID_I2CWriteEx);
            _read = Resolve<I2CExFn>(ID_I2CReadEx);
            if (_init == null || _enum == null || _write == null) return false;
            _ready = _init() == 0;
            return _ready;
        }
        catch { return false; }   // no NVIDIA driver
    }

    public static List<(IntPtr Handle, string Name)> EnumGpus()
    {
        var result = new List<(IntPtr, string)>();
        if (!TryInit()) return result;
        var handles = new IntPtr[64];
        if (_enum!(handles, out int count) != 0) return result;
        for (int i = 0; i < count; i++)
        {
            var sb = new System.Text.StringBuilder(64);
            string name = _name != null && _name(handles[i], sb) == 0 ? sb.ToString() : $"NVIDIA GPU {i}";
            result.Add((handles[i], name));
        }
        return result;
    }

    /// <summary>I2C write: one register byte + payload, on port 1.</summary>
    public static unsafe bool I2CWrite(IntPtr gpu, byte devAddr, byte reg, ReadOnlySpan<byte> payload)
    {
        if (!_ready || _write == null) return false;
        byte regByte = reg;
        fixed (byte* dataPtr = payload)
        {
            var info = new NvI2cInfoV3
            {
                Version = (3u << 16) | (uint)Marshal.SizeOf<NvI2cInfoV3>(),
                IsDdcPort = 0,
                DevAddress = (byte)(devAddr << 1),
                RegAddress = (IntPtr)(&regByte),
                RegAddrSize = 1,
                Data = (IntPtr)dataPtr,
                Size = (uint)payload.Length,
                Speed = 0xFFFF,
                SpeedKhz = 0,
                PortId = 1,
                IsPortIdSet = 1,
            };
            uint unknown = 0;
            return _write(gpu, ref info, ref unknown) == 0;
        }
    }

    /// <summary>I2C read: select one register byte, read payload.Length bytes.</summary>
    public static unsafe bool I2CRead(IntPtr gpu, byte devAddr, byte reg, Span<byte> payload)
    {
        if (!_ready || _read == null) return false;
        byte regByte = reg;
        fixed (byte* dataPtr = payload)
        {
            var info = new NvI2cInfoV3
            {
                Version = (3u << 16) | (uint)Marshal.SizeOf<NvI2cInfoV3>(),
                IsDdcPort = 0,
                DevAddress = (byte)(devAddr << 1),
                RegAddress = (IntPtr)(&regByte),
                RegAddrSize = 1,
                Data = (IntPtr)dataPtr,
                Size = (uint)payload.Length,
                Speed = 0xFFFF,
                SpeedKhz = 0,
                PortId = 1,
                IsPortIdSet = 1,
            };
            uint unknown = 0;
            return _read(gpu, ref info, ref unknown) == 0;
        }
    }

    /*-----------------------------------------------------*\
    | GPU temperature (NvAPI_GPU_GetThermalSettings)        |
    \*-----------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    struct NvThermalSensor
    {
        public int Controller, DefaultMinTemp, DefaultMaxTemp, CurrentTemp, Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NvThermalSettingsV2
    {
        public uint Version;
        public uint Count;
        public NvThermalSensor S0, S1, S2;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetThermalSettingsFn(IntPtr handle, uint sensorIndex, ref NvThermalSettingsV2 settings);
    static GetThermalSettingsFn? _thermal;

    /// <summary>GPU core temperature in °C, or null when unavailable.</summary>
    public static int? GetGpuTemperature(IntPtr gpu)
    {
        try
        {
            if (!TryInit()) return null;
            _thermal ??= Resolve<GetThermalSettingsFn>(0xE3640A56);
            if (_thermal == null) return null;
            var settings = new NvThermalSettingsV2
            {
                Version = (2u << 16) | (uint)Marshal.SizeOf<NvThermalSettingsV2>(),
            };
            // sensorIndex 15 = all sensors; sensor 0 is the GPU core.
            if (_thermal(gpu, 15, ref settings) != 0 || settings.Count == 0) return null;
            int t = settings.S0.CurrentTemp;
            return t is > 0 and < 150 ? t : null;
        }
        catch { return null; }
    }

    /*-----------------------------------------------------*\
    | GPU fan RPM (NvAPI_GPU_GetTachReading)                |
    \*-----------------------------------------------------*/
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetTachReadingFn(IntPtr handle, out uint rpm);
    static GetTachReadingFn? _tach;

    /// <summary>GPU fan speed in RPM, or null when unavailable (no tach,
    /// or fan-stop mode reporting 0 — 0 is returned as a real value so the
    /// UI can show a stopped fan).</summary>
    public static int? GetGpuFanRpm(IntPtr gpu)
    {
        try
        {
            if (!TryInit()) return null;
            _tach ??= Resolve<GetTachReadingFn>(0x5F608315);
            if (_tach == null) return null;
            if (_tach(gpu, out uint rpm) != 0) return null;
            return rpm < 10_000 ? (int)rpm : null;
        }
        catch { return null; }
    }

    /*-----------------------------------------------------*\
    | GPU per-fan RPMs (NvAPI_GPU_ClientFanCoolersGetStatus)|
    | — modern coolers have 2-3 fans with individual tachs; |
    | the legacy call above only reports one.               |
    \*-----------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    struct NvFanCoolerStatusItem
    {
        public uint CoolerId, CurrentRpm, CurrentMinLevel, CurrentMaxLevel, CurrentLevel;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NvFanCoolersStatusV1
    {
        public uint Version;
        public uint Count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public NvFanCoolerStatusItem[] Items;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int FanCoolersGetStatusFn(IntPtr handle, ref NvFanCoolersStatusV1 status);
    static FanCoolersGetStatusFn? _fanCoolers;

    /// <summary>Per-fan RPMs (Turing and newer). Falls back to the single
    /// legacy tach when the fan-coolers API is unavailable. Null = no data;
    /// 0 entries are real (fan-stop).</summary>
    public static int[]? GetGpuFanRpms(IntPtr gpu)
    {
        try
        {
            if (!TryInit()) return null;
            _fanCoolers ??= Resolve<FanCoolersGetStatusFn>(0x35AED5E8);
            if (_fanCoolers != null)
            {
                var st = new NvFanCoolersStatusV1
                {
                    Version = (1u << 16) | (uint)Marshal.SizeOf<NvFanCoolersStatusV1>(),
                    Reserved = new uint[8],
                    Items = new NvFanCoolerStatusItem[32],
                };
                for (int i = 0; i < st.Items.Length; i++) st.Items[i].Reserved = new uint[8];
                if (_fanCoolers(gpu, ref st) == 0 && st.Count is > 0 and <= 32)
                {
                    var rpms = new int[st.Count];
                    bool sane = true;
                    for (int i = 0; i < rpms.Length; i++)
                    {
                        rpms[i] = (int)st.Items[i].CurrentRpm;
                        if (rpms[i] is < 0 or >= 10_000) sane = false;
                    }
                    if (sane) return rpms;
                }
            }
            return GetGpuFanRpm(gpu) is int one ? new[] { one } : null;
        }
        catch { return null; }
    }

    /*-----------------------------------------------------*\
    | GPU fan CONTROL (NvAPI_GPU_ClientFanCoolersGet/Set-   |
    | Control). All coolers are driven together — they're   |
    | one cooler assembly. ControlMode 0 = auto (driver     |
    | curve, incl. fan-stop), 1 = manual level %. The       |
    | driver keeps thermal-throttling the GPU regardless,   |
    | so a low manual duty can't cook the card.             |
    \*-----------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    struct NvFanCoolerControlItem
    {
        public uint CoolerId, Level, ControlMode;   // mode: 0=auto, 1=manual
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    struct NvFanCoolersControlV1
    {
        public uint Version;
        public uint Reserved0;   // yes — the control struct has this extra field
        public uint Count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public NvFanCoolerControlItem[] Items;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int FanCoolersControlFn(IntPtr handle, ref NvFanCoolersControlV1 control);
    static FanCoolersControlFn? _fanCtlGet, _fanCtlSet;

    static bool TryGetFanControl(IntPtr gpu, out NvFanCoolersControlV1 ctl)
    {
        ctl = default;
        if (!TryInit()) return false;
        _fanCtlGet ??= Resolve<FanCoolersControlFn>(0x814B209F);
        if (_fanCtlGet == null) return false;
        ctl = new NvFanCoolersControlV1
        {
            Version = (1u << 16) | (uint)Marshal.SizeOf<NvFanCoolersControlV1>(),
            Reserved = new uint[8],
            Items = new NvFanCoolerControlItem[32],
        };
        for (int i = 0; i < ctl.Items.Length; i++) ctl.Items[i].Reserved = new uint[8];
        return _fanCtlGet(gpu, ref ctl) == 0 && ctl.Count is > 0 and <= 32;
    }

    /// <summary>True when this GPU exposes the fan-coolers control API.</summary>
    public static bool CanControlGpuFans(IntPtr gpu)
    {
        try { return TryGetFanControl(gpu, out _); }
        catch { return false; }
    }

    /// <summary>The lowest manual duty the card accepts (its CurrentMinLevel;
    /// the driver silently clamps anything below it). Below this, only the
    /// driver's auto mode can go — including zero-RPM, where the vBIOS allows
    /// it. Null when unreadable.</summary>
    public static int? GetGpuFanMinLevel(IntPtr gpu)
    {
        try
        {
            if (!TryInit()) return null;
            _fanCoolers ??= Resolve<FanCoolersGetStatusFn>(0x35AED5E8);
            if (_fanCoolers == null) return null;
            var st = new NvFanCoolersStatusV1
            {
                Version = (1u << 16) | (uint)Marshal.SizeOf<NvFanCoolersStatusV1>(),
                Reserved = new uint[8],
                Items = new NvFanCoolerStatusItem[32],
            };
            for (int i = 0; i < st.Items.Length; i++) st.Items[i].Reserved = new uint[8];
            if (_fanCoolers(gpu, ref st) != 0 || st.Count == 0) return null;
            uint min = 0;
            for (int i = 0; i < st.Count; i++) min = Math.Max(min, st.Items[i].CurrentMinLevel);
            return (int)Math.Min(min, 100);
        }
        catch { return null; }
    }

    /// <summary>Drive every GPU cooler at a fixed duty percent.</summary>
    public static bool SetGpuFanDuty(IntPtr gpu, int percent)
    {
        try
        {
            if (!TryGetFanControl(gpu, out var ctl)) return false;
            _fanCtlSet ??= Resolve<FanCoolersControlFn>(0xA58971A5);
            if (_fanCtlSet == null) return false;
            uint level = (uint)Math.Clamp(percent, 0, 100);
            for (int i = 0; i < ctl.Count; i++)
            {
                ctl.Items[i].Level = level;
                ctl.Items[i].ControlMode = 1;   // manual
            }
            return _fanCtlSet(gpu, ref ctl) == 0;
        }
        catch { return false; }
    }

    /*-----------------------------------------------------*\
    | GPU load (NvAPI_GPU_GetDynamicPstatesInfoEx) and core |
    | voltage (private volt-rails status).                  |
    \*-----------------------------------------------------*/
    [StructLayout(LayoutKind.Sequential)]
    struct NvDynamicPState { public uint IsPresent; public int Percentage; }

    [StructLayout(LayoutKind.Sequential)]
    struct NvDynamicPStatesInfo
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public NvDynamicPState[] Utilizations;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetDynamicPstatesFn(IntPtr handle, ref NvDynamicPStatesInfo info);
    static GetDynamicPstatesFn? _pstates;

    /// <summary>GPU utilization percent (domain 0 = graphics engine).</summary>
    public static int? GetGpuLoad(IntPtr gpu)
    {
        try
        {
            if (!TryInit()) return null;
            _pstates ??= Resolve<GetDynamicPstatesFn>(0x60DED2ED);
            if (_pstates == null) return null;
            var info = new NvDynamicPStatesInfo
            {
                Version = (1u << 16) | (uint)Marshal.SizeOf<NvDynamicPStatesInfo>(),
                Utilizations = new NvDynamicPState[8],
            };
            if (_pstates(gpu, ref info) != 0) return null;
            return info.Utilizations[0].IsPresent != 0
                ? Math.Clamp(info.Utilizations[0].Percentage, 0, 100) : null;
        }
        catch { return null; }
    }

    // Volt-rails status: Version, 9 reserved, core µV at 0x28, high at 0x2C,
    // 7 trailing reserved — 76 bytes total, verified live on the RTX 5090 by
    // the --gpu size scan (80 bytes gets NVAPI_INCOMPATIBLE_STRUCT_VERSION).
    [StructLayout(LayoutKind.Sequential)]
    struct NvVoltRailsStatus
    {
        public uint Version;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)] public uint[] ReservedA;
        public uint CoreMicrovolts;
        public uint CoreMicrovoltsHigh;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)] public uint[] ReservedB;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int GetVoltRailsFn(IntPtr handle, ref NvVoltRailsStatus status);
    static GetVoltRailsFn? _volt;

    /// <summary>Probe: full fan-cooler state — per-cooler min/max/current
    /// levels from status, control modes/levels from control. CLI only.</summary>
    public static string DebugFanState(IntPtr gpu)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            if (TryInit())
            {
                _fanCoolers ??= Resolve<FanCoolersGetStatusFn>(0x35AED5E8);
                if (_fanCoolers != null)
                {
                    var st = new NvFanCoolersStatusV1
                    {
                        Version = (1u << 16) | (uint)Marshal.SizeOf<NvFanCoolersStatusV1>(),
                        Reserved = new uint[8],
                        Items = new NvFanCoolerStatusItem[32],
                    };
                    for (int i = 0; i < st.Items.Length; i++) st.Items[i].Reserved = new uint[8];
                    if (_fanCoolers(gpu, ref st) == 0)
                        for (int i = 0; i < st.Count; i++)
                            sb.Append($"status[{i}] id={st.Items[i].CoolerId} rpm={st.Items[i].CurrentRpm} lvl={st.Items[i].CurrentLevel} min={st.Items[i].CurrentMinLevel} max={st.Items[i].CurrentMaxLevel}; ");
                }
                if (TryGetFanControl(gpu, out var ctl))
                    for (int i = 0; i < ctl.Count; i++)
                        sb.Append($"ctl[{i}] id={ctl.Items[i].CoolerId} level={ctl.Items[i].Level} mode={ctl.Items[i].ControlMode}; ");
            }
            return sb.ToString();
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Probe: set an exact level+mode on every cooler, returning the
    /// raw rc. CLI only — production goes through SetGpuFanDuty.</summary>
    public static int DebugSetFanLevel(IntPtr gpu, uint level, uint mode)
    {
        try
        {
            if (!TryGetFanControl(gpu, out var ctl)) return -999;
            _fanCtlSet ??= Resolve<FanCoolersControlFn>(0xA58971A5);
            if (_fanCtlSet == null) return -998;
            for (int i = 0; i < ctl.Count; i++)
            {
                ctl.Items[i].Level = level;
                ctl.Items[i].ControlMode = mode;
            }
            return _fanCtlSet(gpu, ref ctl);
        }
        catch { return -997; }
    }

    /// <summary>Probe diagnostics: is the volt-rails function exported, and
    /// what does it return? (CLI use only.)</summary>
    public static string DebugVoltStatus(IntPtr gpu)
    {
        try
        {
            if (!TryInit()) return "init failed";
            var fn = Resolve<GetVoltRailsFn>(0x465F9BCF);
            if (fn == null) return "not exported";
            var st = new NvVoltRailsStatus
            {
                Version = (1u << 16) | (uint)Marshal.SizeOf<NvVoltRailsStatus>(),
                ReservedA = new uint[9],
                ReservedB = new uint[7],
            };
            int rc = fn(gpu, ref st);
            return $"rc={rc} size={Marshal.SizeOf<NvVoltRailsStatus>()} uV={st.CoreMicrovolts}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int RawBufFn(IntPtr handle, IntPtr buffer);

    /// <summary>Probe: try the volt-rails call at several struct sizes and
    /// report which succeeds plus any plausible microvolt dwords. CLI only.</summary>
    public static string DebugVoltScan(IntPtr gpu)
    {
        try
        {
            if (!TryInit()) return "init failed";
            var fn = Resolve<RawBufFn>(0x465F9BCF);
            if (fn == null) return "not exported";
            var sb = new System.Text.StringBuilder();
            foreach (int size in new[] { 76, 80, 84, 88, 160, 164, 168, 172, 196, 256, 512 })
                foreach (uint ver in new uint[] { 1, 2 })
                {
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        for (int i = 0; i < size; i += 4) Marshal.WriteInt32(buf, i, 0);
                        Marshal.WriteInt32(buf, 0, (int)((ver << 16) | (uint)size));
                        int rc = fn(gpu, buf);
                        if (rc == 0)
                        {
                            sb.Append($"[v{ver} size {size}: OK");
                            for (int off = 4; off < size; off += 4)
                            {
                                uint dw = (uint)Marshal.ReadInt32(buf, off);
                                if (dw is > 200_000 and < 1_500_000) sb.Append($" @0x{off:X2}={dw}uV");
                            }
                            sb.Append("] ");
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            return sb.Length == 0 ? "no size accepted" : sb.ToString();
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>GPU core voltage in volts, or null (not all GPUs report it).</summary>
    public static double? GetGpuCoreVoltage(IntPtr gpu)
    {
        try
        {
            if (!TryInit()) return null;
            _volt ??= Resolve<GetVoltRailsFn>(0x465F9BCF);
            if (_volt == null) return null;
            var st = new NvVoltRailsStatus
            {
                Version = (1u << 16) | (uint)Marshal.SizeOf<NvVoltRailsStatus>(),
                ReservedA = new uint[9],
                ReservedB = new uint[7],
            };
            if (_volt(gpu, ref st) != 0) return null;
            double v = st.CoreMicrovolts / 1_000_000.0;
            return v is > 0.2 and < 2.0 ? v : null;
        }
        catch { return null; }
    }

    /// <summary>Hand the coolers back to the driver's own curve (incl. fan-stop).</summary>
    public static bool RestoreGpuFanAuto(IntPtr gpu)
    {
        try
        {
            if (!TryGetFanControl(gpu, out var ctl)) return false;
            _fanCtlSet ??= Resolve<FanCoolersControlFn>(0xA58971A5);
            if (_fanCtlSet == null) return false;
            for (int i = 0; i < ctl.Count; i++)
                ctl.Items[i].ControlMode = 0;   // auto
            return _fanCtlSet(gpu, ref ctl) == 0;
        }
        catch { return false; }
    }
}
