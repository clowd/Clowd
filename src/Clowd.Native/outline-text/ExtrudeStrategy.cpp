/*
Text Designer Outline Text Library 

Copyright (c) 2009 Wong Shao Voon

The Code Project Open License (CPOL)
http://www.codeproject.com/info/cpol10.aspx
*/

#include "pch.h"
#include "ExtrudeStrategy.h"
#include "GDIPath.h"

using namespace TextDesigner;

ExtrudeStrategy::ExtrudeStrategy(void)
:
m_nThickness(2),
m_pbrushText(NULL),
m_bClrText(true)
{
}

ExtrudeStrategy::~ExtrudeStrategy(void)
{
}

ITextStrategy* ExtrudeStrategy::Clone()
{
	ExtrudeStrategy* p = new ExtrudeStrategy();
	if(m_bClrText)
		p->Init(m_clrText, m_clrOutline, m_nThickness, m_nOffsetX, m_nOffsetY);
	else
		p->Init(m_pbrushText, m_clrOutline, m_nThickness, m_nOffsetX, m_nOffsetY);

	return static_cast<ITextStrategy*>(p);
}

void ExtrudeStrategy::Init(
	Gdiplus::Color clrText, 
	Gdiplus::Color clrOutline, 
	int nThickness,
	int nOffsetX,
	int nOffsetY )
{
	m_clrText = clrText; 
	m_bClrText = true;
	m_clrOutline = clrOutline; 
	m_nThickness = nThickness; 
	m_nOffsetX = nOffsetX;
	m_nOffsetY = nOffsetY;
}

void ExtrudeStrategy::Init(
   Gdiplus::Brush* pbrushText, 
   Gdiplus::Color clrOutline, 
   int nThickness,
   int nOffsetX,
   int nOffsetY )
{
	m_pbrushText = pbrushText; 
	m_bClrText = false;
	m_clrOutline = clrOutline; 
	m_nThickness = nThickness; 
	m_nOffsetX = nOffsetX;
	m_nOffsetY = nOffsetY;
}

bool ExtrudeStrategy::DrawString(
	Gdiplus::Graphics* pGraphics, 
	Gdiplus::FontFamily* pFontFamily,
	Gdiplus::FontStyle fontStyle,
	int nfontSize,
	const wchar_t*pszText, 
	Gdiplus::Point ptDraw, 
	Gdiplus::StringFormat* pStrFormat)
{
	using namespace Gdiplus;
	int nOffset = abs(m_nOffsetX);
	if(abs(m_nOffsetX)==abs(m_nOffsetY))
	{
		nOffset = abs(m_nOffsetX);
	}
	else if(abs(m_nOffsetX)>abs(m_nOffsetY))
	{
		nOffset = abs(m_nOffsetY);
	}
	else if(abs(m_nOffsetX)<abs(m_nOffsetY))
	{
		nOffset = abs(m_nOffsetX);
	}

	Status status2 = Ok;
	for(int i=0; i<nOffset; ++i)
	{
		GraphicsPath path;
		Status status = path.AddString(
			pszText,
			wcslen(pszText),
			pFontFamily,
			fontStyle,
			nfontSize,
			Point(ptDraw.X+((i*(-m_nOffsetX))/nOffset),ptDraw.Y+((i*(-m_nOffsetY))/nOffset) ),
			pStrFormat);
		if(status!=Ok)
			return false;

		Pen pen(m_clrOutline,m_nThickness);
		pen.SetLineJoin(LineJoinRound);
		pGraphics->DrawPath(&pen, &path);

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
	}

	return status2 == Ok;
}

bool ExtrudeStrategy::DrawString(
	Gdiplus::Graphics* pGraphics, 
	Gdiplus::FontFamily* pFontFamily,
	Gdiplus::FontStyle fontStyle,
	int nfontSize,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw, 
	Gdiplus::StringFormat* pStrFormat)
{
	using namespace Gdiplus;
	int nOffset = abs(m_nOffsetX);
	if(abs(m_nOffsetX)==abs(m_nOffsetY))
	{
		nOffset = abs(m_nOffsetX);
	}
	else if(abs(m_nOffsetX)>abs(m_nOffsetY))
	{
		nOffset = abs(m_nOffsetY);
	}
	else if(abs(m_nOffsetX)<abs(m_nOffsetY))
	{
		nOffset = abs(m_nOffsetX);
	}

	Status status2 = Ok;
	for(int i=0; i<nOffset; ++i)
	{
		GraphicsPath path;
		Status status = path.AddString(
			pszText,
			wcslen(pszText),
			pFontFamily,
			fontStyle,
			nfontSize,
			Rect(rtDraw.X+((i*(-m_nOffsetX))/nOffset), rtDraw.Y+((i*(-m_nOffsetY))/nOffset), 
				rtDraw.Width, rtDraw.Height),
			pStrFormat);
		if(status!=Ok)
			return false;

		Pen pen(m_clrOutline,m_nThickness);
		pen.SetLineJoin(LineJoinRound);
		pGraphics->DrawPath(&pen, &path);

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
	}

	return status2 == Ok;
}

