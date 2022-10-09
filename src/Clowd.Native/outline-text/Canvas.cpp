#include "pch.h"
#include "Canvas.h"
#include "MaskColor.h"
#include "PngOutlineText.h"
#include "DrawGradient.h"

using namespace TextDesigner;

ITextStrategy* Canvas::TextGlow(
	Gdiplus::Color clrText, 
	Gdiplus::Color clrOutline, 
	int nThickness)
{
	TextGlowStrategy* pStrat = new TextGlowStrategy();
	pStrat->Init(clrText,clrOutline,nThickness);

	return pStrat;
}

ITextStrategy* Canvas::TextGlow(
	Gdiplus::Brush* pbrushText, 
	Gdiplus::Color clrOutline, 
	int nThickness)
{
	TextGlowStrategy* pStrat = new TextGlowStrategy();
	pStrat->Init(pbrushText,clrOutline,nThickness);

	return pStrat;
}

ITextStrategy* Canvas::TextOutline(
	Gdiplus::Color clrText, 
	Gdiplus::Color clrOutline, 
	int nThickness)
{
	TextOutlineStrategy* pStrat = new TextOutlineStrategy();
	pStrat->Init(clrText,clrOutline,nThickness);

	return pStrat;
}

ITextStrategy* Canvas::TextOutline(
	Gdiplus::Brush* pbrushText, 
	Gdiplus::Color clrOutline, 
	int nThickness)
{
	TextOutlineStrategy* pStrat = new TextOutlineStrategy();
	pStrat->Init(pbrushText,clrOutline,nThickness);

	return pStrat;
}

ITextStrategy* Canvas::TextGradOutline(
	Gdiplus::Color clrText, 
	Gdiplus::Color clrOutline1, 
	Gdiplus::Color clrOutline2, 
	int nThickness)
{
	TextGradOutlineStrategy* pStrat = new TextGradOutlineStrategy();
	pStrat->Init(clrText,clrOutline1,clrOutline2,nThickness);

	return pStrat;
}

ITextStrategy* Canvas::TextGradOutline(
	Gdiplus::Brush* pbrushText, 
	Gdiplus::Color clrOutline1, 
	Gdiplus::Color clrOutline2, 
	int nThickness)
{
	TextGradOutlineStrategy* pStrat = new TextGradOutlineStrategy();
	pStrat->Init(pbrushText,clrOutline1,clrOutline2,nThickness);

	return pStrat;
}

ITextStrategy* Canvas::TextNoOutline(
	Gdiplus::Color clrText)
{
	TextNoOutlineStrategy* pStrat = new TextNoOutlineStrategy();
	pStrat->Init(clrText);

	return pStrat;
}

ITextStrategy* Canvas::TextNoOutline(
	Gdiplus::Brush* pbrushText)
{
	TextNoOutlineStrategy* pStrat = new TextNoOutlineStrategy();
	pStrat->Init(pbrushText);

	return pStrat;
}

ITextStrategy* Canvas::TextOnlyOutline(
	Gdiplus::Color clrOutline, 
	int nThickness,
	bool bRoundedEdge)
{
	TextOnlyOutlineStrategy* pStrat = new TextOnlyOutlineStrategy();
	pStrat->Init(clrOutline,nThickness,bRoundedEdge);

	return pStrat;
}

Gdiplus::Bitmap* Canvas::GenImage(int width, int height)
{
	return new Gdiplus::Bitmap(width, height, PixelFormat32bppARGB);
}

Gdiplus::Bitmap* Canvas::GenImage(int width, int height, std::vector<Gdiplus::Color>& vec, bool bHorizontal)
{
	Gdiplus::Bitmap* bmp = new Gdiplus::Bitmap(width, height, PixelFormat32bppARGB);

	DrawGradient::Draw(*bmp, vec, bHorizontal);

	return bmp;
}

