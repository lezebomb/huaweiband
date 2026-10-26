#include <windows.h>
#include <wincred.h>
#include <lm.h>
#include <wtsapi32.h>
#include <ntsecapi.h>
#include <shellapi.h>
#include <array>
#include <vector>
#include <string>
#include <filesystem>
#include <fstream>
#include "HostCredentialProbePolicy.h"
#include "OnlineCredentialPacking.h"

namespace {
struct Handle { HANDLE value = nullptr; ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); } };
template<size_t N> struct Secret {
    std::array<wchar_t, N> value{};
    ~Secret() { SecureZeroMemory(value.data(), sizeof(value)); }
    Secret() = default; Secret(const Secret&) = delete; Secret& operator=(const Secret&) = delete;
};
struct NetBuffer { LPBYTE value = nullptr; ~NetBuffer() { if (value) NetApiBufferFree(value); } };
struct Failure { DWORD code; };
void Require(bool ok, DWORD error = ERROR_INVALID_DATA) { if (!ok) throw Failure{error}; }
std::vector<BYTE> TokenSid(HANDLE token) {
    DWORD size = 0; GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    Require(size >= sizeof(TOKEN_USER) && size <= 65536);
    std::vector<BYTE> data(size); Require(GetTokenInformation(token, TokenUser, data.data(), size, &size) != FALSE, GetLastError());
    auto sid = reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid; Require(IsValidSid(sid) != FALSE);
    std::vector<BYTE> copy(GetLengthSid(sid)); Require(CopySid(static_cast<DWORD>(copy.size()), copy.data(), sid) != FALSE);
    return copy;
}
struct Account { std::wstring qualified; std::vector<BYTE> sid; };
Account CurrentAccount() {
    DWORD session = 0;
    Require(ProcessIdToSessionId(GetCurrentProcessId(), &session) && session != 0 && !GetSystemMetrics(SM_REMOTESESSION), ERROR_NOT_SUPPORTED);
    LPWSTR state = nullptr; DWORD stateBytes = 0;
    Require(WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, session, WTSConnectState, &state, &stateBytes) != FALSE, GetLastError());
    const bool active = state && stateBytes >= sizeof(WTS_CONNECTSTATE_CLASS) && *reinterpret_cast<WTS_CONNECTSTATE_CLASS*>(state) == WTSActive;
    WTSFreeMemory(state); Require(active, ERROR_NOT_SUPPORTED);
    LPWSTR desktopUser = nullptr; DWORD userBytes = 0;
    Require(WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, session, WTSUserName, &desktopUser, &userBytes) != FALSE, GetLastError());
    std::wstring user(desktopUser ? desktopUser : L""); WTSFreeMemory(desktopUser);
    Require(!user.empty() && user.size() <= 256, ERROR_INVALID_ACCOUNT_NAME);
    NetBuffer internetData, localData;
    const auto status24 = NetUserGetInfo(nullptr, user.c_str(), 24, &internetData.value);
    Require(status24 == NERR_Success && internetData.value, status24);
    const auto status23 = NetUserGetInfo(nullptr, user.c_str(), 23, &localData.value);
    Require(status23 == NERR_Success && localData.value, status23);
    auto internet = reinterpret_cast<USER_INFO_24*>(internetData.value);
    auto local = reinterpret_cast<USER_INFO_23*>(localData.value);
    Require(internet->usri24_internet_identity && internet->usri24_flags == 0 && internet->usri24_internet_provider_name &&
        internet->usri24_internet_principal_name && internet->usri24_user_sid && local->usri23_user_sid &&
        IsValidSid(internet->usri24_user_sid) && IsValidSid(local->usri23_user_sid) &&
        _wcsicmp(internet->usri24_internet_provider_name, L"MicrosoftAccount") == 0 &&
        EqualSid(internet->usri24_user_sid, local->usri23_user_sid), ERROR_NOT_SUPPORTED);
    if (!internet->usri24_user_sid || !internet->usri24_internet_principal_name) throw Failure{ERROR_INVALID_DATA};
    Handle process; Require(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &process.value) != FALSE, GetLastError());
    auto sid = TokenSid(process.value); Require(EqualSid(sid.data(), internet->usri24_user_sid) != FALSE, ERROR_ACCESS_DENIED);
    std::wstring name = L"MicrosoftAccount\\" + std::wstring(internet->usri24_internet_principal_name);
    Require(ValidQualifiedAccount(name, VmAccountKind::MicrosoftAccount));
    return {name, std::move(sid)};
}
ULONG NegotiatePackage() {
    HANDLE lsa = nullptr; NTSTATUS status = LsaConnectUntrusted(&lsa);
    Require(status >= 0, LsaNtStatusToWinError(status));
    char name[] = "Negotiate"; LSA_STRING text{static_cast<USHORT>(sizeof(name) - 1), sizeof(name), name}; ULONG package = 0;
    status = LsaLookupAuthenticationPackage(lsa, &text, &package); LsaDeregisterLogonProcess(lsa);
    Require(status >= 0, LsaNtStatusToWinError(status)); return package;
}
HRESULT CheckProtectedPacking(const std::wstring& account, wchar_t* password) {
    // Same serializer as the candidate VM backend; never pass its output to LogonUI.
    constexpr GUID formatTag = {0x76b424b6,0xa72d,0x4e98,{0x9d,0xc1,0x48,0x5e,0x37,0xa5,0x42,0x9c}};
    const ULONG package = NegotiatePackage();
    for (const auto scenario : {CPUS_LOGON, CPUS_UNLOCK_WORKSTATION}) {
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION packed{};
        HRESULT result = PackMicrosoftAccountCredential(scenario, account, password, package, formatTag, &packed);
        const bool shape = SUCCEEDED(result) && packed.rgbSerialization && packed.cbSerialization && packed.cbSerialization <= 65536 &&
            packed.ulAuthenticationPackage == package && packed.clsidCredentialProvider == formatTag;
        if (packed.rgbSerialization) { SecureZeroMemory(packed.rgbSerialization, packed.cbSerialization); CoTaskMemFree(packed.rgbSerialization); }
        if (!shape) return FAILED(result) ? result : E_FAIL;
    }
    return S_OK;
}
HostProbeAuthentication Authenticate(const Account& expected, wchar_t* password) {
    bool attempted = false, accepted = false;
    try {
        const auto current = CurrentAccount();
        if (_wcsicmp(current.qualified.c_str(), expected.qualified.c_str()) != 0 ||
            !EqualSid(const_cast<BYTE*>(current.sid.data()), const_cast<BYTE*>(expected.sid.data())))
            return {false, false, false, ERROR_ACCESS_DENIED};
        const auto slash = expected.qualified.find(L'\\'); const auto user = expected.qualified.substr(slash + 1); Handle loggedOn;
        attempted = true; accepted = LogonUserW(user.c_str(), L"MicrosoftAccount", password,
            LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, &loggedOn.value) != FALSE;
        if (!accepted) return {true, false, false, GetLastError()};
        const auto actual = TokenSid(loggedOn.value);
        return {true, true, EqualSid(const_cast<BYTE*>(expected.sid.data()), const_cast<BYTE*>(actual.data())) != FALSE, ERROR_SUCCESS};
    } catch (const Failure& failure) { return {attempted, accepted, false, failure.code}; }
    catch (...) { return {attempted, accepted, false, ERROR_NOT_ENOUGH_MEMORY}; }
}
struct App {
    std::filesystem::path output; Account account; HostProbeResult result;
    bool resolved = false, prompt = false, done = false;
    const char* stage = "awaiting-user"; HWND status = nullptr, button = nullptr; DWORD error = 0;
    void Save() const {
        std::ofstream file(output, std::ios::trunc); if (!file) throw Failure{ERROR_WRITE_FAULT};
        file << "{\n  \"schema\":\"bandkey.host-account-probe.v1\",\n  \"stage\":\"" << stage << "\",\n"
            << "  \"accountKind\":\"" << (resolved ? "MicrosoftAccount" : "Unknown") << "\",\n"
            << "  \"identitySidMatched\":" << (resolved ? "true" : "false") << ",\n"
            << "  \"credentialPromptRequested\":" << (prompt ? "true" : "false") << ",\n"
            << "  \"authenticationAttempted\":" << (result.attempted ? "true" : "false") << ",\n"
            << "  \"windowsAcceptedPassword\":" << (result.accepted ? "true" : "false") << ",\n"
            << "  \"authenticatedSidMatched\":" << (result.sameSid ? "true" : "false") << ",\n"
            << "  \"protectedSerializationPassed\":" << (result.packed ? "true" : "false") << ",\n"
            << "  \"error\":" << error << ",\n  \"credentialPersisted\":false,\n  \"providerRegistrationPerformed\":false,\n"
            << "  \"desktopUnlockTested\":false,\n  \"installReady\":false\n}\n";
        file.flush(); Require(file.good(), ERROR_WRITE_FAULT);
    }
    void Resolve() {
        try { account = CurrentAccount(); resolved = true; }
        catch (const Failure& failure) { error = failure.code; stage = "unsupported-account-or-session"; }
        Save();
    }
    void Probe(HWND parent) {
        if (!resolved || done) return;
        done = true; prompt = true; EnableWindow(button, FALSE); Save();
        {
            Secret<CREDUI_MAX_USERNAME_LENGTH + 1> entered;
            Secret<CREDUI_MAX_PASSWORD_LENGTH + 1> password;
            Require(account.qualified.size() < entered.value.size());
            std::copy(account.qualified.begin(), account.qualified.end(), entered.value.begin());
            BOOL save = FALSE; CREDUI_INFOW info{}; info.cbSize = sizeof(info); info.hwndParent = parent;
            info.pszCaptionText = L"BandKey 本机账户兼容性验证";
            info.pszMessageText = L"请仅输入所示本机微软账户的实际密码，不是 Windows Hello PIN。程序只验证一次，不保存密码，不安装登录组件。可取消。";
            const DWORD code = CredUIPromptForCredentialsW(&info, L"BandKey local account compatibility", nullptr, 0,
                entered.value.data(), static_cast<ULONG>(entered.value.size()), password.value.data(), static_cast<ULONG>(password.value.size()), &save,
                CREDUI_FLAGS_GENERIC_CREDENTIALS | CREDUI_FLAGS_ALWAYS_SHOW_UI | CREDUI_FLAGS_DO_NOT_PERSIST | CREDUI_FLAGS_KEEP_USERNAME);
            if (code != ERROR_SUCCESS) { error = code; stage = code == ERROR_CANCELLED ? "cancelled" : "credential-prompt-failed"; }
            else {
                result = VerifyHostPassword(account.qualified, entered.value.data(), password.value.data(),
                    [&](wchar_t* value) { return Authenticate(account, value); },
                    [&](wchar_t* value) { try { return CheckProtectedPacking(account.qualified, value); } catch (const Failure& failure) { return HRESULT_FROM_WIN32(failure.code ? failure.code : ERROR_INVALID_DATA); } catch (...) { return E_OUTOFMEMORY; } });
                error = result.error; stage = result.packed ? "password-compatible" : "verification-failed";
            }
        } // Zero credential buffers before reporting or displaying the result.
        Save(); std::wstring message;
        if (result.packed) message = L"通过：Windows 接受当前账户密码，返回 SID 一致，登录／解锁保护格式均可生成。\r\n程序没有保存密码或安装组件。这仍不是桌面解锁验收；下一步验证正式组件的隔离安装与恢复。";
        else if (error == ERROR_CANCELLED) message = L"已取消，没有提交账户认证，也没有保存密码。可关闭此窗口。";
        else message = L"本次未通过，错误码 " + std::to_wstring(error) + L"。没有保存密码或安装组件，不会自动重试。\r\n若输入的是 PIN，此后端需要微软账户的实际密码。";
        SetWindowTextW(status, message.c_str());
    }
};
LRESULT CALLBACK Window(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    auto app = reinterpret_cast<App*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_CREATE) {
        app = static_cast<App*>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams); SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(app));
        auto intro = CreateWindowW(L"STATIC", L"BandKey 实机准备：验证当前微软账户\r\n只进行一次 Windows 密码核验和保护格式检查。程序不保存密码，不注册登录组件，不锁屏。请在接下来的 Windows 凭据窗口自行输入密码，不要发到聊天里。", WS_CHILD | WS_VISIBLE, 22, 18, 620, 94, window, nullptr, nullptr, nullptr);
        app->button = CreateWindowW(L"BUTTON", L"验证一次当前账户密码", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON, 22, 123, 290, 38, window, reinterpret_cast<HMENU>(1), nullptr, nullptr);
        app->status = CreateWindowW(L"STATIC", app->resolved ? L"当前账户身份与 SID 已核对。点击上方按钮后才开始，随时可在凭据窗口取消。" : L"账户或会话不支持，未发起验证，未修改登录配置。", WS_CHILD | WS_VISIBLE, 22, 181, 620, 105, window, nullptr, nullptr, nullptr);
        for (auto control : {intro, app->button, app->status}) SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT)), TRUE);
        EnableWindow(app->button, app->resolved); return 0;
    }
    if (message == WM_COMMAND && app && LOWORD(wparam) == 1 && HIWORD(wparam) == BN_CLICKED) {
        try { app->Probe(window); }
        catch (const Failure& failure) { app->error = failure.code; app->stage = "probe-error"; try { app->Save(); } catch (...) {} SetWindowTextW(app->status, L"验证未完成；没有安装登录组件。关闭窗口后查看错误码记录。"); }
        catch (...) { app->error = ERROR_INVALID_DATA; app->stage = "probe-error"; try { app->Save(); } catch (...) {} SetWindowTextW(app->status, L"验证未完成；没有安装登录组件。"); }
        return 0;
    }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wparam, lparam);
}
int SelfTest(const std::filesystem::path& output) {
    int count = 0;
    auto check = [&](bool ok) { if (!ok) throw Failure{ERROR_ASSERTION_FAILURE}; ++count; };
    const std::wstring account = L"MicrosoftAccount\\bandkey-synthetic@example.invalid";
    wchar_t value[] = L"synthetic-memory-only"; int calls = 0, packs = 0;
    auto auth = [&](wchar_t*) { ++calls; return HostProbeAuthentication{true,true,true,0}; };
    auto pack = [&](wchar_t*) { ++packs; return S_OK; };
    auto r = VerifyHostPassword(account,L"MicrosoftAccount\\other@example.invalid",value,auth,pack); check(!r.attempted && calls == 0 && packs == 0);
    r = VerifyHostPassword(L"AzureAD\\synthetic",L"AzureAD\\synthetic",value,auth,pack); check(!r.attempted && calls == 0);
    r = VerifyHostPassword(account,account,nullptr,auth,pack); check(!r.attempted && calls == 0);
    wchar_t empty[] = L""; r = VerifyHostPassword(account,account,empty,auth,pack); check(!r.attempted && calls == 0);
    std::array<wchar_t,CREDUI_MAX_PASSWORD_LENGTH + 2> longValue; longValue.fill(L'x'); longValue.back() = 0;
    r = VerifyHostPassword(account,account,longValue.data(),auth,pack); check(!r.attempted && calls == 0);
    r = VerifyHostPassword(account,account,value,[](wchar_t*) { return HostProbeAuthentication{true,false,false,ERROR_LOGON_FAILURE}; },pack); check(r.attempted && !r.accepted && !r.packed && packs == 0);
    r = VerifyHostPassword(account,account,value,[](wchar_t*) { return HostProbeAuthentication{true,true,false,0}; },pack); check(r.accepted && !r.sameSid && !r.packed && r.error == ERROR_ACCESS_DENIED && packs == 0);
    r = VerifyHostPassword(account,account,value,auth,[](wchar_t*) { return E_FAIL; }); check(r.sameSid && !r.packed && r.error != 0);
    r = VerifyHostPassword(account,account,value,[](wchar_t*) { return HostProbeAuthentication{false,false,false,ERROR_ACCESS_DENIED}; },pack); check(!r.attempted && !r.accepted && !r.packed && packs == 0);
    r = VerifyHostPassword(account,account,value,[](wchar_t*) { return HostProbeAuthentication{false,true,true,0}; },pack); check(!r.attempted && !r.accepted && !r.packed && r.error != 0 && packs == 0);
    r = VerifyHostPassword(account,L"microsoftaccount\\BANDKEY-SYNTHETIC@example.invalid",value,auth,pack); check(r.attempted && r.accepted && r.sameSid && r.packed && r.error == 0 && packs == 1);
    SecureZeroMemory(value, sizeof(value)); SecureZeroMemory(longValue.data(), sizeof(longValue));
    std::ofstream report(output); Require(report.good(), ERROR_WRITE_FAULT);
    report << "{\"schema\":\"bandkey.host-probe-tests.v1\",\"passed\":" << count << ",\"failed\":0,\"synthetic\":true,\"authenticationAttempted\":false,\"providerRegistered\":false}\n";
    return 0;
}
}
int WINAPI wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE, _In_ PWSTR, _In_ int show) {
    int argc = 0; auto argv = CommandLineToArgvW(GetCommandLineW(), &argc); if (!argv) return 2;
    std::vector<std::wstring> args; for (int i = 1; i < argc; ++i) args.emplace_back(argv[i]); LocalFree(argv);
    try {
        if (args.size() == 2 && args[0] == L"--self-test") return SelfTest(args[1]);
        if (args.size() != 2 || (args[0] != L"--check" && args[0] != L"--output")) return 2;
        App app; app.output = args[1]; app.Resolve(); if (args[0] == L"--check") return 0;
        SetProcessDPIAware(); WNDCLASSW type{}; type.lpfnWndProc = Window; type.hInstance = instance;
        type.lpszClassName = L"BandKeyHostAccountProbe"; type.hCursor = LoadCursorW(nullptr, IDC_ARROW); type.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        Require(RegisterClassW(&type) != 0, GetLastError());
        auto window = CreateWindowW(type.lpszClassName, L"BandKey 实机账户兼容性检查（不安装）", WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
            CW_USEDEFAULT, CW_USEDEFAULT, 680, 345, nullptr, nullptr, instance, &app);
        if (!window) throw Failure{GetLastError()}; ShowWindow(window, show); UpdateWindow(window);
        MSG message{}; while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
        return 0;
    } catch (...) { return 2; }
}