bool ExtrudeStrategy::GdiDrawString(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Point ptDraw)
{
	using namespace Gdiplus;
	
	int nOffset = abs(m_nOffsetX);
	if(abs(m_nOffsetX)==abs(m_nOffsetY))
	{
		nOffset = abs(m_nOffsetX);
	}
	else if(abs(m_nOffsetX)>abs(m_nOffsetY))
	{
		nOffset = abs(m_nOffsetY);
	}
	else if(abs(m_nOffsetX)<abs(m_nOffsetY))
	{
		nOffset = abs(m_nOffsetX);
	}

	Status status2 = Ok;
	for(int i=0; i<nOffset; ++i)
	{
		Gdiplus::GraphicsPath* pPath=NULL;
		bool b = GDIPath::GetStringPath(
			pGraphics, 
			&pPath, 
			pszText, 
			pLogFont,
			Point(ptDraw.X+((i*(-m_nOffsetX))/nOffset),ptDraw.Y+((i*(-m_nOffsetY))/nOffset) ) );

		if(false==b)
		{
			if(pPath)
			{
				delete pPath;
				pPath = NULL;
			}
			return false;
		}

		Pen pen(m_clrOutline,m_nThickness);
		pen.SetLineJoin(LineJoinRound);
		pGraphics->DrawPath(&pen, pPath);

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
	}
	return status2 == Ok;
}

bool ExtrudeStrategy::GdiDrawString(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw)
{
	using namespace Gdiplus;

	int nOffset = abs(m_nOffsetX);
	if(abs(m_nOffsetX)==abs(m_nOffsetY))
	{
		nOffset = abs(m_nOffsetX);
	}
	else if(abs(m_nOffsetX)>abs(m_nOffsetY))
	{
		nOffset = abs(m_nOffsetY);
	}
	else if(abs(m_nOffsetX)<abs(m_nOffsetY))
	{
		nOffset = abs(m_nOffsetX);
	}

	Status status2 = Ok;
	for(int i=0; i<nOffset; ++i)
	{
		Gdiplus::GraphicsPath* pPath=NULL;
		bool b = GDIPath::GetStringPath(
			pGraphics, 
			&pPath, 
			pszText, 
			pLogFont,
			Rect(rtDraw.X+((i*(-m_nOffsetX))/nOffset), rtDraw.Y+((i*(-m_nOffsetY))/nOffset), 
				rtDraw.Width, rtDraw.Height) );

		if(false==b)
		{
			if(pPath)
			{
				delete pPath;
				pPath = NULL;
			}
			return false;
		}

		Pen pen(m_clrOutline,m_nThickness);
		pen.SetLineJoin(LineJoinRound);
		pGraphics->DrawPath(&pen, pPath);

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
	}
	return status2 == Ok;
}

bool ExtrudeStrategy::MeasureString(
	Gdiplus::Graphics* pGraphics, 
	Gdiplus::FontFamily* pFontFamily,
	Gdiplus::FontStyle fontStyle,
	int nfontSize,
	const wchar_t*pszText, 
	Gdiplus::Point ptDraw, 
	Gdiplus::StringFormat* pStrFormat,
	float* pfPixelsStartX,
	float* pfPixelsStartY,
	float* pfDestWidth,
	float* pfDestHeight )
{
	using namespace Gdiplus;
	GraphicsPath path;
	Status status = path.AddString(pszText,wcslen(pszText),pFontFamily,fontStyle,nfontSize,ptDraw,pStrFormat);
	if(status!=Ok)
		return false;

	*pfDestWidth= ptDraw.X;
	*pfDestHeight= ptDraw.Y;
	bool b = GDIPath::MeasureGraphicsPath(pGraphics, &path, pfPixelsStartX, pfPixelsStartY, pfDestWidth, pfDestHeight);

	if(false==b)
		return false;

	float pixelThick = 0.0f;
	b = GDIPath::ConvertToPixels(pGraphics,m_nThickness,0.0f,NULL,NULL,&pixelThick,NULL);

	if(false==b)
		return false;

	*pfDestWidth += pixelThick;
	*pfDestHeight += pixelThick;

	return true;
}

bool ExtrudeStrategy::MeasureString(
	Gdiplus::Graphics* pGraphics, 
	Gdiplus::FontFamily* pFontFamily,
	Gdiplus::FontStyle fontStyle,
	int nfontSize,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw,
	Gdiplus::StringFormat* pStrFormat,
	float* pfPixelsStartX,
	float* pfPixelsStartY,
	float* pfDestWidth,
	float* pfDestHeight )
{
	using namespace Gdiplus;
	GraphicsPath path;
	Status status = path.AddString(pszText,wcslen(pszText),pFontFamily,fontStyle,nfontSize,rtDraw,pStrFormat);
	if(status!=Ok)
		return false;

	*pfDestWidth= rtDraw.GetLeft();
	*pfDestHeight= rtDraw.GetTop();
	bool b = GDIPath::MeasureGraphicsPath(pGraphics, &path, pfPixelsStartX, pfPixelsStartY, pfDestWidth, pfDestHeight);

	if(false==b)
		return false;

	float pixelThick = 0.0f;
	b = GDIPath::ConvertToPixels(pGraphics,m_nThickness,0.0f,NULL,NULL,&pixelThick,NULL);

	if(false==b)
		return false;

	*pfDestWidth += pixelThick;
	*pfDestHeight += pixelThick;

	return true;
}