Gdiplus::Bitmap* Canvas::GenImage(int width, int height, Gdiplus::Color clr)
{
	Gdiplus::Bitmap* bmp = new Gdiplus::Bitmap(width, height, PixelFormat32bppARGB);

	if(bmp==NULL)
		return NULL;

	UINT* pixels = NULL;

	using namespace Gdiplus;

	BitmapData bitmapData;
	Rect rect(0, 0, bmp->GetWidth(), bmp->GetHeight() );

	bmp->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapData );

	pixels = (UINT*)bitmapData.Scan0;

	if( !pixels )
		return NULL;

	UINT col = 0;
	int stride = bitmapData.Stride >> 2;
	UINT color = clr.GetAlpha() << 24 | clr.GetRed() << 16 | clr.GetGreen() << 8 | clr.GetBlue();
	for(UINT row = 0; row < bitmapData.Height; ++row)
	{
		for(col = 0; col < bitmapData.Width; ++col)
		{
			UINT index = row * stride + col;
			pixels[index] = color;
		}
	}

	bmp->UnlockBits(&bitmapData);

	return bmp;
}

Gdiplus::Bitmap* Canvas::GenImage(int width, int height, Gdiplus::Color clr, BYTE alpha)
{
	Gdiplus::Bitmap* bmp = new Gdiplus::Bitmap(width, height, PixelFormat32bppARGB);

	if(bmp==NULL)
		return NULL;

	UINT* pixels = NULL;

	using namespace Gdiplus;

	BitmapData bitmapData;
	Rect rect(0, 0, bmp->GetWidth(), bmp->GetHeight() );

	bmp->LockBits(
		&rect,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapData );

	pixels = (UINT*)bitmapData.Scan0;

	if( !pixels )
		return NULL;

	UINT col = 0;
	int stride = bitmapData.Stride >> 2;
	UINT color = alpha << 24 | clr.GetRed() << 16 | clr.GetGreen() << 8 | clr.GetBlue();
	for(UINT row = 0; row < bitmapData.Height; ++row)
	{
		for(col = 0; col < bitmapData.Width; ++col)
		{
			UINT index = row * stride + col;
			pixels[index] = color;
		}
	}

	bmp->UnlockBits(&bitmapData);

	return bmp;
}

Gdiplus::Bitmap* Canvas::GenMask(
	ITextStrategy* pStrategy, 
	int width, 
	int height, 
	Gdiplus::Point offset,
	TextContext* pTextContext)
{
	if(pStrategy==NULL||pTextContext==NULL)
		return NULL;

	Gdiplus::Bitmap* pBmp = new Gdiplus::Bitmap(width, height, PixelFormat32bppARGB);

	Gdiplus::Graphics graphics((Gdiplus::Image*)(pBmp));
	graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
	graphics.SetInterpolationMode(Gdiplus::InterpolationModeHighQualityBicubic);

	pStrategy->DrawString(&graphics,
		pTextContext->pFontFamily, 
		pTextContext->fontStyle, 
		pTextContext->nfontSize, 
		pTextContext->pszText, 
		Gdiplus::Point(pTextContext->ptDraw.X + offset.X, pTextContext->ptDraw.Y + offset.Y),
		&(pTextContext->strFormat));

	return pBmp;
}

Gdiplus::Bitmap* Canvas::GenMask(
	ITextStrategy* pStrategy, 
	int width, 
	int height, 
	Gdiplus::Point offset,
	TextContext* pTextContext,
	Gdiplus::Matrix& mat)
{
	if(pStrategy==NULL||pTextContext==NULL)
		return NULL;

	Gdiplus::Bitmap* pBmp = new Gdiplus::Bitmap(width, height, PixelFormat32bppARGB);

	Gdiplus::Graphics graphics((Gdiplus::Image*)(pBmp));
	graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
	graphics.SetInterpolationMode(Gdiplus::InterpolationModeHighQualityBicubic);
	graphics.SetTransform(&mat);

	pStrategy->DrawString(&graphics,
		pTextContext->pFontFamily, 
		pTextContext->fontStyle, 
		pTextContext->nfontSize, 
		pTextContext->pszText, 
		Gdiplus::Point(pTextContext->ptDraw.X + offset.X, pTextContext->ptDraw.Y + offset.Y),
		&(pTextContext->strFormat));

	graphics.ResetTransform();

	return pBmp;
}

