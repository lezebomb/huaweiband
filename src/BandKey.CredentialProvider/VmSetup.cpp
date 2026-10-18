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
#include <wincred.h>
#include <strsafe.h>
#include "SignedGrant.h"
#include "LabCleanup.h"
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
static std::wstring CurrentLocalSid(std::wstring* qualifiedName = nullptr) {
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
    std::wstring sid(sidText); LocalFree(sidText); if (qualifiedName) *qualifiedName = domain + L"\\" + user; return sid;
}
static void EnrollVault(const std::filesystem::path& directory, const std::wstring& sid, const std::wstring& account, std::vector<BYTE>& publicKey) {
    wchar_t user[513]{}; NativeRequire(SUCCEEDED(StringCchCopyW(user, ARRAYSIZE(user), account.c_str())), "account-name-too-long");
    SecretBuffer<257> password; BOOL save = FALSE;
    CREDUI_INFOW info{}; info.cbSize = sizeof(info); info.pszCaptionText = L"BandKey VM encrypted credential enrollment";
    info.pszMessageText = L"Enter only this VM local-account password. It will be encrypted in a SYSTEM/administrator-only vault for the VM signed-confirmation experiment. Do not enter a host password or PIN.";
    DWORD result = CredUIPromptForCredentialsW(&info, L"BandKey VM lab", nullptr, 0, user, ARRAYSIZE(user), password.data.data(), static_cast<ULONG>(password.data.size()), &save,
        CREDUI_FLAGS_GENERIC_CREDENTIALS | CREDUI_FLAGS_ALWAYS_SHOW_UI | CREDUI_FLAGS_DO_NOT_PERSIST | CREDUI_FLAGS_KEEP_USERNAME);
    Require(result == NO_ERROR, L"Enrollment cancelled or unavailable; no credential was saved", result);
    Require(password.data[0] && _wcsicmp(user, account.c_str()) == 0, L"The VM account and a nonempty password are required");
    auto slash = account.find(L'\\'); NativeRequire(slash != std::wstring::npos, "local-account-invalid");
    NativeHandle token; BOOL valid = LogonUserW(account.substr(slash + 1).c_str(), account.substr(0, slash).c_str(), password.data.data(), LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, &token.value);
    Require(valid != FALSE, L"Windows rejected enrollment password; no credential was saved", valid ? ERROR_SUCCESS : GetLastError());
    DWORD bytes = 0; GetTokenInformation(token.value, TokenUser, nullptr, 0, &bytes); std::vector<BYTE> tokenData(bytes);
    NativeRequire(bytes && GetTokenInformation(token.value, TokenUser, tokenData.data(), bytes, &bytes), "enrollment-token-unavailable");
    PSID expected = nullptr; NativeRequire(ConvertStringSidToSidW(sid.c_str(), &expected) != FALSE, "enrollment-sid-invalid");
    bool same = EqualSid(expected, reinterpret_cast<TOKEN_USER*>(tokenData.data())->User.Sid) != FALSE; LocalFree(expected); NativeRequire(same, "enrollment-account-mismatch");
    auto vault = directory / L"Vault"; auto exchange = directory / L"Exchange";
    NativeRequire(CreateDirectoryW(vault.c_str(), nullptr) && CreateDirectoryW(exchange.c_str(), nullptr), "experiment-directories-unavailable");
    SetAdministratorSecretSecurity(vault);
    SetDirectoryDacl(exchange, L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;" + sid + L")");
    ProtectedBytes encrypted;
    EncryptSecret(reinterpret_cast<BYTE*>(password.data.data()), static_cast<DWORD>((wcslen(password.data.data()) + 1) * sizeof(wchar_t)), sid, true, encrypted); password.Clear();
    auto credential = vault / L"credential.dpapi"; WriteNewFile(credential, encrypted.blob.pbData, encrypted.blob.cbData); SetAdministratorSecretSecurity(credential);
    SigningKey key; key.Generate(); publicKey = key.Export(false);
    auto privateKey = key.Export(true);
    ProtectedBytes protectedKey;
    try { EncryptSecret(privateKey.data(), static_cast<DWORD>(privateKey.size()), sid, false, protectedKey); }
    catch (...) { SecureZeroMemory(privateKey.data(), privateKey.size()); throw; }
    SecureZeroMemory(privateKey.data(), privateKey.size());
    WriteNewFile(exchange / L"signer.dpapi", protectedKey.blob.pbData, protectedKey.blob.cbData);
}
static void RemoveExperimentFiles(const std::filesystem::path& directory, LabCleanupResult& result) {
    auto vault = directory / L"Vault"; auto exchange = directory / L"Exchange";
    NativeHandle vaultGuard;
    if (CheckCleanupDirectory(vault, result, &vaultGuard)) {
        result.File(vault / L"credential.dpapi");
        CloseHandle(vaultGuard.value); vaultGuard.value = INVALID_HANDLE_VALUE;
        result.Directory(vault);
    }
    NativeHandle exchangeGuard;
    if (CheckCleanupDirectory(exchange, result, &exchangeGuard)) {
        // No recursion: only known experiment message names. Unknown leftovers stay visible.
        WIN32_FIND_DATAW entry{}; HANDLE find = FindFirstFileW((exchange / L"*").c_str(), &entry);
        if (find != INVALID_HANDLE_VALUE) {
            do { if (!(entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && IsLabExchangeFile(entry.cFileName)) result.File(exchange / entry.cFileName); } while (FindNextFileW(find, &entry));
            FindClose(find);
        } else {
            auto error = GetLastError(); if (error != ERROR_FILE_NOT_FOUND) result.Record(exchange, error);
        }
        CloseHandle(exchangeGuard.value); exchangeGuard.value = INVALID_HANDLE_VALUE;
        result.Directory(exchange);
    }
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
    std::wstring account; auto sid = CurrentLocalSid(&account); auto directory = InstallDirectory(); auto destination = directory / L"BandKey.CredentialProvider.dll";
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
        auto attributeError = PrepareCopiedLabFile(destination);
        Require(attributeError == ERROR_SUCCESS, L"Cannot normalize the copied DLL attributes", attributeError);
        Require(FileHash(destination) == BandKeyExpectedProviderHash, L"Copied DLL hash mismatch");
        std::vector<BYTE> publicKey; EnrollVault(directory, sid, account, publicKey);
        Key config; NewKey(BandKeyLabConfig, config); configCreated = true;
        EnabledValue(config.value, 0); TextValue(config.value, L"TargetSid", sid);
        DWORD mode = 2;
        auto setting = RegSetValueExW(config.value, L"Mode", 0, REG_DWORD, reinterpret_cast<const BYTE*>(&mode), sizeof(mode));
        Require(setting == ERROR_SUCCESS, L"Cannot set experiment mode", setting);
        setting = RegSetValueExW(config.value, L"LabPublicKey", 0, REG_BINARY, publicKey.data(), static_cast<DWORD>(publicKey.size()));
        Require(setting == ERROR_SUCCESS, L"Cannot pin VM simulation key", setting);
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
        try { LabCleanupResult cleanup; RemoveExperimentFiles(directory, cleanup); } catch (...) {}
        if (fileCopied) RemoveOrdinaryLabFile(destination);
        RemoveDirectoryW(directory.c_str()); throw;
    }
}
static LabCleanupResult Remove() {
    // Only this lab's exact registry subtrees, fixed files and known message names.
    DWORD firstError = ERROR_SUCCESS;
    for (auto path : { BandKeyLabRegistration, BandKeyLabConfig, BandKeyLabCom }) {
        auto code = DeleteOwnedKey(path); if (code && !firstError) firstError = code;
    }
    Require(firstError == ERROR_SUCCESS, L"Could not remove all lab registration; restore the baseline snapshot if necessary", firstError);
    auto directory = InstallDirectory();
    LabCleanupResult result;
    NativeHandle directoryGuard;
    if (!CheckCleanupDirectory(directory, result, &directoryGuard)) return result;
    RemoveExperimentFiles(directory, result);
    result.File(directory / L"BandKey.CredentialProvider.dll");
    CloseHandle(directoryGuard.value); directoryGuard.value = INVALID_HANDLE_VALUE;
    result.Directory(directory);
    return result;
}
static std::wstring RemovalMessage(const LabCleanupResult& result) {
    if (result.complete) return L"Lab registration and files removed. System providers were preserved.";
    std::wstring text = L"Lab registration removed; some files remain. System providers were preserved.\n";
    for (const auto& issue : result.issues) {
        text += L"\n" + issue.path.wstring() + L"\nWindows error: " + std::to_wstring(issue.error);
        if (issue.error == ERROR_SHARING_VIOLATION || issue.error == ERROR_LOCK_VIOLATION) text += L" (file in use: close the confirmation client; restart the VM if necessary)";
        else if (issue.error == ERROR_ACCESS_DENIED) text += L" (access denied; do not change unrelated system permissions)";
        else if (issue.error == ERROR_DIR_NOT_EMPTY) text += L" (directory contains remaining files; no recursive deletion was performed)";
        else if (issue.error == ERROR_TOO_MANY_LINKS) text += L" (hard-linked file refused)";
    }
    text += L"\n\nUse this updated setup to retry. If it still fails, report this message; do not repeatedly restart without a sharing/lock error.";
    return text;
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
            const TASKDIALOG_BUTTON buttons[] = {{101, L"Install signed-confirmation VM experiment\nEnroll this VM password once into an encrypted SYSTEM/admin-only vault"}, {102, L"Remove VM experiment\nRemove the lab vault, simulation key and provider; keep system sign-in options"}};
            TASKDIALOGCONFIG dialog = {}; dialog.cbSize = sizeof(dialog); dialog.dwFlags = TDF_USE_COMMAND_LINKS | TDF_ALLOW_DIALOG_CANCELLATION;
            dialog.dwCommonButtons = TDCBF_CANCEL_BUTTON; dialog.pszWindowTitle = L"BandKey VM lab setup";
            dialog.pszMainInstruction = L"VM-only simulated signature and encrypted credential experiment";
            dialog.pszContent = L"Keep a baseline snapshot and working system sign-in option. Enrollment verifies and encrypts this VM password. A deliberately simulated device key is used only in this isolated VM; no real band is tested. Setup does not lock, sign out or reboot. Administrator approval is requested after choosing an action.";
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
        if (operation == L"--install") { Install(); MessageBoxW(nullptr, L"Signed-confirmation VM experiment installed. Enrollment credentials were verified; desktop unlock is still untested.\n\nStart BandKey.VmConfirmation.exe from the lab disc. Arm one confirmation, then manually lock the VM within 10 seconds.\n\nKeep the system password sign-in option. To roll back, run this setup and choose Remove.", L"BandKey VM lab", MB_OK | MB_ICONINFORMATION); }
        else { auto result = Remove(); auto text = RemovalMessage(result); MessageBoxW(nullptr, text.c_str(), L"BandKey VM lab - cleanup fix 1", MB_OK | (result.complete ? MB_ICONINFORMATION : MB_ICONWARNING)); if (!result.complete) return 1; }
        return 0;
    } catch (const SetupFailure& error) { auto text = error.text + L"\nWindows error: " + std::to_wstring(error.code); MessageBoxW(nullptr, text.c_str(), L"BandKey VM lab - action not completed", MB_OK | MB_ICONERROR); }
    catch (const std::exception&) { MessageBoxW(nullptr, L"Setup failed. Use Remove or the VM baseline snapshot for recovery.", L"BandKey VM lab", MB_OK | MB_ICONERROR); }
    return 1;
}
