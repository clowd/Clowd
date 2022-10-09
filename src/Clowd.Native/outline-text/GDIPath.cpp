/*
Text Designer Outline Text Library 

Copyright (c) 2009 Wong Shao Voon

The Code Project Open License (CPOL)
http://www.codeproject.com/info/cpol10.aspx
*/

#include "pch.h"
#include "GDIPath.h"

using namespace TextDesigner;

GDIPath::GDIPath(void)
{
}

GDIPath::~GDIPath(void)
{
}

bool GDIPath::GetStringPath(
	Gdiplus::Graphics* pGraphics, 
	Gdiplus::GraphicsPath** ppPath, 
	const wchar_t* pszText, 
	LOGFONTW* plf,
	Gdiplus::Point ptDraw)
{
	HDC hDC = pGraphics->GetHDC();

	*ppPath = new Gdiplus::GraphicsPath(Gdiplus::FillModeWinding);

	int nPrevMode = SetBkMode(hDC, TRANSPARENT);

	// create and select it
	HFONT hFont = CreateFontIndirectW(plf);
	if (NULL==hFont)
		return false;
	HFONT hOldFont = (HFONT) SelectObject(hDC, (HGDIOBJ)hFont);

	// use a path to record how the text was drawn
	::BeginPath(hDC);
	::TextOutW(hDC, ptDraw.X, ptDraw.Y, pszText, wcslen(pszText));
	::EndPath(hDC);

	// Find out how many points are in the path. Note that
	// for long strings or complex fonts, this number might be
	// gigantic!
	int nNumPts = ::GetPath(hDC, NULL, NULL, 0);
	if (nNumPts == 0)
		return false;

	// Allocate memory to hold points and stroke types from
	// the path.
	LPPOINT lpPoints = new POINT[nNumPts];
	if (lpPoints == NULL)
		return false;
	LPBYTE lpTypes = new BYTE[nNumPts];
	if (lpTypes == NULL)
	{
		delete [] lpPoints;
		return false;
	}

	// Now that we have the memory, really get the path data.
	nNumPts = GetPath(hDC, lpPoints, lpTypes, nNumPts);

	// If it worked, draw the lines. Win95 and Win98 don't support
	// the PolyDraw API, so we use our own member function to do
	// similar work. If you're targeting only Windows NT, you can
	// use the PolyDraw() API and avoid the COutlineView::PolyDraw()
	// member function.

	if (nNumPts != -1)
		PolyDraw(*ppPath, lpPoints, lpTypes, nNumPts);

	// Release the memory we used
	delete [] lpPoints;
	delete [] lpTypes;

	// Put back the old font
	SelectObject(hDC, hOldFont);
	DeleteObject(hFont);
	SetBkMode(hDC, nPrevMode);

	pGraphics->ReleaseHDC(hDC);

	return true;

}

