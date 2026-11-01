#include "transport.hpp"
#include "consent.hpp"
#include "core/broker_pipe.hpp"
#include <bcrypt.h>
#include <thread>

namespace nks::broker {
int serve(const std::wstring& name, DWORD expectedClient)
{
    using namespace actions; using namespace actions::wire;
    Peer self, client, gui;
    if (!verifyPeer(GetCurrentProcessId(), L"nks-elevation-broker.exe", self) || !self.token.elevated
        || self.token.integrity < SECURITY_MANDATORY_HIGH_RID) return 2;
    auto pipe = createPipe(name, self.token);
    if (!pipe || pipe.get() == INVALID_HANDLE_VALUE || !connectPipe(pipe.get(), nullptr, GetTickCount64() + startupMs)) return 2;
    ULONG actualPid = 0;
    if (!GetNamedPipeClientProcessId(pipe.get(), &actualPid) || actualPid != expectedClient
        || !verifyPeer(actualPid, L"nks-elevation-broker.exe", client) || !sameFile(self, client)
        || !sameLogon(self.token, client.token)
        || !verifyPeer(parentPid(client.process.get()), L"NksTaskMgr.WinUI.exe", gui)
        || !sameLogon(client.token, gui.token) || gui.key.generation > client.key.generation
        || gui.key.boot != client.key.boot) return 2;
    Challenge challenge{}; write(challenge, 0, envelopeMagic, 4); write(challenge, 4, 1, 2); write(challenge, 6, challengeBytes, 2);
    keyTo(challenge, 8, self.key); write(challenge, 56, GetTickCount64() + startupMs, 8);
    if (BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(challenge.data() + 40), 16, BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) return 2;
    auto ioDeadline = GetTickCount64() + 5000;
    if (!pipeWrite(pipe.get(), challenge, client.process.get(), ioDeadline)) return 2;
    Envelope binding{}; Header header{};
    if (!pipeIo(pipe.get(), binding, false, client.process.get(), ioDeadline)
        || !pipeIo(pipe.get(), header, false, client.process.get(), ioDeadline) || !headerValid(header)
        || !validEnvelope(binding, header, client, gui, challenge)) return 2;
    std::vector<std::byte> payload(read32(header, 8)); std::vector<Result> results;
    if (!pipeIo(pipe.get(), payload, false, client.process.get(), ioDeadline) || !decodeTargets(header, payload, results)) return 2;
    const auto command = static_cast<Command>(read(header, 6, 2)); bool accepted = false;
    auto send = [&](std::span<const std::byte> bytes) { return pipeWrite(pipe.get(), bytes, client.process.get(), ioDeadline); };
    try {
        Consent consent(gui, client);
        if (!consent.prepare(command, results) || !consent.confirm(command, read64(header, 40), read32(header, 48))) {
            ioDeadline = GetTickCount64() + 1000;
            send(response(header, FrameKind::rejected, 0, consentDeclined)); return 2;
        }
        ioDeadline = GetTickCount64() + operationMs;
        if (!send(response(header, FrameKind::accepted, static_cast<std::uint32_t>(results.size())))) return 4;
        accepted = true;
        std::thread([] { Sleep(static_cast<DWORD>(operationMs)); TerminateProcess(GetCurrentProcess(), ERROR_TIMEOUT); }).detach();
        // The bridge enforces a 5 s operation deadline; every action also rechecks the held bridge and GUI.
        if (command == Command::terminateTree) {
            if (!consent.target(0)) {
                TreeResult tree; tree.root = results[0].target; tree.ordinal = 1;
                TreeNode node; node.result = results[0]; tree.nodes.push_back(std::move(node));
                return treeFrame(send, header, tree, FrameKind::treePlan) && treeFrame(send, header, tree, FrameKind::treeResults) ? 0 : 4;
            }
            bool delivered = true;
            const auto tree = terminateProcessTree(consent.context(), results[0].target, [&](const TreeResult& plan) {
                delivered = treeFrame(send, header, plan, FrameKind::treePlan); return delivered;
            }, consent.target(0));
            return delivered && treeFrame(send, header, tree, FrameKind::treeResults) ? 0 : 4;
        }
        for (std::size_t i = 0; i < results.size(); ++i) {
            if (!consent.target(i)) continue;
            if (GetTickCount64() >= ioDeadline) {
                results[i].outcome = Outcome::refused; results[i].reason = Reason::nativeUnavailable; results[i].error = ERROR_TIMEOUT; continue;
            }
            applyHeldAction(consent.context(), command, read64(header, 40), read32(header, 48), consent.target(i), results[i]);
        }
        std::vector<std::byte> bytes(results.size() * resultBytes);
        for (std::size_t i = 0; i < results.size(); ++i) rowBytes(results[i], std::span(bytes).subspan(i * resultBytes, resultBytes));
        return send(response(header, FrameKind::results, static_cast<std::uint32_t>(results.size()))) && send(bytes) ? 0 : 4;
    } catch (...) {
        send(response(header, accepted ? FrameKind::failed : FrameKind::rejected, 0, ERROR_GEN_FAILURE)); return accepted ? 3 : 2;
    }
}
}
