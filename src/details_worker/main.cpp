#include "protocol.hpp"
#include "collectors/process_details.hpp"
#include <windows.h>
#include <cstring>
#include <limits>
#include <new>
#include <stdexcept>
#include <string_view>
#include <type_traits>
#include <vector>

namespace {

bool readAll(HANDLE pipe, void* destination, DWORD size, bool* ended = nullptr)
{
    auto* bytes = static_cast<std::byte*>(destination);
    const auto initialSize = size;
    while (size) {
        DWORD received{};
        if (!ReadFile(pipe, bytes, size, &received, nullptr)) {
            const auto error = GetLastError();
            if (ended && size == initialSize && (error == ERROR_BROKEN_PIPE || error == ERROR_HANDLE_EOF)) *ended = true;
            return false;
        }
        if (!received) { if (ended && size == initialSize) *ended = true; return false; }
        bytes += received; size -= received;
    }
    return true;
}

bool writeAll(HANDLE pipe, const void* source, DWORD size)
{
    const auto* bytes = static_cast<const std::byte*>(source);
    while (size) {
        DWORD written{};
        if (!WriteFile(pipe, bytes, size, &written, nullptr) || !written) return false;
        bytes += written; size -= written;
    }
    return true;
}

bool validText(std::wstring_view text)
{
    if (text.size() > nks::details::maximumStringChars) return false;
    for (std::size_t i = 0; i < text.size(); ++i) {
        const auto unit = static_cast<std::uint16_t>(text[i]);
        if (!unit) return false;
        if (unit >= 0xd800 && unit <= 0xdbff) {
            if (++i == text.size() || text[i] < 0xdc00 || text[i] > 0xdfff) return false;
        } else if (unit >= 0xdc00 && unit <= 0xdfff) return false;
    }
    return true;
}

bool requestData(const nks::details::Request& header, const std::vector<std::byte>& payload, nks::ProcessDetailsRequest& request)
{
    if (header.reserved || header.generationKind < 1 || header.generationKind > 2
        || (!header.bootLow && !header.bootHigh)) return false;
    request.target = {{header.bootLow, header.bootHigh}, header.pid, static_cast<nks::GenerationKind>(header.generationKind), header.generation};
    request.command = header.command;
    if (header.command == nks::details::Command::owners) return !header.pid && !header.generation && payload.empty();
    if (!header.generation) return false;
    if (header.command == nks::details::Command::threads || header.command == nks::details::Command::modules
        || header.command == nks::details::Command::memory || header.command == nks::details::Command::handles) return payload.empty();
    if (header.command == nks::details::Command::handleInfo) {
        if (payload.size() != sizeof(request.handleValue)) return false;
        std::memcpy(&request.handleValue, payload.data(), sizeof(request.handleValue));
        return request.handleValue != 0;
    }
    if (header.command != nks::details::Command::signature || payload.size() < 12) return false;
    std::uint32_t chars{};
    std::memcpy(&request.moduleBase, payload.data(), 8); std::memcpy(&chars, payload.data() + 8, 4);
    if (!request.moduleBase || !chars || chars > nks::details::maximumStringChars || payload.size() != 12ull + chars * 2ull) return false;
    request.modulePath.resize(chars); std::memcpy(request.modulePath.data(), payload.data() + 12, chars * sizeof(wchar_t));
    return validText(request.modulePath);
}

class Payload {
public:
    template<class T> void append(const T& value) {
        static_assert(std::is_trivially_copyable_v<T>);
        appendBytes(&value, sizeof(value));
    }
    void text(std::wstring_view value) {
        if (!validText(value)) throw std::invalid_argument("Invalid UTF-16 response");
        appendBytes(value.data(), value.size() * sizeof(wchar_t));
    }
    std::vector<std::byte> bytes;
private:
    void appendBytes(const void* source, std::size_t count) {
        if (count > nks::details::maximumResponsePayload - bytes.size()) throw std::length_error("Details response exceeds limit");
        if (!count) return;
        if (!bytes.capacity()) bytes.reserve(nks::details::maximumResponsePayload);
        const auto* at = static_cast<const std::byte*>(source);
        bytes.insert(bytes.end(), at, at + count);
    }
};

void responseData(nks::details::Response& header, const nks::ProcessDetailsResult& result, Payload& payload)
{
    header.state = result.state; header.win32Error = result.win32Error; header.ntStatus = result.ntStatus;
    header.architecture = result.architecture;
    header.flags = header.command == nks::details::Command::signature ? nks::details::diskFile : 0;
    if (result.state != nks::details::ResultState::ok) return;
    header.flags |= result.flags;
    if (header.command == nks::details::Command::threads) {
        if (result.threads.empty() || result.threads.size() > nks::details::maximumThreads) throw std::length_error("Invalid thread count");
        header.rowCount = static_cast<std::uint32_t>(result.threads.size());
        for (const auto& thread : result.threads) payload.append(thread);
    } else if (header.command == nks::details::Command::modules) {
        if (result.modules.empty() || result.modules.size() > nks::details::maximumModules) throw std::length_error("Invalid module count");
        header.rowCount = static_cast<std::uint32_t>(result.modules.size());
        for (const auto& module : result.modules) {
            nks::details::Module row{module.base, module.imageSize, static_cast<std::uint32_t>(module.name.size()), static_cast<std::uint32_t>(module.path.size())};
            payload.append(row); payload.text(module.name); payload.text(module.path);
        }
    } else if (header.command == nks::details::Command::signature && result.signature) {
        const auto& signature = *result.signature;
        nks::details::Signature row{signature.state, signature.source, signature.trustCode, 0,
            static_cast<std::uint32_t>(signature.path.size()), static_cast<std::uint32_t>(signature.publisher.size())};
        header.rowCount = 1; payload.append(row); payload.text(signature.path); payload.text(signature.publisher);
    } else if (header.command == nks::details::Command::memory) {
        if (result.memory.empty() || result.memory.size() > nks::details::maximumMemoryRegions) throw std::length_error("Invalid memory region count");
        header.rowCount = static_cast<std::uint32_t>(result.memory.size());
        for (const auto& region : result.memory) payload.append(region);
    } else if (header.command == nks::details::Command::handles) {
        if (result.handles.size() > nks::details::maximumHandles) throw std::length_error("Invalid handle count");
        header.rowCount = static_cast<std::uint32_t>(result.handles.size());
        for (const auto& handle : result.handles) payload.append(handle);
    } else if (header.command == nks::details::Command::handleInfo && result.handleInfo) {
        header.rowCount = 1;
        payload.append(result.handleInfo->info); payload.text(result.handleInfo->type); payload.text(result.handleInfo->name);
    } else if (header.command == nks::details::Command::owners) {
        if (result.owners.empty() || result.owners.size() > nks::details::maximumOwners) throw std::length_error("Invalid owner count");
        header.rowCount = static_cast<std::uint32_t>(result.owners.size());
        for (const auto& owner : result.owners) {
            nks::details::Owner row{owner.identity.pid, static_cast<std::uint32_t>(owner.identity.generationKind), owner.identity.generation,
                static_cast<std::uint32_t>(owner.name.size()), 0};
            payload.append(row); payload.text(owner.name);
        }
    } else throw std::invalid_argument("Missing signature result");
    header.payloadBytes = static_cast<std::uint32_t>(payload.bytes.size());
}

int serveRequest(HANDLE input, HANDLE output, bool server)
{
    nks::details::Request request{};
    bool ended = false;
    if (!readAll(input, &request, sizeof(request), &ended)) return ended && server ? 1 : 3;
    if (request.magicValue != nks::details::magic
        || request.protocolVersion != nks::details::version || request.payloadBytes > nks::details::maximumRequestPayload) return 3;
    nks::details::Response response{};
    response.command = request.command; response.requestId = request.requestId;
    response.bootLow = request.bootLow; response.bootHigh = request.bootHigh;
    response.pid = request.pid; response.generationKind = request.generationKind; response.generation = request.generation;
    std::vector<std::byte> bytes;
    try {
        bytes.resize(request.payloadBytes);
        if (!readAll(input, bytes.data(), request.payloadBytes)) return 3;
        if (!server) {
            BYTE extra{}; DWORD received{};
            if (ReadFile(input, &extra, 1, &received, nullptr)) { if (received) return 3; }
            else if (const auto error = GetLastError(); error != ERROR_BROKEN_PIPE && error != ERROR_HANDLE_EOF) return 3;
        }
        nks::ProcessDetailsRequest detail;
        if (!requestData(request, bytes, detail)) response.state = nks::details::ResultState::invalidRequest;
        else {
            const auto result = nks::readProcessDetails(detail);
            Payload payload; responseData(response, result, payload); bytes = std::move(payload.bytes);
        }
    } catch (const std::bad_alloc&) { response.state = nks::details::ResultState::error; response.win32Error = ERROR_NOT_ENOUGH_MEMORY; }
    catch (const std::length_error&) { response.state = nks::details::ResultState::limitExceeded; response.win32Error = ERROR_INSUFFICIENT_BUFFER; }
    catch (const std::invalid_argument&) { response.state = nks::details::ResultState::error; response.win32Error = ERROR_NO_UNICODE_TRANSLATION; }
    if (response.state != nks::details::ResultState::ok) {
        response.payloadBytes = response.rowCount = 0; bytes.clear();
        response.flags = response.command == nks::details::Command::signature ? nks::details::diskFile : 0;
    }
    return writeAll(output, &response, sizeof(response)) && writeAll(output, bytes.data(), response.payloadBytes) ? 0 : 4;
}

}

int main(int argc, char** argv)
{
    if (argc != 2) return 2;
    const bool server = std::string_view(argv[1]) == "--stdio-server";
    if (!server && std::string_view(argv[1]) != "--stdio") return 2;
    const HANDLE input = GetStdHandle(STD_INPUT_HANDLE), output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (GetFileType(input) != FILE_TYPE_PIPE || GetFileType(output) != FILE_TYPE_PIPE) return 2;
    do {
        const auto status = serveRequest(input, output, server);
        if (status) return status == 1 ? 0 : status;
    } while (server);
    return 0;
}