bool Canvas::MeasureMaskLength(
	Gdiplus::Bitmap* pMask, 
	Gdiplus::Color maskColor,
	UINT& top,
	UINT& left,
	UINT& bottom,
	UINT& right)
{
	top = 30000;
	left = 30000;
	bottom = 0;
	right = 0;

	if(pMask==NULL)
		return false;

	UINT* pixelsMask = NULL;

	using namespace Gdiplus;

	BitmapData bitmapDataMask;
	Rect rect(0, 0, pMask->GetWidth(), pMask->GetHeight() );

	pMask->LockBits(
		&rect,
		ImageLockModeRead,
		PixelFormat32bppARGB,
		&bitmapDataMask );

	pixelsMask = (UINT*)bitmapDataMask.Scan0;

	if( !pixelsMask )
		return false;

	UINT col = 0;
	int stride = bitmapDataMask.Stride >> 2;
	for(UINT row = 0; row < bitmapDataMask.Height; ++row)
	{
		for(col = 0; col < bitmapDataMask.Width; ++col)
		{
			UINT index = row * stride + col;
			BYTE nAlpha = 0;

			if(MaskColor::IsEqual(maskColor, MaskColor::Red()))
				nAlpha = (pixelsMask[index] & 0xff0000)>>16;
			else if(MaskColor::IsEqual(maskColor, MaskColor::Green()))
				nAlpha = (pixelsMask[index] & 0xff00)>>8;
			else if(MaskColor::IsEqual(maskColor, MaskColor::Blue()))
				nAlpha = pixelsMask[index] & 0xff;

			if(nAlpha>0)
			{
				if(col < left)
					left = col;
				if(row < top)
					top = row;
				if(col > right)
					right = col;
				if(row > bottom)
					bottom = row;

			}
		}
	}

	pMask->UnlockBits(&bitmapDataMask);

	return true;

}

bool Canvas::ApplyImageToMask(
	Gdiplus::Bitmap* pImage, 
	Gdiplus::Bitmap* pMask, 
	Gdiplus::Bitmap* pCanvas, 
	Gdiplus::Color maskColor,
	bool NoAlphaAtBoundary)
{
	if(pImage==NULL||pMask==NULL||pCanvas==NULL)
		return false;

	UINT* pixelsImage = NULL;
	UINT* pixelsMask = NULL;
	UINT* pixelsCanvas = NULL;

	using namespace Gdiplus;

	BitmapData bitmapDataImage;
	BitmapData bitmapDataMask;
	BitmapData bitmapDataCanvas;
	Rect rectCanvas(0, 0, pCanvas->GetWidth(), pCanvas->GetHeight() );
	Rect rectMask(0, 0, pMask->GetWidth(), pMask->GetHeight() );
	Rect rectImage(0, 0, pImage->GetWidth(), pImage->GetHeight() );

	pImage->LockBits(
		&rectImage,
		ImageLockModeRead,
		PixelFormat32bppARGB,
		&bitmapDataImage );

	pMask->LockBits(
		&rectMask,
		ImageLockModeRead,
		PixelFormat32bppARGB,
		&bitmapDataMask );

	pCanvas->LockBits(
		&rectCanvas,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataCanvas );

	pixelsImage = (UINT*)bitmapDataImage.Scan0;
	pixelsMask = (UINT*)bitmapDataMask.Scan0;
	pixelsCanvas = (UINT*)bitmapDataCanvas.Scan0;

	if( !pixelsImage || !pixelsMask || !pixelsCanvas )
		return false;

	UINT col = 0;
	int stride = bitmapDataCanvas.Stride >> 2;
	for(UINT row = 0; row < bitmapDataCanvas.Height; ++row)
	{
		for(col = 0; col < bitmapDataCanvas.Width; ++col)
		{
			if(row >= bitmapDataImage.Height || col >= bitmapDataImage.Width)
				continue;
			if(row >= bitmapDataMask.Height || col >= bitmapDataMask.Width)
				continue;

			UINT index = row * stride + col;
			UINT indexMask = row * (bitmapDataMask.Stride>>2) + col;
			UINT indexImage = row * (bitmapDataImage.Stride>>2) + col;

			BYTE mask = 0;
			
			if(MaskColor::IsEqual(maskColor, MaskColor::Red()))
				mask = (pixelsMask[indexMask] & 0xff0000)>>16;
			else if(MaskColor::IsEqual(maskColor, MaskColor::Green()))
				mask = (pixelsMask[indexMask] & 0xff00)>>8;
			else if(MaskColor::IsEqual(maskColor, MaskColor::Blue()))
				mask = pixelsMask[indexMask] & 0xff;

			if(mask>0)
			{
				if(NoAlphaAtBoundary)
				{
					pixelsCanvas[index] = AlphablendNoAlphaAtBoundary(pixelsCanvas[index], pixelsImage[indexImage], (BYTE)(pixelsMask[indexMask] >> 24), (BYTE)(pixelsMask[indexMask] >> 24));
				}
				else
				{
					pixelsCanvas[index] = Alphablend(pixelsCanvas[index], pixelsImage[indexImage], (BYTE)(pixelsMask[indexMask] >> 24), (BYTE)(pixelsMask[indexMask] >> 24));
				}
			}
		}
	}

	pCanvas->UnlockBits(&bitmapDataCanvas);
	pMask->UnlockBits(&bitmapDataMask);
	pImage->UnlockBits(&bitmapDataImage);

	return true;
}

