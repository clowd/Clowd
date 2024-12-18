#include "pch.h"
#include "WindowWalker.h"

#include <functional>
#include <unordered_set>

//#include "opencv2/core.hpp"
//#include "opencv2/imgproc.hpp"
//#include "opencv2/imgcodecs.hpp"
//#include "opencv2/highgui.hpp"

#define MIN_WINDOW_CHILD_SIZE (200)
#define MIN_WINDOW_SIZE (25)
#define MERGE_WITH_PARENT_THRESHOLD (60)

#define WND_STRING_SIZE (128)

wchar_t wcClsName[WND_STRING_SIZE];
wchar_t wcCaption[WND_STRING_SIZE];
bool _initialized;
unordered_set<wstring> _clsBlacklist;

//using namespace cv;
////using cv::Mat;
////using cv::Point;
////using cv::Size;
//
//int thresh = 50, N = 11;
//
//static double angle(Point pt1, Point pt2, Point pt0)
//{
//	double dx1 = pt1.x - pt0.x;
//	double dy1 = pt1.y - pt0.y;
//	double dx2 = pt2.x - pt0.x;
//	double dy2 = pt2.y - pt0.y;
//	return (dx1 * dx2 + dy1 * dy2) / sqrt((dx1 * dx1 + dy1 * dy1) * (dx2 * dx2 + dy2 * dy2) + 1e-10);
//}
//
////static void findLines2(const Mat& image, vector<vector<Point>>& lines)
////{
////	lines.clear();
////}
//
//static void findSquares(const Mat& image, vector<vector<Point>>& squares)
//{
//	squares.clear();
//	Mat pyr, timg, gray0(image.size(), CV_8U), gray;
//	// down-scale and upscale the image to filter out the noise
//	pyrDown(image, pyr, Size(image.cols / 2, image.rows / 2));
//	pyrUp(pyr, timg, image.size());
//	vector<vector<Point>> contours;
//	// find squares in every color plane of the image
//	for (int c = 0; c < 3; c++)
//	{
//		int ch[] = { c, 0 };
//		mixChannels(&timg, 1, &gray0, 1, ch, 1);
//		// try several threshold levels
//		for (int l = 0; l < N; l++)
//		{
//			// hack: use Canny instead of zero threshold level.
//			// Canny helps to catch squares with gradient shading
//			if (l == 0)
//			{
//				// apply Canny. Take the upper threshold from slider
//				// and set the lower to 0 (which forces edges merging)
//				Canny(gray0, gray, 0, thresh, 5);
//				// dilate canny output to remove potential
//				// holes between edge segments
//				dilate(gray, gray, Mat(), Point(-1, -1));
//			}
//			else
//			{
//				// apply threshold if l!=0:
//				//     tgray(x,y) = gray(x,y) < (l+1)*255/N ? 255 : 0
//				gray = gray0 >= (l + 1) * 255 / N;
//			}
//			// find contours and store them all as a list
//			findContours(gray, contours, cv::RETR_LIST, cv::CHAIN_APPROX_SIMPLE);
//			vector<Point> approx;
//			// test each contour
//			for (size_t i = 0; i < contours.size(); i++)
//			{
//				// approximate contour with accuracy proportional
//				// to the contour perimeter
//				approxPolyDP(contours[i], approx, arcLength(contours[i], true) * 0.02, true);
//				// square contours should have 4 vertices after approximation
//				// relatively large area (to filter out noisy contours)
//				// and be convex.
//				// Note: absolute value of an area is used because
//				// area may be positive or negative - in accordance with the
//				// contour orientation
//				if (approx.size() == 4 && fabs(contourArea(approx)) > 1000 && isContourConvex(approx))
//				{
//					double maxCosine = 0;
//					for (int j = 2; j < 5; j++)
//					{
//						// find the maximum cosine of the angle between joint edges
//						double cosine = fabs(angle(approx[j % 4], approx[j - 2], approx[j - 1]));
//						maxCosine = MAX(maxCosine, cosine);
//					}
//					// if cosines of all angles are small
//					// (all angles are ~90 degree) then write quandrange
//					// vertices to resultant sequence
//					if (maxCosine < 0.3)
//						squares.push_back(approx);
//				}
//			}
//		}
//	}
//}

