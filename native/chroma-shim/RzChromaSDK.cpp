// UnifiedRGB Chroma shim / proxy: implements the Chroma SDK v3 C API so apps
// speaking it (Wallpaper Engine, Chroma-enabled games) drive UnifiedRGB
// devices. Captures the color effects and forwards the grids to the running
// app over a named pipe.
//
// COEXISTENCE (proxy mode): if a backed-up REAL Razer DLL sits beside us as
// "RzChromaSDK64_real.dll" (64-bit) / "RzChromaSDK_real.dll" (32-bit), we load
// it and forward every call, so a machine with genuine Synapse keeps lighting
// its Razer gear AND feeds UnifiedRGB. With no real DLL present we run
// standalone (capture only). Same source for both cases and both bitnesses.
//
// Install: back up the real  ...\Razer Chroma SDK\bin\RzChromaSDK64.dll  to
// RzChromaSDK64_real.dll (and RzChromaSDK.dll to RzChromaSDK_real.dll) if any,
// then place our builds as RzChromaSDK64.dll / RzChromaSDK.dll. This is an
// interoperability layer (as OpenRGB/Aurora do), attributed to UnifiedRGB -
// not a Razer product.

#include <windows.h>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <map>
#include <mutex>
#include <string>
#include <vector>

// Diagnostic log so we can see exactly what the host (Wallpaper Engine) calls.
// Written to %LOCALAPPDATA%\UnifiedRgb\chroma-shim.log (WE runs unelevated and
// can write there). Cheap and rare - a line per lifecycle event.
static void ShimLog(const char* fmt, ...)
{
    char dir[MAX_PATH];
    if (!GetEnvironmentVariableA("LOCALAPPDATA", dir, MAX_PATH)) return;
    char path[MAX_PATH];
    _snprintf_s(path, sizeof(path), _TRUNCATE, "%s\\UnifiedRgb", dir);
    CreateDirectoryA(path, nullptr);
    _snprintf_s(path, sizeof(path), _TRUNCATE, "%s\\UnifiedRgb\\chroma-shim.log", dir);
    FILE* f = nullptr;
    if (fopen_s(&f, path, "a") || !f) return;
    SYSTEMTIME st; GetLocalTime(&st);
    fprintf(f, "%02d:%02d:%02d ", st.wHour, st.wMinute, st.wSecond);
    va_list ap; va_start(ap, fmt); vfprintf(f, fmt, ap); va_end(ap);
    fprintf(f, "\n");
    fclose(f);
}

typedef LONG RZRESULT;             // 0 = RZRESULT_SUCCESS
typedef GUID RZEFFECTID;
typedef GUID RZDEVICEID;
typedef unsigned char* PRZPARAM;

static const RZRESULT RZRESULT_SUCCESS = 0;
static const RZRESULT RZRESULT_FAILED = (RZRESULT)0x80004005;   // E_FAIL, as the SDK defines it

namespace KB { enum { CUSTOM = 2, STATIC = 4, CUSTOM_KEY = 8, CUSTOM2 = 9 }; }
namespace CL { enum { CUSTOM = 1, STATIC = 2 }; }
static const int KB_ROWS = 6, KB_COLS = 22;
// Keyboard::v2::CUSTOM_EFFECT_TYPE (effect CUSTOM2) is COLORREF Color[8][24]
// followed by Key[8][24]. Decoding it with the 6x22 stride skewed every row.
static const int KB2_ROWS = 8, KB2_COLS = 24;

// ---------------------------------------------------------------------------
// Real-DLL forwarding (proxy mode)
// ---------------------------------------------------------------------------
static HMODULE g_self = nullptr;
static HMODULE g_real = nullptr;
static std::once_flag g_realOnce;