bool Canvas::ApplyColorToMask(
	Gdiplus::Color clr, 
	Gdiplus::Bitmap* pMask, 
	Gdiplus::Bitmap* pCanvas, 
	Gdiplus::Color maskColor)
{
	if(pMask==NULL||pCanvas==NULL)
		return false;

	UINT* pixelsMask = NULL;
	UINT* pixelsCanvas = NULL;

	using namespace Gdiplus;

	BitmapData bitmapDataMask;
	BitmapData bitmapDataCanvas;
	Rect rectCanvas(0, 0, pCanvas->GetWidth(), pCanvas->GetHeight() );
	Rect rectMask(0, 0, pMask->GetWidth(), pMask->GetHeight() );

	pMask->LockBits(
		&rectMask,
		ImageLockModeRead,
		PixelFormat32bppARGB,
		&bitmapDataMask );

	pCanvas->LockBits(
		&rectCanvas,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataCanvas );

	pixelsMask = (UINT*)bitmapDataMask.Scan0;
	pixelsCanvas = (UINT*)bitmapDataCanvas.Scan0;

	if( !pixelsMask || !pixelsCanvas )
		return false;

	UINT col = 0;
	int stride = bitmapDataCanvas.Stride >> 2;
	for(UINT row = 0; row < bitmapDataCanvas.Height; ++row)
	{
		for(col = 0; col < bitmapDataCanvas.Width; ++col)
		{
			if(row >= bitmapDataMask.Height || col >= bitmapDataMask.Width)
				continue;

			UINT index = row * stride + col;
			UINT indexMask = row * (bitmapDataMask.Stride>>2) + col;

			BYTE nAlpha = 0;

			if(MaskColor::IsEqual(maskColor, MaskColor::Red()))
				nAlpha = (pixelsMask[indexMask] & 0xff0000)>>16;
			else if(MaskColor::IsEqual(maskColor, MaskColor::Green()))
				nAlpha = (pixelsMask[indexMask] & 0xff00)>>8;
			else if(MaskColor::IsEqual(maskColor, MaskColor::Blue()))
				nAlpha = pixelsMask[indexMask] & 0xff;

			UINT color = 0xff << 24 | clr.GetRed() << 16 | clr.GetGreen() << 8 | clr.GetBlue() ;

			if(nAlpha>0)
				pixelsCanvas[index] = Alphablend(pixelsCanvas[index], color, nAlpha, nAlpha);
		}
	}

	pCanvas->UnlockBits(&bitmapDataCanvas);
	pMask->UnlockBits(&bitmapDataMask);

	return true;
}