bool CheckHwndAccess(HWND hWnd)
{
    DWORD processId;
    DWORD threadID = GetWindowThreadProcessId(hWnd, &processId);
    HANDLE hProc = 0, hToken = 0;
    BOOL result = FALSE;

    hProc = OpenProcess(PROCESS_QUERY_INFORMATION, FALSE, processId);

    // if we were unable to open the process, most likely error is we have no priveledge to do so
    // see https://docs.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights
    if (hProc == NULL)
        goto exit;

    if (!OpenProcessToken(hProc, TOKEN_QUERY, &hToken))
        goto exit;

    // TODO should we check if process is elevated? GetTokenInformation? might not be needed.

    result = TRUE; // we were able to open the process successfully

exit:
    if (hToken) CloseHandle(hToken);
    if (hProc) CloseHandle(hProc);
    return result;
}


void WindowWalker::GetWndTrueBounds(HWND hWnd, WINDOWINFO& winfo, RECT* bounds)
{
    bool maximized = (winfo.dwStyle & WS_MAXIMIZE) > 0;
    if (maximized)
    {
        HMONITOR hMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        const RECT& mrcwork = _screens->ScreenFromHMONITOR(hMon).realWork;

        // if the window is maximized, we just return the working area of that monitor.
        CopyRect(bounds, &mrcwork);
        return;
    }

    BOOL dwmEnabled;
    HRESULT hr = DwmIsCompositionEnabled(&dwmEnabled);
    if (SUCCEEDED(hr) && dwmEnabled)
    {
        hr = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, bounds, sizeof(RECT));
        if (SUCCEEDED(hr))
        {
            return;
        }
    }

    // if we reach here, fallback to the regular window bounds
    CopyRect(bounds, &winfo.rcWindow);
}

BOOL CALLBACK EnumSearchApplicationFrameProc(HWND hWnd, LPARAM lParam)
{
    vector<wstring>* classes = reinterpret_cast<vector<wstring>*>(lParam);
    GetClassName(hWnd, wcClsName, WND_STRING_SIZE);
    classes->emplace_back(wcClsName);
    return TRUE;
}

void WindowInfo::PrintImage()
{
    if (!obstructed) return;
    if (captured) return;

    if (!CheckHwndAccess(hWnd))
    {
        // we have no permission to capture this window
        obstructed = false;
        return;
    }

    _wndPaintStart = std::chrono::high_resolution_clock::now();
    int width = RECT_WIDTH(rcWindow);
    int height = RECT_HEIGHT(rcWindow);
    _dib = BitmapEx::Make32bppDib(width, -height);
    PrintWindow(hWnd, _dib->GetBitmapDC(), PW_RENDERFULLCONTENT);
    _wndPaintEnd = std::chrono::high_resolution_clock::now();
    captured = true;
}

bool WindowInfo::GetBitmap(NativeDib** dib, int* cropX, int* cropY)
{
    if (!obstructed) return false;
    if (!captured) return false;
    *cropX = rcTrueWindow.left - rcWindow.left; // negative?
    *cropY = rcTrueWindow.top - rcWindow.top;
    *dib = _dib.get();
    return true;
}

void WindowInfo::BitBltImage(HDC dest, int x, int y)
{
    if (!obstructed) return;
    if (!captured) return;

    int width = RECT_WIDTH(rcTrueWindow);
    int height = RECT_HEIGHT(rcTrueWindow);

    auto xCropOffset = rcTrueWindow.left - rcWindow.left; // negative?
    auto yCropOffset = rcTrueWindow.top - rcWindow.top;

    BitBlt(dest, x, y, width, height, _dib->GetBitmapDC(), xCropOffset, yCropOffset, SRCCOPY);
}

double WindowInfo::GetTimeToRender()
{
    auto frameTimeNs = _wndPaintEnd - _wndPaintStart;
    return frameTimeNs.count() / (double)1000000;
}

