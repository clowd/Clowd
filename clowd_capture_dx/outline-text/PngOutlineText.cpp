/*
Text Designer Outline Text Library 

Copyright (c) 2009 Wong Shao Voon

The Code Project Open License (CPOL)
http://www.codeproject.com/info/cpol10.aspx
*/

#include "pch.h"
#include "PngOutlineText.h"
#include "GDIPath.h"

using namespace TextDesigner;

PngOutlineText::PngOutlineText(void)
:
m_pTextStrategy(NULL),
m_pShadowStrategy(NULL),
m_pShadowStrategyMask(NULL),
m_pFontBodyShadow(NULL),
m_pFontBodyShadowMask(NULL),
m_pBkgdBitmap(NULL),
m_pPngBitmap(NULL),
m_clrShadow(Gdiplus::Color(0,0,0)),
m_pReflectionPngBitmap(NULL),
m_bEnableReflection(false),
m_bEnableShadow(false),
m_bDiffuseShadow(false),
m_nShadowThickness(2),
m_fBegAlpha(0.6f),
m_fEndAlpha(0.01f),
m_fShown(0.8f)
{
}

PngOutlineText::PngOutlineText(PngOutlineText* rhs)
{
	Init(rhs);
}

PngOutlineText& PngOutlineText::operator=(PngOutlineText& rhs)
{
	if((&rhs)==this)
		return *this;

	Init(&rhs);
	return *this;
}

void PngOutlineText::Init(PngOutlineText* rhs)
{
	if(m_pTextStrategy)
	{
		delete m_pTextStrategy;
		m_pTextStrategy = NULL;
	}
	if(m_pShadowStrategy)
	{
		delete m_pShadowStrategy;
		m_pShadowStrategy = NULL;
	}
	if(m_pShadowStrategyMask)
	{
		delete m_pShadowStrategyMask;
		m_pShadowStrategyMask = NULL;
	}
	if(m_pFontBodyShadow)
	{
		delete m_pFontBodyShadow;
		m_pFontBodyShadow = NULL;
	}
	if(m_pFontBodyShadowMask)
	{
		delete m_pFontBodyShadowMask;
		m_pFontBodyShadowMask = NULL;
	}
	if(m_pReflectionPngBitmap)
	{
		delete m_pReflectionPngBitmap;
		m_pReflectionPngBitmap = NULL;
	}
	if (rhs->m_pTextStrategy != NULL) m_pTextStrategy = rhs->m_pTextStrategy->Clone();
	if (rhs->m_pShadowStrategy != NULL) m_pShadowStrategy = rhs->m_pShadowStrategy->Clone();
	if (rhs->m_pShadowStrategyMask != NULL) m_pShadowStrategyMask = rhs->m_pShadowStrategyMask->Clone();
	if (rhs->m_pFontBodyShadow != NULL) m_pFontBodyShadow = rhs->m_pFontBodyShadow->Clone();
	if (rhs->m_pFontBodyShadowMask != NULL) m_pFontBodyShadowMask = rhs->m_pFontBodyShadowMask->Clone();
	m_pBkgdBitmap = rhs->m_pBkgdBitmap;
	if(rhs->m_pPngBitmap!=NULL)
	{
    	m_pPngBitmap = dynamic_cast<Gdiplus::Bitmap*>(rhs->m_pPngBitmap->Clone
		(0,0,rhs->m_pPngBitmap->GetWidth(),rhs->m_pPngBitmap->GetHeight(),PixelFormat32bppARGB));
	}
	m_clrShadow = rhs->m_clrShadow;
	m_bEnableShadow = rhs->m_bEnableShadow;
	m_bDiffuseShadow = rhs->m_bDiffuseShadow;
	m_nShadowThickness = rhs->m_nShadowThickness;

	if(rhs->m_pReflectionPngBitmap!=NULL)
	{
    	m_pReflectionPngBitmap = dynamic_cast<Gdiplus::Bitmap*>(rhs->m_pReflectionPngBitmap->Clone
		(0,0,rhs->m_pReflectionPngBitmap->GetWidth(),rhs->m_pReflectionPngBitmap->GetHeight(),PixelFormat32bppARGB));
	}
    m_bEnableReflection = rhs->m_bEnableReflection;
    m_fBegAlpha = rhs->m_fBegAlpha;
    m_fEndAlpha = rhs->m_fEndAlpha;
    m_fShown = rhs->m_fShown;

}

PngOutlineText::~PngOutlineText(void)
{
	if(m_pTextStrategy)
	{
		delete m_pTextStrategy;
		m_pTextStrategy = NULL;
	}
	if(m_pShadowStrategy)
	{
		delete m_pShadowStrategy;
		m_pShadowStrategy = NULL;
	}
	if(m_pShadowStrategyMask)
	{
		delete m_pShadowStrategyMask;
		m_pShadowStrategyMask = NULL;
	}
	if(m_pFontBodyShadow)
	{
		delete m_pFontBodyShadow;
		m_pFontBodyShadow = NULL;
	}
	if(m_pFontBodyShadowMask)
	{
		delete m_pFontBodyShadowMask;
		m_pFontBodyShadowMask = NULL;
	}
	if(m_pReflectionPngBitmap)
	{
		delete m_pReflectionPngBitmap;
		m_pReflectionPngBitmap = NULL;
	}
	if(m_pBkgdBitmap)
	{
		delete m_pBkgdBitmap;
		m_pBkgdBitmap = NULL;
	}
}

void PngOutlineText::TextGlow(
	Gdiplus::Color clrText, 
	Gdiplus::Color clrOutline, 
	int nThickness)
{
	TextGlowStrategy* pStrat = new TextGlowStrategy();
	pStrat->Init(clrText,clrOutline,nThickness);

	if(m_pTextStrategy)
		delete m_pTextStrategy;

	m_pTextStrategy = pStrat;
}

void PngOutlineText::TextGlow(
	Gdiplus::Brush* pbrushText, 
	Gdiplus::Color clrOutline, 
	int nThickness)
{
	TextGlowStrategy* pStrat = new TextGlowStrategy();
	pStrat->Init(pbrushText,clrOutline,nThickness);

	if(m_pTextStrategy)
		delete m_pTextStrategy;

	m_pTextStrategy = pStrat;
}

void PngOutlineText::TextOutline(
	Gdiplus::Color clrText, 
	Gdiplus::Color clrOutline, 
	int nThickness)
{
	TextOutlineStrategy* pStrat = new TextOutlineStrategy();
	pStrat->Init(clrText,clrOutline,nThickness);

	if(m_pTextStrategy)
		delete m_pTextStrategy;

	m_pTextStrategy = pStrat;
}

void PngOutlineText::TextOutline(
	Gdiplus::Brush* pbrushText, 
	Gdiplus::Color clrOutline, 
	int nThickness)
{
	TextOutlineStrategy* pStrat = new TextOutlineStrategy();
	pStrat->Init(pbrushText,clrOutline,nThickness);

	if(m_pTextStrategy)
		delete m_pTextStrategy;

	m_pTextStrategy = pStrat;

}

void PngOutlineText::TextDblOutline(
	Gdiplus::Color clrText, 
	Gdiplus::Color clrOutline1, 
	Gdiplus::Color clrOutline2, 
	int nThickness1, 
	int nThickness2)
{
	TextDblOutlineStrategy* pStrat = new TextDblOutlineStrategy();
	pStrat->Init(clrText,clrOutline1,clrOutline2,nThickness1,nThickness2);

	if(m_pTextStrategy)
		delete m_pTextStrategy;

	m_pTextStrategy = pStrat;
}

void PngOutlineText::TextDblOutline(
	Gdiplus::Brush* pbrushText, 
	Gdiplus::Color clrOutline1, 
	Gdiplus::Color clrOutline2, 
	int nThickness1, 
	int nThickness2)
{
	TextDblOutlineStrategy* pStrat = new TextDblOutlineStrategy();
	pStrat->Init(pbrushText,clrOutline1,clrOutline2,nThickness1,nThickness2);

	if(m_pTextStrategy)
		delete m_pTextStrategy;

	m_pTextStrategy = pStrat;
}

void PngOutlineText::TextDblGlow(
	Gdiplus::Color clrText, 
	Gdiplus::Color clrOutline1, 
	Gdiplus::Color clrOutline2, 
	int nThickness1, 
	int nThickness2)
{
	TextDblGlowStrategy* pStrat = new TextDblGlowStrategy();
	pStrat->Init(clrText,clrOutline1,clrOutline2,nThickness1,nThickness2);

	if(m_pTextStrategy)
		delete m_pTextStrategy;

	m_pTextStrategy = pStrat;
}

void PngOutlineText::TextDblGlow(
	Gdiplus::Brush* pbrushText, 
	Gdiplus::Color clrOutline1, 
	Gdiplus::Color clrOutline2, 
	int nThickness1, 
	int nThickness2)
{
	TextDblGlowStrategy* pStrat = new TextDblGlowStrategy();
	pStrat->Init(pbrushText,clrOutline1,clrOutline2,nThickness1,nThickness2);

	if(m_pTextStrategy)
		delete m_pTextStrategy;

	m_pTextStrategy = pStrat;
}