bool GDIPath::GetStringPath(
	Gdiplus::Graphics* pGraphics, 
	Gdiplus::GraphicsPath** ppPath, 
	const wchar_t* pszText, 
	LOGFONTW* plf,
	Gdiplus::Rect rtDraw)
{
	HDC hDC = pGraphics->GetHDC();

	*ppPath = new Gdiplus::GraphicsPath(Gdiplus::FillModeWinding);

	int nPrevMode = SetBkMode(hDC, TRANSPARENT);

	// create and select it
	HFONT hFont = CreateFontIndirectW(plf);
	if (NULL==hFont)
		return false;
	HFONT hOldFont = (HFONT) SelectObject(hDC, (HGDIOBJ)hFont);

	// use a path to record how the text was drawn
	RECT rect;
	rect.left = rtDraw.X;
	rect.top = rtDraw.Y;
	rect.right = rtDraw.X+rtDraw.Width;
	rect.bottom = rtDraw.Y+rtDraw.Height;
	::BeginPath(hDC);
	::DrawTextW(hDC, pszText, wcslen(pszText), &rect, DT_CENTER);
	::EndPath(hDC);

	// Find out how many points are in the path. Note that
	// for long strings or complex fonts, this number might be
	// gigantic!
	int nNumPts = ::GetPath(hDC, NULL, NULL, 0);
	if (nNumPts == 0)
		return false;

	// Allocate memory to hold points and stroke types from
	// the path.
	LPPOINT lpPoints = new POINT[nNumPts];
	if (lpPoints == NULL)
		return false;
	LPBYTE lpTypes = new BYTE[nNumPts];
	if (lpTypes == NULL)
	{
		delete [] lpPoints;
		return false;
	}

	// Now that we have the memory, really get the path data.
	nNumPts = GetPath(hDC, lpPoints, lpTypes, nNumPts);

	// If it worked, draw the lines. Win95 and Win98 don't support
	// the PolyDraw API, so we use our own member function to do
	// similar work. If you're targeting only Windows NT, you can
	// use the PolyDraw() API and avoid the COutlineView::PolyDraw()
	// member function.

	if (nNumPts != -1)
		PolyDraw(*ppPath, lpPoints, lpTypes, nNumPts);

	// Release the memory we used
	delete [] lpPoints;
	delete [] lpTypes;

	// Put back the old font
	SelectObject(hDC, hOldFont);
	DeleteObject(hFont);
	SetBkMode(hDC, nPrevMode);

	pGraphics->ReleaseHDC(hDC);

	return true;

}

void GDIPath::PolyDraw(
	Gdiplus::GraphicsPath* pPath, 
	CONST LPPOINT lppt, 
	CONST LPBYTE lpbTypes,
	int cCount )
{
	int nIndex;
	LPPOINT pptLastMoveTo = NULL;
	LPPOINT pptPrev = NULL;

	// for each of the points we have...
	for (nIndex = 0; nIndex < cCount; nIndex++)
	{
		switch(lpbTypes[nIndex])
		{
		case PT_MOVETO:
			if (pptLastMoveTo != NULL && nIndex > 0)
				pPath->CloseFigure();
			pptLastMoveTo = &lppt[nIndex];
			pptPrev = &lppt[nIndex];
			break;

		case PT_LINETO | PT_CLOSEFIGURE:
			pPath->AddLine( pptPrev->x, pptPrev->y, lppt[nIndex].x, lppt[nIndex].y);
			pptPrev = &lppt[nIndex];
			if (pptLastMoveTo != NULL)
			{
				pPath->CloseFigure();
				pptPrev = pptLastMoveTo;
			}
			pptLastMoveTo = NULL;
			break;

		case PT_LINETO:
			pPath->AddLine( pptPrev->x, pptPrev->y, lppt[nIndex].x, lppt[nIndex].y);
			pptPrev = &lppt[nIndex];
			break;

		case PT_BEZIERTO | PT_CLOSEFIGURE:
			//ASSERT(nIndex + 2 <= cCount);
			pPath->AddBezier( 
				pptPrev->x, pptPrev->y,
				lppt[nIndex].x, lppt[nIndex].y,
				lppt[nIndex+1].x, lppt[nIndex+1].y,
				lppt[nIndex+2].x, lppt[nIndex+2].y );
			nIndex += 2;
			pptPrev = &lppt[nIndex];
			if (pptLastMoveTo != NULL)
			{
				pPath->CloseFigure();
				pptPrev = pptLastMoveTo;
			}
			pptLastMoveTo = NULL;
			break;

		case PT_BEZIERTO:
			//ASSERT(nIndex + 2 <= cCount);
			pPath->AddBezier( 
				pptPrev->x, pptPrev->y,
				lppt[nIndex].x, lppt[nIndex].y,
				lppt[nIndex+1].x, lppt[nIndex+1].y,
				lppt[nIndex+2].x, lppt[nIndex+2].y );
			nIndex += 2;
			pptPrev = &lppt[nIndex];
			break;
		}
	}

	// If the figure was never closed and should be,
	// close it now.
	if (pptLastMoveTo != NULL && nIndex > 1)
	{
		pPath->AddLine( pptPrev->x, pptPrev->y, pptLastMoveTo->x, pptLastMoveTo->y);
		//pPath->CloseFigure();
	}
}

