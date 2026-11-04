#include "collector_worker/wire.hpp"
#include <algorithm>
#include <iostream>

namespace {

using nks::collector::Reader;
using nks::collector::Writer;

void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }

template<class Error, class Action> void rejects(Action action, const char* message)
{
    try { action(); } catch (const Error&) { return; }
    throw std::runtime_error(message);
}

std::vector<std::byte> octets(std::initializer_list<unsigned char> values)
{
    std::vector<std::byte> result;
    for (auto value : values) result.push_back(static_cast<std::byte>(value));
    return result;
}

std::vector<std::byte> primitivePacket()
{
    return octets({0xa5, 0x34, 0x12, 0xef, 0xcd, 0xab, 0x89,
        0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
        0xfe, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xf8, 0x3f});
}

void readPrimitives(Reader& in)
{
    require(in.number<std::uint8_t>() == 0xa5, "Byte value changed");
    require(in.number<std::uint16_t>() == 0x1234, "Unaligned 16-bit value changed");
    require(in.number<std::uint32_t>() == 0x89abcdefu, "Unaligned 32-bit value changed");
    require(in.number<std::uint64_t>() == 0x0102030405060708ull, "Unaligned 64-bit value changed");
    require(in.signedNumber() == -2, "Signed value changed");
    require(in.real() == 1.5, "Double value changed");
}

void primitiveBytes()
{
    Writer out;
    out.number<std::uint8_t>(0xa5); out.number<std::uint16_t>(0x1234);
    out.number<std::uint32_t>(0x89abcdefu); out.number<std::uint64_t>(0x0102030405060708ull);
    out.signedNumber(-2); out.real(1.5);
    const auto expected = primitivePacket();
    require(out.bytes == expected, "Primitive packet is not the fixed little-endian format");
    Reader in(expected); readPrimitives(in); in.finished();
    for (std::size_t size = 0; size < expected.size(); ++size) {
        rejects<std::runtime_error>([&] {
            Reader shortPacket(std::span<const std::byte>(expected).first(size));
            readPrimitives(shortPacket);
        }, "A truncated primitive packet was accepted");
    }
    auto trailing = expected; trailing.push_back(std::byte{0});
    rejects<std::runtime_error>([&] { Reader extra(trailing); readPrimitives(extra); extra.finished(); }, "Trailing primitive data was accepted");
}

void primitiveEdges()
{
    Writer out;
    out.number(std::numeric_limits<std::uint64_t>::max());
    out.signedNumber(std::numeric_limits<std::int64_t>::min()); out.real(-0.0);
    const auto expected = octets({0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
        0, 0, 0, 0, 0, 0, 0, 0x80, 0, 0, 0, 0, 0, 0, 0, 0x80});
    require(out.bytes == expected, "Integer limits or negative zero changed bits");
    Reader in(expected);
    require(in.number<std::uint64_t>() == std::numeric_limits<std::uint64_t>::max(), "Maximum unsigned value changed");
    require(in.signedNumber() == std::numeric_limits<std::int64_t>::min(), "Minimum signed value changed");
    const auto zero = in.real(); require(zero == 0 && std::signbit(zero), "Negative zero lost its sign"); in.finished();
    const auto noncanonicalBool = octets({0x80});
    Reader booleanNumber(noncanonicalBool);
    require(booleanNumber.number<bool>(), "The unsigned bool template changed its nonzero conversion"); booleanNumber.finished();
    const auto qualified = octets({0x78, 0x56, 0x34, 0x12});
    Reader qualifiedNumber(qualified);
    require(qualifiedNumber.number<const std::uint32_t>() == 0x12345678u, "CV-qualified unsigned conversion changed"); qualifiedNumber.finished();
}

