#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <bcrypt.h>
#include <shlobj.h>
#include <sddl.h>
#include <array>
#include <cstring>
#include <filesystem>
#include <span>
#include <stdexcept>
#include <string>
#include <system_error>
#include <vector>

namespace fs = std::filesystem;
using Bytes = std::span<const unsigned char>;
// Public Brotli decoder ABI, statically linked from the same NativeAOT runtime pack.
extern "C" int BrotliDecoderDecompress(size_t inputSize, const unsigned char* input,
    size_t* outputSize, unsigned char* output);

static void check(BOOL success)
{
    if (!success) throw std::system_error(GetLastError(), std::system_category());
}

static Bytes take(Bytes& data, size_t size)
{
    if (size > data.size()) throw std::runtime_error("Invalid archive length");
    auto head = data.first(size);
    data = data.subspan(size);
    return head;
}

static DWORD number(Bytes& data)
{
    DWORD value;
    std::memcpy(&value, take(data, sizeof(value)).data(), sizeof(value));
    return value;
}

static std::vector<unsigned char> unpack(HINSTANCE module)
{
    auto info = FindResourceW(module, MAKEINTRESOURCEW(101), RT_RCDATA);
    check(info != nullptr);
    auto memory = LoadResource(module, info);
    check(memory != nullptr);
    Bytes compressed(static_cast<const unsigned char*>(LockResource(memory)), SizeofResource(module, info));
    DWORD size = number(compressed);
    if (size > 512u * 1024u * 1024u) throw std::runtime_error("Invalid application size");
    DWORD codeOffset = number(compressed), codeLength = number(compressed);
    if (codeOffset > size || codeLength > size - codeOffset) throw std::runtime_error("Invalid code range");
    auto digest = take(compressed, 32);
    std::vector<unsigned char> raw(size);
    size_t actual = size;
    if (BrotliDecoderDecompress(compressed.size(), compressed.data(), &actual, raw.data()) != 1 || actual != size)
        throw std::runtime_error("Invalid Brotli payload");
#ifdef _M_ARM64
    for (DWORD i = 0; i + 4 <= codeLength; i += 4)
    {
        unsigned char* location = raw.data() + codeOffset + i;
        DWORD instruction;
        std::memcpy(&instruction, location, sizeof(instruction));
        if ((instruction >> 26) == 0x25)
        {
            instruction = 0x94000000u | ((instruction - i / 4) & 0x03ffffffu);
            std::memcpy(location, &instruction, sizeof(instruction));
        }
    }
#else
    for (DWORD i = 0; i + 5 <= codeLength; ++i)
    {
        unsigned char* location = raw.data() + codeOffset + i;
        if (*location != 0xe8 && *location != 0xe9) continue;
        if (location[4] == 0 || location[4] == 255)
        {
            DWORD address;
            std::memcpy(&address, location + 1, sizeof(address));
            DWORD relative = (address - i - 5) & 0x01ffffffu;
            if (relative & 0x01000000u) relative |= 0xfe000000u;
            std::memcpy(location + 1, &relative, sizeof(relative));
        }
        i += 4;
    }
#endif
    std::array<unsigned char, 32> hash{};
    if (BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, raw.data(), size,
        hash.data(), static_cast<ULONG>(hash.size())) < 0 || std::memcmp(hash.data(), digest.data(), hash.size()))
        throw std::runtime_error("Application archive failed SHA-256 verification");
    return raw;
}

