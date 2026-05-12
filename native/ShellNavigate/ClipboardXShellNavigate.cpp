// 注入到「打开/保存」宿主进程内执行：WM_USER+7 → IShellBrowser::BrowseObject

#include <windows.h>
#include <initguid.h>
#include <shlobj.h>
#include <shlguid.h>
#include <unknwn.h>

#include <stdio.h>
#include <wchar.h>
#include <stdarg.h>
#include <string.h>

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "user32.lib")

static HMODULE g_hDllModule = nullptr;

struct NavigatePayload
{
    HWND hwnd;
    WCHAR path[520];
};

static void NavAppendUtf8File(PCWSTR wideLine)
{
    WCHAR dir[MAX_PATH];
    if (GetEnvironmentVariableW(L"LOCALAPPDATA", dir, MAX_PATH) == 0)
        return;

    WCHAR folder[MAX_PATH];
    swprintf_s(folder, L"%s\\ClipboardX", dir);
    CreateDirectoryW(folder, nullptr);

    WCHAR path[MAX_PATH];
    swprintf_s(path, L"%s\\shell_navigate.log", folder);

    int u8cap = WideCharToMultiByte(CP_UTF8, 0, wideLine, -1, nullptr, 0, nullptr, nullptr);
    if (u8cap <= 0)
        return;

    char* u8 = static_cast<char*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, (SIZE_T)u8cap + 4));
    if (!u8)
        return;

    WideCharToMultiByte(CP_UTF8, 0, wideLine, -1, u8, u8cap, nullptr, nullptr);

    HANDLE h = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ, nullptr, OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h != INVALID_HANDLE_VALUE)
    {
        LARGE_INTEGER sz{};
        if (GetFileSizeEx(h, &sz) && sz.QuadPart == 0)
        {
            const BYTE bom[] = { 0xEF, 0xBB, 0xBF };
            DWORD w = 0;
            WriteFile(h, bom, sizeof(bom), &w, nullptr);
        }
        DWORD w = 0;
        size_t len = strlen(u8);
        WriteFile(h, u8, (DWORD)len, &w, nullptr);
        const char crlf[] = "\r\n";
        WriteFile(h, crlf, 2, &w, nullptr);
        CloseHandle(h);
    }

    HeapFree(GetProcessHeap(), 0, u8);
}

static void NavLogFmtW(const wchar_t* fmt, ...)
{
    WCHAR wbuf[1536];
    va_list ap;
    va_start(ap, fmt);
    _vsnwprintf_s(wbuf, _TRUNCATE, fmt, ap);
    va_end(ap);

    SYSTEMTIME st;
    GetLocalTime(&st);

    WCHAR line[1792];
    swprintf_s(line, L"[%04u-%02u-%02u %02u:%02u:%02u.%03u] [native pid=%lu] %s",
        (unsigned)st.wYear, (unsigned)st.wMonth, (unsigned)st.wDay,
        (unsigned)st.wHour, (unsigned)st.wMinute, (unsigned)st.wSecond, (unsigned)st.wMilliseconds,
        (unsigned long)GetCurrentProcessId(), wbuf);

    NavAppendUtf8File(line);
}