// The backup name the installer (ChromaShimInstaller.Shims) gives the genuine
// DLL of OUR bitness. One source builds both shims, and a 32-bit host cannot
// load the 64-bit backup (ERROR_BAD_EXE_FORMAT), so the name must follow the
// target: the x86 shim used to look for the 64-bit backup and silently ran
// standalone, blacking out real Razer gear in every 32-bit Chroma game.
#ifdef _WIN64
static const wchar_t* const kRealName = L"RzChromaSDK64_real.dll";
#else
static const wchar_t* const kRealName = L"RzChromaSDK_real.dll";
#endif

typedef RZRESULT (*Fn_v)();
typedef RZRESULT (*Fn_p)(void*);
typedef RZRESULT (*Fn_eff)(int, PRZPARAM, RZEFFECTID*);
typedef RZRESULT (*Fn_dev)(RZDEVICEID, int, PRZPARAM, RZEFFECTID*);
typedef RZRESULT (*Fn_id)(RZEFFECTID);
typedef RZRESULT (*Fn_hwnd)(HWND);
typedef RZRESULT (*Fn_query)(RZDEVICEID, void*);

struct Real {
    Fn_v    Init = nullptr, UnInit = nullptr, UnregisterEventNotification = nullptr;
    Fn_p    InitSDK = nullptr;
    Fn_eff  Keyboard = nullptr, Mouse = nullptr, Headset = nullptr,
            Mousepad = nullptr, Keypad = nullptr, ChromaLink = nullptr;
    Fn_dev  CreateEffect = nullptr;
    Fn_id   SetEffect = nullptr, DeleteEffect = nullptr;
    Fn_hwnd RegisterEventNotification = nullptr;
    Fn_query QueryDevice = nullptr;
} g_r;

static void LoadReal()
{
    // First API call (never DllMain: CRT file I/O under the loader lock).
    ShimLog("=== shim loaded by host ===");
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_self, path, MAX_PATH)) return;
    std::wstring p = path;
    size_t slash = p.find_last_of(L"\\/");
    if (slash == std::wstring::npos) return;
    p = p.substr(0, slash + 1) + kRealName;
    g_real = LoadLibraryW(p.c_str());
    // Once per host load (call_once). ERROR_FILE_NOT_FOUND (2) here is the
    // normal standalone case; anything else means a backup exists but won't
    // load (wrong bitness = 193), which Init()'s "standalone" line would hide.
    if (!g_real) { ShimLog("real DLL not loaded: %ls err=%lu", p.c_str(), GetLastError()); return; }
    auto G = [](const char* n) { return GetProcAddress(g_real, n); };
    g_r.Init       = (Fn_v)G("Init");
    g_r.InitSDK    = (Fn_p)G("InitSDK");
    g_r.UnInit     = (Fn_v)G("UnInit");
    g_r.Keyboard   = (Fn_eff)G("CreateKeyboardEffect");
    g_r.Mouse      = (Fn_eff)G("CreateMouseEffect");
    g_r.Headset    = (Fn_eff)G("CreateHeadsetEffect");
    g_r.Mousepad   = (Fn_eff)G("CreateMousepadEffect");
    g_r.Keypad     = (Fn_eff)G("CreateKeypadEffect");
    g_r.ChromaLink = (Fn_eff)G("CreateChromaLinkEffect");
    g_r.CreateEffect = (Fn_dev)G("CreateEffect");
    g_r.SetEffect  = (Fn_id)G("SetEffect");
    g_r.DeleteEffect = (Fn_id)G("DeleteEffect");
    g_r.RegisterEventNotification = (Fn_hwnd)G("RegisterEventNotification");
    g_r.UnregisterEventNotification = (Fn_v)G("UnregisterEventNotification");
    g_r.QueryDevice = (Fn_query)G("QueryDevice");
}
static void EnsureReal() { std::call_once(g_realOnce, LoadReal); }

// ---------------------------------------------------------------------------
// IPC to UnifiedRGB
// ---------------------------------------------------------------------------
static std::mutex g_pipeMtx;
static HANDLE g_pipe = INVALID_HANDLE_VALUE;
static HANDLE g_pipeEvt = nullptr;                       // overlapped-write completion
static ULONGLONG g_lastConnectTry = 0, g_lastConnectLog = 0;

