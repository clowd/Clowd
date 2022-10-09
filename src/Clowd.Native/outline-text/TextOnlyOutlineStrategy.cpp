/*
Text Designer Outline Text Library 

Copyright (c) 2009 Wong Shao Voon

The Code Project Open License (CPOL)
http://www.codeproject.com/info/cpol10.aspx
*/

#include "pch.h"
#include "TextOnlyOutlineStrategy.h"
#include "GDIPath.h"

using namespace TextDesigner;

TextOnlyOutlineStrategy::TextOnlyOutlineStrategy(void)
:
m_bRoundedEdge(false)
{
}

TextOnlyOutlineStrategy::~TextOnlyOutlineStrategy(void)
{
}

ITextStrategy* TextOnlyOutlineStrategy::Clone()
{
	TextOnlyOutlineStrategy* p = new TextOnlyOutlineStrategy();
	p->Init(m_clrOutline, m_nThickness, m_bRoundedEdge);

	return static_cast<ITextStrategy*>(p);
}

void TextOnlyOutlineStrategy::Init(
	Gdiplus::Color clrOutline, 
	int nThickness,
	bool bRoundedEdge)
{
	m_clrOutline = clrOutline; 
	m_nThickness = nThickness; 
	m_bRoundedEdge = bRoundedEdge;
}

bool TextOnlyOutlineStrategy::DrawString(
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

	Pen pen(m_clrOutline,m_nThickness);
	if(m_bRoundedEdge)
		pen.SetLineJoin(LineJoinRound);
	pGraphics->DrawPath(&pen, &path);

	return status == Ok;
}

bool TextOnlyOutlineStrategy::DrawString(
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

	Pen pen(m_clrOutline,m_nThickness);
	if(m_bRoundedEdge)
		pen.SetLineJoin(LineJoinRound);
	pGraphics->DrawPath(&pen, &path);

	return status == Ok;
}

bool TextOnlyOutlineStrategy::GdiDrawString(
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

	Pen pen(m_clrOutline,m_nThickness);
	if(m_bRoundedEdge)
		pen.SetLineJoin(LineJoinRound);
	pGraphics->DrawPath(&pen, pPath);

	if(pPath)
	{
		delete pPath;
		pPath = NULL;
	}
	return b;
}

bool TextOnlyOutlineStrategy::GdiDrawString(
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

	Pen pen(m_clrOutline,m_nThickness);
	if(m_bRoundedEdge)
		pen.SetLineJoin(LineJoinRound);
	pGraphics->DrawPath(&pen, pPath);

	if(pPath)
	{
		delete pPath;
		pPath = NULL;
	}
	return b;
}
