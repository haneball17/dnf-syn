#define CINTERFACE
#define COBJMACROS

#include <windows.h>
#include <dinput.h>
#include <objbase.h>
#include <strsafe.h>
#include <intrin.h>
#include <string>

#include "MinHook.h"

// ------------------------------
// 全局状态与计数器
// ------------------------------

static HMODULE g_module = nullptr;
static HANDLE g_logFile = INVALID_HANDLE_VALUE;
static CRITICAL_SECTION g_logLock;
static LONG g_logReady = 0;
static LONG g_shouldStop = 0;

static volatile LONG g_countGetAsyncKeyState = 0;
static volatile LONG g_countGetKeyboardState = 0;
static volatile LONG g_countDirectInput8Create = 0;
static volatile LONG g_countCreateDevice = 0;
static volatile LONG g_countGetDeviceState = 0;
static volatile LONG g_countGetDeviceData = 0;
static volatile LONG g_countAcquire = 0;
static volatile LONG g_countPoll = 0;
static volatile LONG g_countUnacquire = 0;
static volatile LONG g_countRegisterRawInput = 0;
static volatile LONG g_countGetRawInputData = 0;

static LONG g_createDeviceHooked = 0;
static LONG g_deviceHooksHooked = 0;

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
    wchar_t basePath[MAX_PATH] = {0};
    DWORD len = GetEnvironmentVariableW(L"APPDATA", basePath, ARRAYSIZE(basePath));
    if (len == 0 || len >= ARRAYSIZE(basePath))
    {
        GetTempPathW(ARRAYSIZE(basePath), basePath);
    }

    std::wstring base(basePath);
    if (!base.empty() && (base.back() == L'\\' || base.back() == L'/'))
    {
        base.pop_back();
    }

    std::wstring dnfDir = base + L"\\DNFSyncBox";
    std::wstring logDir = dnfDir + L"\\logs";
    CreateDirectoryW(dnfDir.c_str(), nullptr);
    CreateDirectoryW(logDir.c_str(), nullptr);

    wchar_t fileName[MAX_PATH] = {0};
    StringCchPrintfW(fileName, ARRAYSIZE(fileName), L"%s\\dnfinput_%lu.log", logDir.c_str(), GetCurrentProcessId());
    return std::wstring(fileName);
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
// Hook 回调
// ------------------------------

static SHORT WINAPI Hook_GetAsyncKeyState(int vKey)
{
    InterlockedIncrement(&g_countGetAsyncKeyState);
    return g_origGetAsyncKeyState ? g_origGetAsyncKeyState(vKey) : 0;
}

static BOOL WINAPI Hook_GetKeyboardState(PBYTE lpKeyState)
{
    InterlockedIncrement(&g_countGetKeyboardState);
    return g_origGetKeyboardState ? g_origGetKeyboardState(lpKeyState) : FALSE;
}

static BOOL WINAPI Hook_RegisterRawInputDevices(PCRAWINPUTDEVICE devices, UINT numDevices, UINT size)
{
    InterlockedIncrement(&g_countRegisterRawInput);
    return g_origRegisterRawInputDevices ? g_origRegisterRawInputDevices(devices, numDevices, size) : FALSE;
}

static UINT WINAPI Hook_GetRawInputData(HRAWINPUT hRawInput, UINT command, LPVOID data, PUINT size, UINT headerSize)
{
    InterlockedIncrement(&g_countGetRawInputData);
    return g_origGetRawInputData ? g_origGetRawInputData(hRawInput, command, data, size, headerSize) : 0;
}

static HRESULT STDMETHODCALLTYPE Hook_GetDeviceState(IDirectInputDevice8W* device, DWORD size, LPVOID data)
{
    InterlockedIncrement(&g_countGetDeviceState);
    return g_origGetDeviceState ? g_origGetDeviceState(device, size, data) : DIERR_GENERIC;
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
    LONG getData = InterlockedExchange(&g_countGetDeviceData, 0);
    LONG acquire = InterlockedExchange(&g_countAcquire, 0);
    LONG poll = InterlockedExchange(&g_countPoll, 0);
    LONG unacquire = InterlockedExchange(&g_countUnacquire, 0);
    LONG rawRegister = InterlockedExchange(&g_countRegisterRawInput, 0);
    LONG rawData = InterlockedExchange(&g_countGetRawInputData, 0);

    wchar_t buffer[512] = {0};
    StringCchPrintfW(
        buffer,
        ARRAYSIZE(buffer),
        L"[STAT] %s Win32: GetAsyncKeyState=%ld GetKeyboardState=%ld | DirectInput: DirectInput8Create=%ld CreateDevice=%ld GetDeviceState=%ld GetDeviceData=%ld Acquire=%ld Poll=%ld Unacquire=%ld | RawInput: Register=%ld GetRawInputData=%ld",
        GetTimestamp().c_str(),
        getAsync,
        getKeyboard,
        diCreate,
        createDevice,
        getState,
        getData,
        acquire,
        poll,
        unacquire,
        rawRegister,
        rawData);

    WriteLogLine(buffer);
}

static DWORD WINAPI WorkerThread(LPVOID)
{
    // 所有耗时与高风险操作都放在工作线程，避免 DllMain 触发 Loader Lock
    InitializeLogging();

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
    }

    return TRUE;
}
