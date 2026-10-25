#pragma once
#include "NativeSecurity.h"

// Handle-bound operations do not follow a substituted path while changing attributes.
inline DWORD OrdinaryLabFileInfo(HANDLE file, BY_HANDLE_FILE_INFORMATION& info) {
    if (!GetFileInformationByHandle(file, &info)) return GetLastError();
    if (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) return ERROR_CANT_ACCESS_FILE;
    if (info.nNumberOfLinks != 1) return ERROR_TOO_MANY_LINKS;
    return ERROR_SUCCESS;
}
inline DWORD ClearLabFileReadOnly(HANDLE file, const BY_HANDLE_FILE_INFORMATION& info) {
    if (!(info.dwFileAttributes & FILE_ATTRIBUTE_READONLY)) return ERROR_SUCCESS;
    FILE_BASIC_INFO attributes{};
    attributes.FileAttributes = info.dwFileAttributes & ~FILE_ATTRIBUTE_READONLY;
    if (!attributes.FileAttributes) attributes.FileAttributes = FILE_ATTRIBUTE_NORMAL;
    return SetFileInformationByHandle(file, FileBasicInfo, &attributes, sizeof(attributes)) ? ERROR_SUCCESS : GetLastError();
}
inline DWORD PrepareCopiedLabFile(const std::filesystem::path& path) {
    NativeHandle file; file.value = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file.value == INVALID_HANDLE_VALUE) return GetLastError();
    BY_HANDLE_FILE_INFORMATION info{}; auto error = OrdinaryLabFileInfo(file.value, info);
    return error ? error : ClearLabFileReadOnly(file.value, info);
}
inline DWORD RemoveOrdinaryLabFile(const std::filesystem::path& path) {
    NativeHandle file; file.value = CreateFileW(path.c_str(), DELETE | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file.value == INVALID_HANDLE_VALUE) {
        auto error = GetLastError(); return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND ? ERROR_SUCCESS : error;
    }
    BY_HANDLE_FILE_INFORMATION info{}; auto error = OrdinaryLabFileInfo(file.value, info);
    if (error) return error;
    error = ClearLabFileReadOnly(file.value, info); if (error) return error;
    FILE_DISPOSITION_INFO disposition{TRUE};
    return SetFileInformationByHandle(file.value, FileDispositionInfo, &disposition, sizeof(disposition)) ? ERROR_SUCCESS : GetLastError();
}
struct LabCleanupIssue { std::filesystem::path path; DWORD error; };
struct LabCleanupResult {
    std::vector<LabCleanupIssue> issues;
    bool complete = true;
    void Record(const std::filesystem::path& path, DWORD error) { if (error) { complete = false; if (issues.size() < 8) issues.push_back({path, error}); } }
    void File(const std::filesystem::path& path) { Record(path, RemoveOrdinaryLabFile(path)); }
    void Directory(const std::filesystem::path& path) {
        if (RemoveDirectoryW(path.c_str())) return;
        auto error = GetLastError(); if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) Record(path, error);
    }
};
inline bool CheckCleanupDirectory(const std::filesystem::path& path, LabCleanupResult& result, NativeHandle* keepOpen = nullptr) {
    NativeHandle directory;
    directory.value = CreateFileW(path.c_str(), FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (directory.value == INVALID_HANDLE_VALUE) {
        auto error = GetLastError(); if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) result.Record(path, error); return false;
    }
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(directory.value, &info)) { result.Record(path, GetLastError()); return false; }
    if (!(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) { result.Record(path, ERROR_CANT_ACCESS_FILE); return false; }
    // Deny data-write/delete opens on this parent while processing its children.
    if (keepOpen) { keepOpen->value = directory.value; directory.value = INVALID_HANDLE_VALUE; }
    return true;
}
inline bool IsLabExchangeFile(const std::wstring& name) {
    if (name == L"signer.dpapi" || name == L"request-renewal.bin" || name == L"request-renewal.tmp") return true;
    if (name.size() < 72) return false;
    for (size_t i = 0; i < 64; ++i) if (!((name[i] >= L'0' && name[i] <= L'9') || (name[i] >= L'a' && name[i] <= L'f'))) return false;
    auto suffix = name.substr(64);
    return suffix == L".request" || suffix == L".request.approval" || suffix == L".request.temporary";
}
