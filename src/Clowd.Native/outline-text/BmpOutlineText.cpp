#include "pch.h"
#include "BmpOutlineText.h"

using namespace TextDesigner;

BmpOutlineText::BmpOutlineText(void)
: m_pbmpResult(NULL)
, m_pbmpMask(NULL)
, m_pbmpShadow(NULL)
, m_clrBkgd(0,255,0)
, m_clrOutline(255,0,0)
, m_clrText(0,0,255)
{
}

BmpOutlineText::~BmpOutlineText(void)
{
	if(m_pbmpMask)
	{
		delete m_pbmpMask;
		m_pbmpMask = NULL;
	}
	if(m_pbmpShadow)
	{
		delete m_pbmpShadow;
		m_pbmpShadow = NULL;
	}
}

Gdiplus::Bitmap* BmpOutlineText::Render(
	UINT nTextX, 
	UINT nTextY,
	Gdiplus::Bitmap* pbmpText, 
	UINT nOutlineX, 
	UINT nOutlineY,
	Gdiplus::Bitmap* pbmpOutline)
{
	if(!pbmpText)
		return NULL;
	if(!pbmpOutline)
		return NULL;

	m_pbmpResult = new Gdiplus::Bitmap(pbmpOutline->GetWidth()+nOutlineX, pbmpOutline->GetHeight()+nOutlineY, PixelFormat32bppARGB);

	
	using namespace Gdiplus;
	Bitmap* png = m_PngOutlineText.GetPngImage();
	BitmapData bitmapDataResult;
	BitmapData bitmapDataText;
	BitmapData bitmapDataOutline;
	BitmapData bitmapDataPng;
	Rect rectResult(0, 0, m_pbmpResult->GetWidth(), m_pbmpResult->GetHeight() );
	Rect rectText(0, 0, pbmpText->GetWidth(), pbmpText->GetHeight() );
	Rect rectOutline(0, 0, pbmpOutline->GetWidth(), pbmpOutline->GetHeight() );
	Rect rectPng(0, 0, m_pbmpMask->GetWidth(), m_pbmpMask->GetHeight() );

	m_pbmpResult->LockBits(
		&rectResult,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataResult );
	pbmpText->LockBits(
		&rectText,
		ImageLockModeRead,
		PixelFormat32bppARGB,
		&bitmapDataText );
	pbmpOutline->LockBits(
		&rectOutline,
		ImageLockModeRead,
		PixelFormat32bppARGB,
		&bitmapDataOutline );
	png->LockBits(
		&rectPng,
		ImageLockModeRead,
		PixelFormat32bppARGB,
		&bitmapDataPng );

	UINT* pixelsResult = (UINT*)bitmapDataResult.Scan0;
	UINT* pixelsText = (UINT*)bitmapDataText.Scan0;
	UINT* pixelsOutline = (UINT*)bitmapDataOutline.Scan0;
	UINT* pixelsPng = (UINT*)bitmapDataPng.Scan0;

	if( !pixelsResult || !pixelsText || !pixelsOutline || !pixelsPng )
		return NULL;

	UINT col = 0;
	int strideResult = bitmapDataResult.Stride >> 2;
	int strideOutline = bitmapDataOutline.Stride >> 2;
	int strideText = bitmapDataText.Stride >> 2;
	int stridePng = bitmapDataPng.Stride >> 2;

	for(UINT row = 0; row < bitmapDataResult.Height; ++row)
	{
		for(col = 0; col < bitmapDataResult.Width; ++col)
		{
			UINT indexResult = row * strideResult + col;
			UINT indexText = (row-nTextY)* strideText + (col-nTextX);
			UINT indexOutline = (row-nOutlineY) * strideOutline + (col-nOutlineX);
			UINT indexPng = row * stridePng + col;
			BYTE red = (pixelsPng[indexPng] & 0xff0000) >> 16;
			BYTE blue = pixelsPng[indexPng] & 0xff;

			if(red>0&&blue>0)
			{
				UINT nOutlineColor = pixelsOutline[indexOutline];
				BYTE aOutline = (nOutlineColor & 0xff000000) >> 24;
				BYTE rOutline = (nOutlineColor & 0xff0000) >> 16;
				BYTE gOutline = (nOutlineColor & 0xff00) >> 8;
				BYTE bOutline = (nOutlineColor & 0xff);

				if(aOutline>0)
				{
					aOutline = red * aOutline >> 8;
					rOutline = red * rOutline >> 8;
					gOutline = red * gOutline >> 8;
					bOutline = red * bOutline >> 8;
				}
				else
				{
					//aOutline = 0;
					rOutline = 0;
					gOutline = 0;
					bOutline = 0;
				}

				UINT nTextColor = pixelsText[indexText];
				BYTE aText = (nTextColor & 0xff000000) >> 24;
				BYTE rText = (nTextColor & 0xff0000) >> 16;
				BYTE gText = (nTextColor & 0xff00) >> 8;
				BYTE bText = (nTextColor & 0xff);

				if(aText>0)
				{
					aText = blue * aText >> 8;
					rText = blue * rText >> 8;
					gText = blue * gText >> 8;
					bText = blue * bText >> 8;
				}
				else
				{
					//aText = 0;
					rText = 0;
					gText = 0;
					bText = 0;
				}


				if(aText>0&&aOutline>0)
				{
					pixelsResult[indexResult] = ( 0xff << 24) | ( Clamp(rOutline+rText) << 16) | ( Clamp(gOutline+gText) << 8) | Clamp(bOutline+bText);
				}
				else if(aOutline>0)
					pixelsResult[indexResult] = ( aOutline << 24) | ( Clamp(rOutline+rText) << 16) | ( Clamp(gOutline+gText) << 8) | Clamp(bOutline+bText);
				else if(aText>0)
					pixelsResult[indexResult] = ( aText << 24) | ( Clamp(rOutline+rText) << 16) | ( Clamp(gOutline+gText) << 8) | Clamp(bOutline+bText);
				else
					pixelsResult[indexResult] = 0;


			}
			else if(red>0)
			{
				UINT nOutlineColor = pixelsOutline[indexOutline];
				BYTE a = (nOutlineColor & 0xff000000) >> 24;
				BYTE r = (nOutlineColor & 0xff0000) >> 16;
				BYTE g = (nOutlineColor & 0xff00) >> 8;
				BYTE b = (nOutlineColor & 0xff);

				if(a>0)
					pixelsResult[indexResult] = (red << 24) | (r << 16) | (g << 8) | b;
				else
					pixelsResult[indexResult] = 0;

			}
			else if(blue>0)
			{
				UINT nTextColor = pixelsText[indexText];
				BYTE a = (nTextColor & 0xff000000) >> 24;
				BYTE r = (nTextColor & 0xff0000) >> 16;
				BYTE g = (nTextColor & 0xff00) >> 8;
				BYTE b = (nTextColor & 0xff);

				if(a>0)
					pixelsResult[indexResult] = (blue << 24) | (r << 16) | (g << 8) | b;
				else
					pixelsResult[indexResult] = 0;
			}
			else
			{
				pixelsResult[indexResult] = 0;
			}
		}
	}

	////////////
	png->UnlockBits(
		&bitmapDataPng );
	pbmpOutline->UnlockBits(
		&bitmapDataOutline );
	pbmpText->UnlockBits(
		&bitmapDataText );
	m_pbmpResult->UnlockBits(
		&bitmapDataResult );



	return m_pbmpResult;
}