bool Canvas::ApplyColorToMask(
	Gdiplus::Color clr, 
	Gdiplus::Bitmap* pMask, 
	Gdiplus::Bitmap* pCanvas, 
	Gdiplus::Color maskColor,
	Gdiplus::Point offset)
{
	if(pMask==NULL||pCanvas==NULL)
		return false;

	UINT* pixelsMask = NULL;
	UINT* pixelsCanvas = NULL;

	using namespace Gdiplus;

	BitmapData bitmapDataMask;
	BitmapData bitmapDataCanvas;
	Rect rectCanvas(0, 0, pCanvas->GetWidth(), pCanvas->GetHeight() );
	Rect rectMask(0, 0, pMask->GetWidth(), pMask->GetHeight() );

	pMask->LockBits(
		&rectMask,
		ImageLockModeRead,
		PixelFormat32bppARGB,
		&bitmapDataMask );

	pCanvas->LockBits(
		&rectCanvas,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataCanvas );

	pixelsMask = (UINT*)bitmapDataMask.Scan0;
	pixelsCanvas = (UINT*)bitmapDataCanvas.Scan0;

	if( !pixelsMask || !pixelsCanvas )
		return false;

	UINT col = 0;
	int stride = bitmapDataCanvas.Stride >> 2;
	for(UINT row = 0; row < bitmapDataCanvas.Height; ++row)
	{
		for(col = 0; col < bitmapDataCanvas.Width; ++col)
		{
			if(row-offset.Y >= bitmapDataMask.Height || col-offset.X >= bitmapDataMask.Width||
				row-offset.Y < 0 || col-offset.X < 0)
				continue;

			UINT index = row * stride + col;
			UINT indexMask = (row-offset.Y) * (bitmapDataMask.Stride>>2) + (col-offset.X);

			BYTE nAlpha = 0;

			if(MaskColor::IsEqual(maskColor, MaskColor::Red()))
				nAlpha = (pixelsMask[indexMask] & 0xff0000)>>16;
			else if(MaskColor::IsEqual(maskColor, MaskColor::Green()))
				nAlpha = (pixelsMask[indexMask] & 0xff00)>>8;
			else if(MaskColor::IsEqual(maskColor, MaskColor::Blue()))
				nAlpha = pixelsMask[indexMask] & 0xff;

			UINT color = 0xff << 24 | clr.GetRed() << 16 | clr.GetGreen() << 8 | clr.GetBlue() ;

			if(nAlpha>0)
				pixelsCanvas[index] = Alphablend(pixelsCanvas[index], color, nAlpha, nAlpha);
		}
	}

	pCanvas->UnlockBits(&bitmapDataCanvas);
	pMask->UnlockBits(&bitmapDataMask);

	return true;
}

bool Canvas::ApplyShadowToMask(
	Gdiplus::Color clrShadow, 
	Gdiplus::Bitmap* pMask, 
	Gdiplus::Bitmap* pCanvas, 
	Gdiplus::Color maskColor)
{
	if(pMask==NULL||pCanvas==NULL)
		return false;

	UINT* pixelsMask = NULL;
	UINT* pixelsCanvas = NULL;

	using namespace Gdiplus;

	BitmapData bitmapDataMask;
	BitmapData bitmapDataCanvas;
	Rect rectCanvas(0, 0, pCanvas->GetWidth(), pCanvas->GetHeight() );
	Rect rectMask(0, 0, pMask->GetWidth(), pMask->GetHeight() );

	pMask->LockBits(
		&rectMask,
		ImageLockModeRead,
		PixelFormat32bppARGB,
		&bitmapDataMask );

	pCanvas->LockBits(
		&rectCanvas,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataCanvas );

	pixelsMask = (UINT*)bitmapDataMask.Scan0;
	pixelsCanvas = (UINT*)bitmapDataCanvas.Scan0;

	if( !pixelsMask || !pixelsCanvas )
		return false;

	UINT col = 0;
	int stride = bitmapDataCanvas.Stride >> 2;
	for(UINT row = 0; row < bitmapDataCanvas.Height; ++row)
	{
		for(col = 0; col < bitmapDataCanvas.Width; ++col)
		{
			if(row >= bitmapDataMask.Height || col >= bitmapDataMask.Width)
				continue;

			UINT index = row * stride + col;
			UINT indexMask = row * (bitmapDataMask.Stride>>2) + col;

			BYTE nAlpha = 0;

			if(MaskColor::IsEqual(maskColor, MaskColor::Red()))
				nAlpha = (pixelsMask[indexMask] & 0xff0000)>>16;
			else if(MaskColor::IsEqual(maskColor, MaskColor::Green()))
				nAlpha = (pixelsMask[indexMask] & 0xff00)>>8;
			else if(MaskColor::IsEqual(maskColor, MaskColor::Blue()))
				nAlpha = pixelsMask[indexMask] & 0xff;

			UINT color = 0xff << 24 | clrShadow.GetRed() << 16 | clrShadow.GetGreen() << 8 | clrShadow.GetBlue() ;

			if(nAlpha>0)
			{
				UINT maskAlpha = (pixelsMask[indexMask] >> 24);

				pixelsCanvas[index] = Alphablend(pixelsCanvas[index], color, maskAlpha, clrShadow.GetAlpha());
			}
		}
	}

	pCanvas->UnlockBits(&bitmapDataCanvas);
	pMask->UnlockBits(&bitmapDataMask);

	return true;
}

