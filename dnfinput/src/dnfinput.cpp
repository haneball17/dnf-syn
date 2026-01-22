#define CINTERFACE
#define COBJMACROS

#include <windows.h>
#include <dinput.h>
#include <objbase.h>
#include <strsafe.h>
#include <intrin.h>
#include <string>
#include <cstdint>
#include <cstring>
#include <cwctype>

#include "MinHook.h"

// ------------------------------
// 全局状态与计数器
// ------------------------------

static HMODULE g_module = nullptr;
static HANDLE g_logFile = INVALID_HANDLE_VALUE;
static CRITICAL_SECTION g_logLock;
static LONG g_logReady = 0;
static LONG g_shouldStop = 0;
static std::wstring g_successFilePath;
static LONG g_successFileCreated = 0;

static volatile LONG g_countGetAsyncKeyState = 0;
static volatile LONG g_countGetKeyboardState = 0;
static volatile LONG g_countDirectInput8Create = 0;
static volatile LONG g_countCreateDevice = 0;
static volatile LONG g_countGetDeviceState = 0;
static volatile LONG g_countGetDeviceStateFailed = 0;
static volatile LONG g_countGetDeviceStateNotAcquired = 0;
static volatile LONG g_countGetDeviceData = 0;
static volatile LONG g_countAcquire = 0;
static volatile LONG g_countPoll = 0;
static volatile LONG g_countUnacquire = 0;
static volatile LONG g_countRegisterRawInput = 0;
static volatile LONG g_countGetRawInputData = 0;
static volatile LONG g_countGetForegroundWindow = 0;
static volatile LONG g_countGetActiveWindow = 0;
static volatile LONG g_countGetFocus = 0;
static volatile LONG g_countSpoofFocus = 0;

static LONG g_createDeviceHooked = 0;
static LONG g_deviceHooksHooked = 0;

// ------------------------------
// 共享内存与伪造配置
// ------------------------------

static const wchar_t* kSharedMemoryName = L"Local\\DNFSyncBox.KeyboardState.V1";
static const uint32_t kSharedVersion = 1;
static const uint32_t kFlagPaused = 0x1;
static const uint32_t kFlagClear = 0x2;
static const ULONGLONG kSharedTimeoutMs = 200;

#pragma pack(push, 1)
struct SharedKeyboardStateV1
{
    uint32_t version;
    uint32_t seq;
    uint32_t flags;
    uint32_t activePid;
    uint32_t profileId;
    uint32_t profileMode;
    uint64_t lastTick;
    uint8_t keyboardState[256];
    uint32_t edgeCounter[256];
    uint8_t targetMask[256];
};
#pragma pack(pop)

struct SharedSnapshot
{
    uint32_t seq;
    uint32_t flags;
    uint32_t activePid;
    uint32_t profileId;
    uint32_t profileMode;
    uint64_t lastTick;
    uint8_t keyboardState[256];
    uint32_t edgeCounter[256];
    uint8_t targetMask[256];
};

static HANDLE g_sharedMapping = nullptr;
static SharedKeyboardStateV1* g_sharedState = nullptr;
static DWORD g_lastSharedAttemptTick = 0;
static LONG g_sharedReadyLogged = 0;
static LONG g_sharedErrorLogged = 0;
static LONG g_sharedVersionLogged = 0;
static uint32_t g_lastEdgeCounter[256] = {};
static uint32_t g_lastProfileId = 0;
static uint32_t g_lastProfileMode = 0;
static uint32_t g_lastClearSeq = 0;
static volatile LONG g_countSpoofAsync = 0;
static volatile LONG g_countSpoofKeyboard = 0;
static volatile LONG g_countSpoofDeviceState = 0;

static int g_vkeyToDik[256] = {};
static LONG g_vkeyMapReady = 0;
static LONG g_forceDeviceStateOk = -1;
static LONG g_forceDeviceStateLogged = 0;

static HWND g_selfWindowCache = nullptr;
static DWORD g_selfWindowCacheTick = 0;

// ------------------------------
// MinHook 目标函数指针
// ------------------------------

using GetAsyncKeyState_t = SHORT(WINAPI*)(int);
static GetAsyncKeyState_t g_origGetAsyncKeyState = nullptr;

using GetKeyboardState_t = BOOL(WINAPI*)(PBYTE);
static GetKeyboardState_t g_origGetKeyboardState = nullptr;

using RegisterRawInputDevices_t = BOOL(WINAPI*)(PCRAWINPUTDEVICE, UINT, UINT);
static RegisterRawInputDevices_t g_origRegisterRawInputDevices = nullptr;

using GetRawInputData_t = UINT(WINAPI*)(HRAWINPUT, UINT, LPVOID, PUINT, UINT);
static GetRawInputData_t g_origGetRawInputData = nullptr;

using GetForegroundWindow_t = HWND(WINAPI*)();
static GetForegroundWindow_t g_origGetForegroundWindow = nullptr;

using GetActiveWindow_t = HWND(WINAPI*)();
static GetActiveWindow_t g_origGetActiveWindow = nullptr;

using GetFocus_t = HWND(WINAPI*)();
static GetFocus_t g_origGetFocus = nullptr;

using DirectInput8Create_t = HRESULT(WINAPI*)(HINSTANCE, DWORD, REFIID, LPVOID*, LPUNKNOWN);
static DirectInput8Create_t g_origDirectInput8Create = nullptr;

using CreateDevice_t = HRESULT(STDMETHODCALLTYPE*)(IDirectInput8W*, REFGUID, LPDIRECTINPUTDEVICE8W*, LPUNKNOWN);
static CreateDevice_t g_origCreateDevice = nullptr;

using GetDeviceState_t = HRESULT(STDMETHODCALLTYPE*)(IDirectInputDevice8W*, DWORD, LPVOID);
static GetDeviceState_t g_origGetDeviceState = nullptr;

using GetDeviceData_t = HRESULT(STDMETHODCALLTYPE*)(IDirectInputDevice8W*, DWORD, LPDIDEVICEOBJECTDATA, LPDWORD, DWORD);
static GetDeviceData_t g_origGetDeviceData = nullptr;