bool BmpOutlineText::DrawString(
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
	BYTE nGlowAlpha)
{
	if(bGlow)
		m_PngOutlineText.TextGlow(m_clrText, Gdiplus::Color(nGlowAlpha, m_clrOutline.GetRed(), m_clrOutline.GetGreen(), m_clrOutline.GetBlue()), nThickness);
	else
		m_PngOutlineText.TextOutline(m_clrText, m_clrOutline, nThickness);
	m_PngOutlineText.EnableReflection(false);
	m_PngOutlineText.EnableShadow(false);

	if(m_pbmpMask)
		delete m_pbmpMask;

	m_pbmpMask = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

	using namespace Gdiplus;
	Graphics graph(m_pbmpMask);
	SolidBrush brush(m_clrBkgd);
	graph.FillRectangle(&brush, 0, 0, m_pbmpMask->GetWidth(), m_pbmpMask->GetHeight());

	m_PngOutlineText.SetPngImage(m_pbmpMask);
	
	m_PngOutlineText.DrawString(
		pGraphics, 
		pFontFamily,
		fontStyle,
		nfontSize,
		pszText, 
		ptDraw, 
		pStrFormat);

	return true;

}

bool BmpOutlineText::DrawString(
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
	BYTE nGlowAlpha)
{
	if(bGlow)
		m_PngOutlineText.TextGlow(m_clrText, Gdiplus::Color(nGlowAlpha, m_clrOutline.GetRed(), m_clrOutline.GetGreen(), m_clrOutline.GetBlue()), nThickness);
	else
		m_PngOutlineText.TextOutline(m_clrText, m_clrOutline, nThickness);
	m_PngOutlineText.EnableReflection(false);
	m_PngOutlineText.EnableShadow(false);

	if(m_pbmpMask)
		delete m_pbmpMask;

	m_pbmpMask = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

	using namespace Gdiplus;
	Graphics graph(m_pbmpMask);
	SolidBrush brush(m_clrBkgd);
	graph.FillRectangle(&brush, 0, 0, m_pbmpMask->GetWidth(), m_pbmpMask->GetHeight());

	m_PngOutlineText.SetPngImage(m_pbmpMask);

	m_PngOutlineText.DrawString(
		pGraphics, 
		pFontFamily,
		fontStyle,
		nfontSize,
		pszText, 
		rtDraw,
		pStrFormat);

	return true;
}

