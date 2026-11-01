#include <windows.h>
#include <shellapi.h>
#include <objbase.h>
#include "collectors/process_identity.hpp"
#include "core/helper_policy.hpp"

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>
#include <vector>

namespace {
constexpr std::uint32_t magic = 0x32494b4e;
constexpr int iconSize = 18;

bool readAll(HANDLE pipe, void* data, DWORD size) {
    auto* at = static_cast<std::byte*>(data);
    while (size != 0) {
        DWORD received{};
        if (!ReadFile(pipe, at, size, &received, nullptr) || received == 0) return false;
        at += received;
        size -= received;
    }
    return true;
}

bool writeAll(HANDLE pipe, const void* data, DWORD size) {
    const auto* at = static_cast<const std::byte*>(data);
    while (size != 0) {
        DWORD sent{};
        if (!WriteFile(pipe, at, size, &sent, nullptr) || sent == 0) return false;
        at += sent;
        size -= sent;
    }
    return true;
}

std::vector<std::byte> bitmap(HICON icon) {
    HDC dc = CreateCompatibleDC(nullptr);
    if (!dc) return {};
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = iconSize;
    info.bmiHeader.biHeight = -iconSize;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    void* pixels{};
    HBITMAP image = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
    HGDIOBJ previous = image ? SelectObject(dc, image) : nullptr;
    std::vector<std::byte> bytes;
    if (previous && previous != HGDI_ERROR) {
        auto* colors = static_cast<std::uint32_t*>(pixels);
        std::fill_n(colors, iconSize * iconSize, 0xff1d252a);
        if (DrawIconEx(dc, 0, 0, icon, iconSize, iconSize, 0, nullptr, DI_NORMAL)) {
            GdiFlush();
            for (int i = 0; i < iconSize * iconSize; ++i) colors[i] |= 0xff000000;
            BITMAPFILEHEADER header{};
            header.bfType = 0x4d42;
            header.bfOffBits = sizeof(header) + sizeof(BITMAPINFOHEADER);
            header.bfSize = header.bfOffBits + iconSize * iconSize * 4;
            bytes.resize(header.bfSize);
            std::memcpy(bytes.data(), &header, sizeof(header));
            std::memcpy(bytes.data() + sizeof(header), &info.bmiHeader, sizeof(BITMAPINFOHEADER));
            std::memcpy(bytes.data() + header.bfOffBits, pixels, iconSize * iconSize * 4);
        }
        SelectObject(dc, previous);
    }
    if (image) DeleteObject(image);
    DeleteDC(dc);
    return bytes;
}

std::vector<std::byte> readIcon(const nks::ProcessIdentity& expected) {
    if (!expected.generation || expected.boot.empty()) return {};
    const std::unique_ptr<void, decltype(&CloseHandle)> process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, expected.pid), CloseHandle);
    if (!process) return {};
    const auto actual = nks::queryProcessIdentity(process.get(), expected.generationKind);
    if (actual.state != nks::SampleState::valid || actual.identity != expected) return {};
    std::array<wchar_t, 32768> path{};
    DWORD length = static_cast<DWORD>(path.size());
    if (!QueryFullProcessImageNameW(process.get(), 0, path.data(), &length) || length < 3 || path[1] != L':') return {};
    const wchar_t drive[]{path[0], L':', L'\\', L'\0'};
    if (GetDriveTypeW(drive) != DRIVE_FIXED) return {};
    SHFILEINFOW shell{};
    if (!SHGetFileInfoW(path.data(), 0, &shell, sizeof(shell), SHGFI_ICON | SHGFI_SMALLICON) || !shell.hIcon) return {};
    auto bytes = bitmap(shell.hIcon);
    DestroyIcon(shell.hIcon);
    return bytes;
}
}

int main() {
    if (!nks::initializeHelperProcess()) return 2;
    const HANDLE input = GetStdHandle(STD_INPUT_HANDLE), output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (GetFileType(input) != FILE_TYPE_PIPE || GetFileType(output) != FILE_TYPE_PIPE) return 2;
    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(initialized)) return 3;
    int result = 0;
    try {
        std::array<std::byte, 40> request{};
        while (readAll(input, request.data(), static_cast<DWORD>(request.size()))) {
            std::uint32_t version{}, kind{}, reserved{};
            nks::ProcessIdentity identity;
            std::memcpy(&version, request.data(), 4);
            std::memcpy(&identity.pid, request.data() + 4, 4);
            std::memcpy(&kind, request.data() + 8, 4);
            std::memcpy(&reserved, request.data() + 12, 4);
            std::memcpy(&identity.generation, request.data() + 16, 8);
            std::memcpy(&identity.boot.low, request.data() + 24, 8);
            std::memcpy(&identity.boot.high, request.data() + 32, 8);
            if (version != magic) { result = 4; break; }
            std::vector<std::byte> bytes;
            if (reserved == 0 && (kind == static_cast<std::uint32_t>(nks::GenerationKind::creationTime)
                || kind == static_cast<std::uint32_t>(nks::GenerationKind::sequenceNumber))) {
                identity.generationKind = static_cast<nks::GenerationKind>(kind);
                bytes = readIcon(identity);
            }
            const std::array<std::uint32_t, 2> response{magic, static_cast<std::uint32_t>(bytes.size())};
            if (!writeAll(output, response.data(), sizeof(response))
                || !writeAll(output, bytes.data(), static_cast<DWORD>(bytes.size()))) break;
        }
    } catch (const std::bad_alloc&) { result = 5; }
    CoUninitialize();
    return result;
}
