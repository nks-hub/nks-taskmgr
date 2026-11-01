#pragma once
#include "journal.hpp"

namespace nks::update {
struct Context {
    broker::Peer gui, self;
    Image installed, worker;
    std::wstring installDir;
};
Context authenticate(const Header& header, bool staged);
bool validHeader(const Header& header, Command command, std::size_t payload);
Plan createPlan(Cache& cache, Context& context, const Hash& requested);
Plan loadPlan(Cache& cache, Context& context, const Header& request, std::span<const std::byte> payload);
void install(Cache& cache, Context& context, Plan& plan, Image& package);
Image cacheSource(Context& context, std::span<const std::byte> payload);
std::vector<std::byte> result(Status status, DWORD error, State state, const std::wstring& detail = {},
    const std::vector<Image>* packages = nullptr, const Plan* plan = nullptr, std::uint64_t commitDeadline = 0, const std::wstring& current = {}, const std::wstring& previous = {});
}
