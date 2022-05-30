#pragma once
#include "pch.h"
#include "NativeBitmap.h"
#include "Screens.h"
#include "WindowWalker.h"
#include "DxOutputDevice.h"
#include "exports.h"

#define NUM_SVG_BUTTONS 7
typedef RECT buttonPositionsArr[NUM_SVG_BUTTONS + 1]; // last item (+1) is the sizing indicator

struct mc_frame_data
{
    int hittest;
    int hittestBtn;
    double zoom;
    bool animated;
    bool debugging;
    bool tips;
    bool crosshair;
    bool mouseDown;
    POINTFF mouse;
    POINTFF mouseDownPt;
    bool mouseAnchored;
    bool captured;
    bool dragging;

    RECT selection;
    HitV2Result windowSelection;
    buttonPositionsArr buttonPositions;
};

struct mc_window_native
{
    std::atomic<int> disposed;
    mc_frame_data frame;
    std::unique_ptr<NativeDib> screenshot;
    std::vector<DxDisplay> monitors;
    std::unique_ptr<Screens> screens;
    std::unique_ptr<WindowWalker> walker;

    std::chrono::steady_clock::time_point t0;
    std::chrono::steady_clock::time_point t1;
    std::chrono::steady_clock::time_point t2;
    std::chrono::steady_clock::time_point t3;
};

//struct ScreenCaptureOptions
//{
//    System::Drawing::Color AccentColor;
//    bool AnimationDisabled;
//    bool ObstructedWindowDisabled;
//    bool TipsDisabled;
//    //property System::String^ SessionDirectory;
//};

struct render_info
{
    unsigned int idx;
    void* pParent;
    HWND hWnd;
    unsigned int threadId;
    HANDLE threadHandle;
    HANDLE eventSignal;

    //System::Threading::Thread^ thread;
    //System::Threading::AutoResetEvent^ signal;
};

//public ref class DxKeyDownEventArgs : public System::EventArgs
//{

//private:
//	int keyCode;

//public:
//	property int KeyCode {
//		int get() { return keyCode; }
//	}
//	DxKeyDownEventArgs(int keyCode) : keyCode(keyCode) {}

//};

//public ref class DxColorCapturedEventArgs : public System::EventArgs
//{

//private:
//	System::Drawing::Color color;

//public:
//	property System::Drawing::Color Color {
//		System::Drawing::Color get() { return color; }
//	}
//	DxColorCapturedEventArgs(System::Drawing::Color clr) : color(clr) {}

//};

//public ref class DxLayoutUpdatedEventArgs : public System::EventArgs
//{

//private:
//	bool captured;
//	System::Drawing::Rectangle selection;

//public:
//	property bool Captured {
//		bool get() { return captured; }
//	}
//	property System::Drawing::Rectangle Selection {
//		System::Drawing::Rectangle get() { return selection; }
//	}
//	DxLayoutUpdatedEventArgs(bool captured, System::Drawing::Rectangle selection) :
//		captured(captured), selection(selection) { }

//};

//public ref class DxDisposedEventArgs : public System::EventArgs
//{

//private:
//	System::Collections::Generic::List<System::Exception^>^ errors;

//public:
//	property System::Exception^ Error {
//		System::Exception^ get() {
//			if (errors->Count > 1)
//				return gcnew System::AggregateException(errors->ToArray());
//			else if (errors->Count == 1)
//				return errors[0];
//			else
//				return nullptr;
//		}
//	}
//	DxDisposedEventArgs(System::Collections::Generic::List<System::Exception^>^ errors)
//		: errors(errors) { }

//};

class DxScreenCapture
{

private:
    int _vx;
    int _vy;
    captureArgs _options;
    //WndProcDel^ _del;
    std::mutex sync;
    HANDLE mainThread;
    unsigned int mainThreadId;
    std::vector<render_info> windows;
    std::vector<std::exception> errors;
    //System::Threading::Thread^ main;
    //System::Collections::Generic::List<render_info>^ windows;
    //System::Collections::Generic::List<System::Exception^>^ errors;
    HWND primaryWindow;
    mc_window_native* native;

    static unsigned int __stdcall MessagePumpProc(void* lpParam);
    void RunMessagePump();
    static unsigned int __stdcall RenderThreadProc(void* lpParam);
    void RunRenderLoop(const render_info& wi, const DxDisplay& mon, const ScreenInfo& myscreen);
    static LRESULT CALLBACK WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);
    LRESULT WndProcImpl(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

    void SetFrame(mc_frame_data* data);
    void FrameMakeSelection(mc_frame_data& data, const RECT& sel, bool preserveWindow);
    void FrameUpdateHitTest(mc_frame_data& data);
    void FrameSetCursor(mc_frame_data& data);
    void FrameUpdateHoveredWindow(mc_frame_data& data);
    System::Drawing::Color GetFrameHoveredColor(mc_frame_data* data);

    std::unique_ptr<NativeDib> GetMergedBitmap(bool flipV, bool cropped);
    void WriteToPointer(void* data, int dataSize);
    //void WriteToPointer(System::IntPtr data, int dataSize) { WriteToPointer((void*)data, dataSize); }

public:
    //fnKeyPressed lpfnKeyPressed;
    //fnColorCaptured lpfnColorCaptured;
    //fnLayoutUpdated lpfnLayoutUpdated;
    //fnDisposed lpfnDisposed;
    DxScreenCapture(captureArgs* options);
    ~DxScreenCapture();
    void Reset();
    void Close(bool waitForExit = false);
    System::String SaveSession(System::String sessionDirectory, System::String createdUtc);
    void WriteToClipboard();
    //RECT GetSelection();
    //bool GetCaptured();
    void GetFrame(mc_frame_data* data);
    void GetSelectionRect(RECT& rect)
    {
        rect = native->frame.selection;
        native->screens->TranslateToSystem(rect);
    }


    //event System::EventHandler<DxKeyDownEventArgs^>^ KeyDown;
    //event System::EventHandler<DxDisposedEventArgs^>^ Disposed;
    //event System::EventHandler<DxLayoutUpdatedEventArgs^>^ LayoutUpdated;
    //event System::EventHandler<DxColorCapturedEventArgs^>^ ColorCaptured;

/*	property System::Drawing::Rectangle Selection {
        System::Drawing::Rectangle get() {
            if (native == nullptr) throw gcnew System::ObjectDisposedException("CaptureWindow");
            RECT r = native->frame.selection;
            native->screens->TranslateToSystem(r);
            return Rect2Gdi(&r);
        }
    }*/

};