bool BmpOutlineText::GdiDrawString(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Point ptDraw,
	int nThickness,
	int nWidth,
	int nHeight,
	bool bGlow,
	BYTE nGlowAlpha)
{
	if(bGlow)
		m_PngOutlineText.TextGlow(m_clrText, Gdiplus::Color(nGlowAlpha, m_clrOutline.GetRed(), m_clrOutline.GetGreen(), m_clrOutline.GetBlue()), nThickness);
	else
		m_PngOutlineText.TextOutline(m_clrText, m_clrOutline, nThickness);
	m_PngOutlineText.EnableReflection(false);
	m_PngOutlineText.EnableShadow(false);

	if(m_pbmpMask)
		delete m_pbmpMask;

	m_pbmpMask = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

	using namespace Gdiplus;
	Graphics graph(m_pbmpMask);
	SolidBrush brush(m_clrBkgd);
	graph.FillRectangle(&brush, 0, 0, m_pbmpMask->GetWidth(), m_pbmpMask->GetHeight());

	m_PngOutlineText.SetPngImage(m_pbmpMask);

	m_PngOutlineText.GdiDrawString(
		pGraphics, 
		pLogFont,
		pszText, 
		ptDraw);

	return true;
}

bool BmpOutlineText::GdiDrawString(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw,
	int nThickness,
	int nWidth,
	int nHeight,
	bool bGlow,
	BYTE nGlowAlpha)
{
	if(bGlow)
		m_PngOutlineText.TextGlow(m_clrText, Gdiplus::Color(nGlowAlpha, m_clrOutline.GetRed(), m_clrOutline.GetGreen(), m_clrOutline.GetBlue()), nThickness);
	else
		m_PngOutlineText.TextOutline(m_clrText, m_clrOutline, nThickness);
	m_PngOutlineText.EnableReflection(false);
	m_PngOutlineText.EnableShadow(false);

	if(m_pbmpMask)
		delete m_pbmpMask;

	m_pbmpMask = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

	using namespace Gdiplus;
	Graphics graph(m_pbmpMask);
	SolidBrush brush(m_clrBkgd);
	graph.FillRectangle(&brush, 0, 0, m_pbmpMask->GetWidth(), m_pbmpMask->GetHeight());

	m_PngOutlineText.SetPngImage(m_pbmpMask);

	m_PngOutlineText.GdiDrawString(
		pGraphics, 
		pLogFont,
		pszText, 
		rtDraw);

	return true;
}