bool Canvas::ApplyShadowToMask(
	Gdiplus::Color clrShadow, 
	Gdiplus::Bitmap* pMask, 
	Gdiplus::Bitmap* pCanvas, 
	Gdiplus::Color maskColor,
	Gdiplus::Point offset)
{
	if(pMask==NULL||pCanvas==NULL)
		return false;

	UINT* pixelsMask = NULL;
	UINT* pixelsCanvas = NULL;

	using namespace Gdiplus;

	BitmapData bitmapDataMask;
	BitmapData bitmapDataCanvas;
	Rect rectCanvas(0, 0, pCanvas->GetWidth(), pCanvas->GetHeight() );
	Rect rectMask(0, 0, pMask->GetWidth(), pMask->GetHeight() );

	pMask->LockBits(
		&rectMask,
		ImageLockModeRead,
		PixelFormat32bppARGB,
		&bitmapDataMask );

	pCanvas->LockBits(
		&rectCanvas,
		ImageLockModeWrite,
		PixelFormat32bppARGB,
		&bitmapDataCanvas );

	pixelsMask = (UINT*)bitmapDataMask.Scan0;
	pixelsCanvas = (UINT*)bitmapDataCanvas.Scan0;

	if( !pixelsMask || !pixelsCanvas )
		return false;

	UINT col = 0;
	int stride = bitmapDataCanvas.Stride >> 2;
	for(UINT row = 0; row < bitmapDataCanvas.Height; ++row)
	{
		for(col = 0; col < bitmapDataCanvas.Width; ++col)
		{
			if(row-offset.Y >= bitmapDataMask.Height || col-offset.X >= bitmapDataMask.Width||
				row-offset.Y < 0 || col-offset.X < 0)
				continue;

			UINT index = row * stride + col;
			UINT indexMask = (row-offset.Y) * (bitmapDataMask.Stride>>2) + (col-offset.X);

			BYTE nAlpha = 0;

			if(MaskColor::IsEqual(maskColor, MaskColor::Red()))
				nAlpha = (pixelsMask[indexMask] & 0xff0000)>>16;
			else if(MaskColor::IsEqual(maskColor, MaskColor::Green()))
				nAlpha = (pixelsMask[indexMask] & 0xff00)>>8;
			else if(MaskColor::IsEqual(maskColor, MaskColor::Blue()))
				nAlpha = pixelsMask[indexMask] & 0xff;

			UINT color = 0xff << 24 | clrShadow.GetRed() << 16 | clrShadow.GetGreen() << 8 | clrShadow.GetBlue() ;

			if(nAlpha>0)
			{
				UINT maskAlpha = (pixelsMask[indexMask] >> 24);

				pixelsCanvas[index] = Alphablend(pixelsCanvas[index], color, maskAlpha, clrShadow.GetAlpha());
			}
		}
	}

	pCanvas->UnlockBits(&bitmapDataCanvas);
	pMask->UnlockBits(&bitmapDataMask);

	return true;
}

