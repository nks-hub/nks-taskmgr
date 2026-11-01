#include "protocol.hpp"
#include "collectors/service_actions.hpp"
#include "core/helper_policy.hpp"
#include <algorithm>
#include <string_view>

namespace {
using namespace nks::service_actions;
bool transfer(HANDLE pipe, void* address, DWORD size, bool write) {
    auto* at = static_cast<std::byte*>(address);
    while (size) {
        DWORD used{};
        const auto count = (std::min)(size, DWORD{65536});
        if (!(write ? WriteFile(pipe, at, count, &used, nullptr) : ReadFile(pipe, at, count, &used, nullptr)) || !used) return false;
        at += used; size -= used;
    }
    return true;
}
bool headerValid(const Header& header) {
    return header.magicValue == magic && header.version == 1 && header.requestId && !header.reserved
        && header.phase == Phase::request && header.status == Status::ok && !header.error
        && header.action >= Action::start && header.action <= Action::restart && header.payloadBytes <= maximumPayload;
}
void send(HANDLE output, Header header, Phase phase, Status status, DWORD error, std::uint32_t count, Writer payload = {}) {
    header.phase = phase; header.status = status; header.error = error; header.count = count;
    header.payloadBytes = static_cast<std::uint32_t>(payload.bytes.size());
    if (!transfer(output, &header, sizeof(header), true) || !transfer(output, payload.bytes.data(), header.payloadBytes, true))
        throw Failure(Status::error, ERROR_BROKEN_PIPE, "Service action parent disconnected");
}
Writer planPayload(const Plan& plan) {
    Writer output;
    for (std::uint32_t id = 0; id < plan.nodes.size(); ++id) {
        const auto& node = plan.nodes[id]; const auto& config = node.config;
        PlanRow row{id, node.flags, node.original.dwCurrentState, plan.action == Action::stop ? std::uint32_t{SERVICE_STOPPED} : std::uint32_t{SERVICE_RUNNING},
            node.stopOrder, node.startOrder, static_cast<std::uint32_t>(node.name.size()), static_cast<std::uint32_t>(config.title.size()),
            static_cast<std::uint32_t>(config.command.size()), static_cast<std::uint32_t>(config.account.size()),
            static_cast<std::uint32_t>(config.dependencies.size()), config.startType, config.type, 0};
        output.append(row); output.text(node.name, false); output.text(config.title, false); output.text(config.command, false); output.text(config.account, false);
        for (const auto& dependency : config.dependencies) output.text(dependency);
    }
    return output;
}
Writer results(std::span<const ResultRow> rows) { Writer output; for (const auto& row : rows) output.append(row); return output; }
int serve() {
    const auto input = GetStdHandle(STD_INPUT_HANDLE), output = GetStdHandle(STD_OUTPUT_HANDLE);
    if (GetFileType(input) != FILE_TYPE_PIPE || GetFileType(output) != FILE_TYPE_PIPE) return 2;
    Header request{};
    if (!transfer(input, &request, sizeof(request), false) || !headerValid(request) || request.command != Command::plan
        || request.count || request.nonceLow || request.nonceHigh || request.payloadBytes > 65538) return 3;
    std::vector<std::byte> bytes(request.payloadBytes);
    if (!transfer(input, bytes.data(), request.payloadBytes, false)) return 3;
    try {
        Reader reader(bytes); auto name = reader.text(); reader.finished();
        auto plan = makePlan(request.action, name);
        request.nonceLow = plan.nonceLow; request.nonceHigh = plan.nonceHigh;
        send(output, request, Phase::plan, Status::ok, 0, static_cast<std::uint32_t>(plan.nodes.size()), planPayload(plan));
        Header apply{};
        if (!transfer(input, &apply, sizeof(apply), false) || !headerValid(apply) || apply.requestId <= request.requestId
            || apply.action != request.action || apply.nonceLow != plan.nonceLow || apply.nonceHigh != plan.nonceHigh
            || (apply.command != Command::apply && apply.command != Command::cancel) || apply.count > maximumServices
            || apply.payloadBytes > 4 + maximumServices * 4) return 3;
        bytes.resize(apply.payloadBytes);
        if (!transfer(input, bytes.data(), apply.payloadBytes, false)) return 3;
        // EOF commits this single immutable request; no pipelined retry is accepted.
        BYTE extra{}; DWORD received{};
        if (ReadFile(input, &extra, 1, &received, nullptr)) { if (received) return 3; }
        else if (GetLastError() != ERROR_BROKEN_PIPE && GetLastError() != ERROR_HANDLE_EOF) return 3;
        request = apply;
        if (apply.command == Command::cancel) {
            if (apply.payloadBytes || apply.count) return 3;
            std::vector<ResultRow> rows(plan.nodes.size());
            for (std::uint32_t i = 0; i < rows.size(); ++i) { rows[i].id = i; rows[i].outcome = Outcome::cancelled; }
            send(output, apply, Phase::result, Status::cancelled, ERROR_CANCELLED, static_cast<std::uint32_t>(rows.size()), results(rows)); return 0;
        }
        Reader approval(bytes); const auto count = approval.value<std::uint32_t>();
        if (count != apply.count || count > maximumServices) return 3;
        std::vector<std::uint32_t> approved; approved.reserve(count);
        for (std::uint32_t i = 0; i < count; ++i) approved.push_back(approval.value<std::uint32_t>());
        approval.finished(); bool accepted = false;
        auto result = applyPlan(plan, approved, [&](const auto& rows) {
            if (!accepted) { send(output, apply, Phase::accepted, Status::ok, 0, static_cast<std::uint32_t>(rows.size())); accepted = true; }
            send(output, apply, Phase::progress, Status::ok, 0, static_cast<std::uint32_t>(rows.size()), results(rows));
        });
        send(output, apply, Phase::result, result.status, result.error, static_cast<std::uint32_t>(result.rows.size()), results(result.rows));
        return 0;
    } catch (const Failure& failure) { send(output, request, Phase::error, failure.status, failure.error, 0); }
    catch (const std::bad_alloc&) { send(output, request, Phase::error, Status::error, ERROR_NOT_ENOUGH_MEMORY, 0); }
    catch (const std::length_error&) { send(output, request, Phase::error, Status::limitExceeded, ERROR_MORE_DATA, 0); }
    catch (const std::exception&) { send(output, request, Phase::error, Status::invalidRequest, ERROR_INVALID_DATA, 0); }
    return 0;
}
}
int main(int argc, char** argv) {
    if (argc != 2 || std::string_view(argv[1]) != "--stdio" || !nks::initializeHelperProcess()) return 2;
    try { return serve(); } catch (...) { return 4; }
}