vector<WindowInfo>* WindowWalker::List()
{
    return &_topWindows;
}

WindowWalker::WindowWalker(Screens* screens)
{
    _screenDC = GetDC(HWND_DESKTOP);
    _screens = screens;
    auto workspace = _screens->WorkspaceBounds();
    _excludedArea = make_unique<Gdiplus::Region>(Rect2Gdiplus(&workspace));
    _topWindows.reserve(100); // we don't want this to realloc

    DxRef<IServiceProvider> pServiceProvider; // will be disposed automatically
    HR(::CoCreateInstance(CLSID_ImmersiveShell, NULL, CLSCTX_LOCAL_SERVER, __uuidof(IServiceProvider), pServiceProvider.put_void()));

    IVirtualDesktopManager* pDesktopManager = NULL;
    HR(pServiceProvider->QueryService(__uuidof(IVirtualDesktopManager), &pDesktopManager));

    _vd = pDesktopManager;

    // this is static and is only done once.
    if (!_initialized)
    {
        _clsBlacklist.insert(L"WorkerW");
        _clsBlacklist.insert(L"Progman");
        _clsBlacklist.insert(L"Shell_TrayWnd");
        _clsBlacklist.insert(L"EdgeUiInputWndClass");
        _clsBlacklist.insert(L"TaskListThumbnailWnd");
        _clsBlacklist.insert(L"LauncherTipWndClass");
        _clsBlacklist.insert(L"SearchPane");
        _clsBlacklist.insert(L"ImmersiveLauncher");
        _clsBlacklist.insert(L"Touch Tooltip Window");
        _clsBlacklist.insert(L"Windows.UI.Core.CoreWindow");
        _clsBlacklist.insert(L"Immersive Chrome Container");
        _clsBlacklist.insert(L"ImmersiveBackgroundWindow");
        _clsBlacklist.insert(L"NativeHWNDHost");
        _clsBlacklist.insert(L"Snapped Desktop");
        _clsBlacklist.insert(L"ModeInputWnd");
        _clsBlacklist.insert(L"MetroGhostWindow");
        _clsBlacklist.insert(L"Shell_Dim");
        _clsBlacklist.insert(L"Shell_Dialog");
        _clsBlacklist.insert(L"ApplicationManager_ImmersiveShellWindow");
        _initialized = true;
    }
}

WindowWalker::~WindowWalker()
{
    ReleaseDC(HWND_DESKTOP, _screenDC);
    _vd->Release();
}

//void WindowWalker::Process24bbpBitmapRectangles(int width, int height, void* scan0)
//{
//	_rectangles.clear();
//	Mat src(height, width, CV_8UC3, scan0, CalcStride(24, width));
//	vector<vector<Point>> squares{};
//	findSquares(src, squares);
//
//	for (int i = 0; i < squares.size(); i++)
//	{
//		vector<Point>& pts = squares[i];
//		Point& p1 = pts[0];
//		Point& p2 = pts[1];
//		Point& p3 = pts[2];
//		Point& p4 = pts[3];
//
//		int x1 = min(min(min(p1.x, p2.x), p3.x), p4.x);
//		int x2 = max(max(max(p1.x, p2.x), p3.x), p4.x);
//		int y1 = min(min(min(p1.y, p2.y), p3.y), p4.y);
//		int y2 = max(max(max(p1.y, p2.y), p3.y), p4.y);
//
//		_rectangles.push_back({ x1, y1, x2, y2 });
//	}
//
//	src.release();
//}

void WindowWalker::ShapshotTopLevel()
{
    EnumWindows(EnumWindowProc, reinterpret_cast<LPARAM>(this));
}

BOOL CALLBACK WindowWalker::EnumWindowProc(HWND hWnd, LPARAM lParam)
{
    WindowWalker* pthis = reinterpret_cast<WindowWalker*>(lParam);
    pthis->EnumWindowCallback(hWnd);
    return TRUE;
}

WindowInfo* WindowWalker::PointToWindow(const POINT& p)
{
    auto tlsize = _topWindows.size();
    for (int i = 0; i < tlsize; i++)
    {
        WindowInfo* wi = &_topWindows[i];
        if (PtInRect(&wi->rcWorkspace, p))
        {
            return wi;
        }
    }

    return nullptr;
}