void PngOutlineText::TextGradOutline(
	Gdiplus::Color clrText, 
	Gdiplus::Color clrOutline1, 
	Gdiplus::Color clrOutline2, 
	int nThickness)
{
	TextGradOutlineStrategy* pStrat = new TextGradOutlineStrategy();
	pStrat->Init(clrText,clrOutline1,clrOutline2,nThickness);

	if(m_pTextStrategy)
		delete m_pTextStrategy;

	m_pTextStrategy = pStrat;
}

void PngOutlineText::TextGradOutline(
	Gdiplus::Brush* pbrushText, 
	Gdiplus::Color clrOutline1, 
	Gdiplus::Color clrOutline2, 
	int nThickness)
{
	TextGradOutlineStrategy* pStrat = new TextGradOutlineStrategy();
	pStrat->Init(pbrushText,clrOutline1,clrOutline2,nThickness);

	if(m_pTextStrategy)
		delete m_pTextStrategy;

	m_pTextStrategy = pStrat;
}

void PngOutlineText::TextNoOutline(
	Gdiplus::Color clrText)
{
	TextNoOutlineStrategy* pStrat = new TextNoOutlineStrategy();
	pStrat->Init(clrText);

	if(m_pTextStrategy)
		delete m_pTextStrategy;

	m_pTextStrategy = pStrat;
}

void PngOutlineText::TextNoOutline(
	Gdiplus::Brush* pbrushText)
{
	TextNoOutlineStrategy* pStrat = new TextNoOutlineStrategy();
	pStrat->Init(pbrushText);

	if(m_pTextStrategy)
		delete m_pTextStrategy;

	m_pTextStrategy = pStrat;
}

void PngOutlineText::TextOnlyOutline(
	Gdiplus::Color clrOutline, 
	int nThickness,
	bool bRoundedEdge)
{
	TextOnlyOutlineStrategy* pStrat = new TextOnlyOutlineStrategy();
	pStrat->Init(clrOutline,nThickness,bRoundedEdge);

	if(m_pTextStrategy)
		delete m_pTextStrategy;

	m_pTextStrategy = pStrat;
}

void PngOutlineText::Shadow(
	Gdiplus::Color color, 
	int nThickness,
	Gdiplus::Point ptOffset)
{
	TextOutlineStrategy* pStrat = new TextOutlineStrategy();
	pStrat->Init(Gdiplus::Color(0,0,0,0),color,nThickness);

	if(m_pShadowStrategy)
		delete m_pShadowStrategy;

	m_ptShadowOffset = ptOffset;
	m_pShadowStrategy = pStrat;

	TextOutlineStrategy* pStrat2 = new TextOutlineStrategy();
	pStrat2->Init(
		Gdiplus::Color(0,0,0,0),
		Gdiplus::Color(color.GetAlpha(),255,255,255),
		nThickness);

	if(m_pShadowStrategyMask)
		delete m_pShadowStrategyMask;

	m_pShadowStrategyMask = pStrat2;

	m_clrShadow = color;

	if(m_pFontBodyShadow)
		delete m_pFontBodyShadow;

	TextOutlineStrategy* pFontBodyShadow = new TextOutlineStrategy();
	pFontBodyShadow->Init(Gdiplus::Color(255,255,255),Gdiplus::Color(0,0,0,0),0);
	m_pFontBodyShadow = pFontBodyShadow;

	if(m_pFontBodyShadowMask)
		delete m_pFontBodyShadowMask;

	TextOutlineStrategy* pFontBodyShadowMask = new TextOutlineStrategy();
	pFontBodyShadowMask->Init(Gdiplus::Color(color.GetAlpha(),255,255,255),Gdiplus::Color(0,0,0,0),0);
	m_pFontBodyShadowMask = pFontBodyShadowMask;
	m_bDiffuseShadow = false;
}

void PngOutlineText::DiffusedShadow(
	Gdiplus::Color color, 
	int nThickness,
	Gdiplus::Point ptOffset)
{
	DiffusedShadowStrategy* pStrat = new DiffusedShadowStrategy();
	pStrat->Init(Gdiplus::Color(0,0,0,0),color,nThickness,false);

	if(m_pShadowStrategy)
		delete m_pShadowStrategy;

	m_ptShadowOffset = ptOffset;
	m_pShadowStrategy = pStrat;

	DiffusedShadowStrategy* pStrat2 = new DiffusedShadowStrategy();
	pStrat2->Init(
		Gdiplus::Color(0,0,0,0),
		Gdiplus::Color(color.GetAlpha(),255,255,255),
		nThickness,
		true);

	if(m_pShadowStrategyMask)
		delete m_pShadowStrategyMask;

	m_pShadowStrategyMask = pStrat2;

	m_clrShadow = color;

	if(m_pFontBodyShadow)
		delete m_pFontBodyShadow;

	DiffusedShadowStrategy* pFontBodyShadow = new DiffusedShadowStrategy();
	pFontBodyShadow->Init(Gdiplus::Color(255,255,255),Gdiplus::Color(0,0,0,0),nThickness,false);

	m_pFontBodyShadow = pFontBodyShadow;

	if(m_pFontBodyShadowMask)
		delete m_pFontBodyShadowMask;

	DiffusedShadowStrategy* pFontBodyShadowMask = new DiffusedShadowStrategy();
	pFontBodyShadowMask->Init(Gdiplus::Color(color.GetAlpha(),255,255,255),Gdiplus::Color(0,0,0,0),
		nThickness,false);

	m_pFontBodyShadowMask = pFontBodyShadowMask;
	m_bDiffuseShadow = true;
	m_bExtrudeShadow = false;
	m_nShadowThickness = nThickness;
}

void PngOutlineText::Extrude(
	Gdiplus::Color color, 
	int nThickness,
	Gdiplus::Point ptOffset)
{
	ExtrudeStrategy* pStrat = new ExtrudeStrategy();
	pStrat->Init(Gdiplus::Color(0,0,0,0), color, nThickness, ptOffset.X, ptOffset.Y);

	if(m_pShadowStrategy)
		delete m_pShadowStrategy;

	m_ptShadowOffset = ptOffset;
	m_pShadowStrategy = pStrat;

	ExtrudeStrategy* pStrat2 = new ExtrudeStrategy();
	pStrat2->Init(
		Gdiplus::Color(0,0,0,0),
		Gdiplus::Color(color.GetAlpha(),255,255,255),
		nThickness,
		ptOffset.X, ptOffset.Y);

	if(m_pShadowStrategyMask)
		delete m_pShadowStrategyMask;

	m_pShadowStrategyMask = pStrat2;

	m_clrShadow = color;

	if(m_pFontBodyShadow)
		delete m_pFontBodyShadow;

	ExtrudeStrategy* pFontBodyShadow = new ExtrudeStrategy();
	pFontBodyShadow->Init(Gdiplus::Color(255,255,255),Gdiplus::Color(0,0,0,0), nThickness, ptOffset.X, ptOffset.Y);

	m_pFontBodyShadow = pFontBodyShadow;

	if(m_pFontBodyShadowMask)
		delete m_pFontBodyShadowMask;

	ExtrudeStrategy* pFontBodyShadowMask = new ExtrudeStrategy();
	pFontBodyShadowMask->Init(Gdiplus::Color(color.GetAlpha(),255,255,255), Gdiplus::Color(0,0,0,0), 
		nThickness, ptOffset.X, ptOffset.Y);

	m_pFontBodyShadowMask = pFontBodyShadowMask;
	m_bExtrudeShadow = true;
	m_bDiffuseShadow = false;
	m_nShadowThickness = nThickness;
}

void PngOutlineText::SetPngImage(Gdiplus::Bitmap* pBitmap)
{
	m_pPngBitmap = pBitmap;
}

Gdiplus::Bitmap* PngOutlineText::GetPngImage()
{
	return m_pPngBitmap;
}

void PngOutlineText::SetShadowBkgd(Gdiplus::Bitmap* pBitmap)
{
	if(m_pBkgdBitmap&&pBitmap!=m_pBkgdBitmap)
		delete m_pBkgdBitmap;

	m_pBkgdBitmap = pBitmap;
}

void PngOutlineText::SetShadowBkgd(Gdiplus::Color clrBkgd, int nWidth, int nHeight)
{
	if(m_pBkgdBitmap)
		delete m_pBkgdBitmap;

	m_pBkgdBitmap = new Gdiplus::Bitmap(nWidth, nHeight, PixelFormat32bppARGB);

	using namespace Gdiplus;

	Gdiplus::Graphics graphics((Gdiplus::Image*)(m_pBkgdBitmap));
	Gdiplus::SolidBrush brush(clrBkgd);
	graphics.FillRectangle(&brush, 0, 0, m_pBkgdBitmap->GetWidth(), m_pBkgdBitmap->GetHeight() );
}

void PngOutlineText::SetNullTextEffect()
{
	if(m_pTextStrategy)
	{
		delete m_pTextStrategy;
		m_pTextStrategy = NULL;
	}
}

void PngOutlineText::SetNullShadow()
{
	//if(m_pBkgdBitmap)
	//{
	//	delete m_pBkgdBitmap;
	//	m_pBkgdBitmap = NULL;
	//}
	if(m_pFontBodyShadow)
	{
		delete m_pFontBodyShadow;
		m_pFontBodyShadow = NULL;
	}
	if(m_pShadowStrategy)
	{
		delete m_pShadowStrategy;
		m_pShadowStrategy = NULL;
	}
	if(m_pShadowStrategyMask)
	{
		delete m_pShadowStrategyMask;
		m_pShadowStrategyMask = NULL;
	}
}