bool Canvas::DrawTextImage(
	ITextStrategy* pStrategy, 
	Gdiplus::Bitmap* pImage, 
	Gdiplus::Point offset,
	TextContext* pTextContext)
{
	if(pStrategy==NULL||pImage==NULL||pTextContext==NULL)
		return false;

	Gdiplus::Graphics graphics((Gdiplus::Image*)(pImage));
	graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
	graphics.SetInterpolationMode(Gdiplus::InterpolationModeHighQualityBicubic);

	bool bRet = pStrategy->DrawString(&graphics,
		pTextContext->pFontFamily, 
		pTextContext->fontStyle, 
		pTextContext->nfontSize, 
		pTextContext->pszText, 
		Gdiplus::Point(pTextContext->ptDraw.X + offset.X, pTextContext->ptDraw.Y + offset.Y),
		&(pTextContext->strFormat));

	return bRet;
}

bool Canvas::DrawTextImage(
	ITextStrategy* pStrategy, 
	Gdiplus::Bitmap* pImage, 
	Gdiplus::Point offset,
	TextContext* pTextContext,
	Gdiplus::Matrix& mat)
{
	if(pStrategy==NULL||pImage==NULL||pTextContext==NULL)
		return false;

	Gdiplus::Graphics graphics((Gdiplus::Image*)(pImage));
	graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
	graphics.SetInterpolationMode(Gdiplus::InterpolationModeHighQualityBicubic);
	graphics.SetTransform(&mat);

	bool bRet = pStrategy->DrawString(&graphics,
		pTextContext->pFontFamily, 
		pTextContext->fontStyle, 
		pTextContext->nfontSize, 
		pTextContext->pszText, 
		Gdiplus::Point(pTextContext->ptDraw.X + offset.X, pTextContext->ptDraw.Y + offset.Y),
		&(pTextContext->strFormat));

	graphics.ResetTransform();

	return bRet;
}

inline UINT Canvas::AddAlpha(UINT dest, UINT source, BYTE nAlpha)
{
	if( 0 == nAlpha )
		return dest;

	if( 255 == nAlpha )
		return source;

	BYTE nSrcRed   = (source & 0xff0000) >> 16; 
	BYTE nSrcGreen = (source & 0xff00) >> 8; 
	BYTE nSrcBlue  = (source & 0xff); 

	BYTE nRed  = nSrcRed;
	BYTE nGreen= nSrcGreen;
	BYTE nBlue = nSrcBlue;

	return nAlpha << 24 | nRed << 16 | nGreen << 8 | nBlue;
}

inline UINT Canvas::AlphablendNoAlphaAtBoundary(UINT dest, UINT source, BYTE nAlpha, BYTE nAlphaFinal)
{
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

	return 0xff << 24 | nRed << 16 | nGreen << 8 | nBlue;
}

inline UINT Canvas::Alphablend(UINT dest, UINT source, BYTE nAlpha, BYTE nAlphaFinal)
{
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

	return nAlphaFinal << 24 | nRed << 16 | nGreen << 8 | nBlue;
}

inline UINT Canvas::PreMultipliedAlphablend(UINT dest, UINT source)
{
	BYTE nAlpha = (source & 0xff000000) >> 24;
	BYTE nInvAlpha = 255 - nAlpha;

	BYTE nSrcRed   = (source & 0xff0000) >> 16; 
	BYTE nSrcGreen = (source & 0xff00) >> 8; 
	BYTE nSrcBlue  = (source & 0xff); 

	BYTE nDestRed   = (dest & 0xff0000) >> 16; 
	BYTE nDestGreen = (dest & 0xff00) >> 8; 
	BYTE nDestBlue  = (dest & 0xff); 

	BYTE nRed  = nSrcRed   + ((nDestRed * nInvAlpha   )>>8);
	BYTE nGreen= nSrcGreen + ((nDestGreen * nInvAlpha )>>8);
	BYTE nBlue = nSrcBlue  + ((nDestBlue * nInvAlpha  )>>8);

	return 0xff << 24 | nRed << 16 | nGreen << 8 | nBlue;
}
