#pragma once
#include "IOutlineText.h"
#include "PngOutlineText.h"

namespace TextDesigner
{

class BmpOutlineText
{
public:
	BmpOutlineText(void);
	virtual ~BmpOutlineText(void);

	Gdiplus::Bitmap* Render(
		UINT nTextX, 
		UINT nTextY,
		Gdiplus::Bitmap* pbmpText, 
		UINT nOutlineX, 
		UINT nOutlineY,
		Gdiplus::Bitmap* pbmpOutline);

	bool DrawString(
		Gdiplus::Graphics* pGraphics, 
		Gdiplus::FontFamily* pFontFamily,
		Gdiplus::FontStyle fontStyle,
		int nfontSize,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw, 
		Gdiplus::StringFormat* pStrFormat,
		int nThickness,
		int nWidth,
		int nHeight,
		bool bGlow,
		BYTE nGlowAlpha);

	bool DrawString(
		Gdiplus::Graphics* pGraphics, 
		Gdiplus::FontFamily* pFontFamily,
		Gdiplus::FontStyle fontStyle,
		int nfontSize,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw,
		Gdiplus::StringFormat* pStrFormat,
		int nThickness,
		int nWidth,
		int nHeight,
		bool bGlow,
		BYTE nGlowAlpha);

	bool GdiDrawString(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw,
		int nThickness,
		int nWidth,
		int nHeight,
		bool bGlow,
		BYTE nGlowAlpha);

	bool GdiDrawString(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw,
		int nThickness,
		int nWidth,
		int nHeight,
		bool bGlow,
		BYTE nGlowAlpha);

	bool DrawString(
		Gdiplus::Graphics* pGraphics, 
		Gdiplus::FontFamily* pFontFamily,
		Gdiplus::FontStyle fontStyle,
		int nfontSize,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw, 
		Gdiplus::StringFormat* pStrFormat,
		int nThickness,
		int nWidth,
		int nHeight,
		bool bGlow,
		BYTE nGlowAlpha,
		bool bShadow,
		Gdiplus::Color clrShadow,
		int nShadowOffsetX,
		int nShadowOffsetY);

	bool DrawString(
		Gdiplus::Graphics* pGraphics, 
		Gdiplus::FontFamily* pFontFamily,
		Gdiplus::FontStyle fontStyle,
		int nfontSize,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw,
		Gdiplus::StringFormat* pStrFormat,
		int nThickness,
		int nWidth,
		int nHeight,
		bool bGlow,
		BYTE nGlowAlpha,
		bool bShadow,
		Gdiplus::Color clrShadow,
		int nShadowOffsetX,
		int nShadowOffsetY);

	bool GdiDrawString(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw,
		int nThickness,
		int nWidth,
		int nHeight,
		bool bGlow,
		BYTE nGlowAlpha,
		bool bShadow,
		Gdiplus::Color clrShadow,
		int nShadowOffsetX,
		int nShadowOffsetY);

	bool GdiDrawString(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw,
		int nThickness,
		int nWidth,
		int nHeight,
		bool bGlow,
		BYTE nGlowAlpha,
		bool bShadow,
		Gdiplus::Color clrShadow,
		int nShadowOffsetX,
		int nShadowOffsetY);

	static bool Measure(
		Gdiplus::Bitmap* png, 
		UINT& nTextX, UINT& nTextY, UINT& nTextWidth, UINT& nTextHeight,
		UINT& nOutlineX, UINT& nOutlineY, UINT& nOutlineWidth, UINT& nOutlineHeight);

	//void SetPngImage(Gdiplus::Bitmap* pPngResult) { m_pbmpResult = pPngResult; }
	Gdiplus::Bitmap* GetInternalMaskImage() { return m_pbmpMask; }
	Gdiplus::Bitmap* GetResultImage() { return m_pbmpResult; }
	Gdiplus::Bitmap* GetShadowImage() { return m_pbmpShadow; }

	//! Save to PNG file format
	bool SavePngFile(const wchar_t* pszFile);

private:
	bool GetEncoderClsid(const WCHAR* format, CLSID* pClsid);

	inline BYTE Clamp(UINT val) 
	{ 
		if(val>255) 
			return 255; 
		else 
			return val; 
	}

	Gdiplus::Bitmap* m_pbmpResult;
	Gdiplus::Bitmap* m_pbmpMask;
	Gdiplus::Bitmap* m_pbmpShadow;
	Gdiplus::Color m_clrBkgd;
	Gdiplus::Color m_clrOutline;
	Gdiplus::Color m_clrText;
	TextDesigner::PngOutlineText m_PngOutlineText;
	TextDesigner::PngOutlineText m_PngShadow;

};

} // ns TextDesigner
