#include <windows.h>

#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <vector>

namespace {
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }

struct Child {
    HANDLE input{}, output{}, job{}, process{};
    Child() {
        try {
        SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
        HANDLE childInput{}, childOutput{};
        require(CreatePipe(&childInput, &input, &security, 0) != FALSE, "Input pipe failed");
        require(CreatePipe(&output, &childOutput, &security, 0) != FALSE, "Output pipe failed");
        SetHandleInformation(input, HANDLE_FLAG_INHERIT, 0);
        SetHandleInformation(output, HANDLE_FLAG_INHERIT, 0);
        job = CreateJobObjectW(nullptr, nullptr);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        require(job && SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)), "Job limits failed");
        std::array<wchar_t, 32768> module{};
        require(GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size())) != 0, "Test path unavailable");
        const auto path = std::filesystem::path(module.data()).parent_path() / L"nks-icon-worker.exe";
        auto command = L"\"" + path.wstring() + L"\"";
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        startup.dwFlags = STARTF_USESTDHANDLES;
        startup.hStdInput = childInput;
        startup.hStdOutput = childOutput;
        startup.hStdError = childOutput;
        PROCESS_INFORMATION created{};
        const BOOL launched = CreateProcessW(path.c_str(), command.data(), nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW | CREATE_SUSPENDED, nullptr, nullptr, &startup, &created);
        CloseHandle(childInput); CloseHandle(childOutput);
        require(launched != FALSE, "Worker did not start");
        process = created.hProcess;
        require(AssignProcessToJobObject(job, process) != FALSE, "Worker job assignment failed");
        ResumeThread(created.hThread); CloseHandle(created.hThread);
        HANDLE token{};
        require(OpenProcessToken(process, TOKEN_QUERY, &token) != FALSE, "Worker token unavailable");
        TOKEN_ELEVATION elevation{}; DWORD used{};
        const BOOL queried = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &used);
        CloseHandle(token);
        require(queried && !elevation.TokenIsElevated, "Icon worker is elevated");
        } catch (...) {
            if (process) TerminateProcess(process, 1);
            for (HANDLE handle : {job, input, output, process}) if (handle) CloseHandle(handle);
            throw;
        }
    }
    ~Child() {
        for (HANDLE handle : {job, input, output, process}) if (handle) CloseHandle(handle);
    }
    Child(const Child&) = delete;
    Child& operator=(const Child&) = delete;

    void write(const void* bytes, DWORD length) {
        DWORD sent{};
        require(WriteFile(input, bytes, length, &sent, nullptr) && sent == length, "Request write failed");
    }
    void read(void* bytes, DWORD length) {
        auto* at = static_cast<std::byte*>(bytes);
        while (length) {
            DWORD received{};
            require(ReadFile(output, at, length, &received, nullptr) && received != 0, "Response was truncated");
            at += received; length -= received;
        }
    }
    void request(std::uint64_t generation, std::uint32_t magic = 0x31494b4e) {
        std::array<std::byte, 16> bytes{};
        const DWORD pid = GetCurrentProcessId();
        std::memcpy(bytes.data(), &magic, 4);
        std::memcpy(bytes.data() + 4, &pid, 4);
        std::memcpy(bytes.data() + 8, &generation, 8);
        write(bytes.data(), static_cast<DWORD>(bytes.size()));
    }
    std::vector<std::byte> response() {
        std::array<std::uint32_t, 2> header{};
        read(header.data(), sizeof(header));
        require(header[0] == 0x31494b4e && (header[1] == 0 || header[1] == 1350), "Invalid response frame");
        std::vector<std::byte> bytes(header[1]);
        if (!bytes.empty()) read(bytes.data(), static_cast<DWORD>(bytes.size()));
        return bytes;
    }
    void exited(DWORD expected) {
        require(WaitForSingleObject(process, 2000) == WAIT_OBJECT_0, "Worker did not exit within two seconds");
        DWORD code{};
        require(GetExitCodeProcess(process, &code) && code == expected, "Unexpected worker exit code");
    }
};
}

int main() {
    try {
        FILETIME created{}, exited{}, kernel{}, user{};
        require(GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user) != FALSE, "Creation time unavailable");
        std::uint64_t generation{};
        std::memcpy(&generation, &created, sizeof(generation));
        {
            Child child;
            child.request(generation + 1);
            require(child.response().empty(), "Recycled PID generation was accepted");
            child.request(generation);
            const auto bytes = child.response();
            require(bytes.size() == 1350 && bytes[0] == std::byte{'B'} && bytes[1] == std::byte{'M'}, "Executable icon was not a bounded bitmap");
            std::int32_t width{}, height{};
            std::memcpy(&width, bytes.data() + 18, 4); std::memcpy(&height, bytes.data() + 22, 4);
            require(width == 18 && height == -18, "Bitmap dimensions changed");
            child.request(0);
            require(child.response().empty(), "Missing generation was accepted");
            CloseHandle(child.input); child.input = nullptr;
            child.exited(0);
        }
        {
            Child child;
            child.request(generation, 0);
            child.exited(4);
        }
        {
            Child child;
            const std::uint32_t prefix = 0x31494b4e;
            child.write(&prefix, sizeof(prefix));
            CloseHandle(child.input); child.input = nullptr;
            child.exited(0);
        }
        {
            Child child;
            CloseHandle(child.job); child.job = nullptr;
            require(WaitForSingleObject(child.process, 2000) == WAIT_OBJECT_0, "Closing the owner job left a live worker");
        }
        std::cout << "Icon worker: generation, bitmap, framing, EOF, owner exit; elevated=false\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
