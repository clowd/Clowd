#pragma once

#ifndef MYRECTEX_H
#define MYRECTEX_H

#define RECT_WIDTH(r) ((r).right - (r).left)
#define RECT_HEIGHT(r) ((r).bottom - (r).top)
#define PRECT_WIDTH(r) ((r)->right - (r)->left)
#define PRECT_HEIGHT(r) ((r)->bottom - (r)->top)

inline int CalcStride(unsigned short bpp, int width)
{
	return (bpp * width + 31) / 32 * 4;
}

inline POINT RectCenterPt(const RECT* rect) 
{
    auto w = PRECT_WIDTH(rect);
    auto h = PRECT_WIDTH(rect);
    return POINT{ rect->left + (w / 2), rect->top + (h / 2) };
}

inline void Gdi2Rect(System::Drawing::Rectangle& gdi, RECT* rect)
{
	rect->left = gdi.GetLeft();
	rect->top = gdi.GetTop();
	rect->right = gdi.GetRight();
	rect->bottom = gdi.GetBottom();
}

inline System::Drawing::Rectangle Rect2Gdi(const RECT* rect)
{
	return System::Drawing::Rectangle(rect->left, rect->top, PRECT_WIDTH(rect), PRECT_HEIGHT(rect));
}

inline Gdiplus::Rect Rect2Gdiplus(const RECT* rect)
{
	return Gdiplus::Rect(rect->left, rect->top, PRECT_WIDTH(rect), PRECT_HEIGHT(rect));
}

inline Gdiplus::Rect Rect2Gdiplus(System::Drawing::Rectangle& gdi)
{
    return gdi;
	//return Gdiplus::Rect(gdi.GetLeft(), gdi.GetTop(), gdi.Width, gdi.Height);
}

inline Gdiplus::RectF Rect2GdiplusF(const RECT* rect)
{
	return Gdiplus::RectF(rect->left, rect->top, PRECT_WIDTH(rect), PRECT_HEIGHT(rect));
}

inline std::wstring Rect2String(const RECT* rect)
{
	std::wostringstream stream;
	stream << "RECT[" << rect->left << ", " << rect->top << ", " << ((rect)->right - (rect)->left) << ", " << ((rect)->bottom - (rect)->top) << "]";
	return stream.str();
}

inline void PrintRect(const RECT* rect)
{
	std::cout << "RECT[" << rect->left << ", " << rect->top << ", " << ((rect)->right - (rect)->left) << ", " << ((rect)->bottom - (rect)->top) << "]\n";
}

inline void GetVirtualDesktopRect(RECT* rect)
{
	rect->left = GetSystemMetrics(SM_XVIRTUALSCREEN);
	rect->top = GetSystemMetrics(SM_YVIRTUALSCREEN);
	int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
	int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
	rect->right = rect->left + width;
	rect->bottom = rect->top + height;
}

inline void ResetRect(RECT* rect)
{
	rect->left = 0;
	rect->top = 0;
	rect->right = 0;
	rect->bottom = 0;
}

inline void ClipRectBy(RECT* clip, const RECT* by)
{
	clip->left = max(clip->left, by->left);
	clip->top = max(clip->top, by->top);
	clip->right = min(clip->right, by->right);
	clip->bottom = min(clip->bottom, by->bottom);
}

inline BOOL BitBltRect(HDC hdcSrc, HDC hdcDest, const RECT* rect)
{
	return BitBlt(hdcDest, rect->left, rect->top, PRECT_WIDTH(rect), PRECT_HEIGHT(rect), hdcSrc, rect->left, rect->top, SRCCOPY);
}

inline BOOL BitBltRect(HDC hdcSrc, HDC hdcDest, const RECT* rect, DWORD rop)
{
	return BitBlt(hdcDest, rect->left, rect->top, PRECT_WIDTH(rect), PRECT_HEIGHT(rect), hdcSrc, rect->left, rect->top, rop);
}

inline BOOL BitBltRectAlpha(HDC hdcSrc, HDC hdcDest, const RECT* rect, BYTE alpha)
{
	BLENDFUNCTION bf;
	bf.BlendOp = AC_SRC_OVER;
	bf.BlendFlags = 0;
	bf.SourceConstantAlpha = alpha;
	bf.AlphaFormat = 0;
	return AlphaBlend(hdcDest, rect->left, rect->top, PRECT_WIDTH(rect), PRECT_HEIGHT(rect),
		hdcSrc, rect->left, rect->top, PRECT_WIDTH(rect), PRECT_HEIGHT(rect), bf);
}

