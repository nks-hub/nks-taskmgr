#include "protocol.hpp"
#include "collectors/process_actions.hpp"
#include "collectors/process_tree_actions.hpp"
#include "collectors/process_actions_wire.hpp"
#include "core/helper_policy.hpp"
#include <windows.h>
#include <array>
#include <new>
#include <string_view>

namespace {
using namespace nks::actions;
using Header = std::array<std::byte, headerBytes>;

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
using namespace nks::actions::wire;
}

int main(int argc, char** argv)
{
    if (!nks::initializeHelperProcess()) return 2;
    if (argc != 2 || std::string_view(argv[1]) != "--stdio") return 2;
    const HANDLE input = GetStdHandle(STD_INPUT_HANDLE), output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (GetFileType(input) != FILE_TYPE_PIPE || GetFileType(output) != FILE_TYPE_PIPE || input == output) return 2;
    Header header{};
    if (!readAll(input, header)) return 2;
    bool accepted = false;
    try {
        if (!headerValid(header)) { writeAll(output, response(header, FrameKind::rejected, 0, ERROR_INVALID_PARAMETER)); return 2; }
        const auto count = read32(header, 12); const auto command = static_cast<Command>(read(header, 6, 2));
        // Only the elevated broker changes the system Task Manager registration.
        if (command == Command::taskManager) { writeAll(output, response(header, FrameKind::rejected, 0, ERROR_NOT_SUPPORTED)); return 2; }
        std::vector<std::byte> payload(read32(header, 8));
        if (!readAll(input, payload)) return 2;
        BYTE extra{}; DWORD received = 0;
        const bool ended = ReadFile(input, &extra, 1, &received, nullptr) != FALSE;
        if (received || (!ended && GetLastError() != ERROR_BROKEN_PIPE && GetLastError() != ERROR_HANDLE_EOF)) return 2;
        std::array<Result, maximumTargets> results{};
        for (std::uint32_t i = 0; i < count; ++i) {
            auto row = std::span<const std::byte>(payload).subspan(i * targetBytes, targetBytes);
            const auto kind = read32(row, 20);
            auto& target = results[i].target;
            target.key = {{read64(row, 0), read64(row, 8)}, read32(row, 16), static_cast<nks::GenerationKind>(kind), read64(row, 24)};
            target.created100ns = read64(row, 32); target.window = read64(row, 40);
            bool duplicate = false;
            for (std::uint32_t before = 0; before < i; ++before) duplicate |= results[before].target.key.pid == target.key.pid;
            if (target.key.boot.empty() || kind < 1 || kind > 2 || !target.key.generation || !target.created100ns || duplicate
                || (command != Command::closeWindows && target.window) || target.window == 0xffff || target.window == UINT64_MAX) {
                writeAll(output, response(header, FrameKind::rejected, 0, ERROR_INVALID_PARAMETER)); return 2;
            }
        }
        CpuSetsHash baseline{}; std::vector<std::uint32_t> selected;
        const bool cpuSets = command == Command::queryCpuSets || command == Command::cpuSets;
        if (cpuSets && !cpuSetsRequest(header, payload, baseline, selected)) {
            writeAll(output, response(header, FrameKind::rejected, 0, ERROR_INVALID_PARAMETER)); return 2;
        }
        if (!writeAll(output, response(header, FrameKind::accepted, count))) return 4;
        accepted = true;
        const auto context = actionContext();
        if (cpuSets) {
            CpuSetsResult result; result.result = results[0];
            processCpuSets(context, command, baseline, selected, result);
            return cpuSetsFrame([&](std::span<const std::byte> bytes) { return writeAll(output, bytes); }, header, result) ? 0 : 4;
        }
        if (command == Command::terminateTree) {
            bool delivered = true;
            const auto tree = terminateProcessTree(context, results[0].target, [&](const TreeResult& plan) {
                delivered = treeFrame([&](std::span<const std::byte> bytes) { return writeAll(output, bytes); }, header, plan, FrameKind::treePlan); return delivered;
            });
            return delivered && treeFrame([&](std::span<const std::byte> bytes) { return writeAll(output, bytes); }, header, tree, FrameKind::treeResults) ? 0 : 4;
        }
        for (std::uint32_t i = 0; i < count; ++i) {
            try { applyAction(context, command, read64(header, 40), read32(header, 48), results[i]); }
            catch (...) {
                auto& result = results[i]; result.outcome = result.flags & mayHaveApplied ? Outcome::unverified : Outcome::error;
                result.flags &= ~static_cast<std::uint32_t>(effectConfirmed);
                result.reason = Reason::nativeUnavailable; result.error = ERROR_GEN_FAILURE;
            }
        }
        std::array<std::byte, maximumTargets * resultBytes> bytes{};
        for (std::uint32_t i = 0; i < count; ++i) rowBytes(results[i], std::span(bytes).subspan(i * resultBytes, resultBytes));
        return writeAll(output, response(header, FrameKind::results, count)) && writeAll(output, std::span(bytes).first(count * resultBytes)) ? 0 : 4;
    } catch (const std::bad_alloc&) { writeAll(output, response(header, FrameKind::failed, 0, ERROR_NOT_ENOUGH_MEMORY)); return 3; }
    catch (...) { writeAll(output, response(header, accepted ? FrameKind::failed : FrameKind::rejected, 0, ERROR_INVALID_DATA)); return accepted ? 3 : 2; }
}
