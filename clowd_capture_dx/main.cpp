#include "pch.h"
#include <string>
#include <iostream>
#include <vector>

#include "windows.h"
#include "gdiplus.h"
#include "argh.h"
#include "BorderWindow.h"
#include "DxScreenCapture.h"

using namespace std;
using namespace Gdiplus;

struct CliColor
{
    BYTE R;
    BYTE G;
    BYTE B;
};

vector<string> util_string_split(const string& input, char delimiter)
{
    stringstream ss(input);
    vector<string> result;

    while (ss.good()) {
        string substr;
        getline(ss, substr, delimiter);
        result.push_back(substr);
    }

    return result;
}

CliColor util_parse_color(const string& input)
{
    auto parts = util_string_split(input, ',');
    if (parts.size() != 3) {
        string message = "Not a valid color: " + input;
        throw std::invalid_argument(message.c_str());
    }

    CliColor r{};
    r.R = stoi(parts[0]);
    r.G = stoi(parts[1]);
    r.B = stoi(parts[2]);
    return r;
}

void run(vector<string> arguments)
{
    argh::parser cmdl;
    cmdl.add_params({ "lowPerfMode", "lastSaveDir", "accentColor", "capturePath", "resultPath" });
    cmdl.parse(arguments);

    cerr << std::endl;
    cerr << "clowd-capture-dx v" << CLOWD_VERSION << ", a utility to screenshot/select screen region" << std::endl;
    cerr << std::endl;

    bool help = cmdl[{"h", "help"}];
    if (help) {
        cerr << "Arguments: " << std::endl;
        cerr << "  -h, --help                 Show this help text" << std::endl;
        cerr << "  --lastSaveDir {filePath}   The last used save directory" << std::endl;
        cerr << "  --lowPerfMode              Disable animations & optimise for old devices" << std::endl;
        cerr << "  --accentColor              Accent color for crosshair, borders, UI etc." << std::endl;
        cerr << "  --capturePath {filePath}   Path to save the captured image" << std::endl;
        cerr << "  --resultPath {filePath}    Path to save the result json" << std::endl;
        return;
    }

    bool lowPerfMode = cmdl["lowPerfMode"];

    string lastSaveDir, accentColor, capturePath, resultPath;
    lastSaveDir = cmdl("lastSaveDir").str();
    accentColor = cmdl("accentColor").str();
    capturePath = cmdl("capturePath").str();
    resultPath = cmdl("resultPath").str();

    if (accentColor.empty()) {
        accentColor = "0,125,180";
    }
    CliColor color = util_parse_color(accentColor);

    auto args = captureArgs{
        color.R,
        color.G,
        color.B,
        lowPerfMode,
        false,
        false,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
        nullptr,
        {},
        {},
        {},
        false
    };

    auto capture = new DxScreenCapture(&args);
    capture->RunMessagePump();
}

std::string wstring_to_utf8(std::wstring const& wstr)
{
    if (wstr.empty()) return std::string();
    int size_needed = WideCharToMultiByte(CP_UTF8, 0, &wstr[0], (int)wstr.size(), NULL, 0, NULL, NULL);
    std::string strTo(size_needed, 0);
    WideCharToMultiByte(CP_UTF8, 0, &wstr[0], (int)wstr.size(), &strTo[0], size_needed, NULL, NULL);
    return strTo;
}

std::wstring utf8_to_wstring(std::string const& str)
{
    if (str.empty()) return std::wstring();
    int size_needed = MultiByteToWideChar(CP_UTF8, 0, &str[0], (int)str.size(), NULL, 0);
    std::wstring strTo(size_needed, 0);
    MultiByteToWideChar(CP_UTF8, 0, &str[0], (int)str.size(), &strTo[0], size_needed);
    return strTo;
}

int wmain(int argc, wchar_t* argv[], wchar_t* envp[])
{
    SetConsoleCP(CP_UTF8);
    setvbuf(stdout, nullptr, _IOFBF, 1000);

    GdiplusStartupInput gdiplusStartupInput;
    ULONG_PTR gdiplusToken;
    GdiplusStartup(&gdiplusToken, &gdiplusStartupInput, NULL);

    try {
        // per monitor dpi aware so winapi does not lie to us
        SetProcessDpiAwareness(PROCESS_DPI_AWARENESS::PROCESS_PER_MONITOR_DPI_AWARE);

        // convert wchar arguments to utf8
        vector<string> utf8argv{};
        for (int i = 0; i < argc; i++) {
            utf8argv.emplace_back(wstring_to_utf8(argv[i]));
        }

        run(utf8argv);
        GdiplusShutdown(gdiplusToken);
        return 0;
    }
    catch (const std::invalid_argument& exc) {
        std::cerr << std::endl << exc.what();
        std::cerr << std::endl << "Invalid or missing arguments. The application will now exit." << std::endl;
    }
    catch (const std::exception& exc) {
        std::cerr << std::endl << exc.what();
        std::cerr << std::endl << "A fatal error has occurred. The application will now exit." << std::endl;
    }
    catch (...) {
        std::cerr << std::endl << "An unknown error has occurred. The application will now exit." << std::endl;
    }
    GdiplusShutdown(gdiplusToken);
    return -1;
}