bool GDIPath::DrawGraphicsPath(
	Gdiplus::Graphics* pGraphics,
	Gdiplus::GraphicsPath* pGraphicsPath,
	Gdiplus::Color clrPen,
	float fPenWidth)
{
	if(!pGraphics||!pGraphicsPath)
		return false;

	using namespace Gdiplus;
	PathData pathData;
	Status status = pGraphicsPath->GetPathData(&pathData);

	if(status != Ok)
		return false;

	if(pathData.Count<=0)
		return false;

	Gdiplus::Pen pen(clrPen, fPenWidth);
	pen.SetLineJoin(LineJoinRound);

	struct stStart
	{
		PointF fPoint;
		int nCount;
		bool bDrawn;
	};

	struct stBezier
	{
		PointF fPoints[4];
		int nCount;
	};

	stBezier bezier;
	bezier.nCount = 0;
	stStart start;
	start.nCount = 0;
	start.bDrawn = false;
	PointF prevPoint;

	for(int i=0; i<pathData.Count; ++i)
	{
		BYTE maskedByte = pathData.Types[i];
		if(pathData.Types[i]==PathPointTypePathTypeMask)
			maskedByte = pathData.Types[i] & 0x3;
		switch(maskedByte)
		{
		case PathPointTypeStart:
		case PathPointTypeStart | PathPointTypePathMarker:
			start.fPoint = pathData.Points[i];
			start.nCount = 1;
			start.bDrawn = false;
			bezier.nCount = 0;
			break;

		case PathPointTypeLine:
		case PathPointTypeLine | PathPointTypePathMarker:
			pGraphics->DrawLine(&pen, prevPoint, pathData.Points[i]);
			break;

		case PathPointTypeLine | PathPointTypeCloseSubpath:
		case PathPointTypeLine | PathPointTypePathMarker | PathPointTypeCloseSubpath:
			pGraphics->DrawLine(&pen, prevPoint, pathData.Points[i]);
			pGraphics->DrawLine(&pen, pathData.Points[i], start.fPoint);
			start.nCount = 0;
			break;

		case PathPointTypeBezier:
		case PathPointTypeBezier | PathPointTypePathMarker:
			bezier.fPoints[bezier.nCount] = pathData.Points[i];
			bezier.nCount++;
			if(bezier.nCount==1)
				pGraphics->DrawLine(&pen, prevPoint, pathData.Points[i]);
			if(bezier.nCount>=4)
			{
				pGraphics->DrawBezier(&pen, 
					bezier.fPoints[0], bezier.fPoints[1],
					bezier.fPoints[2], bezier.fPoints[3]);
				bezier.nCount = 0;
			}
			break;

		case PathPointTypeBezier | PathPointTypeCloseSubpath:
		case PathPointTypeBezier | PathPointTypePathMarker | PathPointTypeCloseSubpath:
			bezier.fPoints[bezier.nCount] = pathData.Points[i];
			bezier.nCount++;
			if(bezier.nCount==1)
				pGraphics->DrawLine(&pen, prevPoint, pathData.Points[i]);
			if(bezier.nCount>=4)
			{
				pGraphics->DrawBezier(&pen, 
					bezier.fPoints[0], bezier.fPoints[1],
					bezier.fPoints[2], bezier.fPoints[3]);
				bezier.nCount = 0;
				if(start.nCount==1)
				{
					pGraphics->DrawLine(&pen, pathData.Points[i], start.fPoint );
					start.nCount = 0;
				}
			}
			else if(start.nCount==1)
			{
				pGraphics->DrawLine(&pen, pathData.Points[i], start.fPoint );
				start.nCount = 0;
				start.bDrawn = true;
			}
			break;
		default:
			{
				//wchar_t buf[200];
				//memset(buf,0, sizeof(buf));
				//wsprintf(buf,_T("maskedByte: 0x%X\n"), maskedByte);
				//OutputDebugStringW(buf);
			}
			break;
		}
		prevPoint = pathData.Points[i];
	}

	return true;
}