void PngOutlineText::EnableShadow(bool bEnable)
{
	m_bEnableShadow = bEnable;
}

bool PngOutlineText::RenderFontShadow(	
	Gdiplus::Graphics* pGraphicsDrawn, 
	Gdiplus::Graphics* pGraphicsMask,
	Gdiplus::Bitmap* pBitmapDrawn,
	Gdiplus::Bitmap* pBitmapMask,
	Gdiplus::FontFamily* pFontFamily,
	Gdiplus::FontStyle fontStyle,
	int nfontSize,
	const wchar_t*pszText, 
	Gdiplus::Point ptDraw, 
	Gdiplus::StringFormat* pStrFormat)
{
	if(!pGraphicsDrawn||!pGraphicsMask||!pBitmapDrawn||!pBitmapMask) return false;

	Gdiplus::Bitmap* pBitmapShadowMask = 
		m_pPngBitmap->Clone(0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight(), PixelFormat32bppARGB);

	Gdiplus::Graphics* pGraphicsShadowMask = new Gdiplus::Graphics((Gdiplus::Image*)(pBitmapShadowMask));
	Gdiplus::SolidBrush brushBlack(Gdiplus::Color(0,0,0));
	pGraphicsShadowMask->FillRectangle(&brushBlack, 0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight() );

	pGraphicsShadowMask->SetCompositingMode(pGraphicsDrawn->GetCompositingMode());
	pGraphicsShadowMask->SetCompositingQuality(pGraphicsDrawn->GetCompositingQuality());
	pGraphicsShadowMask->SetInterpolationMode(pGraphicsDrawn->GetInterpolationMode());
	pGraphicsShadowMask->SetSmoothingMode(pGraphicsDrawn->GetSmoothingMode());
	pGraphicsShadowMask->SetTextRenderingHint(pGraphicsDrawn->GetTextRenderingHint());
	pGraphicsShadowMask->SetPageUnit(pGraphicsDrawn->GetPageUnit());
	pGraphicsShadowMask->SetPageScale(pGraphicsDrawn->GetPageScale());

	bool b = false;

	b = m_pFontBodyShadowMask->DrawString(
			pGraphicsMask, 
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			ptDraw, 
			pStrFormat);

	if(!b) return false;

	b = m_pShadowStrategyMask->DrawString(		
			pGraphicsShadowMask, 
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			ptDraw, 
			pStrFormat);

	if(!b) return false;

	b = m_pFontBodyShadow->DrawString(
			pGraphicsDrawn, 
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			ptDraw, 
			pStrFormat);

	if(!b) return false;

	b = m_pShadowStrategy->DrawString(		
			pGraphicsDrawn, 
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			ptDraw, 
			pStrFormat);

	if(!b) return false;

	UINT* pixelsDest = NULL;
	UINT* pixelsMask = NULL;
	UINT* pixelsShadowMask = NULL;

	using namespace Gdiplus;

	BitmapData bitmapDataDest;
	BitmapData bitmapDataMask;
	BitmapData bitmapDataShadowMask;
	Rect rect(0, 0, m_pBkgdBitmap->GetWidth(), m_pBkgdBitmap->GetHeight() );

	pBitmapDrawn->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataDest );

	pBitmapMask->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataMask );

	pBitmapShadowMask->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataShadowMask );

	pixelsDest = (UINT*)bitmapDataDest.Scan0;
	pixelsMask = (UINT*)bitmapDataMask.Scan0;
	pixelsShadowMask = (UINT*)bitmapDataShadowMask.Scan0;

	if( !pixelsDest || !pixelsMask || !pixelsShadowMask )
		return false;

	UINT col = 0;
	int stride = bitmapDataDest.Stride >> 2;
	for(UINT row = 0; row < bitmapDataDest.Height; ++row)
	{
		for(col = 0; col < bitmapDataDest.Width; ++col)
		{
			using namespace Gdiplus;
			UINT index = row * stride + col;
			BYTE nAlpha = pixelsMask[index] & 0xff;
			BYTE nAlphaShadow = pixelsShadowMask[index] & 0xff;
			if(nAlpha>0&&nAlpha>nAlphaShadow)
			{
				//pixelsDest[index] = nAlpha << 24 | m_clrShadow.GetR()<<16 | m_clrShadow.GetG()<<8 | m_clrShadow.GetB();
				pixelsDest[index] = 0xff << 24 | m_clrShadow.GetR()<<16 | m_clrShadow.GetG()<<8 | m_clrShadow.GetB();
			}
			else if(nAlphaShadow>0)
			{
				//pixelsDest[index] = nAlphaShadow << 24 | m_clrShadow.GetR()<<16 | m_clrShadow.GetG()<<8 | m_clrShadow.GetB();
				pixelsDest[index] = 0xff << 24 | m_clrShadow.GetR()<<16 | m_clrShadow.GetG()<<8 | m_clrShadow.GetB();
				pixelsMask[index] = pixelsShadowMask[index];
			}
		}
	}

	pBitmapShadowMask->UnlockBits(&bitmapDataShadowMask);
	pBitmapMask->UnlockBits(&bitmapDataMask);
	pBitmapDrawn->UnlockBits(&bitmapDataDest);

	if(pGraphicsShadowMask)
	{
		delete pGraphicsShadowMask;
		pGraphicsShadowMask = NULL;
	}

	if(pBitmapShadowMask)
	{
		delete pBitmapShadowMask;
		pBitmapShadowMask = NULL;
	}

	return true;
}

bool PngOutlineText::RenderFontShadow(	
	Gdiplus::Graphics* pGraphicsDrawn, 
	Gdiplus::Graphics* pGraphicsMask,
	Gdiplus::Bitmap* pBitmapDrawn,
	Gdiplus::Bitmap* pBitmapMask,
	Gdiplus::FontFamily* pFontFamily,
	Gdiplus::FontStyle fontStyle,
	int nfontSize,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw, 
	Gdiplus::StringFormat* pStrFormat)
{
	if(!pGraphicsDrawn||!pGraphicsMask||!pBitmapDrawn||!pBitmapMask) return false;

	Gdiplus::Bitmap* pBitmapShadowMask = 
		m_pPngBitmap->Clone(0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight(), PixelFormat32bppARGB);

	Gdiplus::Graphics* pGraphicsShadowMask = new Gdiplus::Graphics((Gdiplus::Image*)(pBitmapShadowMask));
	Gdiplus::SolidBrush brushBlack(Gdiplus::Color(0,0,0));
	pGraphicsShadowMask->FillRectangle(&brushBlack, 0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight() );

	pGraphicsShadowMask->SetCompositingMode(pGraphicsDrawn->GetCompositingMode());
	pGraphicsShadowMask->SetCompositingQuality(pGraphicsDrawn->GetCompositingQuality());
	pGraphicsShadowMask->SetInterpolationMode(pGraphicsDrawn->GetInterpolationMode());
	pGraphicsShadowMask->SetSmoothingMode(pGraphicsDrawn->GetSmoothingMode());
	pGraphicsShadowMask->SetTextRenderingHint(pGraphicsDrawn->GetTextRenderingHint());
	pGraphicsShadowMask->SetPageUnit(pGraphicsDrawn->GetPageUnit());
	pGraphicsShadowMask->SetPageScale(pGraphicsDrawn->GetPageScale());

	bool b = false;

	b = m_pFontBodyShadowMask->DrawString(
		pGraphicsMask, 
		pFontFamily,
		fontStyle,
		nfontSize,
		pszText, 
		rtDraw, 
		pStrFormat);

	if(!b) return false;

	b = m_pShadowStrategyMask->DrawString(		
		pGraphicsShadowMask, 
		pFontFamily,
		fontStyle,
		nfontSize,
		pszText, 
		rtDraw, 
		pStrFormat);

	if(!b) return false;

	b = m_pFontBodyShadow->DrawString(
		pGraphicsDrawn, 
		pFontFamily,
		fontStyle,
		nfontSize,
		pszText, 
		rtDraw, 
		pStrFormat);

	if(!b) return false;

	b = m_pShadowStrategy->DrawString(		
		pGraphicsDrawn, 
		pFontFamily,
		fontStyle,
		nfontSize,
		pszText, 
		rtDraw, 
		pStrFormat);

	if(!b) return false;

	UINT* pixelsDest = NULL;
	UINT* pixelsMask = NULL;
	UINT* pixelsShadowMask = NULL;

	using namespace Gdiplus;

	BitmapData bitmapDataDest;
	BitmapData bitmapDataMask;
	BitmapData bitmapDataShadowMask;
	Rect rect(0, 0, m_pBkgdBitmap->GetWidth(), m_pBkgdBitmap->GetHeight() );

	pBitmapDrawn->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataDest );

	pBitmapMask->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataMask );

	pBitmapShadowMask->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataShadowMask );

	pixelsDest = (UINT*)bitmapDataDest.Scan0;
	pixelsMask = (UINT*)bitmapDataMask.Scan0;
	pixelsShadowMask = (UINT*)bitmapDataShadowMask.Scan0;

	if( !pixelsDest || !pixelsMask || !pixelsShadowMask )
		return false;

	UINT col = 0;
	int stride = bitmapDataDest.Stride >> 2;
	for(UINT row = 0; row < bitmapDataDest.Height; ++row)
	{
		for(col = 0; col < bitmapDataDest.Width; ++col)
		{
			using namespace Gdiplus;
			UINT index = row * stride + col;
			BYTE nAlpha = pixelsMask[index] & 0xff;
			BYTE nAlphaShadow = pixelsShadowMask[index] & 0xff;
			if(nAlpha>0&&nAlpha>nAlphaShadow)
			{
				pixelsDest[index] = nAlpha << 24 | m_clrShadow.GetR()<<16 | m_clrShadow.GetG()<<8 | m_clrShadow.GetB();
			}
			else if(nAlphaShadow>0)
			{
				pixelsDest[index] = nAlphaShadow << 24 | m_clrShadow.GetR()<<16 | m_clrShadow.GetG()<<8 | m_clrShadow.GetB();
				pixelsMask[index] = pixelsShadowMask[index];
			}
		}
	}

	pBitmapShadowMask->UnlockBits(&bitmapDataShadowMask);
	pBitmapMask->UnlockBits(&bitmapDataMask);
	pBitmapDrawn->UnlockBits(&bitmapDataDest);

	if(pGraphicsShadowMask)
	{
		delete pGraphicsShadowMask;
		pGraphicsShadowMask = NULL;
	}

	if(pBitmapShadowMask)
	{
		delete pBitmapShadowMask;
		pBitmapShadowMask = NULL;
	}

	return true;
}

