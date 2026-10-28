#pragma once
#include "LabCleanup.h"

// Caller owns a protected parent and verifies the new payload hash. A failed
// ReplaceFile can rename the original to backup (Windows error 1177); restore it.
inline DWORD ReplaceBandKeyPayload(const std::filesystem::path& target, const std::filesystem::path& staged, const std::filesystem::path& backup) {
    if (target.parent_path() != staged.parent_path() || target.parent_path() != backup.parent_path() || target == staged || target == backup || staged == backup) return ERROR_INVALID_PARAMETER;
    for (const auto& path : {target, staged}) {
        NativeHandle file; file.value = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (file.value == INVALID_HANDLE_VALUE) return GetLastError();
        BY_HANDLE_FILE_INFORMATION info{}; auto code = OrdinaryLabFileInfo(file.value, info); if (code) return code;
    }
    if (GetFileAttributesW(backup.c_str()) != INVALID_FILE_ATTRIBUTES) return ERROR_ALREADY_EXISTS;
    auto code = GetLastError(); if (code != ERROR_FILE_NOT_FOUND) return code;
    if (ReplaceFileW(target.c_str(), staged.c_str(), backup.c_str(), 0, nullptr, nullptr)) return ERROR_SUCCESS;
    code = GetLastError();
    if (GetFileAttributesW(target.c_str()) == INVALID_FILE_ATTRIBUTES && GetLastError() == ERROR_FILE_NOT_FOUND &&
        GetFileAttributesW(backup.c_str()) != INVALID_FILE_ATTRIBUTES) {
        if (!MoveFileExW(backup.c_str(), target.c_str(), MOVEFILE_WRITE_THROUGH)) return GetLastError();
    }
    return code;
}
