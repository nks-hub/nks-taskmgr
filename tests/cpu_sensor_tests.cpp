#include <winsock2.h>
#include <ws2tcpip.h>
#include "collectors/cpu_sensors.hpp"
#include "collectors/resource_collector.hpp"
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string_view>
#include <thread>

namespace {
using namespace std::chrono_literals;
using Facts = std::vector<nks::HardwareFact>;

void require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }

struct PortUnavailable : std::runtime_error { using std::runtime_error::runtime_error; };
struct Socket {
    SOCKET value{INVALID_SOCKET};
    ~Socket() { if (value != INVALID_SOCKET) closesocket(value); }
};
struct Winsock {
    Winsock() { WSADATA data{}; require(WSAStartup(MAKEWORD(2, 2), &data) == 0, "WSAStartup failed"); }
    ~Winsock() { WSACleanup(); }
};

struct Reply { unsigned status{200}; std::string body; bool stall{}; };

class Provider {
public:
    Provider() {
        listener.value = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        require(listener.value != INVALID_SOCKET, "Fixture socket failed");
        const BOOL exclusive = TRUE;
        require(setsockopt(listener.value, SOL_SOCKET, SO_EXCLUSIVEADDRUSE,
            reinterpret_cast<const char*>(&exclusive), sizeof(exclusive)) == 0, "Exclusive fixture binding failed");
        sockaddr_in address{}; address.sin_family = AF_INET; address.sin_port = htons(8085);
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        if (bind(listener.value, reinterpret_cast<sockaddr*>(&address), sizeof(address)) != 0) {
            const auto error = WSAGetLastError();
            if (error == WSAEADDRINUSE || error == WSAEACCES)
                throw PortUnavailable("127.0.0.1:8085 is occupied or reserved; its owner was left untouched");
            throw std::runtime_error("Fixture bind failed: " + std::to_string(error));
        }
        require(listen(listener.value, 4) == 0, "Fixture listen failed");
        worker = std::jthread([this](std::stop_token stop) { serve(stop); });
    }
    ~Provider() { worker.request_stop(); if (worker.joinable()) worker.join(); }
    Provider(const Provider&) = delete;
    Provider& operator=(const Provider&) = delete;