bool PngOutlineText::GdiRenderFontShadow(	
	Gdiplus::Graphics* pGraphicsDrawn, 
	Gdiplus::Graphics* pGraphicsMask,
	Gdiplus::Bitmap* pBitmapDrawn,
	Gdiplus::Bitmap* pBitmapMask,
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Point ptDraw)
{
	if(!pGraphicsDrawn||!pGraphicsMask||!pBitmapDrawn||!pBitmapMask) return false;

	Gdiplus::Bitmap* pBitmapShadowMask = 
		m_pPngBitmap->Clone(0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight(), PixelFormat32bppARGB);

	Gdiplus::Graphics* pGraphicsShadowMask = new Gdiplus::Graphics((Gdiplus::Image*)(pBitmapShadowMask));
	Gdiplus::SolidBrush brushBlack(Gdiplus::Color(0,0,0));
	pGraphicsShadowMask->FillRectangle(&brushBlack, 0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight() );

	pGraphicsShadowMask->SetCompositingMode(pGraphicsDrawn->GetCompositingMode());
	pGraphicsShadowMask->SetCompositingQuality(pGraphicsDrawn->GetCompositingQuality());
	pGraphicsShadowMask->SetInterpolationMode(pGraphicsDrawn->GetInterpolationMode());
	pGraphicsShadowMask->SetSmoothingMode(pGraphicsDrawn->GetSmoothingMode());
	pGraphicsShadowMask->SetTextRenderingHint(pGraphicsDrawn->GetTextRenderingHint());
	pGraphicsShadowMask->SetPageUnit(pGraphicsDrawn->GetPageUnit());
	pGraphicsShadowMask->SetPageScale(pGraphicsDrawn->GetPageScale());

	bool b = false;

	b = m_pFontBodyShadowMask->GdiDrawString(
		pGraphicsMask, 
		pLogFont,
		pszText, 
		ptDraw );

	if(!b) return false;

	b = m_pShadowStrategyMask->GdiDrawString(		
		pGraphicsShadowMask, 
		pLogFont,
		pszText, 
		ptDraw );

	if(!b) return false;

	b = m_pFontBodyShadow->GdiDrawString(
		pGraphicsDrawn, 
		pLogFont,
		pszText, 
		ptDraw );

	if(!b) return false;

	b = m_pShadowStrategy->GdiDrawString(		
		pGraphicsDrawn, 
		pLogFont,
		pszText, 
		ptDraw );

	if(!b) return false;

	UINT* pixelsDest = NULL;
	UINT* pixelsMask = NULL;
	UINT* pixelsShadowMask = NULL;

	using namespace Gdiplus;

	BitmapData bitmapDataDest;
	BitmapData bitmapDataMask;
	BitmapData bitmapDataShadowMask;
	Rect rect(0, 0, m_pBkgdBitmap->GetWidth(), m_pBkgdBitmap->GetHeight() );

	pBitmapDrawn->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataDest );

	pBitmapMask->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataMask );

	pBitmapShadowMask->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataShadowMask );

	pixelsDest = (UINT*)bitmapDataDest.Scan0;
	pixelsMask = (UINT*)bitmapDataMask.Scan0;
	pixelsShadowMask = (UINT*)bitmapDataShadowMask.Scan0;

	if( !pixelsDest || !pixelsMask || !pixelsShadowMask )
		return false;

	UINT col = 0;
	int stride = bitmapDataDest.Stride >> 2;
	for(UINT row = 0; row < bitmapDataDest.Height; ++row)
	{
		for(col = 0; col < bitmapDataDest.Width; ++col)
		{
			using namespace Gdiplus;
			UINT index = row * stride + col;
			BYTE nAlpha = pixelsMask[index] & 0xff;
			BYTE nAlphaShadow = pixelsShadowMask[index] & 0xff;
			if(nAlpha>0&&nAlpha>nAlphaShadow)
			{
				pixelsDest[index] = nAlpha << 24 | m_clrShadow.GetR()<<16 | m_clrShadow.GetG()<<8 | m_clrShadow.GetB();
			}
			else if(nAlphaShadow>0)
			{
				pixelsDest[index] = nAlphaShadow << 24 | m_clrShadow.GetR()<<16 | m_clrShadow.GetG()<<8 | m_clrShadow.GetB();
				pixelsMask[index] = pixelsShadowMask[index];
			}
		}
	}

	pBitmapShadowMask->UnlockBits(&bitmapDataShadowMask);
	pBitmapMask->UnlockBits(&bitmapDataMask);
	pBitmapDrawn->UnlockBits(&bitmapDataDest);

	if(pGraphicsShadowMask)
	{
		delete pGraphicsShadowMask;
		pGraphicsShadowMask = NULL;
	}

	if(pBitmapShadowMask)
	{
		delete pBitmapShadowMask;
		pBitmapShadowMask = NULL;
	}

	return true;
}

bool PngOutlineText::GdiRenderFontShadow(	
	Gdiplus::Graphics* pGraphicsDrawn, 
	Gdiplus::Graphics* pGraphicsMask,
	Gdiplus::Bitmap* pBitmapDrawn,
	Gdiplus::Bitmap* pBitmapMask,
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw)
{
	if(!pGraphicsDrawn||!pGraphicsMask||!pBitmapDrawn||!pBitmapMask) return false;

	Gdiplus::Bitmap* pBitmapShadowMask = 
		m_pPngBitmap->Clone(0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight(), PixelFormat32bppARGB);

	Gdiplus::Graphics* pGraphicsShadowMask = new Gdiplus::Graphics((Gdiplus::Image*)(pBitmapShadowMask));
	Gdiplus::SolidBrush brushBlack(Gdiplus::Color(0,0,0));
	pGraphicsShadowMask->FillRectangle(&brushBlack, 0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight() );

	pGraphicsShadowMask->SetCompositingMode(pGraphicsDrawn->GetCompositingMode());
	pGraphicsShadowMask->SetCompositingQuality(pGraphicsDrawn->GetCompositingQuality());
	pGraphicsShadowMask->SetInterpolationMode(pGraphicsDrawn->GetInterpolationMode());
	pGraphicsShadowMask->SetSmoothingMode(pGraphicsDrawn->GetSmoothingMode());
	pGraphicsShadowMask->SetTextRenderingHint(pGraphicsDrawn->GetTextRenderingHint());
	pGraphicsShadowMask->SetPageUnit(pGraphicsDrawn->GetPageUnit());
	pGraphicsShadowMask->SetPageScale(pGraphicsDrawn->GetPageScale());

	bool b = false;

	b = m_pFontBodyShadowMask->GdiDrawString(
		pGraphicsMask, 
		pLogFont,
		pszText, 
		rtDraw );

	if(!b) return false;

	b = m_pShadowStrategyMask->GdiDrawString(		
		pGraphicsShadowMask, 
		pLogFont,
		pszText, 
		rtDraw );

	if(!b) return false;

	b = m_pFontBodyShadow->GdiDrawString(
		pGraphicsDrawn, 
		pLogFont,
		pszText, 
		rtDraw );

	if(!b) return false;

	b = m_pShadowStrategy->GdiDrawString(		
		pGraphicsDrawn, 
		pLogFont,
		pszText, 
		rtDraw );

	if(!b) return false;

	UINT* pixelsDest = NULL;
	UINT* pixelsMask = NULL;
	UINT* pixelsShadowMask = NULL;

	using namespace Gdiplus;

	BitmapData bitmapDataDest;
	BitmapData bitmapDataMask;
	BitmapData bitmapDataShadowMask;
	Rect rect(0, 0, m_pBkgdBitmap->GetWidth(), m_pBkgdBitmap->GetHeight() );

	pBitmapDrawn->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataDest );

	pBitmapMask->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataMask );

	pBitmapShadowMask->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataShadowMask );

	pixelsDest = (UINT*)bitmapDataDest.Scan0;
	pixelsMask = (UINT*)bitmapDataMask.Scan0;
	pixelsShadowMask = (UINT*)bitmapDataShadowMask.Scan0;

	if( !pixelsDest || !pixelsMask || !pixelsShadowMask )
		return false;

	UINT col = 0;
	int stride = bitmapDataDest.Stride >> 2;
	for(UINT row = 0; row < bitmapDataDest.Height; ++row)
	{
		for(col = 0; col < bitmapDataDest.Width; ++col)
		{
			using namespace Gdiplus;
			UINT index = row * stride + col;
			BYTE nAlpha = pixelsMask[index] & 0xff;
			BYTE nAlphaShadow = pixelsShadowMask[index] & 0xff;
			if(nAlpha>0&&nAlpha>nAlphaShadow)
			{
				pixelsDest[index] = nAlpha << 24 | m_clrShadow.GetR()<<16 | m_clrShadow.GetG()<<8 | m_clrShadow.GetB();
			}
			else if(nAlphaShadow>0)
			{
				pixelsDest[index] = nAlphaShadow << 24 | m_clrShadow.GetR()<<16 | m_clrShadow.GetG()<<8 | m_clrShadow.GetB();
				pixelsMask[index] = pixelsShadowMask[index];
			}
		}
	}

	pBitmapShadowMask->UnlockBits(&bitmapDataShadowMask);
	pBitmapMask->UnlockBits(&bitmapDataMask);
	pBitmapDrawn->UnlockBits(&bitmapDataDest);

	if(pGraphicsShadowMask)
	{
		delete pGraphicsShadowMask;
		pGraphicsShadowMask = NULL;
	}

	if(pBitmapShadowMask)
	{
		delete pBitmapShadowMask;
		pBitmapShadowMask = NULL;
	}

	return true;
}

