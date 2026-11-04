#pragma once

#include <bit>
#include <cstddef>
#include <cstdint>

namespace nks::details {

constexpr std::uint32_t magic = 0x31444b4e;
constexpr std::uint16_t version = 1;
constexpr std::uint32_t maximumStringChars = 32767;
constexpr std::uint32_t maximumRequestPayload = 12 + maximumStringChars * 2;
constexpr std::uint32_t maximumResponsePayload = 4 * 1024 * 1024;
constexpr std::uint32_t maximumThreads = 16384;
constexpr std::uint32_t maximumModules = 4096;
constexpr std::uint32_t maximumMemoryRegions = 65536;
constexpr std::uint32_t maximumHandles = 65536;
constexpr std::uint32_t maximumOwners = 32768;
constexpr std::uint32_t maximumApplications = 1024;

enum class Command : std::uint16_t { threads = 1, modules = 2, signature = 3, memory = 4, handles = 5, handleInfo = 6, owners = 7, applications = 8, fileSignature = 9 };
enum class ResultState : std::uint32_t {
    ok, accessDenied, unsupported, unavailable, targetChanged, invalidRequest, limitExceeded, error
};
enum class Architecture : std::uint32_t { unknown, x86, x64, arm64 };
enum class SignatureState : std::uint32_t { valid, unsignedFile, untrusted, unavailable };
enum class SignatureSource : std::uint32_t { none, embedded, catalog };
constexpr std::uint32_t partialMetrics = 1, diskFile = 2;

#pragma pack(push, 8)
struct Request {
    std::uint32_t magicValue{magic};
    std::uint16_t protocolVersion{version};
    Command command{};
    std::uint32_t payloadBytes{}, reserved{};
    std::uint64_t requestId{}, bootLow{}, bootHigh{};
    std::uint32_t pid{}, generationKind{};
    std::uint64_t generation{};
};

struct Response {
    std::uint32_t magicValue{magic};
    std::uint16_t protocolVersion{version};
    Command command{};
    std::uint32_t payloadBytes{};
    ResultState state{ResultState::unavailable};
    std::uint64_t requestId{}, bootLow{}, bootHigh{};
    std::uint32_t pid{}, generationKind{};
    std::uint64_t generation{};
    std::uint32_t win32Error{};
    std::int32_t ntStatus{};
    Architecture architecture{};
    std::uint32_t rowCount{}, flags{}, reserved{};
};

struct Thread {
    std::uint32_t threadId{}, ownerPid{};
    std::int32_t basePriority{}, relativePriority{};
    std::uint64_t created100ns{}, kernel100ns{}, user100ns{};
    std::uint32_t timesState{}, timesError{}, priorityState{}, priorityError{};
};

struct Module {
    std::uint64_t base{}, imageSize{};
    std::uint32_t nameChars{}, pathChars{};
};

struct Signature {
    SignatureState state{SignatureState::unavailable};
    SignatureSource source{SignatureSource::none};
    std::int32_t trustCode{};
    std::uint32_t reserved{}, pathChars{}, publisherChars{};
};

struct MemoryRegion {
    std::uint64_t base{}, allocationBase{}, regionSize{};
    std::uint32_t state{}, protect{}, allocationProtect{}, type{};
};

struct HandleEntry {
    std::uint64_t value{};
    std::uint32_t grantedAccess{}, typeIndex{}, attributes{}, reserved{};
};

enum class FingerprintKind : std::uint32_t { none, process, thread, file };

// Process/Thread: id is the PID/TID and value0 its creation FILETIME. File: value0 is the volume serial, value1/2 the FILE_ID_128.
struct HandleFingerprint {
    std::uint32_t kind{}, id{};
    std::uint64_t value0{}, value1{}, value2{};
    bool operator==(const HandleFingerprint&) const = default;
};

struct HandleInfo {
    std::uint64_t value{};
    std::uint32_t typeChars{}, nameChars{}, typeState{}, nameState{};
    std::int32_t typeNtStatus{}, nameNtStatus{};
    HandleFingerprint fingerprint;
};

struct Owner {
    std::uint32_t pid{}, generationKind{};
    std::uint64_t generation{};
    std::uint32_t nameChars{}, reserved{};
};

struct Application {
    std::uint64_t window{};
    std::uint32_t pid{}, generationKind{};
    std::uint64_t generation{};
    std::uint32_t hostPid{}, hostGenerationKind{};
    std::uint64_t hostGeneration{};
    std::uint32_t titleChars{}, appIdChars{}, packageChars{}, appIdState{}, packageState{}, flags{};
};
#pragma pack(pop)

static_assert(std::endian::native == std::endian::little && sizeof(wchar_t) == 2);
static_assert(sizeof(Request) == 56 && offsetof(Request, generation) == 48);
static_assert(sizeof(Response) == 80 && offsetof(Response, win32Error) == 56 && offsetof(Response, flags) == 72);
static_assert(sizeof(Thread) == 56 && offsetof(Thread, timesState) == 40);
static_assert(sizeof(Module) == 24 && sizeof(Signature) == 24);
static_assert(sizeof(MemoryRegion) == 40 && offsetof(MemoryRegion, state) == 24);
static_assert(sizeof(HandleEntry) == 24 && sizeof(HandleInfo) == 64 && sizeof(HandleFingerprint) == 32 && sizeof(Owner) == 24);
static_assert(sizeof(Application) == 64 && offsetof(Application, titleChars) == 40);

}
