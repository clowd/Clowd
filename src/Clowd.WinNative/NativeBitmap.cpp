#include "pch.h"
#include "NativeBitmap.h"
#include "rectex.h"

//#include "opencv2/core.hpp"
//#include "opencv2/imgproc.hpp"
//#include "opencv2/imgcodecs.hpp"
//#include "opencv2/highgui.hpp"

NativeBitmap::NativeBitmap(int width, int height)
{
	dcScreen = GetDC(HWND_DESKTOP);
	dcBitmap = CreateCompatibleDC(dcScreen);
	hBitmap = CreateCompatibleBitmap(dcScreen, width, height);
	hOld = SelectObject(dcBitmap, hBitmap);

	w = width;
	h = height;
}

NativeDib::NativeDib(BITMAPINFO* bmi)
{
	void* pixels;

	dcScreen = GetDC(HWND_DESKTOP);
	dcBitmap = CreateCompatibleDC(dcScreen);
	hBitmap = CreateDIBSection(dcScreen, bmi, DIB_RGB_COLORS, &pixels, 0, 0);
	hOld = SelectObject(dcBitmap, hBitmap);

	w = bmi->bmiHeader.biWidth;
	h = abs(bmi->bmiHeader.biHeight);
	dibPixels = pixels;
	dibSize = bmi->bmiHeader.biSizeImage;
	dibHeader = bmi->bmiHeader;
}

void NativeDib::GetDetails(DIBSECTION* dib)
{
	GetObject(hBitmap, sizeof(DIBSECTION), dib);
}

void NativeDib::GetPixels(void** buffer)
{
	*buffer = dibPixels;
}

int NativeDib::GetSize()
{
	return dibSize;
}

NativeBitmap::~NativeBitmap()
{
	SelectObject(dcBitmap, hOld);
	DeleteObject(hBitmap);
	DeleteDC(dcBitmap);
	ReleaseDC(HWND_DESKTOP, dcScreen);
}

HDC NativeBitmap::GetBitmapDC()
{
	return dcBitmap;
}

int NativeBitmap::GetWidth()
{
	return w;
}

int NativeBitmap::GetHeight()
{
	return h;
}

void NativeBitmap::WriteToFilePNG(std::wstring filePath)
{
	auto gdi = std::unique_ptr<Gdiplus::Bitmap>(Gdiplus::Bitmap::FromHBITMAP(hBitmap, 0));
	CLSID pngClsid;
	CLSIDFromString(L"{557CF406-1A04-11D3-9A73-0000F81EF32E}", &pngClsid);
	gdi->Save(filePath.c_str(), &pngClsid, NULL);
}

namespace BitmapEx
{

	std::unique_ptr<NativeBitmap> MakeDDBitmap(int width, int height)
	{
		return std::make_unique<NativeBitmap>(width, height);
	}

	std::unique_ptr<NativeDib> Make24bppDib(int width, int height)
	{
		int size = CalcStride(24, width) * abs(height);

		auto bmi = mkcallocobj<BITMAPINFO>(sizeof(BITMAPINFOHEADER));
        auto& header = bmi->bmiHeader;
        header.biSize = sizeof(BITMAPINFOHEADER);
        header.biBitCount = 24;
        header.biCompression = BI_RGB;
        header.biHeight = height;
        header.biWidth = width;
        header.biPlanes = 1;
        header.biSizeImage = size;

		return std::make_unique<NativeDib>(bmi.get());
	}

	std::unique_ptr<NativeDib> Make32bppDib(int width, int height)
	{
		int size = CalcStride(32, width) * abs(height);

		auto bmi = mkcallocobj<BITMAPINFO>(sizeof(BITMAPINFOHEADER));
        auto& header = bmi->bmiHeader;
        header.biSize = sizeof(BITMAPINFOHEADER);
        header.biBitCount = 32;
        header.biCompression = BI_RGB;
        header.biHeight = height;
        header.biWidth = width;
        header.biPlanes = 1;
        header.biSizeImage = size;

		return std::make_unique<NativeDib>(bmi.get());
	}

	std::unique_ptr<NativeDib> MakeGrayscale8bppDib(int width, int height)
	{
		int size = CalcStride(8, width) * abs(height);

		auto bmi = mkcallocobj<BITMAPINFO>(sizeof(BITMAPINFOHEADER) + (sizeof(RGBQUAD) * 256));
        auto& header = bmi->bmiHeader;
        header.biSize = sizeof(BITMAPINFOHEADER);
        header.biBitCount = 8;
        header.biClrImportant = 256;
        header.biClrUsed = 256;
        header.biCompression = BI_RGB;
        header.biHeight = height;
        header.biWidth = width;
        header.biPlanes = 1;
        header.biSizeImage = size;

		for (byte i = 0; ; i++)
		{
			bmi->bmiColors[i].rgbRed = i;
			bmi->bmiColors[i].rgbGreen = i;
			bmi->bmiColors[i].rgbBlue = i;
			if (i == 255) break;
		}

		return std::make_unique<NativeDib>(bmi.get());
	}

	//void Write24bppHBitmapToFile(void* scan0, int width, int height, std::string fileName)
	//{
	//	auto cstr = fileName.c_str();
	//	cv::Mat src(height, width, CV_8UC3, scan0, CalcStride(24, width));
	//	//cv::Rect cvcrop(crop.left, crop.top, RECT_WIDTH(crop), RECT_HEIGHT(crop));
	//	//cv::Mat cropped = src(cvcrop);
	//	//cv::Mat flipped;
	//	//cv::flip(src, flipped, 0);
	//	cv::imwrite(cstr, src);
	//}

}