static void ClosePipe()
{
    if (g_pipe != INVALID_HANDLE_VALUE) { CloseHandle(g_pipe); g_pipe = INVALID_HANDLE_VALUE; }
}

static void SendFrame(uint8_t type, uint16_t rows, uint16_t cols, const COLORREF* colors)
{
    std::lock_guard<std::mutex> lock(g_pipeMtx);
    if (g_pipe == INVALID_HANDLE_VALUE)
    {
        // Rate-limited reconnect: hosts call this at their frame rate, and with
        // UnifiedRGB not running the old path did a CreateFile AND a disk log
        // line 60x/s forever (chroma-shim.log grew ~5 MB/h) on the render thread.
        ULONGLONG now = GetTickCount64();
        if (now - g_lastConnectTry < 1000) return;
        g_lastConnectTry = now;
        // Ask for exactly what a pipe client needs (0x100082), not GENERIC_WRITE:
        // GENERIC_WRITE maps to FILE_GENERIC_WRITE, which also carries
        // FILE_CREATE_PIPE_INSTANCE and FILE_APPEND_DATA, so the app's pipe DACL
        // (ChromaSync.PipeSddl, Everyone = GRGW) has to keep granting those.
        // With this narrower mask a future release can tighten the DACL to this
        // exact set without breaking installed shims; today's GRGW still grants
        // a superset. SECURITY_IDENTIFICATION caps what the server may do with
        // our token at identify-only (it never impersonates us anyway).
        g_pipe = CreateFileW(L"\\\\.\\pipe\\UnifiedRgbChroma",
                             FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE, 0, nullptr,
                             OPEN_EXISTING,
                             FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
                             nullptr);
        if (g_pipe == INVALID_HANDLE_VALUE)
        {
            if (g_lastConnectLog == 0 || now - g_lastConnectLog > 60000)
            { ShimLog("pipe connect FAILED err=%lu (is UnifiedRGB running?)", GetLastError()); g_lastConnectLog = now; }
            return;
        }
        g_lastConnectLog = 0;
        if (!g_pipeEvt) g_pipeEvt = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!g_pipeEvt) { ClosePipe(); return; }
        ShimLog("pipe connected to UnifiedRGB");
    }
    const uint32_t n = (uint32_t)rows * cols;
    std::vector<uint8_t> buf;
    try { buf.resize(5 + n * 4); } catch (...) { return; }
    buf[0] = type;
    buf[1] = (uint8_t)(rows & 0xFF); buf[2] = (uint8_t)(rows >> 8);
    buf[3] = (uint8_t)(cols & 0xFF); buf[4] = (uint8_t)(cols >> 8);
    memcpy(buf.data() + 5, colors, n * 4);

    // Bounded write. A synchronous WriteFile parked the game's render thread -
    // holding g_pipeMtx, so every SDK call on every thread - for as long as
    // the server's buffer stayed full (UnifiedRGB suspended, hung at exit, in a
    // debugger). A frame that can't be delivered within 50 ms is dropped and the
    // pipe re-opened (a half-written frame would desync the byte stream).
    OVERLAPPED ov{}; ov.hEvent = g_pipeEvt;
    ResetEvent(g_pipeEvt);
    DWORD wrote = 0;
    BOOL ok = WriteFile(g_pipe, buf.data(), (DWORD)buf.size(), &wrote, &ov);
    if (!ok && GetLastError() == ERROR_IO_PENDING)
    {
        if (WaitForSingleObject(g_pipeEvt, 50) != WAIT_OBJECT_0)
        {
            CancelIoEx(g_pipe, &ov);
            GetOverlappedResult(g_pipe, &ov, &wrote, TRUE);   // buf must outlive the I/O
            ClosePipe();
            return;
        }
        ok = GetOverlappedResult(g_pipe, &ov, &wrote, FALSE);
    }
    if (!ok) ClosePipe();
}