inline void Xy12Rect(RECT* rect, int x1, int y1, int x2, int y2)
{
	rect->left = min(x1, x2);
	rect->top = min(y1, y2);
	rect->right = max(x1, x2);
	rect->bottom = max(y1, y2);
}

inline RECT PtToWidenedRect(int radius, int x1, int y1)
{
	RECT rect;
	rect.left = x1 - radius;
	rect.right = x1 + radius;
	rect.top = y1 - radius;
	rect.bottom = y1 + radius;
	return rect;
}

inline RECT LineToWidenedRect(int radius, int x1, int y1, int x2, int y2)
{
	RECT rect;
	rect.left = min(x1, x2) - radius;
	rect.top = min(y1, y2) - radius;
	rect.right = max(x1, x2) + radius;
	rect.bottom = max(y1, y2) + radius;
	return rect;
}

inline float DistancePtToPt(int x1, int y1, int x2, int y2)
{
	return sqrt(pow(x2 - x1, 2) + pow(y2 - y1, 2) * 1.0);
}

//inline float DistancePtToLine(POINT& pt, POINT& ln1, POINT& ln2)
//{
//	// http://csharphelper.com/blog/2016/09/find-the-shortest-distance-between-a-point-and-a-line-segment-in-c/
//	float dx = ln2.x - ln1.x;
//	float dy = ln2.y - ln1.y;
//
//	float t = ((pt.x - ln1.x) * dx + (pt.y - ln1.y) * dy) / (dx * dx + dy * dy);
//
//	if (t < 0) // end
//	{
//		dx = pt.x - ln1.x;
//		dy = pt.y - ln1.y;
//	}
//	else if (t > 1) // other end
//	{
//		dx = pt.x - ln2.x;
//		dy = pt.y - ln2.y;
//	}
//	else // middle somewhere
//	{
//		POINT closest{ ln1.x + t * dx, ln1.y + t * dy };
//		dx = pt.y - closest.x;
//		dy = pt.y - closest.x;
//	}
//
//	return sqrt(dx * dx + dy * dy);
//}
//
//inline float DistancePtToRect(POINT& pt, RECT& rect)
//{
//	//FLOAT tl = DistancePtToPt(rect.left, rect.top, pt.x, pt.y);
//	//FLOAT tr = DistancePtToPt(rect.right, rect.top, pt.x, pt.y);
//	//FLOAT bl = DistancePtToPt(rect.left, rect.bottom, pt.x, pt.y);
//	//FLOAT br = DistancePtToPt(rect.right, rect.bottom, pt.x, pt.y);
//
//	if (PtInRect(&rect, pt))
//		return 0;
//
//	// I don't know why but this is still wrong and janky
//
//	FLOAT top = DistancePtToLine(pt, POINT{ rect.left, rect.top }, POINT{ rect.right, rect.top });
//	FLOAT right = DistancePtToLine(pt, POINT{ rect.right, rect.top }, POINT{ rect.right, rect.bottom });
//	FLOAT bottom = DistancePtToLine(pt, POINT{ rect.left, rect.bottom }, POINT{ rect.right, rect.bottom });
//	FLOAT left = DistancePtToLine(pt, POINT{ rect.left, rect.top }, POINT{ rect.left, rect.bottom });
//
//	return min(min(min(top, right), bottom), left);
//}

inline HRGN CreateRgnFromMultipleRects(RECT* rgnArr, int count)
{
	if (count <= 0)
	{
		return CreateRectRgn(0, 0, 0, 0);
	}

	size_t rdataSize = sizeof(RGNDATAHEADER) + (sizeof(RECT) * count);
	auto rdata = mkcallocobj<RGNDATA>(rdataSize);
    auto& header = rdata->rdh;
    header.dwSize = sizeof(RGNDATAHEADER);
    header.iType = RDH_RECTANGLES;
    header.nCount = count;
    header.nRgnSize = (sizeof(RECT) * count);

	RECT bounds = rgnArr[0];

	RECT* rdhBuf = (RECT*)rdata->Buffer;
	for (int i = 0; i < count; i++)
	{
		RECT& r = rgnArr[i];

		bounds.left = min(bounds.left, r.left);
		bounds.top = min(bounds.top, r.top);
		bounds.right = max(bounds.right, r.right);
		bounds.bottom = max(bounds.bottom, r.bottom);

		rdhBuf[i] = r;
	}

	rdata->rdh.rcBound = bounds;

	return ExtCreateRegion(0, rdataSize, rdata.get());
}

inline void GrowRect(RECT* rect, int growBy)
{
	rect->left -= growBy;
	rect->top -= growBy;
	rect->right += growBy;
	rect->bottom += growBy;
}

#endif