bool WindowWalker::HitTestV2(const POINTFF& wp, HitV2Result* hitResult)
{
    ResetHitResult(hitResult);

    WindowInfo* top = PointToWindow({ (int)floor(wp.x), (int)floor(wp.y) });

    if (top == nullptr)
        return false;

    hitResult->idealSelection = top->rcWorkspace;

    const POINT sysPt = _screens->ToSystemPt(wp);
    RECT parentRect = top->rcWorkspace;
    HWND parent = top->hWnd;

    bool visible = true;
    int idx = 0;
    while (parent != nullptr && idx < ARRAYSIZE(hitResult->rects))
    {
        POINT parentPt = sysPt; // copy
        ScreenToClient(parent, &parentPt); // translate

        HWND child = RealChildWindowFromPoint(parent, parentPt);
        if (child == nullptr || child == parent)
            break; // done searching

        HitV2Rect& href = hitResult->rects[idx];

        // populate data
        href.hWnd = child;
        href.style = GetWindowLong(child, GWL_STYLE);
        href.exStyle = GetWindowLong(child, GWL_EXSTYLE);
        href.scrollH = (href.style & WS_HSCROLL) > 0;
        href.scrollV = (href.style & WS_VSCROLL) > 0;

        // get workspace position
        GetClientRect(child, &href.workspaceRect);
        ClientToScreen(child, reinterpret_cast<POINT*>(&href.workspaceRect.left)); // convert top-left
        ClientToScreen(child, reinterpret_cast<POINT*>(&href.workspaceRect.right)); // convert bottom-right
        _screens->TranslateToWorkspace(href.workspaceRect);

        if (!visible)
        {
            swprintf(href.reason, ARRAYSIZE(href.reason), L"%s", L"(inherited)");
        }

        if ((href.exStyle & WS_EX_TOOLWINDOW) > 0)
        {
            visible = false; // dont capture floating toolbars
            swprintf(href.reason, ARRAYSIZE(href.reason), L"%s", L"(floating toolbar)");
        }

        if (RECT_WIDTH(href.workspaceRect) < MIN_WINDOW_CHILD_SIZE || RECT_HEIGHT(href.workspaceRect) < MIN_WINDOW_CHILD_SIZE)
        {
            visible = false; // too small
            swprintf(href.reason, ARRAYSIZE(href.reason), L"%s", L"(too small)");
        }

        // if this window is practically the same size as the parent, lets skip but keep searching
        if (abs(parentRect.left - href.workspaceRect.left) < MERGE_WITH_PARENT_THRESHOLD &&
            abs(parentRect.top - href.workspaceRect.top) < MERGE_WITH_PARENT_THRESHOLD &&
            abs(parentRect.right - href.workspaceRect.right) < MERGE_WITH_PARENT_THRESHOLD &&
            abs(parentRect.bottom - href.workspaceRect.bottom) < MERGE_WITH_PARENT_THRESHOLD)
        {
            // we don't update the top level 'visible' so future rects can be found
            href.visible = false;
            swprintf(href.reason, ARRAYSIZE(href.reason), L"%s", L"(similar to parent)");
        }
        else
        {
            href.visible = visible;
        }

        idx++;
        parent = child;
        parentRect = href.workspaceRect;

        if (href.visible)
            hitResult->idealSelection = href.workspaceRect;
    }

    hitResult->window = top;
    hitResult->cRects = idx;

    ClipRectBy(&hitResult->idealSelection, &top->rcWorkspace);

    return true;
}

void WindowWalker::ResetHitResult(HitV2Result* hitResult)
{
    memset(&hitResult->rects, 0, sizeof(hitResult->rects));
    hitResult->cRects = 0;
    hitResult->window = nullptr;
}