using Acquire_t = HRESULT(STDMETHODCALLTYPE*)(IDirectInputDevice8W*);
static Acquire_t g_origAcquire = nullptr;

using Unacquire_t = HRESULT(STDMETHODCALLTYPE*)(IDirectInputDevice8W*);
static Unacquire_t g_origUnacquire = nullptr;

using Poll_t = HRESULT(STDMETHODCALLTYPE*)(IDirectInputDevice8W*);
static Poll_t g_origPoll = nullptr;

// ------------------------------
// 日志工具（UTF-8）
// ------------------------------

static std::wstring BuildLogPath()
{
    std::wstring baseDir;
    wchar_t modulePath[MAX_PATH] = {0};
    DWORD len = GetModuleFileNameW(g_module, modulePath, ARRAYSIZE(modulePath));
    if (len > 0 && len < ARRAYSIZE(modulePath))
    {
        for (DWORD i = len; i > 0; i--)
        {
            if (modulePath[i - 1] == L'\\' || modulePath[i - 1] == L'/')
            {
                modulePath[i - 1] = L'\0';
                break;
            }
        }
        baseDir = modulePath;
    }

    if (baseDir.empty())
    {
        wchar_t currentDir[MAX_PATH] = {0};
        if (GetCurrentDirectoryW(ARRAYSIZE(currentDir), currentDir) > 0)
        {
            baseDir = currentDir;
        }
        else
        {
            baseDir = L".";
        }
    }

    wchar_t fileName[MAX_PATH] = {0};
    StringCchPrintfW(fileName, ARRAYSIZE(fileName), L"%s\\dnfinput_%lu.log", baseDir.c_str(), GetCurrentProcessId());
    return std::wstring(fileName);
}

static std::wstring GetModuleBaseName()
{
    wchar_t modulePath[MAX_PATH] = {0};
    DWORD len = GetModuleFileNameW(g_module, modulePath, ARRAYSIZE(modulePath));
    if (len == 0 || len >= ARRAYSIZE(modulePath))
    {
        return L"dnfinput";
    }

    const wchar_t* fileName = modulePath;
    for (DWORD i = 0; i < len; i++)
    {
        if (modulePath[i] == L'\\' || modulePath[i] == L'/')
        {
            fileName = modulePath + i + 1;
        }
    }

    std::wstring name(fileName);
    size_t dot = name.rfind(L'.');
    if (dot != std::wstring::npos)
    {
        name = name.substr(0, dot);
    }

    for (auto& ch : name)
    {
        ch = static_cast<wchar_t>(towlower(ch));
    }

    return name.empty() ? L"dnfinput" : name;
}

static std::wstring BuildSuccessFilePath()
{
    std::wstring logPath = BuildLogPath();
    size_t slash = logPath.rfind(L'\\');
    std::wstring logDir = slash == std::wstring::npos ? L"." : logPath.substr(0, slash);
    std::wstring dllName = GetModuleBaseName();
    std::wstring fileName = L"successfile_" + dllName + L"_" + std::to_wstring(GetCurrentProcessId()) + L".txt";
    return logDir + L"\\" + fileName;
}

// 提前声明，避免在 success file 写入处触发未声明错误
static void WriteUtf8BomIfEmpty(HANDLE file);
static std::wstring GetTimestamp();
static std::string WideToUtf8(const std::wstring& input);

// 注入成功后创建 success file，写入时间戳便于外部判断
static void WriteSuccessFile()
{
    if (InterlockedCompareExchange(&g_successFileCreated, 1, 1) == 1)
    {
        return;
    }

    g_successFilePath = BuildSuccessFilePath();
    HANDLE file = CreateFileW(
        g_successFilePath.c_str(),
        FILE_GENERIC_WRITE,
        FILE_SHARE_READ,
        nullptr,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return;
    }

    WriteUtf8BomIfEmpty(file);
    std::wstring line = GetTimestamp() + L"\r\n";
    std::string utf8 = WideToUtf8(line);
    if (!utf8.empty())
    {
        DWORD written = 0;
        WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
    }

    CloseHandle(file);
    InterlockedExchange(&g_successFileCreated, 1);
}

// 进程退出时清理 success file，避免残留误判
static void RemoveSuccessFile()
{
    if (g_successFilePath.empty())
    {
        return;
    }

    DeleteFileW(g_successFilePath.c_str());
}

static void WriteUtf8BomIfEmpty(HANDLE file)
{
    LARGE_INTEGER size = {};
    if (!GetFileSizeEx(file, &size))
    {
        return;
    }
    if (size.QuadPart != 0)
    {
        return;
    }
    const BYTE bom[] = {0xEF, 0xBB, 0xBF};
    DWORD written = 0;
    WriteFile(file, bom, ARRAYSIZE(bom), &written, nullptr);
}

