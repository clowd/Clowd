/*
Text Designer Outline Text Library 

Copyright (c) 2009 Wong Shao Voon

The Code Project Open License (CPOL)
http://www.codeproject.com/info/cpol10.aspx
*/

#include "pch.h"
#include "TextGradOutlineStrategy.h"
#include "GDIPath.h"

using namespace TextDesigner;

TextGradOutlineStrategy::TextGradOutlineStrategy(void)
:
m_pbrushText(NULL),
m_bClrText(true)
{
}

TextGradOutlineStrategy::~TextGradOutlineStrategy(void)
{
}

ITextStrategy* TextGradOutlineStrategy::Clone()
{
	TextGradOutlineStrategy* p = new TextGradOutlineStrategy();
	if(m_bClrText)
		p->Init(m_clrText, m_clrOutline1, m_clrOutline2, m_nThickness);
	else
		p->Init(m_pbrushText, m_clrOutline1, m_clrOutline2, m_nThickness);

	return static_cast<ITextStrategy*>(p);
}

void TextGradOutlineStrategy::Init(
	Gdiplus::Color clrText, 
	Gdiplus::Color clrOutline1, 
	Gdiplus::Color clrOutline2, 
	int nThickness)
{
	m_clrText = clrText; 
	m_bClrText = true;
	if(clrOutline1.GetAlpha() < 255)
		m_clrOutline1 = Gdiplus::Color(255, clrOutline1.GetRed(), clrOutline1.GetGreen(), clrOutline1.GetBlue() );
	else
		m_clrOutline1 = clrOutline1; 

	if(clrOutline2.GetAlpha() < 255)
		m_clrOutline2 = Gdiplus::Color(255, clrOutline2.GetRed(), clrOutline2.GetGreen(), clrOutline2.GetBlue() );
	else
		m_clrOutline2 = clrOutline2; 

	m_nThickness = nThickness; 
}

void TextGradOutlineStrategy::Init(
	Gdiplus::Brush* pbrushText, 
	Gdiplus::Color clrOutline1, 
	Gdiplus::Color clrOutline2, 
	int nThickness)
{
	if(m_pbrushText&&m_pbrushText!=pbrushText)
		delete m_pbrushText;

	m_pbrushText = pbrushText; 
	m_bClrText = false;
	if(clrOutline1.GetAlpha() < 255)
		m_clrOutline1 = Gdiplus::Color(255, clrOutline1.GetRed(), clrOutline1.GetGreen(), clrOutline1.GetBlue() );
	else
		m_clrOutline1 = clrOutline1; 

	if(clrOutline2.GetAlpha() < 255)
		m_clrOutline2 = Gdiplus::Color(255, clrOutline2.GetRed(), clrOutline2.GetGreen(), clrOutline2.GetBlue() );
	else
		m_clrOutline2 = clrOutline2; 

	m_nThickness = nThickness; 
}

bool TextGradOutlineStrategy::DrawString(
	Gdiplus::Graphics* pGraphics,
	Gdiplus::FontFamily* pFontFamily,
	Gdiplus::FontStyle fontStyle,
	int nfontSize,
	const wchar_t*pszText, 
	Gdiplus::Point ptDraw, 
	Gdiplus::StringFormat* pStrFormat)
{
	using namespace Gdiplus;
	GraphicsPath path;
	Status status = path.AddString(pszText,wcslen(pszText),pFontFamily,fontStyle,nfontSize,ptDraw,pStrFormat);
	if(status!=Ok)
		return false;

	std::vector<Gdiplus::Color> vec;
	CalculateGradient(
		m_clrOutline1,
		m_clrOutline2,
		m_nThickness,
		vec );
	for(int i=m_nThickness; i>=1; --i)
	{
		Gdiplus::Color clr = vec.at(i-1);
		Pen pen(clr, i);
		pen.SetLineJoin(LineJoinRound);
		pGraphics->DrawPath(&pen, &path);
	}

	Status status2 = Ok;
	if(m_bClrText)
	{
		SolidBrush brush(m_clrText);
		status2 = pGraphics->FillPath(&brush, &path);
	}
	else
	{
		status2 = pGraphics->FillPath(m_pbrushText, &path);
	}

	return status2 == Ok;
}

bool TextGradOutlineStrategy::DrawString(
	Gdiplus::Graphics* pGraphics,
	Gdiplus::FontFamily* pFontFamily,
	Gdiplus::FontStyle fontStyle,
	int nfontSize,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw, 
	Gdiplus::StringFormat* pStrFormat)
{
	using namespace Gdiplus;
	GraphicsPath path;
	Status status = path.AddString(pszText,wcslen(pszText),pFontFamily,fontStyle,nfontSize,rtDraw,pStrFormat);
	if(status!=Ok)
		return false;

	std::vector<Gdiplus::Color> vec;
	CalculateGradient(
		m_clrOutline1,
		m_clrOutline2,
		m_nThickness,
		vec );
	for(int i=m_nThickness; i>=1; --i)
	{
		Gdiplus::Color clr = vec.at(i-1);
		Pen pen(clr, i);
		pen.SetLineJoin(LineJoinRound);
		pGraphics->DrawPath(&pen, &path);
	}

	Status status2 = Ok;
	if(m_bClrText)
	{
		SolidBrush brush(m_clrText);
		status2 = pGraphics->FillPath(&brush, &path);
	}
	else
	{
		status2 = pGraphics->FillPath(m_pbrushText, &path);
	}

	return status2 == Ok;
}