    void reply(Reply value) {
        std::unique_lock lock(mutex);
        require(ready.wait_for(lock, 2s, [&] { return !busy; }), "Previous fixture connection did not finish");
        next = std::move(value);
    }
    std::uint64_t connections() const { return accepted.load(); }
    void idle() {
        std::unique_lock lock(mutex);
        require(ready.wait_for(lock, 2s, [&] { return !busy; }), "Fixture connection remained open");
        require(failure.empty(), failure.c_str());
    }
    void noRequestsFor(std::chrono::milliseconds duration) {
        const auto before = accepted.load();
        std::unique_lock lock(mutex);
        require(!ready.wait_for(lock, duration, [&] { return accepted.load() != before; }), "Disabled collector contacted the sensor source");
    }
private:
    static bool readable(SOCKET socket, long microseconds) {
        fd_set read{}; FD_ZERO(&read); FD_SET(socket, &read);
        timeval timeout{0, microseconds};
        const auto result = select(0, &read, nullptr, nullptr, &timeout);
        if (result == SOCKET_ERROR) throw std::runtime_error("Fixture select failed");
        return result != 0;
    }
    static void sendBytes(SOCKET client, std::string_view bytes, std::stop_token stop) {
        while (!bytes.empty() && !stop.stop_requested()) {
            const auto count = static_cast<int>((std::min)(bytes.size(), std::size_t{8192}));
            const auto sent = send(client, bytes.data(), count, 0);
            if (sent <= 0) return; // The bounded reader may close an oversized response early.
            bytes.remove_prefix(static_cast<std::size_t>(sent));
        }
    }
    void connection(SOCKET client, const Reply& value, std::stop_token stop) {
        const DWORD timeout = 500;
        require(setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout)) == 0
            && setsockopt(client, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout)) == 0,
            "Fixture socket timeout failed");
        std::string request;
        std::array<char, 2048> buffer{};
        while (request.find("\r\n\r\n") == std::string::npos && !stop.stop_requested()) {
            const auto received = recv(client, buffer.data(), static_cast<int>(buffer.size()), 0);
            require(received > 0, "Fixture request was truncated or timed out");
            require(static_cast<std::size_t>(received) <= 8192 - request.size(), "Fixture request exceeded limit");
            request.append(buffer.data(), static_cast<std::size_t>(received));
        }
        require(request.starts_with("GET /metrics?archivelength=0&lastvalue=1&timestamps=1 HTTP/1.1\r\n"),
            "Collector used an unexpected HTTP method or sensor endpoint");
        if (value.stall) {
            const auto deadline = std::chrono::steady_clock::now() + 6s;
            while (!stop.stop_requested() && std::chrono::steady_clock::now() < deadline) {
                if (!readable(client, 50000)) continue;
                if (recv(client, buffer.data(), 1, MSG_PEEK) <= 0) return;
            }
            return;
        }
        const auto header = "HTTP/1.1 " + std::to_string(value.status) + " Fixture\r\nContent-Type: text/plain; version=0.0.4\r\n"
            + "Content-Length: " + std::to_string(value.body.size()) + "\r\nConnection: close\r\n\r\n";
        sendBytes(client, header, stop); sendBytes(client, value.body, stop);
    }
    void serve(std::stop_token stop) noexcept {
        try {
            while (!stop.stop_requested()) {
                if (!readable(listener.value, 50000)) continue;
                Socket client{accept(listener.value, nullptr, nullptr)};
                require(client.value != INVALID_SOCKET, "Fixture accept failed");
                Reply value;
                {
                    std::lock_guard lock(mutex); busy = true; ++accepted; value = next;
                }
                ready.notify_all();
                connection(client.value, value, stop);
                {
                    std::lock_guard lock(mutex); busy = false;
                }
                ready.notify_all();
            }
        } catch (const std::exception& error) {
            std::lock_guard lock(mutex); failure = error.what(); busy = false; ready.notify_all();
        }
    }
    Socket listener;
    std::jthread worker;
    std::atomic<std::uint64_t> accepted{};
    std::mutex mutex;
    std::condition_variable ready;
    Reply next;
    bool busy{};
    std::string failure;
};