static HRESULT NavigateViaShellBrowser(HWND hDlg, PCWSTR pathW)
{
    constexpr UINT CWM_GETISHELLBROWSER = WM_USER + 7;
    DWORD tidWnd = GetWindowThreadProcessId(hDlg, nullptr);
    DWORD tidHere = GetCurrentThreadId();
    NavLogFmtW(L"SendMessage WM_USER+7 hwnd=0x%p tidDlg=%lu tidRemote=%lu", (void*)hDlg,
        (unsigned long)tidWnd, (unsigned long)tidHere);

    IUnknown* pUnk = reinterpret_cast<IUnknown*>(SendMessageW(hDlg, CWM_GETISHELLBROWSER, 0, 0));
    if (!pUnk)
    {
        NavLogFmtW(L"No IUnknown from WM_USER+7");
        return E_FAIL;
    }

    IShellBrowser* pSB = nullptr;
    HRESULT hr = pUnk->QueryInterface(IID_IShellBrowser, reinterpret_cast<void**>(&pSB));
    if (FAILED(hr) || !pSB)
    {
        NavLogFmtW(L"QueryInterface IShellBrowser hr=0x%08X", (unsigned)hr);
        return hr;
    }

    PIDLIST_ABSOLUTE pidl = nullptr;
    SFGAOF attrs = 0;
    hr = SHParseDisplayName(pathW, nullptr, &pidl, 0, &attrs);
    if (FAILED(hr) || !pidl)
    {
        NavLogFmtW(L"SHParseDisplayName hr=0x%08X, trying ILCreateFromPathW", (unsigned)hr);
        pidl = ILCreateFromPathW(pathW);
    }
    if (!pidl)
    {
        NavLogFmtW(L"no PIDL for path");
        pSB->Release();
        return E_FAIL;
    }

    constexpr HRESULT hrBusy = static_cast<HRESULT>(0x800700AA);
    HRESULT hrBr = E_FAIL;
    for (int attempt = 0; attempt < 4; ++attempt)
    {
        if (attempt > 0)
        {
            NavLogFmtW(L"BrowseObject retry %d after hrBusy", attempt);
            Sleep(100);
        }
        hrBr = pSB->BrowseObject(pidl, SBSP_ABSOLUTE);
        NavLogFmtW(L"BrowseObject hr=0x%08X", (unsigned)hrBr);
        if (hrBr != hrBusy)
            break;
    }

    ILFree(pidl);
    pSB->Release();
    return hrBr;
}

// IShellBrowser from WM_USER+7 必须在对话框 UI 线程上使用；远程线程直调会出 S_OK 不刷新或 0x800700AA。
struct NavigateHookCtx
{
    HWND hwnd;
    WCHAR path[520];
    HANDLE doneEvent {};
    HRESULT hrResult { E_FAIL };
};

// ---- Read current folder ----
static HRESULT ReadCurrentFolderViaShellBrowser(HWND hDlg, WCHAR* outPath, int outPathChars)
{
    outPath[0] = L'\0';
    constexpr UINT CWM_GETISHELLBROWSER = WM_USER + 7;

    IUnknown* pUnk = reinterpret_cast<IUnknown*>(SendMessageW(hDlg, CWM_GETISHELLBROWSER, 0, 0));
    if (!pUnk) return E_FAIL;

    IShellBrowser* pSB = nullptr;
    HRESULT hr = pUnk->QueryInterface(IID_IShellBrowser, reinterpret_cast<void**>(&pSB));
    if (FAILED(hr) || !pSB) { pUnk->Release(); return hr; }

    IShellView* pSV = nullptr;
    hr = pSB->QueryActiveShellView(&pSV);
    if (FAILED(hr) || !pSV) { pSB->Release(); return hr; }

    IFolderView* pFV = nullptr;
    hr = pSV->QueryInterface(IID_IFolderView, reinterpret_cast<void**>(&pFV));
    if (FAILED(hr) || !pFV) { pSV->Release(); pSB->Release(); return hr; }

    IPersistFolder2* pPF = nullptr;
    hr = pFV->GetFolder(IID_IPersistFolder2, reinterpret_cast<void**>(&pPF));
    if (FAILED(hr) || !pPF) { pFV->Release(); pSV->Release(); pSB->Release(); return hr; }

    PIDLIST_ABSOLUTE pidl = nullptr;
    hr = pPF->GetCurFolder(&pidl);
    pPF->Release();
    pFV->Release();
    pSV->Release();
    pSB->Release();

    if (FAILED(hr) || !pidl) return hr;

    BOOL ok = SHGetPathFromIDListW(pidl, outPath);
    ILFree(pidl);
    return ok ? S_OK : E_FAIL;
}

struct ReadHookCtx
{
    HWND hwnd;
    WCHAR path[520];
    HANDLE doneEvent {};
    HRESULT hrResult { E_FAIL };
};

static volatile LONG g_navHookArmed = 0;
static HHOOK g_getMsgHook {};
static NavigateHookCtx g_navHookCtx {};

static volatile LONG g_readHookArmed = 0;
static HHOOK g_readMsgHook {};
static ReadHookCtx g_readHookCtx {};