inline UINT PngOutlineText::Alphablend(UINT dest, UINT source, BYTE nAlpha)
{
	if( 0 == nAlpha )
		return dest;

	if( 255 == nAlpha )
		return source;

	BYTE nInvAlpha = ~nAlpha;

	BYTE nSrcRed   = (source & 0xff0000) >> 16; 
	BYTE nSrcGreen = (source & 0xff00) >> 8; 
	BYTE nSrcBlue  = (source & 0xff); 

	BYTE nDestRed   = (dest & 0xff0000) >> 16; 
	BYTE nDestGreen = (dest & 0xff00) >> 8; 
	BYTE nDestBlue  = (dest & 0xff); 

	BYTE nRed  = ( nSrcRed   * nAlpha + nDestRed * nInvAlpha   )>>8;
	BYTE nGreen= ( nSrcGreen * nAlpha + nDestGreen * nInvAlpha )>>8;
	BYTE nBlue = ( nSrcBlue  * nAlpha + nDestBlue * nInvAlpha  )>>8;

	return 0xff000000 | nRed << 16 | nGreen << 8 | nBlue;
}

bool PngOutlineText::DrawString(
	Gdiplus::Graphics* pGraphics, 
	Gdiplus::FontFamily* pFontFamily,
	Gdiplus::FontStyle fontStyle,
	int nfontSize,
	const wchar_t*pszText, 
	Gdiplus::Point ptDraw, 
	Gdiplus::StringFormat* pStrFormat)
{
	if(!pGraphics) return false;

	Gdiplus::Graphics* pGraphicsDrawn=NULL;
	Gdiplus::Bitmap* pBmpDrawn=NULL;

	if(m_bEnableShadow&&m_pBkgdBitmap&&m_pFontBodyShadow&&m_pShadowStrategy&&m_pShadowStrategyMask)
	{
		Gdiplus::Graphics* pGraphicsMask=NULL;
		Gdiplus::Bitmap* pBmpMask=NULL;

		bool b = RenderTransShadowA( pGraphics, &pGraphicsMask, &pBmpMask, &pGraphicsDrawn, &pBmpDrawn);

		if(!b) return false;

		b = RenderFontShadow(
			pGraphicsDrawn,
			pGraphicsMask,
			pBmpDrawn,
			pBmpMask,
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			Gdiplus::Point(ptDraw.X+m_ptShadowOffset.X, ptDraw.Y+m_ptShadowOffset.Y),
			pStrFormat);

		if(!b) 
		{
			delete pGraphicsMask;
			delete pGraphicsDrawn;
			delete pBmpDrawn;
			return false;
		}

		b = RenderTransShadowB( pGraphics, pGraphicsMask, pBmpMask, pGraphicsDrawn, pBmpDrawn);

		delete pGraphicsMask;
		delete pGraphicsDrawn;
		delete pBmpDrawn;

		if(!b) return false;
	}

	if(m_pTextStrategy)
	{
		Gdiplus::Graphics* pGraphicsPng = new Gdiplus::Graphics((Gdiplus::Image*)(m_pPngBitmap));

		pGraphicsPng->SetCompositingMode(pGraphics->GetCompositingMode());
		pGraphicsPng->SetCompositingQuality(pGraphics->GetCompositingQuality());
		pGraphicsPng->SetInterpolationMode(pGraphics->GetInterpolationMode());
		pGraphicsPng->SetSmoothingMode(pGraphics->GetSmoothingMode());
		pGraphicsPng->SetTextRenderingHint(pGraphics->GetTextRenderingHint());
		pGraphicsPng->SetPageUnit(pGraphics->GetPageUnit());
		pGraphicsPng->SetPageScale(pGraphics->GetPageScale());


		bool b = m_pTextStrategy->DrawString(
			pGraphicsPng, 
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			ptDraw, 
			pStrFormat);

		delete pGraphicsPng;

		if(!b)
			return false;
	}

	if(m_bEnableReflection)
		ProcessReflection();

	return true;
}

bool PngOutlineText::DrawString(
	Gdiplus::Graphics* pGraphics, 
	Gdiplus::FontFamily* pFontFamily,
	Gdiplus::FontStyle fontStyle,
	int nfontSize,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw, 
	Gdiplus::StringFormat* pStrFormat)
{
	if(!pGraphics) return false;

	if(m_bEnableShadow&&m_pBkgdBitmap&&m_pFontBodyShadow&&m_pShadowStrategy&&m_pShadowStrategyMask)
	{
		Gdiplus::Graphics* pGraphicsMask=NULL;
		Gdiplus::Bitmap* pBmpMask=NULL;
		Gdiplus::Graphics* pGraphicsDrawn=NULL;
		Gdiplus::Bitmap* pBmpDrawn=NULL;

		bool b = RenderTransShadowA( pGraphics, &pGraphicsMask, &pBmpMask, &pGraphicsDrawn, &pBmpDrawn);

		if(!b) return false;

		b = RenderFontShadow(
			pGraphicsDrawn,
			pGraphicsMask,
			pBmpDrawn,
			pBmpMask,
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			Gdiplus::Rect(rtDraw.X+m_ptShadowOffset.X, rtDraw.Y+m_ptShadowOffset.Y,rtDraw.Width,rtDraw.Height),
			pStrFormat);

		if(!b) 
		{
			delete pGraphicsMask;
			delete pGraphicsDrawn;
			delete pBmpDrawn;
			return false;
		}

		b = RenderTransShadowB( pGraphics, pGraphicsMask, pBmpMask, pGraphicsDrawn, pBmpDrawn);

		delete pGraphicsMask;
		delete pGraphicsDrawn;
		delete pBmpDrawn;

		if(!b) return false;
	}

	if(m_pTextStrategy)
	{
		Gdiplus::Graphics* pGraphicsPng = new Gdiplus::Graphics((Gdiplus::Image*)(m_pPngBitmap));

		pGraphicsPng->SetCompositingMode(pGraphics->GetCompositingMode());
		pGraphicsPng->SetCompositingQuality(pGraphics->GetCompositingQuality());
		pGraphicsPng->SetInterpolationMode(pGraphics->GetInterpolationMode());
		pGraphicsPng->SetSmoothingMode(pGraphics->GetSmoothingMode());
		pGraphicsPng->SetTextRenderingHint(pGraphics->GetTextRenderingHint());
		pGraphicsPng->SetPageUnit(pGraphics->GetPageUnit());
		pGraphicsPng->SetPageScale(pGraphics->GetPageScale());

		bool b = m_pTextStrategy->DrawString(
			pGraphicsPng, 
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			rtDraw, 
			pStrFormat);

		delete pGraphicsPng;

		if(!b)
			return false;
	}

	if(m_bEnableReflection)
		ProcessReflection();

	return true;
}