void WindowWalker::EnumWindowCallback(HWND hWnd)
{
    // we try to eliminate windows as quickly and as cheaply as possible.. 
    // ie, if they are too small, minimized, invisible overlays, etc.

    WINDOWINFO winfo{};
    winfo.cbSize = sizeof(WINDOWINFO);
    GetWindowInfo(hWnd, &winfo);

    // ignore: obviously invisible or minimized windows
    if ((winfo.dwStyle & WS_VISIBLE) == 0)
        return;

    if ((winfo.dwStyle & WS_MINIMIZE) > 0)
        return;

    // ignore: windows which are probably transparent overlays / do not exist
    if ((winfo.dwStyle & WS_CAPTION) == 0 && (winfo.dwExStyle & WS_EX_LAYERED) != 0)
        return;

    // ignore: 0 size windows
    if (IsRectEmpty(&winfo.rcWindow))
        return;

    // ignore: windows on other desktops
    BOOL onCurrentDesktop;
    _vd->IsWindowOnCurrentVirtualDesktop(hWnd, &onCurrentDesktop);
    if (!onCurrentDesktop)
        return;

    // ignore: windows that are DWM cloaked
    int dwmCloaked = 0;
    if (S_OK == DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, &dwmCloaked, sizeof(dwmCloaked)) && dwmCloaked != 0)
        return;

    RECT rcTrueWindow;
    GetWndTrueBounds(hWnd, winfo, &rcTrueWindow);

    RECT rcWorkspace = rcTrueWindow;
    _screens->TranslateToWorkspace(rcWorkspace);

    auto gdip = Rect2Gdiplus(&rcWorkspace);

    // ignore: windows that are too small
    if (gdip.Width < MIN_WINDOW_SIZE || gdip.Height < MIN_WINDOW_SIZE)
        return;

    // ignore: blacklisted window classes, like known transparent overlays.
    GetClassName(hWnd, wcClsName, WND_STRING_SIZE);
    wstring clsName(wcClsName);
    if (_clsBlacklist.count(clsName) > 0)
        return;

    // ignore: windows with no caption
    GetWindowText(hWnd, wcCaption, WND_STRING_SIZE);
    wstring caption(wcCaption);
    if (caption.length() == 0)
        return;

    // ignore: phantom metro windows
    // if it's class is ApplicationFrameWindow, and it has the children ApplicationFrameInputSinkWindow 
    // and ApplicationFrameTitleBarWindow and not Windows.UI.Core.CoreWindow, it is not a real window.
    if (clsName == L"ApplicationFrameWindow")
    {
        vector<wstring> classes;
        EnumChildWindows(hWnd, EnumSearchApplicationFrameProc, reinterpret_cast<LPARAM>(&classes));
        bool hasFrame = false;
        bool hasCore = false;
        for (int i = 0; i < classes.size(); i++)
        {
            wstring& item = classes[i];
            if (!hasFrame && (item == L"ApplicationFrameInputSinkWindow" || item == L"ApplicationFrameTitleBarWindow"))
            {
                hasFrame = true;
                if (hasCore) break;
            }
            if (!hasCore && item == L"Windows.UI.Core.CoreWindow")
            {
                hasCore = true;
                if (hasFrame) break;
            }
        }
        if (hasFrame && !hasCore)
        {
            return; // not a real window
        }
    }

    // ignore: windows entirely covered by other windows
    if (!_excludedArea->IsVisible(gdip))
        return;

    _excludedArea->Exclude(gdip);

    // add window to search results
    _topWindows.emplace_back(_screenDC, hWnd,
        caption, clsName, winfo.dwStyle, winfo.dwExStyle,
        rcWorkspace, rcTrueWindow, winfo.rcWindow, _topWindows.size());

    // record if this window is partially obstructed
    // TODO - record windows as obstructed if they are partially off screen
    WindowInfo& myinfo = _topWindows.back();
    RECT intersect;
    for (int z = _topWindows.size() - 2; z >= 0; z--) // (-2) because exclude the window we just added
    {
        IntersectRect(&intersect, &_topWindows[z].rcWorkspace, &rcWorkspace);
        if (!IsRectEmpty(&intersect))
        {
            myinfo.obstructed = true;
            myinfo.obstructionRects.push_back(intersect);
        }
    }
}