void textBytes()
{
    const std::wstring text{L'A', L'\0', static_cast<wchar_t>(0xd83d), static_cast<wchar_t>(0xde80),
        static_cast<wchar_t>(0xd800), L'B', static_cast<wchar_t>(0xdc00), static_cast<wchar_t>(0xffff)};
    const auto expected = octets({8, 0, 0, 0, 0x41, 0, 0, 0, 0x3d, 0xd8, 0x80, 0xde, 0, 0xd8, 0x42, 0, 0, 0xdc, 0xff, 0xff});
    Writer out; out.text(text);
    require(out.bytes == expected, "UTF-16 packet changed embedded NUL or surrogate code units");
    Reader in(expected); require(in.text() == text, "UTF-16 code units did not round trip"); in.finished();
    Writer empty; empty.text(L"");
    const auto emptyPacket = octets({0, 0, 0, 0});
    require(empty.bytes == emptyPacket, "Empty text has a different prefix");
    Reader emptyIn(emptyPacket); require(emptyIn.text().empty(), "Empty text did not round trip"); emptyIn.finished();
    for (std::size_t size = 0; size < expected.size(); ++size) {
        rejects<std::runtime_error>([&] { Reader truncated(std::span<const std::byte>(expected).first(size)); truncated.text(); },
            "A truncated UTF-16 packet was accepted");
    }
}

void textLimits()
{
    const std::wstring maximum(32767, L'X');
    Writer out; out.text(maximum);
    require(out.bytes.size() == 65538 && out.bytes[0] == std::byte{0xff} && out.bytes[1] == std::byte{0x7f}
        && out.bytes[2] == std::byte{0} && out.bytes[3] == std::byte{0}, "Maximum text length prefix changed");
    Reader in(out.bytes); require(in.text() == maximum, "Maximum text did not round trip"); in.finished();
    Writer tooLong;
    rejects<std::length_error>([&] { tooLong.text(std::wstring(32768, L'X')); }, "Oversized writer text was accepted");
    require(tooLong.bytes.empty(), "Oversized text wrote a partial prefix");
    const auto oversized = octets({0, 0x80, 0, 0});
    rejects<std::runtime_error>([&] { Reader reader(oversized); reader.text(); }, "Oversized reader text was accepted");
    const auto hostile = octets({0xff, 0xff, 0xff, 0xff});
    rejects<std::runtime_error>([&] { Reader reader(hostile); reader.text(); }, "Unbounded text length was accepted");
}

void finiteAndEnumGuards()
{
    for (const auto value : {std::numeric_limits<double>::infinity(), -std::numeric_limits<double>::infinity(),
        std::numeric_limits<double>::quiet_NaN()}) {
        Writer out;
        rejects<std::runtime_error>([&] { out.real(value); }, "Non-finite output was accepted");
        require(out.bytes.empty(), "Non-finite output wrote partial data");
    }
    for (const auto& payload : {octets({0, 0, 0, 0, 0, 0, 0xf0, 0x7f}),
        octets({0, 0, 0, 0, 0, 0, 0xf0, 0xff}), octets({0, 0, 0, 0, 0, 0, 0xf8, 0x7f})}) {
        rejects<std::runtime_error>([&] { Reader in(payload); in.real(); }, "Non-finite input was accepted");
    }
    const auto invalidEnum = octets({3, 0, 0, 0});
    rejects<std::runtime_error>([&] { Reader in(invalidEnum); in.enumeration<unsigned>(2); }, "Out-of-range enumeration was accepted");
    const auto invalidBool = octets({2, 0, 0, 0});
    rejects<std::runtime_error>([&] { Reader in(invalidBool); in.boolean(); }, "Out-of-range protocol boolean was accepted");
    for (const auto& payload : {octets({0, 0, 0, 0}), octets({1, 0, 0, 0})}) {
        Reader in(payload); require(in.boolean() == (payload[0] != std::byte{0}), "Protocol boolean changed"); in.finished();
    }
}

void arrayGuards()
{
    Writer out; out.count(2, 2);
    require(out.bytes == octets({2, 0, 0, 0}), "Array count format changed");
    const auto before = out.bytes;
    rejects<std::length_error>([&] { out.count(3, 2); }, "Oversized output array was accepted");
    require(out.bytes == before, "Invalid array count wrote partial data");
    const auto valid = octets({2, 0, 0, 0, 0x34, 0x12, 0x78, 0x56});
    Reader in(valid); require(in.count(2, 2) == 2, "Valid array count changed");
    require(in.number<std::uint16_t>() == 0x1234 && in.number<std::uint16_t>() == 0x5678, "Array contents changed"); in.finished();
    rejects<std::runtime_error>([&] { Reader limited(valid); limited.count(1, 2); }, "Array maximum was ignored");
    const auto shortArray = octets({2, 0, 0, 0, 1, 0, 2});
    rejects<std::runtime_error>([&] { Reader truncated(shortArray); truncated.count(2, 2); }, "Array byte bound was ignored");
}