bool PngOutlineText::GdiDrawString(
   Gdiplus::Graphics* pGraphics, 
   LOGFONTW* pLogFont,
   const wchar_t*pszText, 
   Gdiplus::Point ptDraw)
{
	if(!pGraphics) return false;

	if(m_bEnableShadow&&m_pBkgdBitmap&&m_pFontBodyShadow&&m_pShadowStrategy&&m_pShadowStrategyMask)
	{
		Gdiplus::Graphics* pGraphicsMask=NULL;
		Gdiplus::Bitmap* pBmpMask=NULL;
		Gdiplus::Graphics* pGraphicsDrawn=NULL;
		Gdiplus::Bitmap* pBmpDrawn=NULL;

		bool b = RenderTransShadowA( pGraphics, &pGraphicsMask, &pBmpMask, &pGraphicsDrawn, &pBmpDrawn);

		if(!b) return false;
		b = GdiRenderFontShadow(
			pGraphicsDrawn,
			pGraphicsMask,
			pBmpDrawn,
			pBmpMask,
			pLogFont,
			pszText, 
			Gdiplus::Point(ptDraw.X+m_ptShadowOffset.X, ptDraw.Y+m_ptShadowOffset.Y));

		if(!b) 
		{
			delete pGraphicsMask;
			delete pGraphicsDrawn;
			delete pBmpDrawn;
			return false;
		}

		b = RenderTransShadowB( pGraphics, pGraphicsMask, pBmpMask, pGraphicsDrawn, pBmpDrawn);

		delete pGraphicsMask;
		delete pGraphicsDrawn;
		delete pBmpDrawn;

		if(!b) return false;
	}

	if(m_pTextStrategy)
	{
		Gdiplus::Graphics* pGraphicsPng = new Gdiplus::Graphics((Gdiplus::Image*)(m_pPngBitmap));

		pGraphicsPng->SetCompositingMode(pGraphics->GetCompositingMode());
		pGraphicsPng->SetCompositingQuality(pGraphics->GetCompositingQuality());
		pGraphicsPng->SetInterpolationMode(pGraphics->GetInterpolationMode());
		pGraphicsPng->SetSmoothingMode(pGraphics->GetSmoothingMode());
		pGraphicsPng->SetTextRenderingHint(pGraphics->GetTextRenderingHint());
		pGraphicsPng->SetPageUnit(pGraphics->GetPageUnit());
		pGraphicsPng->SetPageScale(pGraphics->GetPageScale());

		bool b = m_pTextStrategy->GdiDrawString(
			pGraphicsPng, 
			pLogFont,
			pszText, 
			ptDraw);

		delete pGraphicsPng;

		if(!b)
			return false;
	}

	if(m_bEnableReflection)
		ProcessReflection();

	return true;
}

bool PngOutlineText::GdiDrawString(   
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw)
{
	if(!pGraphics) return false;

	if(m_bEnableShadow&&m_pBkgdBitmap&&m_pFontBodyShadow&&m_pShadowStrategy&&m_pShadowStrategyMask)
	{
		Gdiplus::Graphics* pGraphicsMask=NULL;
		Gdiplus::Bitmap* pBmpMask=NULL;
		Gdiplus::Graphics* pGraphicsDrawn=NULL;
		Gdiplus::Bitmap* pBmpDrawn=NULL;

		bool b = RenderTransShadowA( pGraphics, &pGraphicsMask, &pBmpMask, &pGraphicsDrawn, &pBmpDrawn);

		if(!b) return false;

		b = GdiRenderFontShadow(
			pGraphicsDrawn,
			pGraphicsMask,
			pBmpDrawn,
			pBmpMask,
			pLogFont,
			pszText, 
			Gdiplus::Rect(rtDraw.X+m_ptShadowOffset.X, rtDraw.Y+m_ptShadowOffset.Y,rtDraw.Width,rtDraw.Height));

		if(!b) 
		{
			delete pGraphicsMask;
			delete pGraphicsDrawn;
			delete pBmpDrawn;
			return false;
		}

		b = RenderTransShadowB( pGraphics, pGraphicsMask, pBmpMask, pGraphicsDrawn, pBmpDrawn);

		delete pGraphicsMask;
		delete pGraphicsDrawn;
		delete pBmpDrawn;

		if(!b) return false;
	}

	if(m_pTextStrategy)
	{
		Gdiplus::Graphics* pGraphicsPng = new Gdiplus::Graphics((Gdiplus::Image*)(m_pPngBitmap));

		pGraphicsPng->SetCompositingMode(pGraphics->GetCompositingMode());
		pGraphicsPng->SetCompositingQuality(pGraphics->GetCompositingQuality());
		pGraphicsPng->SetInterpolationMode(pGraphics->GetInterpolationMode());
		pGraphicsPng->SetSmoothingMode(pGraphics->GetSmoothingMode());
		pGraphicsPng->SetTextRenderingHint(pGraphics->GetTextRenderingHint());
		pGraphicsPng->SetPageUnit(pGraphics->GetPageUnit());
		pGraphicsPng->SetPageScale(pGraphics->GetPageScale());

		bool b = m_pTextStrategy->GdiDrawString(
			pGraphicsPng, 
			pLogFont,
			pszText, 
			rtDraw);

		delete pGraphicsPng;

		if(!b)
			return false;
	}

	if(m_bEnableReflection)
		ProcessReflection();

	return true;
}

bool PngOutlineText::RenderTransShadowA(
	Gdiplus::Graphics* pGraphics,
	Gdiplus::Graphics** ppGraphicsMask,
	Gdiplus::Bitmap** ppBmpMask,
	Gdiplus::Graphics** ppGraphicsDrawn,
	Gdiplus::Bitmap** ppBmpDrawn)
{
	if(!pGraphics) return false;

	*ppBmpMask = 
		m_pPngBitmap->Clone(0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight(), PixelFormat32bppARGB);

	*ppGraphicsMask = new Gdiplus::Graphics((Gdiplus::Image*)(*ppBmpMask));
	Gdiplus::SolidBrush brushBlack(Gdiplus::Color(0,0,0));
	(*ppGraphicsMask)->FillRectangle(&brushBlack, 0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight() );

	(*ppGraphicsMask)->SetCompositingMode(pGraphics->GetCompositingMode());
	(*ppGraphicsMask)->SetCompositingQuality(pGraphics->GetCompositingQuality());
	(*ppGraphicsMask)->SetInterpolationMode(pGraphics->GetInterpolationMode());
	(*ppGraphicsMask)->SetSmoothingMode(pGraphics->GetSmoothingMode());
	(*ppGraphicsMask)->SetTextRenderingHint(pGraphics->GetTextRenderingHint());
	(*ppGraphicsMask)->SetPageUnit(pGraphics->GetPageUnit());
	(*ppGraphicsMask)->SetPageScale(pGraphics->GetPageScale());

	*ppBmpDrawn = 
		m_pPngBitmap->Clone(0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight(), PixelFormat32bppARGB);

	*ppGraphicsDrawn = new Gdiplus::Graphics((Gdiplus::Image*)(*ppBmpDrawn));
	Gdiplus::SolidBrush brushWhite(Gdiplus::Color(255,255,255));
	(*ppGraphicsDrawn)->FillRectangle(&brushWhite, 0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight() );

	(*ppGraphicsDrawn)->SetCompositingMode(pGraphics->GetCompositingMode());
	(*ppGraphicsDrawn)->SetCompositingQuality(pGraphics->GetCompositingQuality());
	(*ppGraphicsDrawn)->SetInterpolationMode(pGraphics->GetInterpolationMode());
	(*ppGraphicsDrawn)->SetSmoothingMode(pGraphics->GetSmoothingMode());
	(*ppGraphicsDrawn)->SetTextRenderingHint(pGraphics->GetTextRenderingHint());
	(*ppGraphicsDrawn)->SetPageUnit(pGraphics->GetPageUnit());
	(*ppGraphicsDrawn)->SetPageScale(pGraphics->GetPageScale());

	return true;
}

bool PngOutlineText::RenderTransShadowB(
	Gdiplus::Graphics* pGraphics,
	Gdiplus::Graphics* pGraphicsMask,
	Gdiplus::Bitmap* pBmpMask,
	Gdiplus::Graphics* pGraphicsDrawn,
	Gdiplus::Bitmap* pBmpDrawn)
{
	if(!pGraphics||!pGraphicsMask||!pBmpMask||!pGraphicsDrawn||!pBmpDrawn)
		return false;

	UINT* pixelsSrc = NULL;
	UINT* pixelsDest = NULL;
	UINT* pixelsMask = NULL;
	UINT* pixelsDrawn = NULL;

	using namespace Gdiplus;

	BitmapData bitmapDataDest;
	BitmapData bitmapDataMask;
	BitmapData bitmapDataDrawn;
	Rect rect(0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight() );

	m_pPngBitmap->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataDest );

	pBmpMask->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataMask );

	pBmpDrawn->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataDrawn );

	// Write to the temporary buffer provided by LockBits.
	pixelsDest = (UINT*)bitmapDataDest.Scan0;
	pixelsMask = (UINT*)bitmapDataMask.Scan0;
	pixelsDrawn = (UINT*)bitmapDataDrawn.Scan0;

	if( !pixelsDest || !pixelsMask || !pixelsDrawn)
	{
		return false;
	}

	UINT col = 0;
	int stride = bitmapDataDest.Stride >> 2;
	for(UINT row = 0; row < bitmapDataDest.Height; ++row)
	{
		for(col = 0; col < bitmapDataDest.Width; ++col)
		{
			using namespace Gdiplus;
			UINT index = row * stride + col;
			BYTE nAlpha = pixelsMask[index] & 0xff;
			if(nAlpha>0)
			{
				UINT nDrawn = 
					nAlpha << 24 | m_clrShadow.GetR() << 16 | m_clrShadow.GetG() << 8 | m_clrShadow.GetB();
				nDrawn &= 0x00ffffff;
				pixelsDest[index] = nDrawn | nAlpha<<24;
				//pixelsDest[index] = nDrawn; // cannot work
				//pixelsDrawn[index] |= 0xff000000; // cannot work
			}
		}
	}

	pBmpDrawn->UnlockBits(&bitmapDataDrawn);
	pBmpMask->UnlockBits(&bitmapDataMask);
	m_pPngBitmap->UnlockBits(&bitmapDataDest);

	if(pBmpMask)
	{
		delete pBmpMask;
		pBmpMask = NULL;
	}

	return true;
}

