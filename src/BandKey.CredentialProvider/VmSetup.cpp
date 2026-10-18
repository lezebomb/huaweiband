#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <knownfolders.h>
#include <commctrl.h>
#include <aclapi.h>
#include <wtsapi32.h>
#include <lm.h>
#include <bcrypt.h>
#include <filesystem>
#include <fstream>
#include <vector>
#include <array>
#include <stdexcept>
#include "LabPolicy.h"
#include "ProviderHash.h"
#pragma comment(linker, "/manifestdependency:\"type='win32' name='Microsoft.Windows.Common-Controls' version='6.0.0.0' processorArchitecture='amd64' publicKeyToken='6595b64144ccf1df' language='*'\"")

struct SetupFailure { std::wstring text; DWORD code; };
static void Require(bool success, const wchar_t* text, DWORD code = ERROR_INVALID_DATA) { if (!success) throw SetupFailure{text, code}; }
struct Key {
    HKEY value = nullptr;
    ~Key() { if (value) RegCloseKey(value); }
};
static bool KeyExists(const wchar_t* path) {
    Key key; auto result = RegOpenKeyExW(HKEY_LOCAL_MACHINE, path, 0, KEY_READ | KEY_WOW64_64KEY, &key.value);
    Require(result == ERROR_SUCCESS || result == ERROR_FILE_NOT_FOUND, L"Cannot inspect lab registration", result);
    return result == ERROR_SUCCESS;
}
static void NewKey(const wchar_t* path, Key& key) {
    DWORD disposition = 0;
    auto code = RegCreateKeyExW(HKEY_LOCAL_MACHINE, path, 0, nullptr, 0, KEY_WRITE | KEY_WOW64_64KEY, nullptr, &key.value, &disposition);
    Require(code == ERROR_SUCCESS, L"Cannot create lab registry key", code);
    Require(disposition == REG_CREATED_NEW_KEY, L"Lab key already exists; remove the old lab first", ERROR_ALREADY_EXISTS);
}
static void TextValue(HKEY key, const wchar_t* name, const std::wstring& text) {
    auto code = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(text.c_str()), static_cast<DWORD>((text.size() + 1) * sizeof(wchar_t)));
    Require(code == ERROR_SUCCESS, L"Cannot write lab registry value", code);
}
static void EnabledValue(HKEY key, DWORD value) {
    auto code = RegSetValueExW(key, L"Enabled", 0, REG_DWORD, reinterpret_cast<const BYTE*>(&value), sizeof(value));
    Require(code == ERROR_SUCCESS, L"Cannot update lab enable flag", code);
}
static DWORD DeleteOwnedKey(const wchar_t* path) {
    auto code = RegDeleteTreeW(HKEY_LOCAL_MACHINE, path);
    return code == ERROR_FILE_NOT_FOUND ? ERROR_SUCCESS : code;
}
static std::filesystem::path ExecutablePath() {
    std::vector<wchar_t> path(32768); DWORD n = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    Require(n && n < path.size(), L"Cannot resolve setup path"); return path.data();
}
static std::filesystem::path InstallDirectory() {
    PWSTR folder = nullptr;
    Require(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_ProgramFilesX64, 0, nullptr, &folder)), L"Cannot resolve Program Files");
    std::filesystem::path path(folder); CoTaskMemFree(folder); return path / L"BandKeyVmLab";
}
static void CheckOrdinaryDirectory(const std::filesystem::path& path) {
    auto attributes = GetFileAttributesW(path.c_str());
    Require(attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) && !(attributes & FILE_ATTRIBUTE_REPARSE_POINT), L"Install directory is missing or is a reparse point");
}
static bool IsAdmin() {
    SID_IDENTIFIER_AUTHORITY authority = SECURITY_NT_AUTHORITY; PSID administrators = nullptr; BOOL member = FALSE;
    if (!AllocateAndInitializeSid(&authority, 2, SECURITY_BUILTIN_DOMAIN_RID, DOMAIN_ALIAS_RID_ADMINS, 0, 0, 0, 0, 0, 0, &administrators)) return false;
    CheckTokenMembership(nullptr, administrators, &member); FreeSid(administrators); return member != FALSE;
}
static std::wstring CurrentLocalSid() {
    DWORD session = 0; Require(ProcessIdToSessionId(GetCurrentProcessId(), &session) && session, L"Interactive VM session required");
    PWSTR userText = nullptr, domainText = nullptr; DWORD bytes = 0;
    Require(WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, session, WTSUserName, &userText, &bytes) != FALSE, L"Cannot inspect interactive user");
    std::wstring user(userText ? userText : L""); WTSFreeMemory(userText);
    Require(WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, session, WTSDomainName, &domainText, &bytes) != FALSE, L"Cannot inspect interactive account type");
    std::wstring domain(domainText ? domainText : L""); WTSFreeMemory(domainText);
    wchar_t computer[256] = {}; DWORD chars = ARRAYSIZE(computer);
    Require(GetComputerNameW(computer, &chars) && !user.empty() && _wcsicmp(domain.c_str(), computer) == 0, L"Use an independent local VM account; domain accounts are not supported");
    USER_INFO_24* internet = nullptr;
    auto code = NetUserGetInfo(nullptr, user.c_str(), 24, reinterpret_cast<LPBYTE*>(&internet));
    Require(code == NERR_Success && internet, L"Cannot confirm local account identity", code);
    bool online = internet->usri24_internet_identity != FALSE; NetApiBufferFree(internet);
    Require(!online, L"Microsoft/Internet accounts are not supported by this VM lab");
    USER_INFO_23* account = nullptr; code = NetUserGetInfo(nullptr, user.c_str(), 23, reinterpret_cast<LPBYTE*>(&account));
    Require(code == NERR_Success && account, L"Cannot resolve VM account SID", code);
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) { NetApiBufferFree(account); throw SetupFailure{L"Cannot inspect setup identity", GetLastError()}; }
    bytes = 0; GetTokenInformation(token, TokenUser, nullptr, 0, &bytes);
    std::vector<BYTE> tokenData(bytes);
    bool read = bytes && GetTokenInformation(token, TokenUser, tokenData.data(), bytes, &bytes); CloseHandle(token);
    bool matches = read && EqualSid(reinterpret_cast<TOKEN_USER*>(tokenData.data())->User.Sid, account->usri23_user_sid);
    PWSTR sidText = nullptr;
    bool converted = matches && ConvertSidToStringSidW(account->usri23_user_sid, &sidText);
    NetApiBufferFree(account);
    if (!converted || !sidText) throw SetupFailure{L"Run setup as the same local account used on the VM desktop", ERROR_INVALID_DATA};
    std::wstring sid(sidText); LocalFree(sidText); return sid;
}
static std::string FileHash(const std::filesystem::path& path) {
    BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_HASH_HANDLE hash = nullptr;
    Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0, L"SHA256 unavailable");
    if (BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) < 0) { BCryptCloseAlgorithmProvider(algorithm, 0); throw SetupFailure{L"SHA256 initialization failed", ERROR_INVALID_DATA}; }
    bool success = true;
    std::ifstream stream(path, std::ios::binary); std::vector<char> buffer(65536);
    if (!stream) success = false;
    while (success && stream) {
        stream.read(buffer.data(), buffer.size()); auto length = stream.gcount();
        if (length && BCryptHashData(hash, reinterpret_cast<PUCHAR>(buffer.data()), static_cast<ULONG>(length), 0) < 0) success = false;
    }
    BYTE digest[32] = {}; success = success && stream.eof() && BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0);
    Require(success, L"Cannot read or hash provider DLL");
    constexpr char hex[] = "0123456789abcdef"; std::string text;
    for (BYTE b : digest) { text += hex[b >> 4]; text += hex[b & 15]; } return text;
}
static void ProtectDirectory(const std::filesystem::path& path) {
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    Require(ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)", SDDL_REVISION_1, &descriptor, nullptr) != FALSE, L"Cannot build directory ACL");
    PACL acl = nullptr; BOOL present = FALSE, defaulted = FALSE;
    GetSecurityDescriptorDacl(descriptor, &present, &acl, &defaulted);
    auto code = SetNamedSecurityInfoW(const_cast<PWSTR>(path.c_str()), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, nullptr, nullptr, acl, nullptr);
    LocalFree(descriptor); Require(code == ERROR_SUCCESS, L"Cannot protect lab directory", code);
}
static void Install() {
    auto sid = CurrentLocalSid(); auto directory = InstallDirectory(); auto destination = directory / L"BandKey.CredentialProvider.dll";
    auto source = ExecutablePath().parent_path() / destination.filename();
    Require(FileHash(source) == BandKeyExpectedProviderHash, L"Provider DLL hash does not match this setup build");
    Require(!KeyExists(BandKeyLabConfig) && !KeyExists(BandKeyLabCom) && !KeyExists(BandKeyLabRegistration), L"Existing lab registration found; remove it first", ERROR_ALREADY_EXISTS);
    BOOL created = CreateDirectoryW(directory.c_str(), nullptr);
    Require(created != FALSE, L"Lab folder already exists or cannot be created; remove the old lab first", created ? ERROR_SUCCESS : GetLastError());
    bool configCreated = false, comCreated = false, registrationCreated = false, fileCopied = false;
    try {
        CheckOrdinaryDirectory(directory); ProtectDirectory(directory);
        BOOL copied = CopyFileW(source.c_str(), destination.c_str(), TRUE);
        Require(copied != FALSE, L"Cannot copy provider DLL", copied ? ERROR_SUCCESS : GetLastError()); fileCopied = true;
        Require(FileHash(destination) == BandKeyExpectedProviderHash, L"Copied DLL hash mismatch");
        Key config; NewKey(BandKeyLabConfig, config); configCreated = true;
        EnabledValue(config.value, 0); TextValue(config.value, L"TargetSid", sid);
        Key com; NewKey(BandKeyLabCom, com); comCreated = true; TextValue(com.value, nullptr, L"BandKey VM credential test");
        Key server; DWORD disposition = 0;
        auto code = RegCreateKeyExW(com.value, L"InprocServer32", 0, nullptr, 0, KEY_WRITE | KEY_WOW64_64KEY, nullptr, &server.value, &disposition);
        Require(code == ERROR_SUCCESS, L"Cannot register lab COM server", code);
        TextValue(server.value, nullptr, destination.wstring()); TextValue(server.value, L"ThreadingModel", L"Apartment");
        Key registration; NewKey(BandKeyLabRegistration, registration); registrationCreated = true;
        TextValue(registration.value, nullptr, L"BandKey VM credential test");
        EnabledValue(config.value, 1); // Last operation: partial installs cannot expose a tile.
    } catch (...) {
        if (registrationCreated) DeleteOwnedKey(BandKeyLabRegistration);
        if (configCreated) DeleteOwnedKey(BandKeyLabConfig);
        if (comCreated) DeleteOwnedKey(BandKeyLabCom);
        if (fileCopied) DeleteFileW(destination.c_str());
        RemoveDirectoryW(directory.c_str()); throw;
    }
}
static bool Remove() {
    // Only this lab's three exact registry subtrees and two fixed filesystem names.
    DWORD firstError = ERROR_SUCCESS;
    for (auto path : { BandKeyLabRegistration, BandKeyLabConfig, BandKeyLabCom }) {
        auto code = DeleteOwnedKey(path); if (code && !firstError) firstError = code;
    }
    Require(firstError == ERROR_SUCCESS, L"Could not remove all lab registration; restore the baseline snapshot if necessary", firstError);
    auto directory = InstallDirectory();
    if (GetFileAttributesW(directory.c_str()) == INVALID_FILE_ATTRIBUTES) return true;
    CheckOrdinaryDirectory(directory);
    auto dll = directory / L"BandKey.CredentialProvider.dll";
    if (!DeleteFileW(dll.c_str()) && GetLastError() != ERROR_FILE_NOT_FOUND) return false;
    if (!RemoveDirectoryW(directory.c_str()) && GetLastError() != ERROR_PATH_NOT_FOUND) return false;
    return true;
}
int WINAPI wWinMain(_In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ PWSTR, _In_ int) {
    int argc = 0; PWSTR* args = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!args) return 2;
    std::vector<std::wstring> arguments; for (int i = 0; i < argc; i++) arguments.emplace_back(args[i]); LocalFree(args);
    if (argc == 3 && arguments[1] == L"--check") {
        try {
            std::ofstream report{std::filesystem::path(arguments[2])};
            report << "{\"vmGuardAccepted\":" << (IsSupportedVmwareGuest() ? "true" : "false") << ",\"mutationPerformed\":false,\"supportedBuild\":26300,\"providerRegistered\":" << (KeyExists(BandKeyLabRegistration) ? "true" : "false") << '}';
            report.close(); return report ? 0 : 1;
        } catch (...) { return 1; }
    }
    try {
        Require(IsSupportedVmwareGuest() && !GetSystemMetrics(SM_REMOTESESSION), L"This laboratory setup only supports the local VMware Windows 11 build 26300 guest. No changes were made.");
        std::wstring operation;
        if (argc == 2 && (arguments[1] == L"--install" || arguments[1] == L"--remove")) operation = arguments[1];
        else {
            Require(argc == 1, L"Unsupported setup arguments");
            const TASKDIALOG_BUTTON buttons[] = {{101, L"Install VM password-test tile\nOnly the current independent local VM account"}, {102, L"Remove VM password-test tile\nKeep all system sign-in options"}};
            TASKDIALOGCONFIG dialog = {}; dialog.cbSize = sizeof(dialog); dialog.dwFlags = TDF_USE_COMMAND_LINKS | TDF_ALLOW_DIALOG_CANCELLATION;
            dialog.dwCommonButtons = TDCBF_CANCEL_BUTTON; dialog.pszWindowTitle = L"BandKey VM lab setup";
            dialog.pszMainInstruction = L"Manual Windows password test - no band authorization";
            dialog.pszContent = L"Keep your baseline VM snapshot and original Windows sign-in option. This lab does not store passwords, change the default provider, or sign you out. Administrator approval is requested only after choosing an action.";
            dialog.cButtons = ARRAYSIZE(buttons); dialog.pButtons = buttons; int choice = IDCANCEL;
            Require(SUCCEEDED(TaskDialogIndirect(&dialog, &choice, nullptr, nullptr)), L"Cannot open setup dialog");
            if (choice != 101 && choice != 102) return 0;
            operation = choice == 101 ? L"--install" : L"--remove";
            if (!IsAdmin()) {
                auto executable = ExecutablePath(); SHELLEXECUTEINFOW start = {}; start.cbSize = sizeof(start);
                start.lpVerb = L"runas"; start.lpFile = executable.c_str(); start.lpParameters = operation.c_str(); start.nShow = SW_SHOWNORMAL;
                BOOL launched = ShellExecuteExW(&start);
                Require(launched != FALSE, L"Administrator launch was cancelled or failed", launched ? ERROR_SUCCESS : GetLastError()); return 0;
            }
        }
        Require(IsAdmin(), L"Administrator approval is required for this action");
        if (operation == L"--install") { Install(); MessageBoxW(nullptr, L"VM lab tile installed. No Windows authentication has been tested.\n\nKeep a working VM account password. Lock the VM manually and choose BandKey VM credential test under Sign-in options.\n\nTo roll back: run this setup again and choose Remove.", L"BandKey VM lab", MB_OK | MB_ICONINFORMATION); }
        else { bool filesRemoved = Remove(); MessageBoxW(nullptr, filesRemoved ? L"Lab registration and files removed. System providers were preserved." : L"Lab registration removed. The DLL or directory is still in use or not empty; restart the VM, then run Remove again. System providers were preserved.", L"BandKey VM lab", MB_OK | MB_ICONINFORMATION); }
        return 0;
    } catch (const SetupFailure& error) { auto text = error.text + L"\nWindows error: " + std::to_wstring(error.code); MessageBoxW(nullptr, text.c_str(), L"BandKey VM lab - action not completed", MB_OK | MB_ICONERROR); }
    catch (const std::exception&) { MessageBoxW(nullptr, L"Setup failed. Use Remove or the VM baseline snapshot for recovery.", L"BandKey VM lab", MB_OK | MB_ICONERROR); }
    return 1;
}
