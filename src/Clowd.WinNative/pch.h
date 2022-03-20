// pch.h: This is a precompiled header file.
// Files listed below are compiled only once, improving build performance for future builds.
// This also affects IntelliSense performance, including code completion and many code browsing features.
// However, files listed here are ALL re-compiled if any one of them is updated between builds.
// Do not add files here that you will be updating frequently as this negates the performance advantage.

#ifndef PCH_H
#define PCH_H

#pragma comment(lib, "user32.lib") 
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "shcore.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "gdiplus.lib")
#pragma comment(lib, "msimg32.lib")
#pragma comment(lib, "winmm.lib")

#include <string>
#include <iostream>
#include <chrono>
#include <map>
#include <vector>
#include <memory>
#include <deque>
#include <numeric>
#include <algorithm>
#include <iomanip>
#include <thread>
#include <sstream>
#include <fstream>
#include <locale>
#include <codecvt>
#include <mutex>

#include <time.h>
#include <windows.h>
#include <dwmapi.h>
#include <objidl.h>
#include <gdiplus.h>
#include <shellscalingapi.h>
#include <comdef.h>

//#include <msclr\marshal.h>
//#include <msclr\marshal_cppstd.h>
//#include <msclr\lock.h>

#define NS_TO_MS_DIV (1000000)
#define DEBUGBOX_SIZE (600)
#define DEBUGBOX_MARGIN (50)
#define BASE_DPI ((double)96.0)

#define WMEX_SHOWWINDOW (WM_USER + 2)
#define WMEX_RESET (WM_USER + 3)
#define WMEX_PAINTGREY (WM_USER + 4)
#define WMEX_FASTTIMER (WM_USER + 5)
#define WMEX_DESTROY (WM_USER + 6)

#define FONT_ARIAL_BLACK (L"Arial Black")
#define GWL_USERDATA        (-21)

// nop this keyword
#define gcnew

namespace System {
    namespace Drawing {
        using Color = Gdiplus::Color;
        using Rectangle = Gdiplus::Rect;
        using RectangleF = Gdiplus::RectF;
    }

    using String = std::wstring;
    using Exception = std::exception;
    using InvalidOperationException = std::logic_error;
    using ObjectDisposedException = std::logic_error;
    using OutOfMemoryException = std::overflow_error;
}

//delegate LRESULT WndProcDel(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

struct free_calloc { void operator()(void* x) { free(x); } };
struct free_gdiobject { void operator()(void* x) { DeleteObject(x); } };

template<typename TT>
std::unique_ptr<TT, free_calloc> mkcallocobj(size_t size)
{
	return std::unique_ptr<TT, free_calloc>((TT*)calloc(1, size));
}

typedef std::unique_ptr<std::remove_pointer<HPEN>::type, free_gdiobject> SMART_PEN;

struct POINTFF
{
	double x;
	double y;
};

struct RECTFF
{
	double x;
	double y;
	double w;
	double h;
};

inline double to_time_ms(std::chrono::steady_clock::time_point start, std::chrono::steady_clock::time_point end)
{
	return (end - start).count() / (double)NS_TO_MS_DIV;
	//return ((int)((end - start).count() / 10000.0)) / 100.0;
}

inline std::wstring to_wfmt(double v, const wchar_t* format = L"%05.2f")
{
	wchar_t tmp[32];
	swprintf(tmp, 32, format, v);
	return std::wstring(tmp);
}

inline std::wstring to_hex(System::Drawing::Color clr)
{
	std::wstringstream ss;
	ss << L"#";
	ss << std::uppercase << std::setfill(L'0') << std::hex;
	ss << std::setw(2) << +clr.GetR();
	ss << std::setw(2) << +clr.GetG();
	ss << std::setw(2) << +clr.GetB();
	return ss.str();
}

inline std::wstring to_hex(HWND hWnd)
{
	std::wstringstream ss;
	ss << std::hex << L"0x" << std::setw(16) << std::setfill(L'0') <<
		*reinterpret_cast<uint64_t*>(&hWnd) << std::endl;
	return ss.str();
}

//std::wstring to_hex(unsigned char c) {
//
//	std::wstringstream ss;
//
//	// Set stream modes
//	ss << std::uppercase << std::setw(2) << std::setfill('0') << std::hex;
//
//	// Stream in the character's ASCII code
//	// (using `+` for promotion to `int`)
//	ss << +c;
//
//	// Return resultant string content
//	return ss.str();
//}

inline std::wstring s2ws(const std::string& str)
{
    using convert_typeX = std::codecvt_utf8<wchar_t>;
    std::wstring_convert<convert_typeX, wchar_t> converterX;
    return converterX.from_bytes(str);
}

inline std::string ws2s(const std::wstring& wstr)
{
    using convert_typeX = std::codecvt_utf8<wchar_t>;
    std::wstring_convert<convert_typeX, wchar_t> converterX;
    return converterX.to_bytes(wstr);
}

inline void HR(const HRESULT result)
{
	if (FAILED(result))
	{
        _com_error err(result);
        std::wstring msg(err.ErrorMessage());
        msg += L" (" + std::to_wstring(result) + L")";
        throw std::exception(ws2s(msg).c_str());
	}
}

inline void HR_last_error()
{
    HR(GetLastError());
    throw std::exception("Unknown Error"); // throws if GetLastError=0
}

template <class T> void SafeRelease(T** ppT)
{
    if (*ppT)
    {
        (*ppT)->Release();
        *ppT = NULL;
    }
}

template<typename TT>
class DxRef
{

private:
    TT* ptr;

    // make this class un-copyable
    DxRef& operator=(nullptr_t) = delete;
    DxRef& operator=(const DxRef&) = delete;
    DxRef(const DxRef&) = delete;

public:
    DxRef(TT* ptr) : ptr(ptr) {}
    DxRef() : ptr(0) {}

    IID uid() noexcept
    {
        return __uuidof(TT);
    }

    void reset() noexcept
    {
        SafeRelease(&ptr);
    }

    bool has() noexcept
    {
        return ptr != nullptr;
    }

    TT** put() noexcept
    {
        reset();
        return &ptr;
    }

    void** put_void() noexcept
    {
        reset();
        return reinterpret_cast<void**>(&ptr);
    }

    ~DxRef() noexcept
    {
        reset();
    }

    _NODISCARD TT* operator->()
    {
        if (ptr == nullptr)
        {
            std::string str = "DxRef<" + std::string(typeid(TT).name()) + ">";
            //auto clrstr = msclr::interop::marshal_as<System::String^>(str);
            throw gcnew System::ObjectDisposedException(str.c_str());
        }
        return ptr;
    }

    operator TT* () noexcept { return ptr; }

};

#endif //PCH_H