// ---------------------------------------------------------------------------
// Capture: the color grid is decoded from pParam BEFORE forwarding, so proxy
// mode taps the exact bytes Razer will render.
// ---------------------------------------------------------------------------
static void CaptureKeyboard(int effect, PRZPARAM param)
{
    static int n = 0;
    if (n++ < 3) ShimLog("CreateKeyboardEffect effect=%d param=%p", effect, param);
    if (!param) return;
    if (effect == KB::CUSTOM2)
        SendFrame(1, KB2_ROWS, KB2_COLS, reinterpret_cast<COLORREF*>(param));   // v2 layout, Color[8][24] first
    else if (effect == KB::CUSTOM || effect == KB::CUSTOM_KEY)
        SendFrame(1, KB_ROWS, KB_COLS, reinterpret_cast<COLORREF*>(param));
    else if (effect == KB::STATIC)
        SendFrame(1, 1, 1, reinterpret_cast<COLORREF*>(param));
}
static void CaptureChromaLink(int effect, PRZPARAM param)
{
    static int n = 0;
    if (n++ < 3) ShimLog("CreateChromaLinkEffect effect=%d param=%p", effect, param);
    if (!param) return;
    if (effect == CL::CUSTOM)   SendFrame(2, 1, 5, reinterpret_cast<COLORREF*>(param));
    else if (effect == CL::STATIC) SendFrame(2, 1, 1, reinterpret_cast<COLORREF*>(param));
}

// Standalone mode still needs a SetEffect->frame path (no real DLL to defer
// to), so remember the last captured frame per synthesized id.
struct Frame { uint8_t type; uint16_t rows, cols; std::vector<COLORREF> colors; };
static std::map<uint64_t, Frame> g_pending;
static std::mutex g_fxMtx;
static std::atomic<uint64_t> g_counter{1};             // was a plain ++ outside the lock: duplicate ids across threads
static const size_t kMaxPending = 256;
static GUID SynthId(const Frame* f)
{
    GUID g{}; uint64_t c = g_counter++;
    memcpy(&g.Data1, &c, sizeof(c));
    if (f)
    {
        std::lock_guard<std::mutex> lock(g_fxMtx);
        // Bounded: a host that creates per frame and never DeleteEffect()s (common)
        // stored a 528-byte frame per frame - ~110 MB/h inside the game process.
        // Keys are the ascending counter, so begin() is the oldest.
        while (g_pending.size() >= kMaxPending) g_pending.erase(g_pending.begin());
        g_pending[c] = *f;
    }
    return g;
}
static bool TakeFrame(const RZEFFECTID& id, Frame& out)
{
    uint64_t c; memcpy(&c, &id.Data1, sizeof(c));
    std::lock_guard<std::mutex> lock(g_fxMtx);
    auto it = g_pending.find(c);
    if (it == g_pending.end()) return false;
    out = it->second; return true;
}

// Standalone delivery per the SDK contract: a NULL effect id applies the effect
// NOW; a non-NULL id only STORES it until SetEffect(id). The old path sent at
// Create AND again at Set, so an effect a host created and never Set flashed
// anyway, and every Set repainted twice.
static RZRESULT Deliver(const Frame& f, RZEFFECTID* id)
{
    if (!id)
    {
        if (!f.colors.empty()) SendFrame(f.type, f.rows, f.cols, f.colors.data());
        return RZRESULT_SUCCESS;
    }
    *id = SynthId(f.colors.empty() ? nullptr : &f);
    return RZRESULT_SUCCESS;
}

static void LogCreate(const char* what, int effect, PRZPARAM param, RZEFFECTID* id)
{
    static std::atomic<int> n{0};
    if (n++ < 6) ShimLog("%s effect=%d param=%p id=%s", what, effect, param, id ? "yes (deferred to SetEffect)" : "null (apply now)");
}