bool TextGradOutlineStrategy::GdiDrawString(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Point ptDraw)
{
	using namespace Gdiplus;
	Gdiplus::GraphicsPath* pPath=NULL;
	bool b = GDIPath::GetStringPath(
		pGraphics, 
		&pPath, 
		pszText, 
		pLogFont,
		ptDraw);

	if(false==b)
	{
		if(pPath)
		{
			delete pPath;
			pPath = NULL;
		}
		return false;
	}

	std::vector<Gdiplus::Color> vec;
	CalculateGradient(
		m_clrOutline1,
		m_clrOutline2,
		m_nThickness,
		vec );
	for(int i=m_nThickness; i>=1; --i)
	{
		Gdiplus::Color clr = vec.at(i-1);
		Pen pen(clr, i);
		pen.SetLineJoin(LineJoinRound);
		pGraphics->DrawPath(&pen, pPath);
	}

	Status status2 = Ok;
	if(m_bClrText)
	{
		SolidBrush brush(m_clrText);
		status2 = pGraphics->FillPath(&brush, pPath);
	}
	else
	{
		status2 = pGraphics->FillPath(m_pbrushText, pPath);
	}

	if(pPath)
	{
		delete pPath;
		pPath = NULL;
	}

	return status2 == Ok;
}

bool TextGradOutlineStrategy::GdiDrawString(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw)
{
	using namespace Gdiplus;
	Gdiplus::GraphicsPath* pPath=NULL;
	bool b = GDIPath::GetStringPath(
		pGraphics, 
		&pPath, 
		pszText, 
		pLogFont,
		rtDraw);

	if(false==b)
	{
		if(pPath)
		{
			delete pPath;
			pPath = NULL;
		}
		return false;
	}

	std::vector<Gdiplus::Color> vec;
	CalculateGradient(
		m_clrOutline1,
		m_clrOutline2,
		m_nThickness,
		vec );
	for(int i=m_nThickness; i>=1; --i)
	{
		Gdiplus::Color clr = vec.at(i-1);
		Pen pen(clr, i);
		pen.SetLineJoin(LineJoinRound);
		pGraphics->DrawPath(&pen, pPath);
	}

	Status status2 = Ok;
	if(m_bClrText)
	{
		SolidBrush brush(m_clrText);
		status2 = pGraphics->FillPath(&brush, pPath);
	}
	else
	{
		status2 = pGraphics->FillPath(m_pbrushText, pPath);
	}

	if(pPath)
	{
		delete pPath;
		pPath = NULL;
	}
	return status2 == Ok;
}

void TextGradOutlineStrategy::CalculateGradient(
	Gdiplus::Color clr1,
	Gdiplus::Color clr2,
	int nThickness,
	std::vector<Gdiplus::Color>& vec )
{
	using namespace Gdiplus;
	vec.clear();
	int nWidth = nThickness;
	int nHeight = 1;
	Gdiplus::Rect rect(0, 0, nWidth, nHeight);
	LinearGradientBrush brush(rect, 
		clr1, clr2, LinearGradientModeHorizontal);

	Gdiplus::Bitmap* pImage = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

	Gdiplus::Graphics graphics((Gdiplus::Image*)(pImage));
	graphics.FillRectangle(&brush, 0, 0, pImage->GetWidth(), pImage->GetHeight() );

	BitmapData bitmapData;

	pImage->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapData );

	UINT* pixels = (UINT*)bitmapData.Scan0;

	if( !pixels )
	{
		pImage->UnlockBits(&bitmapData);
		delete pImage;
		pImage = NULL;
		return;
	}

	UINT col = 0;
	int stride = bitmapData.Stride >> 2;
	for(UINT row = 0; row < bitmapData.Height; ++row)
	{
		for(col = 0; col < bitmapData.Width; ++col)
		{
			using namespace Gdiplus;
			UINT index = row * stride + col;
			UINT color = pixels[index];
			// if use color directly, instead of a colorref conversion, the colors sometimes appear incorrectly.
			// That's why this conversion is here! Do not remove!
			COLORREF colorref = RGB((color & 0xff0000)>>16, (color & 0xff00)>>8, color & 0xff);
			Gdiplus::Color gdiColor;
			gdiColor.SetFromCOLORREF(colorref);
			vec.push_back(gdiColor);
		}
	}

	pImage->UnlockBits(&bitmapData);
	delete pImage;
	pImage = NULL;
}