static std::string WideToUtf8(const std::wstring& input)
{
    if (input.empty())
    {
        return std::string();
    }
    int size = WideCharToMultiByte(CP_UTF8, 0, input.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (size <= 0)
    {
        return std::string();
    }
    std::string output(static_cast<size_t>(size - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, input.c_str(), -1, output.data(), size - 1, nullptr, nullptr);
    return output;
}

static void WriteLogLine(const std::wstring& line)
{
    if (InterlockedCompareExchange(&g_logReady, 1, 1) != 1)
    {
        return;
    }

    EnterCriticalSection(&g_logLock);

    std::wstring withNewline = line + L"\r\n";
    std::string utf8 = WideToUtf8(withNewline);
    if (!utf8.empty())
    {
        DWORD written = 0;
        WriteFile(g_logFile, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
    }

    LeaveCriticalSection(&g_logLock);
}

static std::wstring GetTimestamp()
{
    SYSTEMTIME st = {};
    GetLocalTime(&st);
    wchar_t buffer[64] = {0};
    StringCchPrintfW(
        buffer,
        ARRAYSIZE(buffer),
        L"%04u-%02u-%02u %02u:%02u:%02u.%03u",
        st.wYear,
        st.wMonth,
        st.wDay,
        st.wHour,
        st.wMinute,
        st.wSecond,
        st.wMilliseconds);
    return std::wstring(buffer);
}

static void LogInfo(const std::wstring& message)
{
    WriteLogLine(L"[INFO] " + GetTimestamp() + L" " + message);
}

static void LogError(const std::wstring& message)
{
    WriteLogLine(L"[ERROR] " + GetTimestamp() + L" " + message);
}

static std::wstring AnsiToWide(const char* text)
{
    if (!text)
    {
        return L"";
    }
    int size = MultiByteToWideChar(CP_UTF8, 0, text, -1, nullptr, 0);
    if (size <= 0)
    {
        return L"";
    }
    std::wstring output(static_cast<size_t>(size - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text, -1, output.data(), size - 1);
    return output;
}

static void LogMinHookStatus(const std::wstring& action, MH_STATUS status)
{
    std::wstring detail = AnsiToWide(MH_StatusToString(status));
    LogInfo(action + L" -> " + detail);
}

static void InitializeLogging()
{
    InitializeCriticalSection(&g_logLock);

    std::wstring path = BuildLogPath();
    g_logFile = CreateFileW(
        path.c_str(),
        FILE_APPEND_DATA,
        FILE_SHARE_READ,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);

    if (g_logFile == INVALID_HANDLE_VALUE)
    {
        return;
    }

    WriteUtf8BomIfEmpty(g_logFile);
    InterlockedExchange(&g_logReady, 1);

    LogInfo(L"dnfinput 初始化日志文件成功");
    LogInfo(L"日志路径: " + path);
}

// ------------------------------
// 断链（LDR Unlink）与抹头
// ------------------------------

typedef struct _UNICODE_STRING_T
{
    USHORT Length;
    USHORT MaximumLength;
    PWSTR Buffer;
} UNICODE_STRING_T, *PUNICODE_STRING_T;

typedef struct _LDR_DATA_TABLE_ENTRY_T
{
    LIST_ENTRY InLoadOrderLinks;
    LIST_ENTRY InMemoryOrderLinks;
    LIST_ENTRY InInitializationOrderLinks;
    PVOID DllBase;
    PVOID EntryPoint;
    ULONG SizeOfImage;
    UNICODE_STRING_T FullDllName;
    UNICODE_STRING_T BaseDllName;
} LDR_DATA_TABLE_ENTRY_T, *PLDR_DATA_TABLE_ENTRY_T;

typedef struct _PEB_LDR_DATA_T
{
    ULONG Length;
    BOOLEAN Initialized;
    PVOID SsHandle;
    LIST_ENTRY InLoadOrderModuleList;
    LIST_ENTRY InMemoryOrderModuleList;
    LIST_ENTRY InInitializationOrderModuleList;
} PEB_LDR_DATA_T, *PPEB_LDR_DATA_T;

typedef struct _PEB_T
{
    BYTE Reserved1[2];
    BYTE BeingDebugged;
    BYTE Reserved2[1];
    PVOID Reserved3[2];
    PPEB_LDR_DATA_T Ldr;
} PEB_T, *PPEB_T;

static PPEB_T GetPeb()
{
#if defined(_M_IX86)
    return reinterpret_cast<PPEB_T>(__readfsdword(0x30));
#else
    return nullptr;
#endif
}

static bool UnlinkFromPeb(HMODULE module)
{
    // 目的：把自身从 PEB 模块链表移除，降低被枚举发现的概率
    PPEB_T peb = GetPeb();
    if (!peb || !peb->Ldr || !module)
    {
        return false;
    }

    LIST_ENTRY* head = &peb->Ldr->InLoadOrderModuleList;
    for (LIST_ENTRY* entry = head->Flink; entry != head; entry = entry->Flink)
    {
        auto* data = CONTAINING_RECORD(entry, LDR_DATA_TABLE_ENTRY_T, InLoadOrderLinks);
        if (data && data->DllBase == module)
        {
            auto removeEntry = [](LIST_ENTRY* item)
            {
                if (!item || !item->Flink || !item->Blink)
                {
                    return;
                }
                item->Blink->Flink = item->Flink;
                item->Flink->Blink = item->Blink;
                item->Flink = item;
                item->Blink = item;
            };

            removeEntry(&data->InLoadOrderLinks);
            removeEntry(&data->InMemoryOrderLinks);
            removeEntry(&data->InInitializationOrderLinks);
            return true;
        }
    }

    return false;
}

static bool ErasePeHeader(HMODULE module)
{
    // 目的：清除 PE 头部特征，降低内存特征扫描命中率
    if (!module)
    {
        return false;
    }

    auto* dos = reinterpret_cast<PIMAGE_DOS_HEADER>(module);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return false;
    }

    auto* nt = reinterpret_cast<PIMAGE_NT_HEADERS32>(reinterpret_cast<BYTE*>(module) + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
    {
        return false;
    }

    SIZE_T headerSize = nt->OptionalHeader.SizeOfHeaders;
    if (headerSize == 0)
    {
        return false;
    }

    // 保守抹头：最大 4KB，避免影响过多内存区域
    const SIZE_T eraseSize = (headerSize > 0x1000) ? 0x1000 : headerSize;
    DWORD oldProtect = 0;
    if (!VirtualProtect(module, eraseSize, PAGE_READWRITE, &oldProtect))
    {
        return false;
    }

    SecureZeroMemory(module, eraseSize);
    VirtualProtect(module, eraseSize, oldProtect, &oldProtect);
    return true;
}

// ------------------------------
// 共享内存读取与快照
// ------------------------------

static bool EnsureSharedMemory()
{
    if (g_sharedState)
    {
        return true;
    }

    DWORD now = GetTickCount();
    if (now - g_lastSharedAttemptTick < 1000)
    {
        return false;
    }
    g_lastSharedAttemptTick = now;

    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, kSharedMemoryName);
    if (!mapping)
    {
        if (InterlockedCompareExchange(&g_sharedErrorLogged, 1, 0) == 0)
        {
            LogError(L"共享内存未就绪，等待控制端启动");
        }
        return false;
    }

    void* view = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, sizeof(SharedKeyboardStateV1));
    if (!view)
    {
        CloseHandle(mapping);
        return false;
    }

    g_sharedMapping = mapping;
    g_sharedState = static_cast<SharedKeyboardStateV1*>(view);

    if (InterlockedCompareExchange(&g_sharedReadyLogged, 1, 0) == 0)
    {
        LogInfo(L"共享内存已连接");
    }

    return true;
}

static bool ReadSharedSnapshot(SharedSnapshot& snapshot)
{
    if (!EnsureSharedMemory() || !g_sharedState)
    {
        return false;
    }

    if (g_sharedState->version != kSharedVersion)
    {
        if (InterlockedCompareExchange(&g_sharedVersionLogged, 1, 0) == 0)
        {
            LogError(L"共享内存版本不匹配，已停止伪造");
        }
        return false;
    }

    for (int i = 0; i < 3; i++)
    {
        uint32_t seq1 = g_sharedState->seq;
        if ((seq1 & 1) != 0)
        {
            continue;
        }

        snapshot.seq = seq1;
        snapshot.flags = g_sharedState->flags;
        snapshot.activePid = g_sharedState->activePid;
        snapshot.profileId = g_sharedState->profileId;
        snapshot.profileMode = g_sharedState->profileMode;
        snapshot.lastTick = g_sharedState->lastTick;
        memcpy(snapshot.keyboardState, g_sharedState->keyboardState, sizeof(snapshot.keyboardState));
        memcpy(snapshot.edgeCounter, g_sharedState->edgeCounter, sizeof(snapshot.edgeCounter));
        memcpy(snapshot.targetMask, g_sharedState->targetMask, sizeof(snapshot.targetMask));

        uint32_t seq2 = g_sharedState->seq;
        if (seq1 == seq2 && (seq2 & 1) == 0)
        {
            g_lastProfileId = snapshot.profileId;
            g_lastProfileMode = snapshot.profileMode;
            return true;
        }
    }

    return false;
}

static void ApplyClearIfNeeded(const SharedSnapshot& snapshot)
{
    if ((snapshot.flags & kFlagClear) == 0)
    {
        return;
    }

    if (snapshot.seq == g_lastClearSeq)
    {
        return;
    }

    g_lastClearSeq = snapshot.seq;
    for (int i = 0; i < 256; i++)
    {
        g_lastEdgeCounter[i] = snapshot.edgeCounter[i];
    }
}

static bool IsSnapshotAlive(const SharedSnapshot& snapshot)
{
    if (snapshot.lastTick == 0)
    {
        return false;
    }

    ULONGLONG now = GetTickCount64();
    return now - snapshot.lastTick <= kSharedTimeoutMs;
}

static bool IsBypassProcess(const SharedSnapshot& snapshot)
{
    if (snapshot.activePid == 0)
    {
        return false;
    }

    return snapshot.activePid == GetCurrentProcessId();
}

static void EnsureVkeyToDikMap()
{
    // DirectInput 键盘使用 DIK 扫描码索引 256 字节状态数组，这里把 vKey 映射到 DIK
    // 以复用共享内存的键盘状态；扩展键需要补 0x80。
    if (InterlockedCompareExchange(&g_vkeyMapReady, 1, 0) != 0)
    {
        return;
    }

    for (int i = 0; i < 256; i++)
    {
        g_vkeyToDik[i] = -1;
    }

    for (int vKey = 0; vKey < 256; vKey++)
    {
        UINT scan = MapVirtualKeyW(static_cast<UINT>(vKey), MAPVK_VK_TO_VSC_EX);
        if (scan == 0)
        {
            continue;
        }

        int dik = static_cast<int>(scan & 0xFF);
        if ((scan & 0x100) != 0)
        {
            dik |= 0x80;
        }

        if (dik >= 0 && dik < 256)
        {
            g_vkeyToDik[vKey] = dik;
        }
    }
}

static bool ShouldForceDeviceStateOk()
{
    LONG cached = InterlockedCompareExchange(&g_forceDeviceStateOk, -1, -1);
    if (cached != -1)
    {
        return cached != 0;
    }

    wchar_t value[8] = {0};
    DWORD len = GetEnvironmentVariableW(L"DNFSYNC_FORCE_DI_OK", value, ARRAYSIZE(value));
    bool enabled = false;
    if (len > 0)
    {
        wchar_t ch = value[0];
        enabled = (ch == L'1' || ch == L'y' || ch == L'Y' || ch == L't' || ch == L'T');
    }

    InterlockedExchange(&g_forceDeviceStateOk, enabled ? 1 : 0);
    return enabled;
}

struct WindowSearchContext
{
    DWORD pid;
    HWND best;
    HWND fallback;
};

static BOOL CALLBACK EnumWindowsFindSelf(HWND hwnd, LPARAM lparam)
{
    auto* ctx = reinterpret_cast<WindowSearchContext*>(lparam);
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ctx->pid)
    {
        return TRUE;
    }

    if (!IsWindowVisible(hwnd))
    {
        return TRUE;
    }

    if (GetWindow(hwnd, GW_OWNER) != nullptr)
    {
        return TRUE;
    }

    if (!ctx->fallback)
    {
        ctx->fallback = hwnd;
    }

    if (GetWindowTextLengthW(hwnd) > 0)
    {
        ctx->best = hwnd;
        return FALSE;
    }

    return TRUE;
}

static HWND GetSelfMainWindow()
{
    if (g_selfWindowCache && IsWindow(g_selfWindowCache))
    {
        return g_selfWindowCache;
    }

    DWORD now = GetTickCount();
    if (now - g_selfWindowCacheTick < 1000)
    {
        return g_selfWindowCache;
    }

    g_selfWindowCacheTick = now;
    WindowSearchContext ctx = {};
    ctx.pid = GetCurrentProcessId();
    EnumWindows(EnumWindowsFindSelf, reinterpret_cast<LPARAM>(&ctx));

    HWND result = ctx.best ? ctx.best : ctx.fallback;
    if (result && IsWindow(result))
    {
        g_selfWindowCache = result;
    }

    return g_selfWindowCache;
}

static bool IsWindowOwnedBySelf(HWND hwnd)
{
    if (!hwnd)
    {
        return false;
    }

    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    return pid == GetCurrentProcessId();
}

static bool ShouldSpoofFocus()
{
    // 仅在后台且共享内存存活/未暂停时伪造焦点，避免影响前台与暂停态。
    SharedSnapshot snapshot = {};
    if (!ReadSharedSnapshot(snapshot))
    {
        return false;
    }

    if (!IsSnapshotAlive(snapshot))
    {
        return false;
    }

    if ((snapshot.flags & kFlagPaused) != 0)
    {
        return false;
    }

    if (snapshot.activePid == 0)
    {
        return false;
    }

    if (IsBypassProcess(snapshot))
    {
        return false;
    }

    return true;
}

// ------------------------------
// Hook 回调
// ------------------------------

static SHORT WINAPI Hook_GetAsyncKeyState(int vKey)
{
    InterlockedIncrement(&g_countGetAsyncKeyState);

    if (!g_origGetAsyncKeyState)
    {
        return 0;
    }

    SHORT original = g_origGetAsyncKeyState(vKey);
    if (vKey < 0 || vKey >= 256)
    {
        return original;
    }

    SharedSnapshot snapshot = {};
    if (!ReadSharedSnapshot(snapshot))
    {
        return original;
    }

    ApplyClearIfNeeded(snapshot);

    if (IsBypassProcess(snapshot))
    {
        return original;
    }

    const bool alive = IsSnapshotAlive(snapshot);
    const bool paused = (snapshot.flags & kFlagPaused) != 0;
    const bool shouldOverride = snapshot.targetMask[vKey] != 0;

    if (!shouldOverride)
    {
        return original;
    }

    if (!alive || paused)
    {
        return 0;
    }

    SHORT result = 0;
    if (snapshot.keyboardState[vKey] & 0x80)
    {
        result |= static_cast<SHORT>(0x8000);
    }

    uint32_t currentEdge = snapshot.edgeCounter[vKey];
    if (currentEdge != g_lastEdgeCounter[vKey])
    {
        g_lastEdgeCounter[vKey] = currentEdge;
        result |= 0x0001;
    }

    InterlockedIncrement(&g_countSpoofAsync);
    return result;
}

static BOOL WINAPI Hook_GetKeyboardState(PBYTE lpKeyState)
{
    InterlockedIncrement(&g_countGetKeyboardState);

    if (!g_origGetKeyboardState)
    {
        return FALSE;
    }

    BOOL ok = g_origGetKeyboardState(lpKeyState);
    if (!ok || !lpKeyState)
    {
        return ok;
    }

    SharedSnapshot snapshot = {};
    if (!ReadSharedSnapshot(snapshot))
    {
        return ok;
    }

    ApplyClearIfNeeded(snapshot);

    if (IsBypassProcess(snapshot))
    {
        return ok;
    }

    const bool alive = IsSnapshotAlive(snapshot);
    const bool paused = (snapshot.flags & kFlagPaused) != 0;
    bool spoofed = false;

    for (int i = 0; i < 256; i++)
    {
        if (snapshot.targetMask[i] == 0)
        {
            continue;
        }

        if (!alive || paused)
        {
            lpKeyState[i] &= static_cast<BYTE>(~0x81);
            spoofed = true;
            continue;
        }

        BYTE desired = snapshot.keyboardState[i];
        lpKeyState[i] = (lpKeyState[i] & static_cast<BYTE>(~0x81)) | (desired & 0x81);
        spoofed = true;
    }

    if (spoofed)
    {
        InterlockedIncrement(&g_countSpoofKeyboard);
    }

    return ok;
}

static BOOL WINAPI Hook_RegisterRawInputDevices(PCRAWINPUTDEVICE devices, UINT numDevices, UINT size)
{
    InterlockedIncrement(&g_countRegisterRawInput);
    return g_origRegisterRawInputDevices ? g_origRegisterRawInputDevices(devices, numDevices, size) : FALSE;
}

static UINT WINAPI Hook_GetRawInputData(HRAWINPUT hRawInput, UINT command, LPVOID data, PUINT size, UINT headerSize)
{
    InterlockedIncrement(&g_countGetRawInputData);
    if (!g_origGetRawInputData)
    {
        return 0;
    }

    UINT result = g_origGetRawInputData(hRawInput, command, data, size, headerSize);

    // 仅记录键盘 RawInput，便于判定按键是否只通过 RawInput 进入 DNF。
    if (result > 0 && command == RID_INPUT && data && size && *size >= sizeof(RAWINPUT))
    {
        const RAWINPUT* raw = static_cast<const RAWINPUT*>(data);
        if (raw->header.dwType == RIM_TYPEKEYBOARD)
        {
            const RAWKEYBOARD& kb = raw->data.keyboard;
            wchar_t buffer[256] = {0};
            StringCchPrintfW(
                buffer,
                ARRAYSIZE(buffer),
                L"[RAW] %s RawInput 键盘: VKey=%u(0x%02X) MakeCode=%u(0x%02X) Flags=0x%X",
                GetTimestamp().c_str(),
                kb.VKey,
                kb.VKey,
                kb.MakeCode,
                kb.MakeCode,
                kb.Flags);
            WriteLogLine(buffer);
        }
    }

    return result;
}

static HWND WINAPI Hook_GetForegroundWindow()
{
    InterlockedIncrement(&g_countGetForegroundWindow);
    HWND original = g_origGetForegroundWindow ? g_origGetForegroundWindow() : nullptr;
    if (!ShouldSpoofFocus())
    {
        return original;
    }

    // 后台时伪造前台窗口，避免客户端因焦点限制丢弃输入。
    if (IsWindowOwnedBySelf(original))
    {
        return original;
    }

    HWND selfWindow = GetSelfMainWindow();
    if (!selfWindow)
    {
        return original;
    }

    InterlockedIncrement(&g_countSpoofFocus);
    return selfWindow;
}

static HWND WINAPI Hook_GetActiveWindow()
{
    InterlockedIncrement(&g_countGetActiveWindow);
    HWND original = g_origGetActiveWindow ? g_origGetActiveWindow() : nullptr;
    if (!ShouldSpoofFocus())
    {
        return original;
    }

    if (IsWindowOwnedBySelf(original))
    {
        return original;
    }

    HWND selfWindow = GetSelfMainWindow();
    if (!selfWindow)
    {
        return original;
    }

    InterlockedIncrement(&g_countSpoofFocus);
    return selfWindow;
}

static HWND WINAPI Hook_GetFocus()
{
    InterlockedIncrement(&g_countGetFocus);
    HWND original = g_origGetFocus ? g_origGetFocus() : nullptr;
    if (!ShouldSpoofFocus())
    {
        return original;
    }

    if (IsWindowOwnedBySelf(original))
    {
        return original;
    }

    HWND selfWindow = GetSelfMainWindow();
    if (!selfWindow)
    {
        return original;
    }

    InterlockedIncrement(&g_countSpoofFocus);
    return selfWindow;
}

static HRESULT STDMETHODCALLTYPE Hook_GetDeviceState(IDirectInputDevice8W* device, DWORD size, LPVOID data)
{
    InterlockedIncrement(&g_countGetDeviceState);

    if (!g_origGetDeviceState)
    {
        return DIERR_GENERIC;
    }

    HRESULT hr = g_origGetDeviceState(device, size, data);
    bool forceOk = false;
    if (FAILED(hr))
    {
        InterlockedIncrement(&g_countGetDeviceStateFailed);
        if (hr == DIERR_NOTACQUIRED)
        {
            InterlockedIncrement(&g_countGetDeviceStateNotAcquired);
            forceOk = ShouldForceDeviceStateOk();
            if (!forceOk)
            {
                return hr;
            }
        }
        else
        {
            return hr;
        }
    }

    // DirectInput 键盘状态固定 256 字节，避免对非键盘设备误改。
    if (size < 256 || !data)
    {
        return hr;
    }

    SharedSnapshot snapshot = {};
    if (!ReadSharedSnapshot(snapshot))
    {
        return hr;
    }

    ApplyClearIfNeeded(snapshot);
    if (IsBypassProcess(snapshot))
    {
        return hr;
    }

    const bool alive = IsSnapshotAlive(snapshot);
    const bool paused = (snapshot.flags & kFlagPaused) != 0;

    // DNF 走 DirectInput 轮询时需要覆盖 GetDeviceState，否则后台状态无法被读取到。
    EnsureVkeyToDikMap();
    auto* state = static_cast<BYTE*>(data);
    bool spoofed = false;

    for (int vKey = 0; vKey < 256; vKey++)
    {
        if (snapshot.targetMask[vKey] == 0)
        {
            continue;
        }

        int dik = g_vkeyToDik[vKey];
        if (dik < 0 || dik >= 256)
        {
            continue;
        }

        if (!alive || paused)
        {
            state[dik] = 0;
            spoofed = true;
            continue;
        }

        state[dik] = (snapshot.keyboardState[vKey] & 0x80) ? 0x80 : 0x00;
        spoofed = true;
    }

    if (spoofed)
    {
        InterlockedIncrement(&g_countSpoofDeviceState);
    }

    if (forceOk && spoofed)
    {
        if (InterlockedCompareExchange(&g_forceDeviceStateLogged, 1, 0) == 0)
        {
            LogInfo(L"GetDeviceState 返回 DIERR_NOTACQUIRED，已按配置强制返回 DI_OK");
        }
        return DI_OK;
    }

    return hr;
}

static HRESULT STDMETHODCALLTYPE Hook_GetDeviceData(
    IDirectInputDevice8W* device,
    DWORD objectDataSize,
    LPDIDEVICEOBJECTDATA data,
    LPDWORD entries,
    DWORD flags)
{
    InterlockedIncrement(&g_countGetDeviceData);
    return g_origGetDeviceData ? g_origGetDeviceData(device, objectDataSize, data, entries, flags) : DIERR_GENERIC;
}

static HRESULT STDMETHODCALLTYPE Hook_Acquire(IDirectInputDevice8W* device)
{
    InterlockedIncrement(&g_countAcquire);
    return g_origAcquire ? g_origAcquire(device) : DIERR_GENERIC;
}

static HRESULT STDMETHODCALLTYPE Hook_Unacquire(IDirectInputDevice8W* device)
{
    InterlockedIncrement(&g_countUnacquire);
    return g_origUnacquire ? g_origUnacquire(device) : DIERR_GENERIC;
}

static HRESULT STDMETHODCALLTYPE Hook_Poll(IDirectInputDevice8W* device)
{
    InterlockedIncrement(&g_countPoll);
    return g_origPoll ? g_origPoll(device) : DIERR_GENERIC;
}

static void LogGuid(const wchar_t* prefix, REFGUID guid)
{
    wchar_t guidText[64] = {0};
    if (StringFromGUID2(guid, guidText, ARRAYSIZE(guidText)) > 0)
    {
        LogInfo(std::wstring(prefix) + L" " + guidText);
    }
}

static void InstallDeviceHooks(IDirectInputDevice8W* device)
{
    if (!device)
    {
        return;
    }

    // vtbl 地址通常全局共享，首次 Hook 即可覆盖后续设备
    if (InterlockedCompareExchange(&g_deviceHooksHooked, 1, 0) != 0)
    {
        return;
    }

    IDirectInputDevice8WVtbl* vtbl = device->lpVtbl;
    if (!vtbl)
    {
        return;
    }

    MH_STATUS status = MH_CreateHook(reinterpret_cast<LPVOID>(vtbl->GetDeviceState), Hook_GetDeviceState, reinterpret_cast<LPVOID*>(&g_origGetDeviceState));
    LogMinHookStatus(L"Hook GetDeviceState", status);
    if (status == MH_OK)
    {
        MH_EnableHook(reinterpret_cast<LPVOID>(vtbl->GetDeviceState));
    }

    status = MH_CreateHook(reinterpret_cast<LPVOID>(vtbl->GetDeviceData), Hook_GetDeviceData, reinterpret_cast<LPVOID*>(&g_origGetDeviceData));
    LogMinHookStatus(L"Hook GetDeviceData", status);
    if (status == MH_OK)
    {
        MH_EnableHook(reinterpret_cast<LPVOID>(vtbl->GetDeviceData));
    }

    status = MH_CreateHook(reinterpret_cast<LPVOID>(vtbl->Acquire), Hook_Acquire, reinterpret_cast<LPVOID*>(&g_origAcquire));
    LogMinHookStatus(L"Hook Acquire", status);
    if (status == MH_OK)
    {
        MH_EnableHook(reinterpret_cast<LPVOID>(vtbl->Acquire));
    }

    status = MH_CreateHook(reinterpret_cast<LPVOID>(vtbl->Unacquire), Hook_Unacquire, reinterpret_cast<LPVOID*>(&g_origUnacquire));
    LogMinHookStatus(L"Hook Unacquire", status);
    if (status == MH_OK)
    {
        MH_EnableHook(reinterpret_cast<LPVOID>(vtbl->Unacquire));
    }

    status = MH_CreateHook(reinterpret_cast<LPVOID>(vtbl->Poll), Hook_Poll, reinterpret_cast<LPVOID*>(&g_origPoll));
    LogMinHookStatus(L"Hook Poll", status);
    if (status == MH_OK)
    {
        MH_EnableHook(reinterpret_cast<LPVOID>(vtbl->Poll));
    }
}

static HRESULT STDMETHODCALLTYPE Hook_CreateDevice(
    IDirectInput8W* self,
    REFGUID rguid,
    LPDIRECTINPUTDEVICE8W* device,
    LPUNKNOWN unkOuter)
{
    InterlockedIncrement(&g_countCreateDevice);
    LogGuid(L"CreateDevice GUID:", rguid);

    HRESULT hr = g_origCreateDevice ? g_origCreateDevice(self, rguid, device, unkOuter) : DIERR_GENERIC;
    if (SUCCEEDED(hr) && device && *device)
    {
        InstallDeviceHooks(*device);
    }
    return hr;
}

static HRESULT WINAPI Hook_DirectInput8Create(
    HINSTANCE hinst,
    DWORD version,
    REFIID riid,
    LPVOID* out,
    LPUNKNOWN unkOuter)
{
    InterlockedIncrement(&g_countDirectInput8Create);

    HRESULT hr = g_origDirectInput8Create ? g_origDirectInput8Create(hinst, version, riid, out, unkOuter) : DIERR_GENERIC;
    if (SUCCEEDED(hr) && out && *out)
    {
        // 首次创建接口时再 Hook CreateDevice，避免提前拿不到 vtbl
        if (InterlockedCompareExchange(&g_createDeviceHooked, 1, 0) == 0)
        {
            auto* dinput = reinterpret_cast<IDirectInput8W*>(*out);
            if (dinput && dinput->lpVtbl)
            {
                MH_STATUS status = MH_CreateHook(
                    reinterpret_cast<LPVOID>(dinput->lpVtbl->CreateDevice),
                    Hook_CreateDevice,
                    reinterpret_cast<LPVOID*>(&g_origCreateDevice));
                LogMinHookStatus(L"Hook CreateDevice", status);
                if (status == MH_OK)
                {
                    MH_EnableHook(reinterpret_cast<LPVOID>(dinput->lpVtbl->CreateDevice));
                }
            }
        }
    }

    return hr;
}

// ------------------------------
// Hook 初始化
// ------------------------------

static void InstallUser32Hooks()
{
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    if (!user32)
    {
        LogError(L"user32.dll 未加载，无法安装 Win32 Hook");
        return;
    }

    auto* getAsync = reinterpret_cast<LPVOID>(GetProcAddress(user32, "GetAsyncKeyState"));
    if (getAsync)
    {
        MH_STATUS status = MH_CreateHook(getAsync, Hook_GetAsyncKeyState, reinterpret_cast<LPVOID*>(&g_origGetAsyncKeyState));
        LogMinHookStatus(L"Hook GetAsyncKeyState", status);
        if (status == MH_OK)
        {
            MH_EnableHook(getAsync);
        }
    }

    auto* getKeyboard = reinterpret_cast<LPVOID>(GetProcAddress(user32, "GetKeyboardState"));
    if (getKeyboard)
    {
        MH_STATUS status = MH_CreateHook(getKeyboard, Hook_GetKeyboardState, reinterpret_cast<LPVOID*>(&g_origGetKeyboardState));
        LogMinHookStatus(L"Hook GetKeyboardState", status);
        if (status == MH_OK)
        {
            MH_EnableHook(getKeyboard);
        }
    }

    auto* regRaw = reinterpret_cast<LPVOID>(GetProcAddress(user32, "RegisterRawInputDevices"));
    if (regRaw)
    {
        MH_STATUS status = MH_CreateHook(regRaw, Hook_RegisterRawInputDevices, reinterpret_cast<LPVOID*>(&g_origRegisterRawInputDevices));
        LogMinHookStatus(L"Hook RegisterRawInputDevices", status);
        if (status == MH_OK)
        {
            MH_EnableHook(regRaw);
        }
    }

    auto* rawData = reinterpret_cast<LPVOID>(GetProcAddress(user32, "GetRawInputData"));
    if (rawData)
    {
        MH_STATUS status = MH_CreateHook(rawData, Hook_GetRawInputData, reinterpret_cast<LPVOID*>(&g_origGetRawInputData));
        LogMinHookStatus(L"Hook GetRawInputData", status);
        if (status == MH_OK)
        {
            MH_EnableHook(rawData);
        }
    }

    auto* getForeground = reinterpret_cast<LPVOID>(GetProcAddress(user32, "GetForegroundWindow"));
    if (getForeground)
    {
        MH_STATUS status = MH_CreateHook(getForeground, Hook_GetForegroundWindow, reinterpret_cast<LPVOID*>(&g_origGetForegroundWindow));
        LogMinHookStatus(L"Hook GetForegroundWindow", status);
        if (status == MH_OK)
        {
            MH_EnableHook(getForeground);
        }
    }

    auto* getActive = reinterpret_cast<LPVOID>(GetProcAddress(user32, "GetActiveWindow"));
    if (getActive)
    {
        MH_STATUS status = MH_CreateHook(getActive, Hook_GetActiveWindow, reinterpret_cast<LPVOID*>(&g_origGetActiveWindow));
        LogMinHookStatus(L"Hook GetActiveWindow", status);
        if (status == MH_OK)
        {
            MH_EnableHook(getActive);
        }
    }

    auto* getFocus = reinterpret_cast<LPVOID>(GetProcAddress(user32, "GetFocus"));
    if (getFocus)
    {
        MH_STATUS status = MH_CreateHook(getFocus, Hook_GetFocus, reinterpret_cast<LPVOID*>(&g_origGetFocus));
        LogMinHookStatus(L"Hook GetFocus", status);
        if (status == MH_OK)
        {
            MH_EnableHook(getFocus);
        }
    }
}

static void InstallDirectInputHook()
{
    HMODULE dinput = GetModuleHandleW(L"dinput8.dll");
    if (!dinput)
    {
        // 确保 dinput8 已加载，方便取得导出函数地址
        dinput = LoadLibraryW(L"dinput8.dll");
    }

    if (!dinput)
    {
        LogError(L"dinput8.dll 未加载，无法 Hook DirectInput8Create");
        return;
    }

    auto* proc = reinterpret_cast<LPVOID>(GetProcAddress(dinput, "DirectInput8Create"));
    if (!proc)
    {
        LogError(L"未找到 DirectInput8Create 导出");
        return;
    }

    MH_STATUS status = MH_CreateHook(proc, Hook_DirectInput8Create, reinterpret_cast<LPVOID*>(&g_origDirectInput8Create));
    LogMinHookStatus(L"Hook DirectInput8Create", status);
    if (status == MH_OK)
    {
        MH_EnableHook(proc);
    }
}

static void LogCountersOnce()
{
    // 每秒输出一次统计，避免在高频回调里写日志造成干扰
    LONG getAsync = InterlockedExchange(&g_countGetAsyncKeyState, 0);
    LONG getKeyboard = InterlockedExchange(&g_countGetKeyboardState, 0);
    LONG diCreate = InterlockedExchange(&g_countDirectInput8Create, 0);
    LONG createDevice = InterlockedExchange(&g_countCreateDevice, 0);
    LONG getState = InterlockedExchange(&g_countGetDeviceState, 0);
    LONG getStateFailed = InterlockedExchange(&g_countGetDeviceStateFailed, 0);
    LONG getStateNotAcquired = InterlockedExchange(&g_countGetDeviceStateNotAcquired, 0);
    LONG getData = InterlockedExchange(&g_countGetDeviceData, 0);
    LONG acquire = InterlockedExchange(&g_countAcquire, 0);
    LONG poll = InterlockedExchange(&g_countPoll, 0);
    LONG unacquire = InterlockedExchange(&g_countUnacquire, 0);
    LONG rawRegister = InterlockedExchange(&g_countRegisterRawInput, 0);
    LONG rawData = InterlockedExchange(&g_countGetRawInputData, 0);
    LONG getForeground = InterlockedExchange(&g_countGetForegroundWindow, 0);
    LONG getActive = InterlockedExchange(&g_countGetActiveWindow, 0);
    LONG getFocus = InterlockedExchange(&g_countGetFocus, 0);
    LONG spoofFocus = InterlockedExchange(&g_countSpoofFocus, 0);
    LONG spoofAsync = InterlockedExchange(&g_countSpoofAsync, 0);
    LONG spoofKeyboard = InterlockedExchange(&g_countSpoofKeyboard, 0);
    LONG spoofDeviceState = InterlockedExchange(&g_countSpoofDeviceState, 0);

    wchar_t buffer[640] = {0};
    StringCchPrintfW(
        buffer,
        ARRAYSIZE(buffer),
        L"[STAT] %s Win32: GetAsyncKeyState=%ld GetKeyboardState=%ld SpoofAsync=%ld SpoofKeyboard=%ld | Focus: Foreground=%ld Active=%ld Focus=%ld Spoof=%ld | DirectInput: DirectInput8Create=%ld CreateDevice=%ld GetDeviceState=%ld Fail=%ld NotAcquired=%ld GetDeviceData=%ld Acquire=%ld Poll=%ld Unacquire=%ld SpoofDI=%ld | RawInput: Register=%ld GetRawInputData=%ld | Profile=%lu Mode=%lu",
        GetTimestamp().c_str(),
        getAsync,
        getKeyboard,
        spoofAsync,
        spoofKeyboard,
        getForeground,
        getActive,
        getFocus,
        spoofFocus,
        diCreate,
        createDevice,
        getState,
        getStateFailed,
        getStateNotAcquired,
        getData,
        acquire,
        poll,
        unacquire,
        spoofDeviceState,
        rawRegister,
        rawData,
        g_lastProfileId,
        g_lastProfileMode);

    WriteLogLine(buffer);
}

static DWORD WINAPI WorkerThread(LPVOID)
{
    // 所有耗时与高风险操作都放在工作线程，避免 DllMain 触发 Loader Lock
    InitializeLogging();
    // 成功文件写入：避免在 DllMain 中做 I/O，降低 Loader Lock 风险
    WriteSuccessFile();

    LogInfo(L"工作线程启动，准备初始化 MinHook 与输入路径统计");

    MH_STATUS status = MH_Initialize();
    LogMinHookStatus(L"MinHook 初始化", status);
    if (status != MH_OK)
    {
        LogError(L"MinHook 初始化失败，终止 Hook 安装");
        return 0;
    }

    InstallUser32Hooks();
    InstallDirectInputHook();

    LogInfo(L"Hook 安装完成，开始统计调用频率");

    // 抹头放在 Hook 初始化之后，避免影响需要解析 PE 的逻辑
    if (ErasePeHeader(g_module))
    {
        LogInfo(L"抹头完成（PE Header 已清零）");
    }
    else
    {
        LogError(L"抹头失败或被跳过");
    }

    while (InterlockedCompareExchange(&g_shouldStop, 0, 0) == 0)
    {
        Sleep(1000);
        LogCountersOnce();
    }

    LogInfo(L"工作线程退出");
    return 0;
}

// ------------------------------
// DllMain
// ------------------------------

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        // 关键点：DllMain 内只做最小动作，避免触发 Loader Lock 风险
        g_module = module;
        DisableThreadLibraryCalls(module);

        // 断链：降低被模块枚举发现的概率
        UnlinkFromPeb(module);

        HANDLE thread = CreateThread(nullptr, 0, WorkerThread, nullptr, 0, nullptr);
        if (thread)
        {
            CloseHandle(thread);
        }
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        InterlockedExchange(&g_shouldStop, 1);
        // 进程退出时清理 success file，避免残留误判
        RemoveSuccessFile();
    }

    return TRUE;
}