extern "C" {

__declspec(dllexport) RZRESULT Init()            { ShimLog("Init()"); EnsureReal(); ShimLog(g_real ? "  proxy: real DLL loaded" : "  standalone (no real DLL)"); return g_r.Init ? g_r.Init() : RZRESULT_SUCCESS; }
__declspec(dllexport) RZRESULT InitSDK(void* a)  { ShimLog("InitSDK()"); EnsureReal(); return g_r.InitSDK ? g_r.InitSDK(a) : RZRESULT_SUCCESS; }
__declspec(dllexport) RZRESULT UnInit()          { return g_r.UnInit ? g_r.UnInit() : RZRESULT_SUCCESS; }

// Exports are extern "C" with /EHsc: a C++ exception (allocation failure in
// the vectors/map) escaping one would terminate the HOST. Every body that can
// allocate is wrapped and reports RZRESULT_FAILED instead.
__declspec(dllexport) RZRESULT CreateKeyboardEffect(int effect, PRZPARAM param, RZEFFECTID* id)
{
    try
    {
        EnsureReal();
        // Proxy: tap the bytes at Create (Razer's own SDK owns the Set timing).
        if (g_r.Keyboard) { CaptureKeyboard(effect, param); return g_r.Keyboard(effect, param, id); }
        LogCreate("CreateKeyboardEffect", effect, param, id);
        Frame f{};
        if (effect == KB::CUSTOM || effect == KB::CUSTOM_KEY || effect == KB::CUSTOM2)
        {
            bool v2 = effect == KB::CUSTOM2;
            f.type = 1; f.rows = v2 ? KB2_ROWS : KB_ROWS; f.cols = v2 ? KB2_COLS : KB_COLS;
            f.colors.resize((size_t)f.rows * f.cols);
            if (param) memcpy(f.colors.data(), param, f.colors.size() * 4);
        }
        else if (effect == KB::STATIC && param)
        { f.type = 1; f.rows = 1; f.cols = 1; f.colors.assign(1, *(COLORREF*)param); }
        return Deliver(f, id);
    }
    catch (...) { return RZRESULT_FAILED; }
}

__declspec(dllexport) RZRESULT CreateChromaLinkEffect(int effect, PRZPARAM param, RZEFFECTID* id)
{
    try
    {
        EnsureReal();
        if (g_r.ChromaLink) { CaptureChromaLink(effect, param); return g_r.ChromaLink(effect, param, id); }
        LogCreate("CreateChromaLinkEffect", effect, param, id);
        Frame f{};
        if (effect == CL::CUSTOM && param) { f.type = 2; f.rows = 1; f.cols = 5; f.colors.resize(5); memcpy(f.colors.data(), param, 20); }
        else if (effect == CL::STATIC && param) { f.type = 2; f.rows = 1; f.cols = 1; f.colors.assign(1, *(COLORREF*)param); }
        return Deliver(f, id);
    }
    catch (...) { return RZRESULT_FAILED; }
}

__declspec(dllexport) RZRESULT CreateMouseEffect(int e, PRZPARAM p, RZEFFECTID* id)    { EnsureReal(); return g_r.Mouse    ? g_r.Mouse(e,p,id)    : (id ? (*id=SynthId(nullptr), RZRESULT_SUCCESS) : RZRESULT_SUCCESS); }
__declspec(dllexport) RZRESULT CreateHeadsetEffect(int e, PRZPARAM p, RZEFFECTID* id)  { EnsureReal(); return g_r.Headset  ? g_r.Headset(e,p,id)  : (id ? (*id=SynthId(nullptr), RZRESULT_SUCCESS) : RZRESULT_SUCCESS); }
__declspec(dllexport) RZRESULT CreateMousepadEffect(int e, PRZPARAM p, RZEFFECTID* id) { EnsureReal(); return g_r.Mousepad ? g_r.Mousepad(e,p,id) : (id ? (*id=SynthId(nullptr), RZRESULT_SUCCESS) : RZRESULT_SUCCESS); }
__declspec(dllexport) RZRESULT CreateKeypadEffect(int e, PRZPARAM p, RZEFFECTID* id)   { EnsureReal(); return g_r.Keypad   ? g_r.Keypad(e,p,id)   : (id ? (*id=SynthId(nullptr), RZRESULT_SUCCESS) : RZRESULT_SUCCESS); }
__declspec(dllexport) RZRESULT CreateEffect(RZDEVICEID d, int e, PRZPARAM p, RZEFFECTID* id) { EnsureReal(); return g_r.CreateEffect ? g_r.CreateEffect(d,e,p,id) : (id ? (*id=SynthId(nullptr), RZRESULT_SUCCESS) : RZRESULT_SUCCESS); }

__declspec(dllexport) RZRESULT SetEffect(RZEFFECTID id)
{
    try
    {
        if (g_r.SetEffect) return g_r.SetEffect(id);      // proxy path already captured at Create
        Frame f;                                          // standalone: send the stored frame
        if (TakeFrame(id, f) && !f.colors.empty()) SendFrame(f.type, f.rows, f.cols, f.colors.data());
        return RZRESULT_SUCCESS;
    }
    catch (...) { return RZRESULT_FAILED; }
}

__declspec(dllexport) RZRESULT DeleteEffect(RZEFFECTID id)
{
    if (g_r.DeleteEffect) return g_r.DeleteEffect(id);
    uint64_t c; memcpy(&c, &id.Data1, sizeof(c));
    std::lock_guard<std::mutex> lock(g_fxMtx); g_pending.erase(c);
    return RZRESULT_SUCCESS;
}

__declspec(dllexport) RZRESULT RegisterEventNotification(HWND h) { return g_r.RegisterEventNotification ? g_r.RegisterEventNotification(h) : RZRESULT_SUCCESS; }
__declspec(dllexport) RZRESULT UnregisterEventNotification()     { return g_r.UnregisterEventNotification ? g_r.UnregisterEventNotification() : RZRESULT_SUCCESS; }
__declspec(dllexport) RZRESULT QueryDevice(RZDEVICEID d, void* i)
{
    EnsureReal();
    if (g_r.QueryDevice) return g_r.QueryDevice(d, i);
    // Standalone (no real Razer DLL): a host asks "is this device connected?" and
    // only pushes effects if we say yes. Report a connected keyboard so hosts
    // actually feed us. DEVICE_INFO_TYPE = { DeviceType Type; DWORD Connected; }.
    if (i) { struct DevInfo { int Type; unsigned long Connected; }* di = (DevInfo*)i; di->Type = 1 /*DEVICE_KEYBOARD*/; di->Connected = 1; }
    return RZRESULT_SUCCESS;
}

} // extern "C"

BOOL APIENTRY DllMain(HMODULE self, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH) { g_self = self; DisableThreadLibraryCalls(self); }
    else if (reason == DLL_PROCESS_DETACH)
    {
        // Process termination (reserved != nullptr): the other threads are
        // already gone - one may have died holding g_pipeMtx inside WriteFile -
        // and taking the mutex here would hang the game's exit under the loader
        // lock. The kernel closes the handles. Only a live FreeLibrary cleans up.
        if (reserved != nullptr) return TRUE;
        std::lock_guard<std::mutex> lock(g_pipeMtx);
        ClosePipe();
        if (g_pipeEvt) { CloseHandle(g_pipeEvt); g_pipeEvt = nullptr; }
        // g_real is deliberately NOT FreeLibrary'd: FreeLibrary from DllMain
        // is documented-unsafe (it runs the real Razer DLL's detach under the
        // loader lock). Each load/unload cycle of the SDK by the host re-maps
        // this shim with fresh statics, so LoadReal LoadLibrary's the real DLL
        // again and leaves one extra reference on it PER cycle. The image stays
        // mapped once, so no memory growth; the references go at process exit.
    }
    return TRUE;
}