bool BmpOutlineText::DrawString(
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
	int nShadowOffsetY)
{
	if(bGlow)
		m_PngOutlineText.TextGlow(m_clrText, Gdiplus::Color(nGlowAlpha, m_clrOutline.GetRed(), m_clrOutline.GetGreen(), m_clrOutline.GetBlue()), nThickness);
	else
		m_PngOutlineText.TextOutline(m_clrText, m_clrOutline, nThickness);
	m_PngOutlineText.EnableReflection(false);
	m_PngOutlineText.EnableShadow(false);

	if(bShadow)
	{
		m_PngShadow.SetNullTextEffect();
		m_PngShadow.EnableShadow(true);
		m_PngShadow.Shadow(clrShadow, nThickness, Gdiplus::Point(nShadowOffsetX, nShadowOffsetY));
		m_PngShadow.SetShadowBkgd(Gdiplus::Color(0,0,0), nWidth, nHeight);
	}
	else
		m_PngShadow.EnableShadow(false);


	if(m_pbmpMask)
		delete m_pbmpMask;

	m_pbmpMask = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

	using namespace Gdiplus;
	Graphics graph(m_pbmpMask);
	SolidBrush brush(m_clrBkgd);
	graph.FillRectangle(&brush, 0, 0, m_pbmpMask->GetWidth(), m_pbmpMask->GetHeight());

	m_PngOutlineText.SetPngImage(m_pbmpMask);

	m_PngOutlineText.DrawString(
		pGraphics, 
		pFontFamily,
		fontStyle,
		nfontSize,
		pszText, 
		ptDraw, 
		pStrFormat);

	if(bShadow)
	{
		if(m_pbmpShadow)
			delete m_pbmpShadow;

		m_pbmpShadow = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

		m_PngShadow.SetPngImage(m_pbmpShadow);

		m_PngShadow.DrawString(
			pGraphics, 
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			ptDraw, 
			pStrFormat);
	}

	return true;

}

bool BmpOutlineText::DrawString(
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
	int nShadowOffsetY)
{
	if(bGlow)
		m_PngOutlineText.TextGlow(m_clrText, Gdiplus::Color(nGlowAlpha, m_clrOutline.GetRed(), m_clrOutline.GetGreen(), m_clrOutline.GetBlue()), nThickness);
	else
		m_PngOutlineText.TextOutline(m_clrText, m_clrOutline, nThickness);
	m_PngOutlineText.EnableReflection(false);
	m_PngOutlineText.EnableShadow(false);

	if(bShadow)
	{
		m_PngShadow.SetNullTextEffect();
		m_PngShadow.EnableShadow(true);
		m_PngShadow.Shadow(clrShadow, nThickness, Gdiplus::Point(nShadowOffsetX, nShadowOffsetY));
		m_PngShadow.SetShadowBkgd(Gdiplus::Color(0,0,0), nWidth, nHeight);
	}
	else
		m_PngShadow.EnableShadow(false);

	if(m_pbmpMask)
		delete m_pbmpMask;

	m_pbmpMask = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

	using namespace Gdiplus;
	Graphics graph(m_pbmpMask);
	SolidBrush brush(m_clrBkgd);
	graph.FillRectangle(&brush, 0, 0, m_pbmpMask->GetWidth(), m_pbmpMask->GetHeight());

	m_PngOutlineText.SetPngImage(m_pbmpMask);

	m_PngOutlineText.DrawString(
		pGraphics, 
		pFontFamily,
		fontStyle,
		nfontSize,
		pszText, 
		rtDraw,
		pStrFormat);

	if(bShadow)
	{
		if(m_pbmpShadow)
			delete m_pbmpShadow;

		m_pbmpShadow = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

		m_PngShadow.SetPngImage(m_pbmpShadow);

		m_PngShadow.DrawString(
			pGraphics, 
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			rtDraw, 
			pStrFormat);
	}

	return true;
}