bool PngOutlineText::GetEncoderClsid(const WCHAR* format, CLSID* pClsid)
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

bool PngOutlineText::SavePngFile(const wchar_t* pszFile)
{
	if(m_pPngBitmap)
	{
		CLSID pngClsid;
		GetEncoderClsid(L"image/png", &pngClsid);
		Gdiplus::Status status = m_pPngBitmap->Save(pszFile, &pngClsid, NULL);
		return status == Gdiplus::Ok ? true : false;
	}

	return false;
}

bool PngOutlineText::SaveReflectionPngFile(const wchar_t* pszFile)
{
	if(m_pReflectionPngBitmap)
	{
		CLSID pngClsid;
		GetEncoderClsid(L"image/png", &pngClsid);
		Gdiplus::Status status = m_pReflectionPngBitmap->Save(pszFile, &pngClsid, NULL);
		return status == Gdiplus::Ok ? true : false;
	}

	return false;
}

bool PngOutlineText::MultiplyAlpha(BYTE nMultiplyAlpha)
{
	if(!m_pPngBitmap)
		return false;

	UINT* pixelsDest = NULL;

	using namespace Gdiplus;

	BitmapData bitmapDataDest;
	Rect rect(0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight() );

	m_pPngBitmap->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataDest );


	// Write to the temporary buffer provided by LockBits.
	pixelsDest = (UINT*)bitmapDataDest.Scan0;

	if( !pixelsDest )
		return false;

	UINT col = 0;
	int stride = bitmapDataDest.Stride >> 2;
	for(UINT row = 0; row < bitmapDataDest.Height; ++row)
	{
		for(col = 0; col < bitmapDataDest.Width; ++col)
		{
			UINT index = row * stride + col;
			UINT nOrigAlpha = pixelsDest[index] & 0xff000000;
			nOrigAlpha = nOrigAlpha>>24;
			UINT nDestAlpha = (nMultiplyAlpha*nOrigAlpha)>>8;
			if(nDestAlpha>255)
				nDestAlpha=255;

			pixelsDest[index] &= 0xffffff;
			pixelsDest[index] |= nDestAlpha<<24;
		}
	}

	m_pPngBitmap->UnlockBits(&bitmapDataDest);

	return true;
}

bool PngOutlineText::BitBlt(
	Gdiplus::Bitmap* pBmpSrc,
	UINT nSrcStartX,
	UINT nSrcStartY,
	UINT nSrcWidth,
	UINT nSrcHeight,
	Gdiplus::Bitmap* pBmpDest,
	UINT nDestStartX,
	UINT nDestStartY)
{
	if(!pBmpSrc||!pBmpDest)
		return false;

	UINT* pixelsDest = NULL;
	UINT* pixelsSrc = NULL;

	using namespace Gdiplus;

	BitmapData bitmapDataDest;
	Rect rectDest(0, 0, pBmpDest->GetWidth(), pBmpDest->GetHeight() );

	BitmapData bitmapDataSrc;
	Rect rectSrc(0, 0, pBmpSrc->GetWidth(), pBmpSrc->GetHeight() );

	pBmpDest->LockBits(
		&rectDest,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataDest );

	pBmpSrc->LockBits(
		&rectSrc,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataSrc );

	pixelsDest = (UINT*)bitmapDataDest.Scan0;
	pixelsSrc = (UINT*)bitmapDataSrc.Scan0;

	if( !pixelsDest || !pixelsSrc )
		return false;

	UINT colSrc = 0;
	int strideDest = bitmapDataDest.Stride >> 2;
	int strideSrc = bitmapDataSrc.Stride >> 2;
	bool bBreak=false;
	for(UINT rowSrc = nSrcStartY; rowSrc < bitmapDataSrc.Height; ++rowSrc)
	{
		if(rowSrc>bitmapDataSrc.Height||
			(nDestStartY+(rowSrc-nSrcStartY))>bitmapDataDest.Height||
			rowSrc>nSrcHeight)
			break;
		for(colSrc = nSrcStartX; colSrc < bitmapDataSrc.Width; ++colSrc)
		{
			if(colSrc>bitmapDataSrc.Width||
				(nDestStartX+(colSrc-nSrcStartX))>bitmapDataDest.Width||
				colSrc>nSrcWidth)
			{
				bBreak=true;
				break;
			}
			UINT indexDest = (nDestStartY+(rowSrc-nSrcStartY)) * strideDest + (nDestStartX+(colSrc-nSrcStartX));
			UINT indexSrc = rowSrc * strideSrc + colSrc;
			UINT nOrigAlpha = pixelsSrc[indexSrc] & 0xff000000;
			BYTE nAlpha = nOrigAlpha>>24;
			if(nAlpha>0)
				pixelsDest[indexDest] = Alphablend(pixelsDest[indexDest],pixelsSrc[indexSrc],nAlpha);
		}
		if(bBreak)
			break;
	}

	pBmpSrc->UnlockBits(&bitmapDataSrc);
	pBmpDest->UnlockBits(&bitmapDataDest);

	return true;
}

bool PngOutlineText::MeasureString(
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
	float fDestWidth1 = 0.0f;
	float fDestHeight1 = 0.0f;
	bool b = false;
	if(m_pTextStrategy)
	{
		b = m_pTextStrategy->MeasureString(
			pGraphics, 
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			ptDraw, 
			pStrFormat,
			pfPixelsStartX,
			pfPixelsStartY,
			&fDestWidth1,
			&fDestHeight1 );

		if(!b)
			return false;
	}

	float fDestWidth2 = 0.0f;
	float fDestHeight2 = 0.0f;
	if(m_bEnableShadow)
	{
		bool b = m_pShadowStrategy->MeasureString(
			pGraphics, 
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			ptDraw, 
			pStrFormat,
			NULL,
			NULL,
			&fDestWidth2,
			&fDestHeight2 );

		if(b)
		{
			float fDestWidth3 = 0.0f;
			float fDestHeight3 = 0.0f;
			b = GDIPath::ConvertToPixels(pGraphics,m_ptShadowOffset.X,m_ptShadowOffset.Y,
				NULL,NULL,&fDestWidth3,&fDestHeight3);
			if(b)
			{
				fDestWidth2 += fDestWidth3;
				fDestHeight2 += fDestHeight3;
			}
		}
		else
			return false;
	}

	if(fDestWidth1>fDestWidth2 || fDestHeight1>fDestHeight2)
	{
		*pfDestWidth = fDestWidth1;
		*pfDestHeight = fDestHeight1;
	}
	else
	{
		*pfDestWidth = fDestWidth2;
		*pfDestHeight = fDestHeight2;
	}

	return true;
}

bool PngOutlineText::MeasureString(
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
	float fDestWidth1 = 0.0f;
	float fDestHeight1 = 0.0f;
	bool b = false;
	if(m_pTextStrategy)
	{
		b = m_pTextStrategy->MeasureString(
			pGraphics, 
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			rtDraw, 
			pStrFormat,
			pfPixelsStartX,
			pfPixelsStartY,
			&fDestWidth1,
			&fDestHeight1 );

		if(!b)
			return false;
	}

	float fDestWidth2 = 0.0f;
	float fDestHeight2 = 0.0f;
	if(m_bEnableShadow)
	{
		bool b = m_pShadowStrategy->MeasureString(
			pGraphics, 
			pFontFamily,
			fontStyle,
			nfontSize,
			pszText, 
			rtDraw, 
			pStrFormat,
			NULL,
			NULL,
			&fDestWidth2,
			&fDestHeight2 );

		if(b)
		{
			float fDestWidth3 = 0.0f;
			float fDestHeight3 = 0.0f;
			b = GDIPath::ConvertToPixels(pGraphics,m_ptShadowOffset.X,m_ptShadowOffset.Y,
				NULL,NULL,&fDestWidth3,&fDestHeight3);
			if(b)
			{
				fDestWidth2 += fDestWidth3;
				fDestHeight2 += fDestHeight3;
			}
		}
		else
			return false;
	}

	if(fDestWidth1>fDestWidth2 || fDestHeight1>fDestHeight2)
	{
		*pfDestWidth = fDestWidth1;
		*pfDestHeight = fDestHeight1;
	}
	else
	{
		*pfDestWidth = fDestWidth2;
		*pfDestHeight = fDestHeight2;
	}

	return true;
}