std::int64_t unixMs()
{
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

struct PublishedTime { std::int64_t milliseconds; std::wstring utc; };
PublishedTime publishedTime()
{
    SYSTEMTIME time{}; GetSystemTime(&time);
    FILETIME file{};
    require(SystemTimeToFileTime(&time, &file) != FALSE, "Fixture UTC time conversion failed");
    std::uint64_t ticks = (static_cast<std::uint64_t>(file.dwHighDateTime) << 32) | file.dwLowDateTime;
    std::wostringstream label;
    label << std::setfill(L'0') << std::setw(4) << time.wYear << L'-' << std::setw(2) << time.wMonth
        << L'-' << std::setw(2) << time.wDay << L' ' << std::setw(2) << time.wHour
        << L':' << std::setw(2) << time.wMinute << L':' << std::setw(2) << time.wSecond << L" UTC";
    return {static_cast<std::int64_t>(ticks / 10000 - 11644473600000ull), label.str()};
}

std::string metric(std::string_view value, std::int64_t timestamp, std::string_view name = "CPU Package",
    std::string_view type = "lhm_cpu_temperature_celsius")
{
    return "# TYPE " + std::string(type) + " gauge\n" + std::string(type)
        + "{\"sensorName\"=\"" + std::string(name) + "\",\"hardwareName\"=\"Fixture CPU\",\"sensorId\"=\"/temperature/0\",\"hardwareId\"=\"/cpu-test/0\"} "
        + std::string(value) + " " + std::to_string(timestamp) + "\n";
}

const std::wstring& fact(const Facts& values, std::wstring_view key)
{
    const auto found = std::find_if(values.begin(), values.end(), [&](const auto& value) { return value.name == key; });
    require(found != values.end(), "Expected sensor fact was missing");
    return found->value;
}

bool hasReading(const Facts& values)
{
    return std::any_of(values.begin(), values.end(), [](const auto& value) { return value.name.find(L"[/cpu-test/0/temperature/0]") != std::wstring::npos; });
}

const std::wstring& reading(const Facts& values)
{
    const auto found = std::find_if(values.begin(), values.end(), [](const auto& value) { return value.name.find(L"[/cpu-test/0/temperature/0]") != std::wstring::npos; });
    require(found != values.end(), "Typed CPU temperature was not reported");
    return found->value;
}

Facts collect(Provider& provider, Reply reply)
{
    provider.reply(std::move(reply));
    const auto before = provider.connections();
    nks::CpuSensors sensors;
    auto facts = sensors.collect(true);
    provider.idle();
    require(provider.connections() == before + 1, "Sensor read did not make exactly one fixture request");
    return facts;
}

void unavailable(Provider& provider, Reply reply, std::wstring_view state)
{
    const auto result = collect(provider, std::move(reply));
    require(fact(result, L"Stav") == state && !hasReading(result), "Unavailable sensor input became a measured temperature");
}

void enabledAndDisabled(Provider& provider)
{
    nks::ResourceCollector resources;
    const auto before = provider.connections();
    const auto off = resources.collect();
    require(fact(off.cpuSensorFacts, L"Stav").starts_with(L"Vypnuto"), "Resource collection enabled CPU sensors by default");
    provider.noRequestsFor(100ms);
    require(provider.connections() == before, "Default resource collection contacted a sensor source");

    const auto published = publishedTime();
    provider.reply({200, metric("61.5", published.milliseconds)});
    const auto on = resources.collect(true);
    provider.idle();
    require(provider.connections() == before + 1 && fact(on.cpuSensorFacts, L"Stav") == L"Publikované CPU vzorky",
        "Opt-in resource collection did not publish a CPU temperature");
    require(reading(on.cpuSensorFacts).starts_with(L"61.5 °C") && reading(on.cpuSensorFacts).find(published.utc) != std::wstring::npos,
        "Temperature units or the exact source UTC timestamp changed");
    require(fact(on.cpuSensorFacts, L"Zdroj").find(L"identita poskytovatele neověřena") != std::wstring::npos
        && fact(on.cpuSensorFacts, L"Adresa čtení").starts_with(L"http://127.0.0.1:8085/metrics"),
        "The external compatible source was represented as verified hardware");
    const auto disabled = resources.collect(false);
    provider.noRequestsFor(100ms);
    require(!hasReading(disabled.cpuSensorFacts) && fact(disabled.cpuSensorFacts, L"Stav").starts_with(L"Vypnuto")
        && provider.connections() == before + 1, "Disabling sensors retained a live reading or issued a request");
    std::cout << "PASS default-off and opt-in resource facts, exact source UTC, disable\n";
}

void cacheAndDisable(Provider& provider)
{
    provider.reply({200, metric("52.5", unixMs())});
    const auto before = provider.connections();
    nks::CpuSensors sensors;
    const auto first = sensors.collect(true);
    provider.idle();
    const auto cached = sensors.collect(true);
    provider.noRequestsFor(100ms);
    require(provider.connections() == before + 1 && hasReading(first) && hasReading(cached), "Repeated polling bypassed the five-second sensor cache");
    const auto disabled = sensors.collect(false);
    provider.noRequestsFor(100ms);
    require(!hasReading(disabled) && fact(disabled, L"Stav").starts_with(L"Vypnuto")
        && provider.connections() == before + 1, "Disabling sensors retained a live reading or issued a request");
    provider.reply({200, metric("53.5", unixMs())});
    const auto restarted = sensors.collect(true);
    provider.idle();
    require(provider.connections() == before + 2 && reading(restarted).starts_with(L"53.5 °C"), "Re-enabling reused the disabled source's cached reading");
    std::cout << "PASS cache, disable without requests, fresh read after re-enable\n";
}

void valuesAndFreshness(Provider& provider)
{
    const auto negative = collect(provider, {200, metric("-10", unixMs())});
    require(fact(negative, L"Stav") == L"Publikované CPU vzorky" && reading(negative).starts_with(L"-10.0 °C"),
        "A finite negative Celsius measurement was replaced by zero or discarded");
    const auto stale = collect(provider, {200, metric("47.25", unixMs() - 60000)});
    require(fact(stale, L"Stav").starts_with(L"Zastaralé") && reading(stale).find(L"zastaralé") != std::wstring::npos,
        "An old source timestamp was presented as current");
    const auto future = collect(provider, {200, metric("48.5", unixMs() + 60000)});
    require(fact(future, L"Stav").starts_with(L"Zastaralé") && reading(future).find(L"v budoucnu") != std::wstring::npos,
        "A future source timestamp was presented as current");
    std::cout << "PASS negative Celsius, stale and future source times\n";
}

void invalidInputs(Provider& provider)
{
    unavailable(provider, {200, metric("40", unixMs(), "GPU Core", "lhm_gpu_temperature_celsius")}, L"Nepodporováno");
    unavailable(provider, {200, metric("40", unixMs(), "Thermal Zone", "lhm_acpi_temperature_celsius")}, L"Nepodporováno");
    unavailable(provider, {200, metric("NaN", unixMs())}, L"Chyba čtecího API");
    unavailable(provider, {200, metric("-274", unixMs())}, L"Chyba čtecího API");
    auto missingTime = metric("50", unixMs());
    missingTime.erase(missingTime.rfind(' ')); missingTime += '\n';
    unavailable(provider, {200, missingTime}, L"Chyba čtecího API");
    auto missingIdentity = metric("50", unixMs());
    const auto begin = missingIdentity.find(",\"hardwareId\"");
    missingIdentity.erase(begin, missingIdentity.find('}', begin) - begin);
    unavailable(provider, {200, missingIdentity}, L"Chyba čtecího API");
    unavailable(provider, {200, metric("50", -1)}, L"Chyba čtecího API");
    unavailable(provider, {200, std::string(512 * 1024 + 1, 'x')}, L"Chyba čtecího API");
    unavailable(provider, {403, "denied"}, L"Přístup zamítnut");
    unavailable(provider, {404, "missing"}, L"Nepodporováno");
    std::cout << "PASS non-CPU/ACPI, malformed/missing/negative data, byte cap, HTTP denied/missing\n";
}

void boundedTimeout(Provider& provider)
{
    provider.reply({200, {}, true});
    const auto before = provider.connections();
    const auto began = std::chrono::steady_clock::now();
    nks::CpuSensors sensors;
    const auto facts = sensors.collect(true);
    const auto elapsed = std::chrono::steady_clock::now() - began;
    require(fact(facts, L"Stav") == L"Poskytovatel nedostupný" && !hasReading(facts), "An unresponsive source became a measured temperature");
    require(elapsed < 4s, "Sensor timeout exceeded the collector's five-second parent deadline");
    provider.idle();
    require(provider.connections() == before + 1, "Timeout did not exercise the owned unresponsive provider");
    std::cout << "PASS bounded unresponsive provider: " << std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count() << " ms\n";
}
}

int main()
{
    try {
        Winsock winsock;
        Provider provider;
        enabledAndDisabled(provider);
        cacheAndDisable(provider);
        valuesAndFreshness(provider);
        invalidInputs(provider);
        boundedTimeout(provider);
        std::cout << "CPU sensors: production collector with owned loopback fixture; real hardware remains unverified\n";
        return 0;
    } catch (const PortUnavailable& error) {
        std::cout << "SKIP CPU sensor fixture: " << error.what() << '\n'; return 77;
    } catch (const std::exception& error) {
        std::cerr << "FAIL CPU sensors: " << error.what() << '\n'; return 1;
    }
}
