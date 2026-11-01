#include <phnt_windows.h>
#include <phnt.h>
#include "process_dump.hpp"
#include <dbghelp.h>
#include <bcrypt.h>
#include <algorithm>
#include <vector>

namespace nks::dump {
namespace {
struct Capture {
    const Context& context;
    std::uint64_t limit{}, started{}, highWater{};
    DWORD error{};
    bool customIo{};
    bool canceled() {
        if (!error && GetTickCount64() - started >= 60000) error = ERROR_TIMEOUT;
        if (!error && !dumpParentAlive(context)) error = ERROR_PROCESS_ABORTED;
        return error != 0;
    }
};

BOOL CALLBACK callback(PVOID parameter, const PMINIDUMP_CALLBACK_INPUT input, PMINIDUMP_CALLBACK_OUTPUT output)
{
    if (!parameter || !input || !output) return FALSE;
    auto& capture = *static_cast<Capture*>(parameter);
    if (input->CallbackType == CancelCallback) {
        output->CheckCancel = TRUE; output->Cancel = capture.canceled(); return TRUE;
    }
    if (input->CallbackType == IoStartCallback) {
        capture.customIo = true; output->Status = S_FALSE; return TRUE;
    }
    if (input->CallbackType == IoWriteAllCallback) {
        const auto& io = input->Io;
        if (capture.canceled()) { output->Status = HRESULT_FROM_WIN32(capture.error); return FALSE; }
        if (io.Offset > capture.limit || io.BufferBytes > capture.limit - io.Offset) {
            capture.error = ERROR_FILE_TOO_LARGE; output->Status = HRESULT_FROM_WIN32(capture.error); return FALSE;
        }
        LARGE_INTEGER offset{}; offset.QuadPart = static_cast<LONGLONG>(io.Offset);
        DWORD written = 0;
        if (!SetFilePointerEx(capture.context.file.get(), offset, nullptr, FILE_BEGIN)
            || !WriteFile(capture.context.file.get(), io.Buffer, io.BufferBytes, &written, nullptr) || written != io.BufferBytes) {
            capture.error = dumpError(); output->Status = HRESULT_FROM_WIN32(capture.error); return FALSE;
        }
        capture.highWater = std::max(capture.highWater, io.Offset + io.BufferBytes); output->Status = S_OK; return TRUE;
    }
    if (input->CallbackType == IoFinishCallback) { output->Status = capture.error ? HRESULT_FROM_WIN32(capture.error) : S_OK; return TRUE; }
    if (input->CallbackType == MemoryCallback) return FALSE;
    return TRUE;
}

bool hashFile(Context& context, Capture& capture, Result& result)
{
    struct Hash {
        BCRYPT_ALG_HANDLE algorithm{};
        BCRYPT_HASH_HANDLE hash{};
        ~Hash() { if (hash) BCryptDestroyHash(hash); if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0); }
    } held;
    auto status = BCryptOpenAlgorithmProvider(&held.algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0);
    DWORD length = 0, used = 0;
    if (NT_SUCCESS(status)) status = BCryptGetProperty(held.algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&length), sizeof(length), &used, 0);
    if (!NT_SUCCESS(status) || used != sizeof(length) || !length || length > 65536) { result.error = ERROR_INVALID_DATA; return false; }
    std::vector<UCHAR> object(length);
    status = BCryptCreateHash(held.algorithm, &held.hash, object.data(), length, nullptr, 0, 0);
    if (!NT_SUCCESS(status)) { result.error = RtlNtStatusToDosError(status); return false; }
    LARGE_INTEGER offset{};
    if (!SetFilePointerEx(context.file.get(), offset, nullptr, FILE_BEGIN)) { result.error = dumpError(); return false; }
    std::array<UCHAR, 65536> bytes{};
    std::uint64_t total = 0;
    while (total < result.fileBytes) {
        if (capture.canceled()) { result.error = capture.error; return false; }
        const auto wanted = static_cast<DWORD>(std::min<std::uint64_t>(bytes.size(), result.fileBytes - total));
        DWORD read = 0;
        if (!ReadFile(context.file.get(), bytes.data(), wanted, &read, nullptr) || read != wanted) { result.error = dumpError(); return false; }
        status = BCryptHashData(held.hash, bytes.data(), read, 0);
        if (!NT_SUCCESS(status)) { result.error = RtlNtStatusToDosError(status); return false; }
        total += read;
    }
    status = BCryptFinishHash(held.hash, reinterpret_cast<PUCHAR>(result.sha256.data()), static_cast<ULONG>(result.sha256.size()), 0);
    if (!NT_SUCCESS(status)) { result.error = RtlNtStatusToDosError(status); return false; }
    return true;
}
}

Result captureDump(const Request& request)
{
    Result result;
    Context context;
    if (!prepareDump(request, context, result)) return result;
    Capture capture{context, request.limitBytes, GetTickCount64()};
    result.captureFlags = request.mode == Mode::full ? MiniDumpWithFullMemory | MiniDumpWithFullMemoryInfo | MiniDumpWithThreadInfo : MiniDumpNormal;
    MINIDUMP_CALLBACK_INFORMATION callbacks{callback, &capture};
    if (!dumpParentAlive(context)) { result.state = State::denied; result.error = ERROR_PROCESS_ABORTED; return result; }
    if (!verifyDumpProcess(request, context.process.get(), result)) return result;
    if (!MiniDumpWriteDump(context.process.get(), request.key.pid, context.file.get(), static_cast<MINIDUMP_TYPE>(result.captureFlags), nullptr, nullptr, &callbacks)) {
        result.error = capture.error ? capture.error : dumpError();
        result.state = result.error == ERROR_FILE_TOO_LARGE ? State::limitExceeded
            : result.error == ERROR_TIMEOUT || result.error == ERROR_PROCESS_ABORTED ? State::canceled : State::error;
        return result;
    }
    if (!capture.customIo || capture.canceled()) { result.state = State::canceled; result.error = capture.error ? capture.error : ERROR_NOT_SUPPORTED; return result; }
    if (!verifyDumpProcess(request, context.process.get(), result)) return result;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(context.file.get(), &size) || !FlushFileBuffers(context.file.get())) { result.error = dumpError(); return result; }
    if (size.QuadPart <= 0 || static_cast<std::uint64_t>(size.QuadPart) > request.limitBytes || static_cast<std::uint64_t>(size.QuadPart) != capture.highWater) {
        result.state = State::limitExceeded; result.error = ERROR_FILE_TOO_LARGE; return result;
    }
    result.fileBytes = static_cast<std::uint64_t>(size.QuadPart);
    if (!hashFile(context, capture, result)) { result.state = capture.error ? State::canceled : State::error; return result; }
    if (!verifyDumpProcess(request, context.process.get(), result) || !dumpParentAlive(context)) {
        result.state = State::targetChanged; result.error = ERROR_PROCESS_ABORTED; return result;
    }
    result.state = State::captured; result.error = 0; return result;
}
}