void headerPackets()
{
    const nks::collector::Header value{nks::collector::Kind::processes, 0, 0x0102030405060708ull, 0x1234};
    const auto expected = octets({0x4e, 0x4b, 0x43, 0x31, 3, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
        8, 7, 6, 5, 4, 3, 2, 1, 0x34, 0x12, 0, 0, 0, 0, 0, 0});
    require(nks::collector::encodeHeader(value) == expected, "Protocol-3 header bytes changed");
    const auto read = nks::collector::decodeHeader(expected);
    require(read.kind == value.kind && read.status == value.status && read.requestId == value.requestId
        && read.payloadBytes == value.payloadBytes, "Header did not round trip");
    for (std::size_t size = 0; size < expected.size(); ++size)
        rejects<std::runtime_error>([&] { nks::collector::decodeHeader(std::span<const std::byte>(expected).first(size)); }, "Short header was accepted");
    auto extra = expected; extra.push_back(std::byte{0});
    rejects<std::runtime_error>([&] { nks::collector::decodeHeader(extra); }, "Long header was accepted");
    for (const auto offset : {0u, 4u, 8u, 12u, 28u}) {
        auto corrupt = expected;
        corrupt[offset] = std::byte{offset == 0 ? static_cast<unsigned char>(0) : static_cast<unsigned char>(5)};
        rejects<std::runtime_error>([&] { nks::collector::decodeHeader(corrupt); }, "Invalid header field was accepted");
    }
    auto zeroId = expected; std::fill(zeroId.begin() + 16, zeroId.begin() + 24, std::byte{0});
    rejects<std::runtime_error>([&] { nks::collector::decodeHeader(zeroId); }, "Zero request identity was accepted");
    auto oversize = expected;
    oversize[24] = std::byte{1}; oversize[25] = oversize[26] = std::byte{0}; oversize[27] = std::byte{4};
    rejects<std::runtime_error>([&] { nks::collector::decodeHeader(oversize); }, "Oversized header payload was accepted");
    auto output = value; output.payloadBytes = static_cast<std::uint32_t>(nks::collector::maximumPayload + 1);
    rejects<std::length_error>([&] { nks::collector::encodeHeader(output); }, "Oversized output header was accepted");
}

void payloadBounds()
{
    Writer out; out.bytes.resize(nks::collector::maximumPayload - 4);
    out.text(L""); require(out.bytes.size() == nks::collector::maximumPayload, "Exact payload boundary was rejected");
    Reader tail(std::span<const std::byte>(out.bytes).last(4)); require(tail.text().empty(), "Boundary text prefix changed"); tail.finished();
    rejects<std::length_error>([&] { out.number<std::uint8_t>(1); }, "Number exceeded payload boundary");
    rejects<std::length_error>([&] { out.text(L""); }, "Text exceeded payload boundary");
    require(out.bytes.size() == nks::collector::maximumPayload, "Rejected boundary write changed payload size");
    out.bytes.resize(nks::collector::maximumPayload + 1);
    rejects<std::length_error>([&] { Reader in(out.bytes); }, "Oversized input payload was accepted");
    rejects<std::length_error>([&] { out.number<std::uint32_t>(1); }, "Oversized public writer buffer bypassed the payload limit");
}

}

int main()
{
    try {
        primitiveBytes(); primitiveEdges(); textBytes(); textLimits(); finiteAndEnumGuards(); arrayGuards(); headerPackets(); payloadBounds();
        std::cout << "Collector wire checks passed: 8 cases\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << "Collector wire checks failed: " << error.what() << '\n'; return 1; }
}
