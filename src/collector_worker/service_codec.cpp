#include "service_codec.hpp"
#include <set>

namespace nks::collector {
namespace {

constexpr std::size_t maximumServices = 16384;
constexpr std::size_t maximumDependencies = 4096;
constexpr std::size_t maximumDependents = 16384;

void validateText(const std::wstring& value, bool required)
{
    if ((required && value.empty()) || value.size() > 32767) throw std::runtime_error("Invalid collector service text length");
    for (std::size_t i = 0; i < value.size(); ++i) {
        const auto unit = static_cast<std::uint16_t>(value[i]);
        if (!unit) throw std::runtime_error("Null in collector service text");
        if (unit >= 0xd800 && unit <= 0xdbff) {
            if (++i == value.size() || value[i] < 0xdc00 || value[i] > 0xdfff) throw std::runtime_error("Invalid collector service UTF-16");
        } else if (unit >= 0xdc00 && unit <= 0xdfff) throw std::runtime_error("Invalid collector service UTF-16");
    }
}

void text(Writer& out, const std::wstring& value, bool required = false)
{
    validateText(value, required); out.text(value);
}

std::wstring text(Reader& in, bool required = false)
{
    auto value = in.text(); validateText(value, required); return value;
}

void strings(Writer& out, const std::vector<std::wstring>& values, std::size_t maximum)
{
    out.count(values.size(), maximum);
    for (const auto& value : values) text(out, value, true);
}

std::vector<std::wstring> strings(Reader& in, std::size_t maximum)
{
    const auto count = in.count(maximum, 4);
    std::vector<std::wstring> values; values.reserve(count);
    for (std::uint32_t i = 0; i < count; ++i) values.push_back(text(in, true));
    return values;
}

void config(Writer& out, const ServiceConfig& value)
{
    out.number(value.error); out.number(value.startType); out.number(value.delayedError);
    out.number<std::uint32_t>(value.delayed ? 1 : 0);
    text(out, value.account); text(out, value.group); text(out, value.command);
    strings(out, value.dependencies, maximumDependencies);
}

ServiceConfig config(Reader& in)
{
    ServiceConfig value;
    value.error = in.number<std::uint32_t>(); value.startType = in.number<std::uint32_t>();
    value.delayedError = in.number<std::uint32_t>(); value.delayed = in.boolean();
    value.account = text(in); value.group = text(in); value.command = text(in);
    value.dependencies = strings(in, maximumDependencies); return value;
}

void nonnegative(Writer& out, std::int64_t value)
{
    if (value < 0) throw std::runtime_error("Invalid collector service time");
    out.signedNumber(value);
}

std::int64_t nonnegative(Reader& in)
{
    const auto value = in.signedNumber();
    if (value < 0) throw std::runtime_error("Invalid collector service time");
    return value;
}

void time(Writer& out, std::chrono::steady_clock::time_point value)
{
    nonnegative(out, std::chrono::duration_cast<std::chrono::nanoseconds>(value.time_since_epoch()).count());
}

std::chrono::steady_clock::time_point time(Reader& in)
{
    return std::chrono::steady_clock::time_point(std::chrono::duration_cast<std::chrono::steady_clock::duration>(
        std::chrono::nanoseconds(nonnegative(in))));
}

struct ServiceNameLess {
    bool operator()(const std::wstring& a, const std::wstring& b) const {
        const auto order = CompareStringOrdinal(a.data(), static_cast<int>(a.size()), b.data(), static_cast<int>(b.size()), TRUE);
        if (!order) throw std::runtime_error("Collector service name comparison failed");
        return order == CSTR_LESS_THAN;
    }
};

}

std::vector<std::byte> encode(const ServiceSnapshot& snapshot)
{
    Writer out;
    time(out, snapshot.measuredAt); nonnegative(out, snapshot.collectionTime.count());
    out.count(snapshot.services.size(), maximumServices);
    std::set<std::wstring, ServiceNameLess> names;
    for (const auto& service : snapshot.services) {
        text(out, service.name, true);
        if (!names.insert(service.name).second) throw std::runtime_error("Duplicate collector service name");
        text(out, service.title);
        out.number(service.state); out.number(service.pid); out.number(service.type); out.number(service.acceptedControls);
        config(out, service.config);
    }
    return std::move(out.bytes);
}

ServiceSnapshot decodeServices(std::span<const std::byte> payload)
{
    Reader in(payload); ServiceSnapshot snapshot;
    snapshot.measuredAt = time(in); snapshot.collectionTime = std::chrono::nanoseconds(nonnegative(in));
    const auto count = in.count(maximumServices, 56); snapshot.services.reserve(count);
    std::set<std::wstring, ServiceNameLess> names;
    for (std::uint32_t i = 0; i < count; ++i) {
        ServiceRecord service;
        service.name = text(in, true);
        if (!names.insert(service.name).second) throw std::runtime_error("Duplicate collector service name");
        service.title = text(in); service.state = in.number<std::uint32_t>(); service.pid = in.number<std::uint32_t>();
        service.type = in.number<std::uint32_t>(); service.acceptedControls = in.number<std::uint32_t>();
        service.config = config(in); snapshot.services.push_back(std::move(service));
    }
    in.finished(); return snapshot;
}

std::vector<std::byte> encode(const ServiceDetail& detail)
{
    Writer out;
    time(out, detail.measuredAt); text(out, detail.name, true); text(out, detail.description); config(out, detail.config);
    out.number(detail.statusError); out.number(detail.descriptionError); out.number(detail.dependentsError); out.number(detail.hostError);
    out.number(detail.pid); out.number(detail.state); out.number(detail.hostCreated100ns);
    strings(out, detail.dependents, maximumDependents); return std::move(out.bytes);
}

ServiceDetail decodeServiceDetail(std::span<const std::byte> payload)
{
    Reader in(payload); ServiceDetail detail;
    detail.measuredAt = time(in); detail.name = text(in, true); detail.description = text(in); detail.config = config(in);
    detail.statusError = in.number<std::uint32_t>(); detail.descriptionError = in.number<std::uint32_t>();
    detail.dependentsError = in.number<std::uint32_t>(); detail.hostError = in.number<std::uint32_t>();
    detail.pid = in.number<std::uint32_t>(); detail.state = in.number<std::uint32_t>(); detail.hostCreated100ns = in.number<std::uint64_t>();
    detail.dependents = strings(in, maximumDependents); in.finished(); return detail;
}

}
