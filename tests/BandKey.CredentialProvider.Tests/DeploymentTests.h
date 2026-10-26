#pragma once
#include "../../src/BandKey.CredentialProvider/DeploymentLifecycle.h"
#include <thread>
#include <atomic>

template<class Check>
void TestDeploymentLifecycle(Check check, const std::filesystem::path& root) {
    {
        std::vector<int> order;
        const auto revoked = RevokeDeployment({true,true,true}, [&]() { order.push_back(0); return ERROR_SUCCESS; },
            [&](DeploymentKey key) { order.push_back(1+static_cast<int>(key)); return ERROR_SUCCESS; });
        check("deployment disables policy before deleting each owned registration", revoked.Complete() && revoked.disabled &&
            revoked.providerRemoved && revoked.policyRemoved && revoked.comRemoved && order == std::vector<int>({0,1,2,3}));
        order.clear();
        const auto failed = RevokeDeployment({true,true,true}, [&]() { order.push_back(0); return ERROR_ACCESS_DENIED; },
            [&](DeploymentKey key) { order.push_back(1+static_cast<int>(key)); return key == DeploymentKey::Provider ? ERROR_SHARING_VIOLATION : ERROR_SUCCESS; });
        check("deployment revoke keeps the first failure and still attempts every cleanup", !failed.Complete() && !failed.disabled &&
            !failed.providerRemoved && failed.policyRemoved && failed.comRemoved && failed.firstError == ERROR_ACCESS_DENIED && order.size() == 4);
        const auto providerFailure = RevokeDeployment({true,true,true}, []() { return ERROR_SUCCESS; },
            [](DeploymentKey key) { return key == DeploymentKey::Provider ? ERROR_ACCESS_DENIED : ERROR_SUCCESS; });
        check("deployment retains disabled policy evidence when provider removal fails", providerFailure.disabled &&
            !providerFailure.Complete() && providerFailure.policyRemoved && providerFailure.comRemoved);
        const auto retry = RevokeDeployment({true,true,true}, []() { return ERROR_FILE_NOT_FOUND; },
            [](DeploymentKey key) { return key == DeploymentKey::Com ? ERROR_KEY_DELETED : ERROR_PATH_NOT_FOUND; });
        check("deployment revocation retry accepts already removed keys", retry.Complete() && retry.disabled && retry.providerRemoved && retry.policyRemoved && retry.comRemoved);
        int calls = 0;
        const auto owned = RevokeDeployment({false,false,true}, [&]() { ++calls; return ERROR_SUCCESS; },
            [&](DeploymentKey key) { if (key == DeploymentKey::Com) ++calls; else calls += 100; return ERROR_SUCCESS; });
        check("deployment rollback never touches registry keys it did not create", owned.Complete() && calls == 1);
    }
    NativeRequire(CreateDirectoryW(root.c_str(), nullptr) != FALSE, "deployment-test-root-unavailable");
    const auto journal = root / DeploymentStateName, temporary = root / DeploymentTempName;
    check("deployment without a journal reports no invented phase", !ReadDeploymentProgress(root).has_value());
    WriteDeploymentProgress(root, DeploymentPhase::CredentialEnrolled);
    auto progress = ReadDeploymentProgress(root);
    check("deployment journal round trip is bounded and carries only phase and error", progress && progress->phase == 3 &&
        progress->error == 0 && std::filesystem::file_size(journal) == sizeof(DeploymentProgress) && !std::filesystem::exists(temporary));
    WriteDeploymentProgress(root, DeploymentPhase::RecoveryRequired, ERROR_SHARING_VIOLATION);
    progress = ReadDeploymentProgress(root);
    check("deployment atomic replacement preserves the latest recovery error", progress && progress->phase == 8 && progress->error == ERROR_SHARING_VIOLATION);
    bool refused = false;
    try { WriteDeploymentProgress(root, static_cast<DeploymentPhase>(0xffffffff)); } catch (const std::exception&) { refused = true; }
    check("deployment refuses invalid phase without changing the previous journal", refused && ReadDeploymentProgress(root)->phase == 8);
    const BYTE stale[] = {1,2,3}; WriteNewFile(temporary, stale, sizeof(stale));
    WriteDeploymentProgress(root, DeploymentPhase::ReadyToActivate);
    check("deployment replaces an owned stale temporary file after interruption", ReadDeploymentProgress(root)->phase == 6 && !std::filesystem::exists(temporary));
    {
        auto invalid = *ReadDeploymentProgress(root); invalid.version = 99;
        NativeHandle file; file.value = CreateFileW(journal.c_str(), GENERIC_WRITE, 0, nullptr, TRUNCATE_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        NativeRequire(file.value != INVALID_HANDLE_VALUE, "deployment-test-corrupt-open");
        DWORD written = 0; NativeRequire(WriteFile(file.value, &invalid, sizeof(invalid), &written, nullptr) && written == sizeof(invalid), "deployment-test-corrupt-write");
    }
    refused = false; try { (void)ReadDeploymentProgress(root); } catch (const std::exception&) { refused = true; }
    check("deployment rejects an unrecognized journal version", refused);
    NativeRequire(RemoveOrdinaryLabFile(journal) == ERROR_SUCCESS, "deployment-test-journal-remove");
    const auto foreign = root / L"foreign-fixture.bin"; WriteNewFile(foreign, stale, sizeof(stale));
    NativeRequire(CreateHardLinkW(journal.c_str(), foreign.c_str(), nullptr) != FALSE, "deployment-test-hardlink-create");
    refused = false; try { WriteDeploymentProgress(root, DeploymentPhase::Removing); } catch (const std::exception&) { refused = true; }
    check("deployment refuses a hard-linked journal without modifying its target", refused && ReadBoundedFile(foreign, 3) == std::vector<BYTE>({1,2,3}));
    NativeRequire(DeleteFileW(journal.c_str()) != FALSE, "deployment-test-hardlink-remove");
    NativeRequire(CreateHardLinkW(temporary.c_str(), foreign.c_str(), nullptr) != FALSE, "deployment-test-temp-link-create");
    refused = false; try { WriteDeploymentProgress(root, DeploymentPhase::Removing); } catch (const std::exception&) { refused = true; }
    check("deployment refuses a hard-linked stale temporary file", refused && ReadBoundedFile(foreign, 3) == std::vector<BYTE>({1,2,3}));
    NativeRequire(DeleteFileW(temporary.c_str()) && RemoveOrdinaryLabFile(foreign) == ERROR_SUCCESS, "deployment-test-fixture-remove");
    const std::wstring name = L"Local\\BandKeyDeploymentTest-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
    std::atomic<bool> busy{false};
    {
        SetupOperationLock first(name.c_str());
        std::thread second([&]() { try { SetupOperationLock other(name.c_str()); } catch (const std::exception&) { busy = true; } });
        second.join();
    }
    { SetupOperationLock after(name.c_str()); check("deployment serializes concurrent setup and permits a later retry", busy.load()); }
    NativeHandle abandoned;
    const auto abandonedName = name + L"-abandoned";
    std::thread exited([&]() { abandoned.value = CreateMutexW(nullptr, TRUE, abandonedName.c_str()); });
    exited.join(); NativeRequire(abandoned.value != nullptr, "deployment-test-abandoned-create");
    { SetupOperationLock recovered(abandonedName.c_str()); check("deployment acquires an abandoned setup lock after interruption", true); }
    NativeRequire(RemoveDirectoryW(root.c_str()) != FALSE, "deployment-test-directory-remove");
}