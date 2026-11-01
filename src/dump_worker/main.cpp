#include "protocol.hpp"
#include "collectors/process_dump.hpp"
#include <windows.h>
#include <algorithm>
#include <string_view>
#include <vector>

namespace {
using namespace nks::dump;
bool readAll(HANDLE pipe, std::span<std::byte> bytes)
{
    while (!bytes.empty()) {
        DWORD count = 0;
        if (!ReadFile(pipe, bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr) || !count) return false;
        bytes = bytes.subspan(count);
    }
    return true;
}
bool writeAll(HANDLE pipe, std::span<const std::byte> bytes)
{
    while (!bytes.empty()) {
        DWORD count = 0;
        if (!WriteFile(pipe, bytes.data(), static_cast<DWORD>(bytes.size()), &count, nullptr) || !count) return false;
        bytes = bytes.subspan(count);
    }
    return true;
}
std::wstring text(std::span<const std::byte> bytes, std::size_t at, std::size_t chars)
{
    std::wstring value(chars, L'\0');
    for (std::size_t i = 0; i < chars; ++i) {
        const auto c = static_cast<wchar_t>(read(bytes, at + i * 2, 2));
        if (!c || (c >= 0xdc00 && c <= 0xdfff && (i == 0 || value[i - 1] < 0xd800 || value[i - 1] > 0xdbff))
            || (c >= 0xd800 && c <= 0xdbff && (i + 1 == chars || read(bytes, at + (i + 1) * 2, 2) < 0xdc00 || read(bytes, at + (i + 1) * 2, 2) > 0xdfff)))
            throw std::invalid_argument("Invalid dump path");
        value[i] = c;
    }
    return value;
}
bool validPath(const std::wstring& path)
{
    if (path.size() < 8 || path.substr(0, 4) != L"\\\\?\\" || path[5] != L':' || path[6] != L'\\'
        || !((path[4] >= L'A' && path[4] <= L'Z') || (path[4] >= L'a' && path[4] <= L'z'))) return false;
    std::size_t start = 7;
    for (std::size_t i = start; i <= path.size(); ++i) {
        if (i != path.size() && path[i] != L'\\') {
            if (path[i] < L' ' || std::wstring_view(L":/*?\"<>|").find(path[i]) != std::wstring_view::npos) return false;
            continue;
        }
        if (i == start || path[i - 1] == L'.' || path[i - 1] == L' ') return false;
        start = i + 1;
    }
    return true;
}
std::array<std::byte, responseBytes> response(std::span<const std::byte> request, const Result& result)
{
    std::array<std::byte, responseBytes> bytes{};
    std::copy_n(request.begin(), 8, bytes.begin());
    write(bytes, 12, static_cast<std::uint32_t>(result.state), 4);
    std::copy_n(request.begin() + 16, 64, bytes.begin() + 16);
    write(bytes, 80, result.error, 4); write(bytes, 84, result.captureFlags, 4); write(bytes, 88, static_cast<std::uint32_t>(result.architecture), 4);
    write(bytes, 96, result.fileBytes, 8); write(bytes, 104, result.observedCreated100ns, 8);
    std::copy(result.sha256.begin(), result.sha256.end(), bytes.begin() + 112);
    write(bytes, 144, result.fileId.VolumeSerialNumber, 8);
    std::copy_n(reinterpret_cast<const std::byte*>(result.fileId.FileId.Identifier), 16, bytes.begin() + 152);
    return bytes;
}
}

int main(int argc, char** argv)
{
    if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32) || argc != 2 || std::string_view(argv[1]) != "--stdio") return 2;
    const auto input = GetStdHandle(STD_INPUT_HANDLE), output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (GetFileType(input) != FILE_TYPE_PIPE || GetFileType(output) != FILE_TYPE_PIPE || input == output) return 2;
    std::array<std::byte, requestBytes> header{};
    if (!readAll(input, header)) return 2;
    try {
        const auto finalChars = read32(header, 12), tempChars = read32(header, 96);
        if (read32(header, 0) != magic || read(header, 4, 2) != version || read(header, 6, 2) > 1
            || !read64(header, 16) || (!read64(header, 24) && !read64(header, 32))
            || (!read64(header, 40) && !read64(header, 48)) || !read32(header, 56) || read32(header, 60) < 1 || read32(header, 60) > 2
            || !read64(header, 64) || !read64(header, 72) || !read64(header, 80) || read64(header, 88) != maximumFileBytes
            || !read32(header, 100) || read64(header, 104) || !finalChars || !tempChars || finalChars > maximumPathChars || tempChars > maximumPathChars
            || read32(header, 8) != (finalChars + tempChars) * 2) return 2;
        std::vector<std::byte> payload(read32(header, 8));
        if (!readAll(input, payload)) return 2;
        BYTE extra{}; DWORD count = 0;
        const auto ended = ReadFile(input, &extra, 1, &count, nullptr);
        if (count || (!ended && GetLastError() != ERROR_BROKEN_PIPE && GetLastError() != ERROR_HANDLE_EOF)) return 2;
        Request request{static_cast<Mode>(read(header, 6, 2)), {{read64(header, 40), read64(header, 48)}, read32(header, 56),
            static_cast<nks::GenerationKind>(read32(header, 60)), read64(header, 64)}, read64(header, 72), read64(header, 80),
            read64(header, 88), read32(header, 100), text(payload, 0, finalChars), text(payload, finalChars * 2, tempChars)};
        const auto finalSlash = request.destination.find_last_of(L'\\'), tempSlash = request.temporary.find_last_of(L'\\');
        if (!validPath(request.destination) || !validPath(request.temporary) || finalSlash != tempSlash
            || CompareStringOrdinal(request.destination.data(), static_cast<int>(finalSlash), request.temporary.data(), static_cast<int>(tempSlash), TRUE) != CSTR_EQUAL
            || request.temporary.substr(tempSlash + 1, 10) != L".nks-dump-" || request.temporary == request.destination) return 2;
        const auto result = captureDump(request);
        return writeAll(output, response(header, result)) ? 0 : 4;
    } catch (const std::bad_alloc&) {
        Result result; result.error = ERROR_NOT_ENOUGH_MEMORY; return writeAll(output, response(header, result)) ? 0 : 4;
    } catch (...) {
        Result result; result.state = State::invalidRequest; result.error = ERROR_INVALID_DATA; return writeAll(output, response(header, result)) ? 0 : 4;
    }
}