bool PngOutlineText::GdiMeasureString(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Point ptDraw,
	float* pfPixelsStartX,
	float* pfPixelsStartY,
	float* pfDestWidth,
	float* pfDestHeight )
{
	float fDestWidth1 = 0.0f;
	float fDestHeight1 = 0.0f;
	bool b = false;
	if(m_pTextStrategy)
	{
		b = m_pTextStrategy->GdiMeasureString(
			pGraphics, 
			pLogFont,
			pszText, 
			ptDraw,
			pfPixelsStartX,
			pfPixelsStartY,
			&fDestWidth1,
			&fDestHeight1 );

		if(!b)
			return false;
	}

	float fDestWidth2 = 0.0f;
	float fDestHeight2 = 0.0f;
	if(m_bEnableShadow)
	{
		bool b = m_pShadowStrategy->GdiMeasureString(
			pGraphics, 
			pLogFont,
			pszText, 
			ptDraw,
			NULL,
			NULL,
			&fDestWidth2,
			&fDestHeight2 );

		if(b)
		{
			float fDestWidth3 = 0.0f;
			float fDestHeight3 = 0.0f;
			b = GDIPath::ConvertToPixels(pGraphics,m_ptShadowOffset.X,m_ptShadowOffset.Y,
				NULL,NULL,&fDestWidth3,&fDestHeight3);
			if(b)
			{
				fDestWidth2 += fDestWidth3;
				fDestHeight2 += fDestHeight3;
			}
		}
		else
			return false;
	}

	if(fDestWidth1>fDestWidth2 || fDestHeight1>fDestHeight2)
	{
		*pfDestWidth = fDestWidth1;
		*pfDestHeight = fDestHeight1;
	}
	else
	{
		*pfDestWidth = fDestWidth2;
		*pfDestHeight = fDestHeight2;
	}

	return true;
}

bool PngOutlineText::GdiMeasureString(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw,
	float* pfPixelsStartX,
	float* pfPixelsStartY,
	float* pfDestWidth,
	float* pfDestHeight )
{
	float fDestWidth1 = 0.0f;
	float fDestHeight1 = 0.0f;
	bool b = false;
	if(m_pTextStrategy)
	{
		b = m_pTextStrategy->GdiMeasureString(
			pGraphics, 
			pLogFont,
			pszText, 
			rtDraw,
			pfPixelsStartX,
			pfPixelsStartY,
			&fDestWidth1,
			&fDestHeight1 );

		if(!b)
			return false;
	}

	float fDestWidth2 = 0.0f;
	float fDestHeight2 = 0.0f;
	if(m_bEnableShadow)
	{
		bool b = m_pShadowStrategy->GdiMeasureString(
			pGraphics, 
			pLogFont,
			pszText, 
			rtDraw,
			NULL,
			NULL,
			&fDestWidth2,
			&fDestHeight2 );

		if(b)
		{
			float fDestWidth3 = 0.0f;
			float fDestHeight3 = 0.0f;
			b = GDIPath::ConvertToPixels(pGraphics,m_ptShadowOffset.X,m_ptShadowOffset.Y,
				NULL,NULL,&fDestWidth3,&fDestHeight3);
			if(b)
			{
				fDestWidth2 += fDestWidth3;
				fDestHeight2 += fDestHeight3;
			}
		}
		else
			return false;
	}

	if(fDestWidth1>fDestWidth2 || fDestHeight1>fDestHeight2)
	{
		*pfDestWidth = fDestWidth1;
		*pfDestHeight = fDestHeight1;
	}
	else
	{
		*pfDestWidth = fDestWidth2;
		*pfDestHeight = fDestHeight2;
	}

	return true;
}

bool PngOutlineText::GdiMeasureStringRealHeight(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Point ptDraw,
	float* pfPixelsStartX,
	float* pfPixelsStartY,
	float* pfDestWidth,
	float* pfDestHeight )
{
	float fDestWidth1 = 0.0f;
	float fDestHeight1 = 0.0f;
	bool b = false;
	if(m_pTextStrategy)
	{
		b = m_pTextStrategy->GdiMeasureStringRealHeight(
			pGraphics, 
			pLogFont,
			pszText, 
			ptDraw,
			pfPixelsStartX,
			pfPixelsStartY,
			&fDestWidth1,
			&fDestHeight1 );

		if(!b)
			return false;
	}

	float fDestWidth2 = 0.0f;
	float fDestHeight2 = 0.0f;
	if(m_bEnableShadow)
	{
		bool b = m_pShadowStrategy->GdiMeasureStringRealHeight(
			pGraphics, 
			pLogFont,
			pszText, 
			ptDraw,
			NULL,
			NULL,
			&fDestWidth2,
			&fDestHeight2 );

		if(b)
		{
			float fDestWidth3 = 0.0f;
			float fDestHeight3 = 0.0f;
			b = GDIPath::ConvertToPixels(pGraphics,m_ptShadowOffset.X,m_ptShadowOffset.Y,
				NULL,NULL,&fDestWidth3,&fDestHeight3);
			if(b)
			{
				fDestWidth2 += fDestWidth3;
				fDestHeight2 += fDestHeight3;
			}
		}
		else
			return false;
	}

	if(fDestWidth1>fDestWidth2 || fDestHeight1>fDestHeight2)
	{
		*pfDestWidth = fDestWidth1;
		*pfDestHeight = fDestHeight1;
	}
	else
	{
		*pfDestWidth = fDestWidth2;
		*pfDestHeight = fDestHeight2;
	}

	return true;
}

bool PngOutlineText::GdiMeasureStringRealHeight(
	Gdiplus::Graphics* pGraphics, 
	LOGFONTW* pLogFont,
	const wchar_t*pszText, 
	Gdiplus::Rect rtDraw,
	float* pfPixelsStartX,
	float* pfPixelsStartY,
	float* pfDestWidth,
	float* pfDestHeight )
{
	float fDestWidth1 = 0.0f;
	float fDestHeight1 = 0.0f;
	bool b = false;
	if(m_pTextStrategy)
	{
		b = m_pTextStrategy->GdiMeasureStringRealHeight(
			pGraphics, 
			pLogFont,
			pszText, 
			rtDraw,
			pfPixelsStartX,
			pfPixelsStartY,
			&fDestWidth1,
			&fDestHeight1 );

		if(!b)
			return false;
	}

	float fDestWidth2 = 0.0f;
	float fDestHeight2 = 0.0f;
	if(m_bEnableShadow)
	{
		bool b = m_pShadowStrategy->GdiMeasureStringRealHeight(
			pGraphics, 
			pLogFont,
			pszText, 
			rtDraw,
			NULL,
			NULL,
			&fDestWidth2,
			&fDestHeight2 );

		if(b)
		{
			float fDestWidth3 = 0.0f;
			float fDestHeight3 = 0.0f;
			b = GDIPath::ConvertToPixels(pGraphics,m_ptShadowOffset.X,m_ptShadowOffset.Y,
				NULL,NULL,&fDestWidth3,&fDestHeight3);
			if(b)
			{
				fDestWidth2 += fDestWidth3;
				fDestHeight2 += fDestHeight3;
			}
		}
		else
			return false;
	}

	if(fDestWidth1>fDestWidth2 || fDestHeight1>fDestHeight2)
	{
		*pfDestWidth = fDestWidth1;
		*pfDestHeight = fDestHeight1;
	}
	else
	{
		*pfDestWidth = fDestWidth2;
		*pfDestHeight = fDestHeight2;
	}

	return true;
}

void PngOutlineText::Reflection(float fBegAlpha, float fEndAlpha, float fShown)
{
	m_fBegAlpha = fBegAlpha;
	m_fEndAlpha = fEndAlpha;
	m_fShown = fShown;
}

bool PngOutlineText::ProcessReflection()
{
	using namespace Gdiplus;
	if(m_pPngBitmap)
	{
		if(m_pReflectionPngBitmap)
		{
			delete m_pReflectionPngBitmap;
			m_pReflectionPngBitmap = NULL;
		}

		m_pReflectionPngBitmap = m_pPngBitmap->Clone(0, 0, m_pPngBitmap->GetWidth(), m_pPngBitmap->GetHeight(), PixelFormat32bppARGB);
		m_pReflectionPngBitmap->RotateFlip(RotateNoneFlipY);

		UINT* pixels = NULL;

		BitmapData bitmapData;
		Rect rect(0, 0, m_pReflectionPngBitmap->GetWidth(), m_pReflectionPngBitmap->GetHeight() );

		m_pReflectionPngBitmap->LockBits(
			&rect,
			ImageLockModeWrite,
			PixelFormat32bppARGB,
			&bitmapData );

		// Write to the buffer provided by LockBits.
		pixels = (UINT*)bitmapData.Scan0;

		if( !pixels)
			return false;

		UINT end = UINT(m_fShown * bitmapData.Height);
		UINT nMultiplyAlpha = 0;
		float diff = m_fBegAlpha - m_fEndAlpha;

		UINT col = 0;
		int stride = bitmapData.Stride >> 2;
		for(UINT row = 0; row < bitmapData.Height; ++row)
		{
			if(row<end)
			{
				if(m_fBegAlpha == m_fEndAlpha)
					nMultiplyAlpha = UINT(m_fBegAlpha * 255);
				else
				{
					if(m_fBegAlpha > m_fEndAlpha)
						nMultiplyAlpha = (m_fBegAlpha - (diff * row / end)) * 255;
					else
						nMultiplyAlpha = (m_fBegAlpha + (diff * row / end)) * 255;
				}
			}
			else
				nMultiplyAlpha = 0;

			for(col = 0; col < bitmapData.Width; ++col)
			{
				UINT index = row * stride + col;
				BYTE nAlpha = (pixels[index] & 0xff000000) >> 24;

				nAlpha = nAlpha * nMultiplyAlpha / 255;
				pixels[index] &= 0xffffff;
				pixels[index] |= (nAlpha << 24);
			}
		}
		m_pReflectionPngBitmap->UnlockBits(&bitmapData);

		return true;
	}

	return false;
}
