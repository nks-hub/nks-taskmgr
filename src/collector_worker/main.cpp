#include "snapshot_codec.hpp"
#include "core/helper_policy.hpp"
#include "service_codec.hpp"
#include "collectors/process_collector.hpp"
#include "collectors/resource_collector.hpp"
#include "collectors/service_collector.hpp"
#include <windows.h>
#include <array>
#include <algorithm>
#include <memory>
#include <string_view>

namespace {

bool transfer(HANDLE pipe, std::span<std::byte> data, bool write)
{
    std::size_t at{};
    while (at < data.size()) {
        DWORD transferred{};
        const auto size = static_cast<DWORD>((std::min)(data.size() - at, std::size_t{65536}));
        const auto ok = write ? WriteFile(pipe, data.data() + at, size, &transferred, nullptr)
            : ReadFile(pipe, data.data() + at, size, &transferred, nullptr);
        if (!ok || !transferred) return false;
        at += transferred;
    }
    return true;
}

int serve(nks::collector::Kind kind)
{
    const auto input = GetStdHandle(STD_INPUT_HANDLE), output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (GetFileType(input) != FILE_TYPE_PIPE || GetFileType(output) != FILE_TYPE_PIPE) return 2;
    // Providers are constructed lazily inside the parent's timed request.
    std::unique_ptr<nks::ProcessCollector> processes;
    std::unique_ptr<nks::ResourceCollector> resources;
    std::unique_ptr<nks::ServiceCollector> services;
    std::uint64_t previousId{};
    for (;;) {
        std::array<std::byte, nks::collector::headerBytes> bytes{};
        if (!transfer(input, bytes, false)) return 0;
        auto request = nks::collector::decodeHeader(bytes);
        const bool serviceMode = kind == nks::collector::Kind::services;
        if ((request.kind != kind && !(serviceMode && request.kind == nks::collector::Kind::serviceDetail))
            || request.status || request.requestId <= previousId) return 3;
        const bool detailRequest = request.kind == nks::collector::Kind::serviceDetail;
        if (request.payloadBytes > (detailRequest ? 65538u : 0u)) return 3;
        previousId = request.requestId;
        std::vector<std::byte> body(request.payloadBytes);
        if (!transfer(input, body, false)) return 0;
        std::vector<std::byte> payload;
        try {
            if (kind == nks::collector::Kind::processes) {
                if (!processes) processes = std::make_unique<nks::ProcessCollector>(nks::ProcessIdentityPreference::sequenceNumberWhenSupported);
                payload = nks::collector::encode(processes->collect());
            } else if (kind == nks::collector::Kind::resources) {
                if (!resources) resources = std::make_unique<nks::ResourceCollector>();
                payload = nks::collector::encode(resources->collect());
            } else {
                std::wstring name;
                if (detailRequest) {
                    nks::collector::Reader in(body); name = in.text(); in.finished();
                    if (name.empty() || name.find(L'\0') != std::wstring::npos) throw std::invalid_argument("Invalid service detail request");
                }
                if (!services) services = std::make_unique<nks::ServiceCollector>();
                payload = detailRequest ? nks::collector::encode(services->detail(name))
                    : nks::collector::encode(services->collect(std::stop_token{}));
            }
        } catch (const std::exception& error) {
            nks::collector::Writer failure;
            const std::string_view message(error.what());
            const auto size = (std::min)(message.size(), std::size_t{1024});
            failure.text(std::wstring(message.begin(), message.begin() + size));
            payload = std::move(failure.bytes); request.status = 1;
        }
        request.payloadBytes = static_cast<std::uint32_t>(payload.size());
        auto header = nks::collector::encodeHeader(request);
        if (!transfer(output, header, true) || !transfer(output, payload, true)) return 0;
    }
}

}

int wmain(int argc, wchar_t** argv)
{
    if (!nks::initializeHelperProcess()) return 2;
    if (argc != 2) return 2;
    const std::wstring_view mode(argv[1]);
    if (mode != L"--processes" && mode != L"--resources" && mode != L"--services") return 2;
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    try { return serve(mode == L"--processes" ? nks::collector::Kind::processes
        : mode == L"--resources" ? nks::collector::Kind::resources : nks::collector::Kind::services); }
    catch (...) { return 1; }
}