bool GDIPath::MeasureGraphicsPath(
	Gdiplus::Graphics* pGraphics,
	Gdiplus::GraphicsPath* pGraphicsPath,
	float* pfPixelsStartX,
	float* pfPixelsStartY,
	float* pfPixelsWidth,
	float* pfPixelsHeight)
{
	if(!pGraphics||!pGraphicsPath||!pfPixelsWidth||!pfPixelsHeight)
		return false;

	using namespace Gdiplus;
	PathData pathData;
	Status status = pGraphicsPath->GetPathData(&pathData);

	if(status != Ok)
		return false;

	if(pathData.Count<=0)
		return false;

	float fHighestX = pathData.Points[0].X;
	float fHighestY = pathData.Points[0].Y;
	float fLowestX = pathData.Points[0].X;
	float fLowestY = pathData.Points[0].Y;
	Gdiplus::PointF* points = pathData.Points;
	INT count = pathData.Count;
	for(int i=1; i<count; ++i)
	{
		if(points[i].X < fLowestX)
			fLowestX = points[i].X;
		if(points[i].Y < fLowestY)
			fLowestY = points[i].Y;
		if(points[i].X > fHighestX)
			fHighestX = points[i].X;
		if(points[i].Y > fHighestY)
			fHighestY = points[i].Y;
	}

	// Hack!
	if(fLowestX<0.0f)
	{
		if(pfPixelsStartX) *pfPixelsStartX = fLowestX;
		fLowestX = -fLowestX;
	}
	else
	{
		if(pfPixelsStartX) *pfPixelsStartX = fLowestX;
		fLowestX = 0.0f;
	}

	if(fLowestY<0.0f)
	{
		if(pfPixelsStartY) *pfPixelsStartY = fLowestY;
		fLowestY = -fLowestY;
	}
	else
	{
		if(pfPixelsStartY) *pfPixelsStartY = fLowestY;
		fLowestY = 0.0f;
	}

	bool b = ConvertToPixels(
		pGraphics,
		fLowestX + fHighestX - *pfPixelsWidth,
		fLowestY + fHighestY - *pfPixelsHeight,
		pfPixelsStartX,
		pfPixelsStartY,
		pfPixelsWidth,
		pfPixelsHeight );

	//bool b = ConvertToPixels(
	//	pGraphics,
	//	fHighestX - fLowestX,
	//	fHighestY - fLowestY,
	//	pfPixelsStartX,
	//	pfPixelsStartY,
	//	pfPixelsWidth,
	//	pfPixelsHeight );

	return b;
}

bool GDIPath::MeasureGraphicsPathRealHeight(
	Gdiplus::Graphics* pGraphics,
	Gdiplus::GraphicsPath* pGraphicsPath,
	float* pfPixelsStartX,
	float* pfPixelsStartY,
	float* pfPixelsWidth,
	float* pfPixelsHeight)
{
	if(!pGraphics||!pGraphicsPath||!pfPixelsWidth||!pfPixelsHeight)
		return false;

	using namespace Gdiplus;
	PathData pathData;
	Status status = pGraphicsPath->GetPathData(&pathData);

	if(status != Ok)
		return false;

	if(pathData.Count<=0)
		return false;

	float fHighestX = pathData.Points[0].X;
	float fHighestY = pathData.Points[0].Y;
	float fLowestX = pathData.Points[0].X;
	float fLowestY = pathData.Points[0].Y;
	Gdiplus::PointF* points = pathData.Points;
	INT count = pathData.Count;
	for(int i=1; i<count; ++i)
	{
		if(points[i].X < fLowestX)
			fLowestX = points[i].X;
		if(points[i].Y < fLowestY)
			fLowestY = points[i].Y;
		if(points[i].X > fHighestX)
			fHighestX = points[i].X;
		if(points[i].Y > fHighestY)
			fHighestY = points[i].Y;
	}

	if(pfPixelsStartX) *pfPixelsStartX = fLowestX;
	if(pfPixelsStartY) *pfPixelsStartY = fLowestY;


	bool b = ConvertToPixels(
		pGraphics,
		(fHighestX - fLowestX) - *pfPixelsWidth,
		(fHighestY - fLowestY) - *pfPixelsHeight,
		pfPixelsStartX,
		pfPixelsStartY,
		pfPixelsWidth,
		pfPixelsHeight );

	return b;
}