bool BmpOutlineText::GdiDrawString(
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
	int nShadowOffsetY)
{
	if(bGlow)
		m_PngOutlineText.TextGlow(m_clrText, Gdiplus::Color(nGlowAlpha, m_clrOutline.GetRed(), m_clrOutline.GetGreen(), m_clrOutline.GetBlue()), nThickness);
	else
		m_PngOutlineText.TextOutline(m_clrText, m_clrOutline, nThickness);
	m_PngOutlineText.EnableReflection(false);
	m_PngOutlineText.EnableShadow(false);

	if(bShadow)
	{
		m_PngShadow.SetNullTextEffect();
		m_PngShadow.EnableShadow(true);
		m_PngShadow.Shadow(clrShadow, nThickness, Gdiplus::Point(nShadowOffsetX, nShadowOffsetY));
		m_PngShadow.SetShadowBkgd(Gdiplus::Color(0,0,0), nWidth, nHeight);
	}
	else
		m_PngShadow.EnableShadow(false);

	if(m_pbmpMask)
		delete m_pbmpMask;

	m_pbmpMask = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

	using namespace Gdiplus;
	Graphics graph(m_pbmpMask);
	SolidBrush brush(m_clrBkgd);
	graph.FillRectangle(&brush, 0, 0, m_pbmpMask->GetWidth(), m_pbmpMask->GetHeight());

	m_PngOutlineText.SetPngImage(m_pbmpMask);

	m_PngOutlineText.GdiDrawString(
		pGraphics, 
		pLogFont,
		pszText, 
		ptDraw);

	if(bShadow)
	{
		if(m_pbmpShadow)
			delete m_pbmpShadow;

		m_pbmpShadow = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

		m_PngShadow.SetPngImage(m_pbmpShadow);

		m_PngShadow.GdiDrawString(
			pGraphics, 
			pLogFont,
			pszText, 
			ptDraw);
	}


	return true;
}

bool BmpOutlineText::GdiDrawString(
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
	int nShadowOffsetY)
{
	if(bGlow)
		m_PngOutlineText.TextGlow(m_clrText, Gdiplus::Color(nGlowAlpha, m_clrOutline.GetRed(), m_clrOutline.GetGreen(), m_clrOutline.GetBlue()), nThickness);
	else
		m_PngOutlineText.TextOutline(m_clrText, m_clrOutline, nThickness);
	m_PngOutlineText.EnableReflection(false);
	m_PngOutlineText.EnableShadow(false);

	if(bShadow)
	{
		m_PngShadow.SetNullTextEffect();
		m_PngShadow.EnableShadow(true);
		m_PngShadow.Shadow(clrShadow, nThickness, Gdiplus::Point(nShadowOffsetX, nShadowOffsetY));
		m_PngShadow.SetShadowBkgd(Gdiplus::Color(0,0,0), nWidth, nHeight);
	}
	else
		m_PngShadow.EnableShadow(false);

	if(m_pbmpMask)
		delete m_pbmpMask;

	m_pbmpMask = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

	using namespace Gdiplus;
	Graphics graph(m_pbmpMask);
	SolidBrush brush(m_clrBkgd);
	graph.FillRectangle(&brush, 0, 0, m_pbmpMask->GetWidth(), m_pbmpMask->GetHeight());

	m_PngOutlineText.SetPngImage(m_pbmpMask);

	m_PngOutlineText.GdiDrawString(
		pGraphics, 
		pLogFont,
		pszText, 
		rtDraw);

	if(bShadow)
	{
		if(m_pbmpShadow)
			delete m_pbmpShadow;

		m_pbmpShadow = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

		m_PngShadow.SetPngImage(m_pbmpShadow);

		m_PngShadow.GdiDrawString(
			pGraphics, 
			pLogFont,
			pszText, 
			rtDraw);
	}

	return true;
}