static LRESULT CALLBACK ClipboardX_GetMsgProc(_In_ int code, _In_ WPARAM wp, _In_ LPARAM lp)
{
    if (code < 0)
        return CallNextHookEx(g_getMsgHook, code, wp, lp);

    if (InterlockedExchange(&g_navHookArmed, 0) != 1)
        return CallNextHookEx(g_getMsgHook, code, wp, lp);

    HHOOK hookCopy = g_getMsgHook;
    g_getMsgHook = nullptr;
    if (hookCopy)
        UnhookWindowsHookEx(hookCopy);

    NavLogFmtW(L"WH_GETMESSAGE navigate on dlg UI thread tid=%lu", (unsigned long)GetCurrentThreadId());
    g_navHookCtx.hrResult = NavigateViaShellBrowser(g_navHookCtx.hwnd, g_navHookCtx.path);
    if (g_navHookCtx.doneEvent)
        SetEvent(g_navHookCtx.doneEvent);

    return CallNextHookEx(nullptr, code, wp, lp);
}

static HRESULT NavigateOnDialogUiThread(HWND hDlg, PCWSTR pathW)
{
    DWORD tidDlg = GetWindowThreadProcessId(hDlg, nullptr);
    DWORD tidHere = GetCurrentThreadId();
    if (!tidDlg)
        return E_FAIL;
    if (tidDlg == tidHere)
        return NavigateViaShellBrowser(hDlg, pathW);

    HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!ev)
        return E_FAIL;

    ZeroMemory(&g_navHookCtx, sizeof(g_navHookCtx));
    g_navHookCtx.hwnd = hDlg;
    wcsncpy_s(g_navHookCtx.path, pathW, _TRUNCATE);
    g_navHookCtx.doneEvent = ev;
    g_navHookCtx.hrResult = E_FAIL;

    InterlockedExchange(&g_navHookArmed, 1);
    g_getMsgHook = SetWindowsHookExW(WH_GETMESSAGE, ClipboardX_GetMsgProc, g_hDllModule, tidDlg);
    if (!g_getMsgHook)
    {
        NavLogFmtW(L"SetWindowsHookEx WH_GETMESSAGE failed err=%lu", (unsigned long)GetLastError());
        CloseHandle(ev);
        return E_FAIL;
    }

    PostMessageW(hDlg, WM_NULL, 0, 0);

    const DWORD w = WaitForSingleObject(ev, 20000);
    if (w != WAIT_OBJECT_0)
    {
        NavLogFmtW(L"nav hook wait %lu (timeout=%d)", (unsigned long)w, w == WAIT_TIMEOUT ? 1 : 0);
        HHOOK hLeft = (HHOOK)InterlockedExchangePointer((PVOID volatile*)&g_getMsgHook, nullptr);
        if (hLeft)
            UnhookWindowsHookEx(hLeft);
        InterlockedExchange(&g_navHookArmed, 0);
    }

    const HRESULT hr = g_navHookCtx.hrResult;
    CloseHandle(ev);
    g_navHookCtx.doneEvent = nullptr;
    return hr;
}

// ---- Read current folder via hook ----
static LRESULT CALLBACK ClipboardX_GetMsgProc_Read(_In_ int code, _In_ WPARAM wp, _In_ LPARAM lp)
{
    if (code < 0)
        return CallNextHookEx(g_readMsgHook, code, wp, lp);

    if (InterlockedExchange(&g_readHookArmed, 0) != 1)
        return CallNextHookEx(g_readMsgHook, code, wp, lp);

    HHOOK hookCopy = g_readMsgHook;
    g_readMsgHook = nullptr;
    if (hookCopy)
        UnhookWindowsHookEx(hookCopy);

    NavLogFmtW(L"WH_GETMESSAGE read on dlg UI thread tid=%lu", (unsigned long)GetCurrentThreadId());
    g_readHookCtx.hrResult = ReadCurrentFolderViaShellBrowser(
        g_readHookCtx.hwnd, g_readHookCtx.path, 520);
    if (g_readHookCtx.doneEvent)
        SetEvent(g_readHookCtx.doneEvent);

    return CallNextHookEx(nullptr, code, wp, lp);
}