bool GDIPath::ConvertToPixels(
	Gdiplus::Graphics* pGraphics,
	float fSrcWidth,
	float fSrcHeight,
	float* pfPixelsStartX,
	float* pfPixelsStartY,
	float* pfDestWidth,
	float* pfDestHeight )
{
	if(!pGraphics)
		return false;

	using namespace Gdiplus;
	Unit unit = pGraphics->GetPageUnit();
	float fDpiX = pGraphics->GetDpiX();
	float fDpiY = pGraphics->GetDpiY();

	if(unit==UnitWorld)
		return false; // dunno how to convert

	if(unit==UnitDisplay||unit==UnitPixel)
	{
		if(pfDestWidth)
			*pfDestWidth = fSrcWidth;
		if(pfDestHeight)
			*pfDestHeight = fSrcHeight;
		return true;
	}

	if(unit==UnitPoint)
	{
		if(pfPixelsStartX)
			*pfPixelsStartX = 1.0f/72.0f * fDpiX * (*pfPixelsStartX);
		if(pfPixelsStartY)
			*pfPixelsStartY = 1.0f/72.0f * fDpiY * (*pfPixelsStartY);
		if(pfDestWidth)
			*pfDestWidth = 1.0f/72.0f * fDpiX * fSrcWidth;
		if(pfDestHeight)
			*pfDestHeight = 1.0f/72.0f * fDpiY * fSrcHeight;
		return true;
	}

	if(unit==UnitInch)
	{
		if(pfPixelsStartX)
			*pfPixelsStartX = fDpiX * (*pfPixelsStartX);
		if(pfPixelsStartY)
			*pfPixelsStartY = fDpiY * (*pfPixelsStartY);
		if(pfDestWidth)
			*pfDestWidth = fDpiX * fSrcWidth;
		if(pfDestHeight)
			*pfDestHeight = fDpiY * fSrcHeight;
		return true;
	}

	if(unit==UnitDocument)
	{
		if(pfPixelsStartX)
			*pfPixelsStartX = 1.0f/300.0f * fDpiX * (*pfPixelsStartX);
		if(pfPixelsStartY)
			*pfPixelsStartY = 1.0f/300.0f * fDpiY * (*pfPixelsStartY);
		if(pfDestWidth)
			*pfDestWidth = 1.0f/300.0f * fDpiX * fSrcWidth;
		if(pfDestHeight)
			*pfDestHeight = 1.0f/300.0f * fDpiY * fSrcHeight;
		return true;
	}

	if(unit==UnitMillimeter)
	{
		if(pfPixelsStartX)
			*pfPixelsStartX = 1.0f/25.4f * fDpiX * (*pfPixelsStartX);
		if(pfPixelsStartY)
			*pfPixelsStartY = 1.0f/25.4f * fDpiY * (*pfPixelsStartY);
		if(pfDestWidth)
			*pfDestWidth = 1.0f/25.4f * fDpiX * fSrcWidth;
		if(pfDestHeight)
			*pfDestHeight = 1.0f/25.4f * fDpiY * fSrcHeight;
		return true;
	}

	return false;
}