bool BmpOutlineText::Measure(
	Gdiplus::Bitmap* png, 
	UINT& nTextX, UINT& nTextY, UINT& nTextWidth, UINT& nTextHeight,
	UINT& nOutlineX, UINT& nOutlineY, UINT& nOutlineWidth, UINT& nOutlineHeight)
{
	using namespace Gdiplus;
	BitmapData bitmapData;
	Rect rect(0, 0, png->GetWidth(), png->GetHeight() );

	png->LockBits(
		&rect,
		ImageLockModeRead,
		PixelFormat32bppARGB,
		&bitmapData );

	UINT* pixels = (UINT*)bitmapData.Scan0;

	if( !pixels )
		return false;

	UINT col = 0;
	int stride = bitmapData.Stride >> 2;
	nTextX = 50000;
	nTextY = 50000;
	nTextWidth = 0;
	nTextHeight = 0;

	nOutlineX = 50000;
	nOutlineY = 50000;
	nOutlineWidth = 0;
	nOutlineHeight = 0;
	for(UINT row = 0; row < bitmapData.Height; ++row)
	{
		for(col = 0; col < bitmapData.Width; ++col)
		{
			UINT index = row * stride + col;
			BYTE red = (pixels[index] & 0xff0000) >> 16;
			BYTE blue = pixels[index] & 0xff;

			if(red>0)
			{
				if(col<nOutlineX)
					nOutlineX = col;
				if(row<nOutlineY)
					nOutlineY = row;
				if(col>nOutlineWidth)
					nOutlineWidth = col;
				if(row>nOutlineHeight)
					nOutlineHeight = row;
			}
			if(blue>0)
			{
				if(col<nTextX)
					nTextX = col;
				if(row<nTextY)
					nTextY = row;
				if(col>nTextWidth)
					nTextWidth = col;
				if(row>nTextHeight)
					nTextHeight = row;
			}
		}
	}

	png->UnlockBits(&bitmapData);

	nTextWidth -= nTextX;
	nTextHeight -= nTextY;

	nOutlineWidth -= nOutlineX;
	nOutlineHeight -= nOutlineY;

	++nTextWidth;
	++nTextHeight;

	++nOutlineWidth;
	++nOutlineHeight;

	return true;
}

bool BmpOutlineText::GetEncoderClsid(const WCHAR* format, CLSID* pClsid)
{
	UINT  num = 0;          // number of image encoders
	UINT  size = 0;         // size of the image encoder array in bytes

	using namespace Gdiplus;

	ImageCodecInfo* pImageCodecInfo = NULL;

	GetImageEncodersSize(&num, &size);
	if(size == 0)
		return false;  // Failure

	pImageCodecInfo = (ImageCodecInfo*)(malloc(size));
	if(pImageCodecInfo == NULL)
		return false;  // Failure

	GetImageEncoders(num, size, pImageCodecInfo);

	for(UINT j = 0; j < num; ++j)
	{
		if( wcscmp(pImageCodecInfo[j].MimeType, format) == 0 )
		{
			*pClsid = pImageCodecInfo[j].Clsid;
			free(pImageCodecInfo);
			return true;  // Success
		}    
	}

	free(pImageCodecInfo);
	return false;  // Failure
}

bool BmpOutlineText::SavePngFile(const wchar_t* pszFile)
{
	if(m_pbmpResult)
	{
		CLSID pngClsid;
		GetEncoderClsid(L"image/png", &pngClsid);
		Gdiplus::Status status = m_pbmpResult->Save(pszFile, &pngClsid, NULL);
		return status == Gdiplus::Ok ? true : false;
	}

	return false;
}
