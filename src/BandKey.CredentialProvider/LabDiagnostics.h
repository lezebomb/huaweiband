#pragma once
#include "NativeSecurity.h"
#include <cstdio>

enum class LabStage {
    ProviderLoaded, UsageLogon, UsageUnlock, Advise, Apartment, NotificationsArmed, NotificationsFailed,
    TimerActive, UserBound, ChallengeWritten, WaitingApproval, ApprovalRejected, ApprovalVerified,
    RefreshRequested, RefreshFailed, AutoCredential, SelectedReady, SelectedWaiting, CredentialUnAdvise,
    Deselected, ProviderUnAdvise, ChallengeExpired, ContextCancelled, SerializationRequested, GrantConsumed,
    VaultReadStarted, VaultPermissionRejected, VaultDpapiFailed, VaultReadFailed, VaultReadSucceeded,
    SerializationPrepared, SerializationFailed, WindowsResult, SessionBoundary
};
inline const char* LabStageName(LabStage stage) {
    static constexpr const char* names[] = {
        "provider-loaded", "usage-logon", "usage-unlock", "provider-advise", "COM-apartment", "notifications-armed", "notifications-failed",
        "timer-active", "user-bound", "challenge-written", "waiting-approval", "approval-rejected", "approval-verified",
        "refresh-requested", "refresh-failed", "auto-credential", "selected-ready", "selected-waiting", "credential-unadvise",
        "tile-deselected", "provider-unadvise", "challenge-expired", "context-cancelled", "serialization-requested", "grant-consumed",
        "vault-read-started", "vault-permission-rejected", "vault-DPAPI-failed", "vault-read-failed", "vault-read-succeeded",
        "serialization-prepared", "serialization-failed", "Windows-result", "session-boundary"
    };
    auto index = static_cast<size_t>(stage); return index < ARRAYSIZE(names) ? names[index] : "invalid-stage";
}
// Diagnostics never authorize authentication; inputs are a fixed stage enum and number.
inline bool AppendLabDiagnostic(const std::filesystem::path& path, LabStage stage, DWORD code = 0) noexcept {
    try {
        NativeHandle file; file.value = CreateFileW(path.c_str(), FILE_APPEND_DATA | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (file.value == INVALID_HANDLE_VALUE) return false;
        BY_HANDLE_FILE_INFORMATION info{}; LARGE_INTEGER size{};
        if (!GetFileInformationByHandle(file.value, &info) || info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT) || info.nNumberOfLinks != 1 || !GetFileSizeEx(file.value, &size) || size.QuadPart >= 262144) return false;
        char line[160]{};
        int count = sprintf_s(line, "%llu | %s | 0x%08lX\r\n", GetTickCount64(), LabStageName(stage), code);
        DWORD written = 0; return count > 0 && WriteFile(file.value, line, static_cast<DWORD>(count), &written, nullptr) && written == static_cast<DWORD>(count);
    } catch (...) { return false; }
}
