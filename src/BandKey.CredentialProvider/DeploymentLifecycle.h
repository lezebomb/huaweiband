#pragma once
#include "LabCleanup.h"
#include <optional>
#include <algorithm>
#include <cstring>

enum class DeploymentPhase : DWORD {
    DirectoryPrepared = 1, PayloadVerified, CredentialEnrolled, PolicyWritten,
    ComRegistered, ReadyToActivate, Removing, RecoveryRequired
};
struct DeploymentProgress {
    BYTE magic[8] = {'B','K','D','E','P','0','1',0};
    DWORD version = 1, phase = 0, error = 0, reserved = 0;
};
static_assert(sizeof(DeploymentProgress) == 24);
inline bool ValidDeploymentPhase(DWORD phase) {
    return phase >= static_cast<DWORD>(DeploymentPhase::DirectoryPrepared) && phase <= static_cast<DWORD>(DeploymentPhase::RecoveryRequired);
}
inline constexpr wchar_t DeploymentStateName[] = L"deployment-state.bin";
inline constexpr wchar_t DeploymentTempName[] = L"deployment-state.tmp";

// Progress is diagnostic only: it never selects registry keys, paths, accounts or authority.
inline std::optional<DeploymentProgress> ReadDeploymentProgress(const std::filesystem::path& directory) {
    const auto path = directory / DeploymentStateName;
    const auto attributes = GetFileAttributesW(path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) {
        const auto error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) return {};
        throw std::runtime_error("deployment-progress-unavailable");
    }
    CheckNoReparseDirectory(directory);
    const auto bytes = ReadBoundedFile(path, sizeof(DeploymentProgress));
    if (bytes.size() != sizeof(DeploymentProgress)) throw std::runtime_error("deployment-progress-size");
    DeploymentProgress value; std::memcpy(&value, bytes.data(), sizeof(value));
    const DeploymentProgress expected;
    if (!std::equal(std::begin(value.magic), std::end(value.magic), std::begin(expected.magic)) ||
        value.version != 1 || !ValidDeploymentPhase(value.phase) || value.reserved != 0)
        throw std::runtime_error("deployment-progress-format");
    return value;
}
inline void WriteDeploymentProgress(const std::filesystem::path& directory, DeploymentPhase phase, DWORD error = 0) {
    NativeRequire(ValidDeploymentPhase(static_cast<DWORD>(phase)), "deployment-stage-invalid");
    CheckNoReparseDirectory(directory);
    const auto temporary = directory / DeploymentTempName;
    const auto target = directory / DeploymentStateName;
    NativeRequire(RemoveOrdinaryLabFile(temporary) == ERROR_SUCCESS, "deployment-temp-unavailable");
    // Refuse substituted/hard-linked progress files even though replacement does not write their target.
    {
        NativeHandle existing;
        existing.value = CreateFileW(target.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (existing.value != INVALID_HANDLE_VALUE) {
            BY_HANDLE_FILE_INFORMATION info{};
            NativeRequire(OrdinaryLabFileInfo(existing.value, info) == ERROR_SUCCESS, "deployment-progress-file-invalid");
        } else {
            const auto missing = GetLastError();
            NativeRequire(missing == ERROR_FILE_NOT_FOUND || missing == ERROR_PATH_NOT_FOUND, "deployment-progress-open-failed");
        }
    }
    DeploymentProgress progress; progress.phase = static_cast<DWORD>(phase); progress.error = error;
    WriteNewFile(temporary, reinterpret_cast<const BYTE*>(&progress), sizeof(progress));
    NativeRequire(MoveFileExW(temporary.c_str(), target.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != FALSE,
        "deployment-progress-replace-failed");
}
inline DWORD MissingDeploymentKeyIsSuccess(DWORD error) {
    return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND || error == ERROR_KEY_DELETED ? ERROR_SUCCESS : error;
}
enum class DeploymentKey { Provider, Policy, Com };
struct DeploymentOwnedKeys { bool provider = false, policy = false, com = false; };
struct DeploymentRevocation {
    DWORD firstError = ERROR_SUCCESS;
    bool disabled = false, providerRemoved = false, policyRemoved = false, comRemoved = false;
    bool Complete() const { return firstError == ERROR_SUCCESS; }
};
// Disable first, then try EVERY owned deletion, even when an earlier operation fails.
template<class Disable, class Delete>
DeploymentRevocation RevokeDeployment(DeploymentOwnedKeys owned, Disable disable, Delete remove) {
    DeploymentRevocation result;
    auto record = [&](DWORD code) { code = MissingDeploymentKeyIsSuccess(code); if (code && !result.firstError) result.firstError = code; return code == ERROR_SUCCESS; };
    result.disabled = !owned.policy || record(disable());
    result.providerRemoved = !owned.provider || record(remove(DeploymentKey::Provider));
    result.policyRemoved = !owned.policy || record(remove(DeploymentKey::Policy));
    result.comRemoved = !owned.com || record(remove(DeploymentKey::Com));
    return result;
}
class SetupOperationBusy : public std::runtime_error { public: SetupOperationBusy() : std::runtime_error("another-setup-operation-is-running") {} };
class SetupOperationLock {
    NativeHandle mutex; bool owned = false;
public:
    explicit SetupOperationLock(const wchar_t* name) {
        mutex.value = CreateMutexW(nullptr, FALSE, name);
        if (!mutex.value) throw std::runtime_error("setup-operation-lock-unavailable");
        const auto waited = WaitForSingleObject(mutex.value, 0);
        owned = waited == WAIT_OBJECT_0 || waited == WAIT_ABANDONED;
        if (!owned) throw SetupOperationBusy();
    }
    ~SetupOperationLock() { if (owned) ReleaseMutex(mutex.value); }
    SetupOperationLock(const SetupOperationLock&) = delete;
    SetupOperationLock& operator=(const SetupOperationLock&) = delete;
};