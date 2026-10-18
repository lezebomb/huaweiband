#include <windows.h>
#include <initguid.h>
#include <shellapi.h>
#include <memory>
#include "SignedGrant.h"
#include "LabPolicy.h"
#include "LabDiagnostics.h"

static LabPolicy policy;
static std::unique_ptr<SigningKey> signer;
static ULONGLONG armedAt = 0, armedWall = 0;
static bool tampered = false;
static DWORD session = 0;
static HWND statusWindow = nullptr;
static HWND diagnosticsWindow = nullptr;
static ULONGLONG diagnosticsUpdated = 0;
static std::wstring lastDiagnostics;
static void RefreshDiagnostics() {
    auto tick = GetTickCount64(); if (tick - diagnosticsUpdated < 1000) return; diagnosticsUpdated = tick;
    try {
        auto bytes = ReadBoundedFile(LabInstallDirectory() / L"Diagnostics" / L"provider.log", 262304);
        auto start = bytes.size() > 6000 ? bytes.size() - 6000 : 0;
        while (start && start < bytes.size() && bytes[start - 1] != '\n') ++start;
        std::wstring text; for (size_t i = start; i < bytes.size(); ++i) text += bytes[i] < 128 ? static_cast<wchar_t>(bytes[i]) : L'?';
        if (text.size() > 6000) text.resize(6000);
        if (text != lastDiagnostics) { lastDiagnostics = text; SetWindowTextW(diagnosticsWindow, text.c_str()); SendMessageW(diagnosticsWindow, EM_SETSEL, static_cast<WPARAM>(-1), static_cast<LPARAM>(-1)); SendMessageW(diagnosticsWindow, EM_SCROLLCARET, 0, 0); }
    } catch (...) { SetWindowTextW(diagnosticsWindow, L"No provider diagnostics yet. Lock the VM and select the BandKey sign-in option once. This text is diagnostic only."); }
}
static void Status(const wchar_t* text) { SetWindowTextW(statusWindow, text); }
static void Disarm(const wchar_t* status) { signer.reset(); armedAt = 0; Status(status); }
static void Arm(bool invalidSignature) {
    signer.reset(); armedAt = 0;
    NativeRequire(IsSupportedVmwareGuest() && !GetSystemMetrics(SM_REMOTESESSION), "supported-local-VM-required");
    auto current = ReadLabPolicy(); NativeRequire(current.enabled && current.mode == 2 && current.signerKind == 0 && current.sid == policy.sid && current.publicKey == policy.publicKey, "experiment-not-installed-or-changed");
    auto root = LabInstallDirectory(); auto exchange = root / L"Exchange"; CheckNoReparseDirectory(root); CheckNoReparseDirectory(exchange);
    auto encrypted = ReadBoundedFile(exchange / L"signer.dpapi", 16384); ProtectedBytes privateKey; DecryptSecret(encrypted, policy.sid, privateKey);
    auto key = std::make_unique<SigningKey>(); key->Import(privateKey.blob.pbData, privateKey.blob.cbData, true);
    NativeRequire(key->Export(false) == policy.publicKey, "simulation-key-mismatch");
    signer = std::move(key); armedAt = GetTickCount64(); armedWall = FileTimeMilliseconds(); tampered = invalidSignature;
    Status(invalidSignature ? L"Armed ONCE for 30 seconds: invalid signature test. Lock the VM within 10 seconds. No unlock is expected." : L"Armed ONCE for 30 seconds. Lock the VM within 10 seconds. One new signed request may be approved.");
}
static void Poll() {
    if (!signer) return;
    auto monotonic = GetTickCount64();
    if (monotonic < armedAt || monotonic - armedAt >= 30000 || FileTimeMilliseconds() < armedWall) { Disarm(L"Expired. No further request will be signed. Unlock normally before arming another test."); return; }
    auto current = ReadLabPolicy();
    if (!current.enabled || current.mode != 2 || current.sid != policy.sid || current.publicKey != policy.publicKey) { Disarm(L"Experiment revoked or changed; signing cancelled."); return; }
    if (!SessionIsLocked(session)) return;
    auto root = LabInstallDirectory(); auto exchange = root / L"Exchange"; CheckNoReparseDirectory(root); CheckNoReparseDirectory(exchange);
    WIN32_FIND_DATAW entry{}; HANDLE find = FindFirstFileW((exchange / L"*.request").c_str(), &entry);
    if (find == INVALID_HANDLE_VALUE) return;
    unsigned count = 0; bool signedOne = false;
    do {
        if (++count > 32) break;
        if (entry.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) continue;
        try {
            auto request = exchange / entry.cFileName; auto bytes = ReadBoundedFile(request, sizeof(WindowsChallenge));
            if (bytes.size() != sizeof(WindowsChallenge)) continue;
            WindowsChallenge challenge; CopyMemory(&challenge, bytes.data(), sizeof(challenge));
            if (!ChallengeContextValid(challenge, policy.sid, session, FileTimeMilliseconds()) || challenge.issuedMs < armedWall || request.filename() != ChallengeFileName(challenge)) continue;
            auto approval = SignWindowsChallenge(challenge, *signer); if (tampered) approval.signature[0] ^= 1;
            auto temporary = request.wstring() + L".temporary";
            WriteNewFile(temporary, reinterpret_cast<const BYTE*>(&approval), sizeof(approval));
            if (!MoveFileExW(temporary.c_str(), (request.wstring() + L".approval").c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) { DeleteFileW(temporary.c_str()); continue; }
            signedOne = true; break;
        } catch (...) { /* Ignore malformed/unavailable public messages; never sign a different context. */ }
    } while (FindNextFileW(find, &entry));
    FindClose(find);
    if (signedOne) Disarm(tampered ? L"One INVALID signature delivered. No unlock is expected. Test is disarmed." : L"One signed authorization delivered. This is not a Windows unlock confirmation. Test is disarmed.");
}
static LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    try {
        if (message == WM_COMMAND) {
            if (LOWORD(wParam) == 101) Arm(false);
            if (LOWORD(wParam) == 102) Arm(true);
            if (LOWORD(wParam) == 103) Disarm(L"Cancelled. No request will be signed.");
            return 0;
        }
        if (message == WM_TIMER) { Poll(); RefreshDiagnostics(); return 0; }
        if (message == WM_DESTROY) { signer.reset(); KillTimer(window, 1); PostQuitMessage(0); return 0; }
    } catch (...) { Disarm(L"Operation failed. No additional request will be signed. Check installation and account, then retry."); }
    return DefWindowProcW(window, message, wParam, lParam);
}
int WINAPI wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE, _In_ PWSTR, _In_ int) {
    try {
        NativeRequire(IsSupportedVmwareGuest() && !GetSystemMetrics(SM_REMOTESESSION), "local-VM-required");
        policy = ReadLabPolicy(); NativeRequire(policy.enabled && policy.mode == 2, "signed-experiment-not-installed");
        NativeHandle token; NativeRequire(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token.value) != FALSE, "token-unavailable");
        DWORD size = 0; GetTokenInformation(token.value, TokenUser, nullptr, 0, &size); std::vector<BYTE> data(size);
        NativeRequire(size && GetTokenInformation(token.value, TokenUser, data.data(), size, &size), "identity-unavailable");
        PSID expected = nullptr; NativeRequire(ConvertStringSidToSidW(policy.sid.c_str(), &expected) != FALSE, "configured-sid-invalid");
        bool matches = EqualSid(expected, reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid) != FALSE; LocalFree(expected); NativeRequire(matches, "configured-account-required");
        NativeRequire(ProcessIdToSessionId(GetCurrentProcessId(), &session) && session, "interactive-session-required");
        WNDCLASSW type{}; type.hInstance = instance; type.lpfnWndProc = WindowProcedure; type.lpszClassName = L"BandKey.VmConfirmation"; type.hCursor = LoadCursorW(nullptr, IDC_ARROW); type.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        NativeRequire(RegisterClassW(&type) != 0, "window-class-unavailable");
        HWND window = CreateWindowExW(0, type.lpszClassName, L"BandKey VM confirmation - lifecycle fix 2 / diagnostics", WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX, CW_USEDEFAULT, CW_USEDEFAULT, 720, 530, nullptr, nullptr, instance, nullptr);
        if (!window) throw std::runtime_error("window-unavailable");
        CreateWindowExW(0, L"STATIC", L"Isolated VM experiment: simulated device event, real Windows authentication.\nEach Arm allows only one NEW request within 30 seconds. The app does not lock the VM.\nKeep the original system password sign-in option. It never receives your Windows password.", WS_CHILD | WS_VISIBLE, 18, 18, 670, 78, window, nullptr, instance, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Arm one valid confirmation", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, 18, 106, 235, 36, window, reinterpret_cast<HMENU>(101), instance, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Arm one INVALID signature", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, 266, 106, 235, 36, window, reinterpret_cast<HMENU>(102), instance, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Cancel", WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON, 515, 106, 160, 36, window, reinterpret_cast<HMENU>(103), instance, nullptr);
        statusWindow = CreateWindowExW(0, L"STATIC", L"Disarmed. No authorization will be signed until you choose Arm.", WS_CHILD | WS_VISIBLE, 18, 164, 655, 84, window, nullptr, instance, nullptr);
        CreateWindowExW(0, L"STATIC", L"Provider diagnostics (Ctrl+A / Ctrl+C to copy; no password, key or nonce):", WS_CHILD | WS_VISIBLE, 18, 253, 665, 24, window, nullptr, instance, nullptr);
        diagnosticsWindow = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"No provider diagnostics yet.", WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL, 18, 280, 665, 190, window, nullptr, instance, nullptr);
        NativeRequire(statusWindow && diagnosticsWindow && SetTimer(window, 1, 200, nullptr), "timer-unavailable"); ShowWindow(window, SW_SHOW);
        MSG message{}; while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); } return 0;
    } catch (...) { MessageBoxW(nullptr, L"This client requires the signed-confirmation experiment installed for the current local account inside the supported VMware guest. No authorization was issued.", L"BandKey VM confirmation", MB_OK | MB_ICONERROR); return 1; }
}