static HRESULT ReadOnDialogUiThread(HWND hDlg, WCHAR* outPath, int outPathChars)
{
    outPath[0] = L'\0';
    DWORD tidDlg = GetWindowThreadProcessId(hDlg, nullptr);
    DWORD tidHere = GetCurrentThreadId();
    if (!tidDlg)
        return E_FAIL;
    if (tidHere == tidDlg)
        return ReadCurrentFolderViaShellBrowser(hDlg, outPath, outPathChars);

    HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!ev)
        return E_FAIL;

    ZeroMemory(&g_readHookCtx, sizeof(g_readHookCtx));
    g_readHookCtx.hwnd = hDlg;
    g_readHookCtx.doneEvent = ev;
    g_readHookCtx.hrResult = E_FAIL;

    InterlockedExchange(&g_readHookArmed, 1);
    g_readMsgHook = SetWindowsHookExW(WH_GETMESSAGE, ClipboardX_GetMsgProc_Read, g_hDllModule, tidDlg);
    if (!g_readMsgHook)
    {
        NavLogFmtW(L"SetWindowsHookEx WH_GETMESSAGE read failed err=%lu", (unsigned long)GetLastError());
        CloseHandle(ev);
        return E_FAIL;
    }

    PostMessageW(hDlg, WM_NULL, 0, 0);

    const DWORD w = WaitForSingleObject(ev, 5000);
    if (w != WAIT_OBJECT_0)
    {
        NavLogFmtW(L"read hook wait %lu (timeout=%d)", (unsigned long)w, w == WAIT_TIMEOUT ? 1 : 0);
        HHOOK hLeft = (HHOOK)InterlockedExchangePointer((PVOID volatile*)&g_readMsgHook, nullptr);
        if (hLeft)
            UnhookWindowsHookEx(hLeft);
        InterlockedExchange(&g_readHookArmed, 0);
        CloseHandle(ev);
        return E_FAIL;
    }

    const HRESULT hr = g_readHookCtx.hrResult;
    if (SUCCEEDED(hr))
        wcsncpy_s(outPath, outPathChars, g_readHookCtx.path, _TRUNCATE);
    CloseHandle(ev);
    g_readHookCtx.doneEvent = nullptr;
    return hr;
}

struct ReadPayload
{
    HWND hwnd;
    WCHAR path[520];
};

extern "C" __declspec(dllexport) DWORD WINAPI ClipboardX_RemoteReadCurrentFolder(_In_ LPVOID param)
{
    auto* p = static_cast<ReadPayload*>(param);
    if (!p || !IsWindow(p->hwnd))
    {
        NavLogFmtW(L"Bad read payload hwnd=0x%p", (void*)(p ? p->hwnd : nullptr));
        return 9;
    }

    NavLogFmtW(L"ReadEnter hwnd=0x%p", (void*)p->hwnd);

    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    HRESULT hr = ReadOnDialogUiThread(p->hwnd, p->path, 520);

    if (hrCo == S_OK)
        CoUninitialize();

    NavLogFmtW(L"ReadDone hr=0x%08X path=%s", (unsigned)hr, p->path);
    return SUCCEEDED(hr) ? 0 : static_cast<DWORD>(hr & 0xFFFFFFFFUL);
}

extern "C" __declspec(dllexport) DWORD WINAPI ClipboardX_RemoteNavigate(_In_ LPVOID param)
{
    auto* p = static_cast<NavigatePayload*>(param);
    if (!p || !IsWindow(p->hwnd) || !p->path[0])
    {
        NavLogFmtW(L"Bad payload hwnd=0x%p", (void*)(p ? p->hwnd : nullptr));
        return 9;
    }

    NavLogFmtW(L"Enter hwnd=0x%p path=%s", (void*)p->hwnd, p->path);

    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    NavLogFmtW(L"CoInitializeEx hr=0x%08X", (unsigned)hrCo);

    HRESULT hr = NavigateOnDialogUiThread(p->hwnd, p->path);

    if (hrCo == S_OK)
        CoUninitialize();

    if (SUCCEEDED(hr))
    {
        NavLogFmtW(L"OK");
        return 0;
    }
    NavLogFmtW(L"Fail exit hr=0x%08X", (unsigned)hr);
    return static_cast<DWORD>(hr & 0xFFFFFFFFUL);
}

BOOL APIENTRY DllMain(HMODULE hMod, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
        g_hDllModule = hMod;
    return TRUE;
}