// An elevated process must never execute code from a directory writable by a medium-integrity user.
// A fresh directory also rules out stale DLLs and makes concurrent launches independent.
class ExtractedApplication
{
    fs::path root;
    std::vector<fs::path> files;
    std::vector<fs::path> folders;

public:
    ExtractedApplication()
    {
        TOKEN_ELEVATION elevation{};
        DWORD length;
        check(GetTokenInformation(GetCurrentProcessToken(), TokenElevation, &elevation, sizeof(elevation), &length));
        fs::path parent;
        if (elevation.TokenIsElevated)
        {
            PWSTR path = nullptr;
            if (FAILED(SHGetKnownFolderPath(FOLDERID_ProgramFiles, KF_FLAG_DEFAULT, nullptr, &path)))
                throw std::runtime_error("Cannot resolve Program Files");
            parent = path;
            CoTaskMemFree(path);
        }
        else parent = fs::temp_directory_path();
        GUID guid{};
        wchar_t name[40];
        if (FAILED(CoCreateGuid(&guid)) || !StringFromGUID2(guid, name, 40))
            throw std::runtime_error("Cannot create extraction identifier");
        root = parent / (std::wstring(L"ExHyperV-") + name);
        PSECURITY_DESCRIPTOR security = nullptr;
        SECURITY_ATTRIBUTES attributes{sizeof(attributes), nullptr, FALSE};
        if (elevation.TokenIsElevated)
        {
            check(ConvertStringSecurityDescriptorToSecurityDescriptorW(
                L"O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", SDDL_REVISION_1, &security, nullptr));
            attributes.lpSecurityDescriptor = security;
        }
        BOOL created = CreateDirectoryW(root.c_str(), security ? &attributes : nullptr);
        DWORD error = GetLastError();
        if (security) LocalFree(security);
        if (!created) throw std::system_error(error, std::system_category());
    }

    ~ExtractedApplication()
    {
        for (auto it = files.rbegin(); it != files.rend(); ++it) DeleteFileW(it->c_str());
        for (auto it = folders.rbegin(); it != folders.rend(); ++it) RemoveDirectoryW(it->c_str());
        RemoveDirectoryW(root.c_str());
    }

    void write(Bytes data)
    {
        auto count = number(data);
        for (DWORD i = 0; i < count; ++i)
        {
            auto name = take(data, number(data));
            if (name.empty() || name.size() % sizeof(wchar_t)) throw std::runtime_error("Invalid filename");
            std::wstring text(name.size() / sizeof(wchar_t), L'\0');
            std::memcpy(text.data(), name.data(), name.size());
            fs::path relative(text);
            if (relative.has_root_path() || text.find(L':') != text.npos || text.find(L'\0') != text.npos)
                throw std::runtime_error("Invalid application path");
            fs::path path = root;
            for (const auto& part : relative)
            {
                if (part.empty() || part == L".." || part == L"." || part.native().back() == L'.' || part.native().back() == L' ')
                    throw std::runtime_error("Invalid application path component");
                if (path != root && fs::create_directory(path)) folders.push_back(path);
                path /= part;
            }
            auto bytes = take(data, number(data));
            HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
            check(file != INVALID_HANDLE_VALUE);
            DWORD written;
            BOOL ok = WriteFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr);
            DWORD error = GetLastError();
            BOOL closed = CloseHandle(file);
            files.push_back(path);
            if (!ok) throw std::system_error(error, std::system_category());
            check(closed);
            if (written != bytes.size()) throw std::runtime_error("Incomplete application file");
        }
        if (!data.empty()) throw std::runtime_error("Invalid application archive");
    }

    DWORD run(const wchar_t* arguments) const
    {
        auto exe = root / L"ExHyperV.exe";
        std::wstring command = L"\"" + exe.wstring() + L"\" " + arguments;
        std::wstring launcher(32768, L'\0');
        DWORD length = GetModuleFileNameW(nullptr, launcher.data(), static_cast<DWORD>(launcher.size()));
        check(length != 0 && length < launcher.size());
        launcher.resize(length);
        check(SetEnvironmentVariableW(L"EXHYPERV_PORTABLE_LAUNCHER", launcher.c_str()));
        STARTUPINFOW startup{sizeof(startup)};
        PROCESS_INFORMATION process{};
        check(CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process));
        CloseHandle(process.hThread);
        DWORD status = ERROR_PROCESS_ABORTED;
        if (WaitForSingleObject(process.hProcess, INFINITE) == WAIT_OBJECT_0)
            GetExitCodeProcess(process.hProcess, &status);
        CloseHandle(process.hProcess);
        return status;
    }
};

int WINAPI wWinMain(HINSTANCE module, HINSTANCE, PWSTR arguments, int)
{
    try
    {
        ExtractedApplication application;
        application.write(unpack(module));
        return application.run(arguments);
    }
    catch (const std::exception& error)
    {
        MessageBoxA(nullptr, error.what(), "ExHyperV", MB_ICONERROR | MB_OK);
        return 1;
    }
}