bool ExtrudeStrategy::GdiMeasureString(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Point ptDraw,
	float* pfPixelsStartX,
	float* pfPixelsStartY,
	float* pfDestWidth,
	float* pfDestHeight )
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

	*pfDestWidth= ptDraw.X;
	*pfDestHeight= ptDraw.Y;
	b = GDIPath::MeasureGraphicsPath(pGraphics, pPath, pfPixelsStartX, pfPixelsStartY, pfDestWidth, pfDestHeight);

	if(false==b)
	{
		if(pPath)
		{
			delete pPath;
			pPath = NULL;
		}
		return false;
	}

	float pixelThick = 0.0f;
	b = GDIPath::ConvertToPixels(pGraphics,m_nThickness,0.0f,NULL,NULL,&pixelThick,NULL);

	if(false==b)
	{
		if(pPath)
		{
			delete pPath;
			pPath = NULL;
		}
		return false;
	}

	*pfDestWidth += pixelThick;
	*pfDestHeight += pixelThick;

	if(pPath)
	{
		delete pPath;
		pPath = NULL;
	}
	return true;
}

bool ExtrudeStrategy::GdiMeasureString(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw,
	float* pfPixelsStartX,
	float* pfPixelsStartY,
	float* pfDestWidth,
	float* pfDestHeight )
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
	*pfDestWidth= rtDraw.GetLeft();
	*pfDestHeight= rtDraw.GetTop();
	b = GDIPath::MeasureGraphicsPath(pGraphics, pPath, pfPixelsStartX, pfPixelsStartY, pfDestWidth, pfDestHeight);

	if(false==b)
	{
		if(pPath)
		{
			delete pPath;
			pPath = NULL;
		}
		return false;
	}

	float pixelThick = 0.0f;
	b = GDIPath::ConvertToPixels(pGraphics,m_nThickness,0.0f,NULL,NULL,&pixelThick,NULL);

	if(false==b)
	{
		if(pPath)
		{
			delete pPath;
			pPath = NULL;
		}
		return false;
	}

	*pfDestWidth += pixelThick;
	*pfDestHeight += pixelThick;

	if(pPath)
	{
		delete pPath;
		pPath = NULL;
	}
	return true;
}

bool ExtrudeStrategy::GdiMeasureStringRealHeight(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Point ptDraw,
	float* pfPixelsStartX,
	float* pfPixelsStartY,
	float* pfDestWidth,
	float* pfDestHeight )
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

	*pfDestWidth= ptDraw.X;
	*pfDestHeight= ptDraw.Y;
	b = GDIPath::MeasureGraphicsPathRealHeight(pGraphics, pPath, pfPixelsStartX, pfPixelsStartY, pfDestWidth, pfDestHeight);

	if(false==b)
	{
		if(pPath)
		{
			delete pPath;
			pPath = NULL;
		}
		return false;
	}

	float pixelThick = 0.0f;
	b = GDIPath::ConvertToPixels(pGraphics,m_nThickness,0.0f,NULL,NULL,&pixelThick,NULL);

	if(false==b)
	{
		if(pPath)
		{
			delete pPath;
			pPath = NULL;
		}
		return false;
	}

	*pfDestWidth += pixelThick;
	*pfDestHeight += pixelThick;

	if(pPath)
	{
		delete pPath;
		pPath = NULL;
	}
	return true;
}

bool ExtrudeStrategy::GdiMeasureStringRealHeight(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw,
	float* pfPixelsStartX,
	float* pfPixelsStartY,
	float* pfDestWidth,
	float* pfDestHeight )
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
	*pfDestWidth= rtDraw.GetLeft();
	*pfDestHeight= rtDraw.GetTop();
	b = GDIPath::MeasureGraphicsPathRealHeight(pGraphics, pPath, pfPixelsStartX, pfPixelsStartY, pfDestWidth, pfDestHeight);

	if(false==b)
	{
		if(pPath)
		{
			delete pPath;
			pPath = NULL;
		}
		return false;
	}

	float pixelThick = 0.0f;
	b = GDIPath::ConvertToPixels(pGraphics,m_nThickness,0.0f,NULL,NULL,&pixelThick,NULL);

	if(false==b)
	{
		if(pPath)
		{
			delete pPath;
			pPath = NULL;
		}
		return false;
	}

	*pfDestWidth += pixelThick;
	*pfDestHeight += pixelThick;

	if(pPath)
	{
		delete pPath;
		pPath = NULL;
	}
	return true;
}