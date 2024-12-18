#pragma once
#include "pch.h"

class NativeBitmap
{

protected:
	HDC dcScreen;
	HDC dcBitmap;
	HBITMAP hBitmap;
	HGDIOBJ hOld;
	int w;
	int h;

protected:
	NativeBitmap() {}

public:
	NativeBitmap(int width, int height);
	~NativeBitmap();
	HDC GetBitmapDC();
	int GetWidth();
	int GetHeight();
	void WriteToFilePNG(std::wstring filePath);
};

class NativeDib : public NativeBitmap
{

private:
	BITMAPINFOHEADER dibHeader;
	void* dibPixels;
	int dibSize;

public:
	NativeDib(BITMAPINFO* bmi);
	void GetDetails(DIBSECTION* dib);
	void GetPixels(void** buffer);
	int GetSize();

};

namespace BitmapEx
{
	std::unique_ptr<NativeBitmap> MakeDDBitmap(int width, int height);
	std::unique_ptr<NativeDib> Make24bppDib(int width, int height);
	std::unique_ptr<NativeDib> Make32bppDib(int width, int height);
	std::unique_ptr<NativeDib> MakeGrayscale8bppDib(int width, int height);
	//void Write24bppHBitmapToFile(void* scan0, int width, int height, std::string fileName);

	class __declspec(uuid("{b96b3caf-0728-11d3-9d7b-0000f81ef32e}")) _SPECPngEncoder;
	static const CLSID CLSID_PngEncoder = __uuidof(_SPECPngEncoder);
}