/*
Text Designer Outline Text Library 

Copyright (c) 2009 Wong Shao Voon

The Code Project Open License (CPOL)
http://www.codeproject.com/info/cpol10.aspx
*/


#ifndef _PNGOUTLINETEXT_H_
#define _PNGOUTLINETEXT_H_

#include <Gdiplus.h>
#include "IOutlineText.h"
#include "ITextStrategy.h"
#include "TextDblOutlineStrategy.h"
#include "TextOutlineStrategy.h"
#include "TextGlowStrategy.h"
#include "DiffusedShadowStrategy.h"
#include "ExtrudeStrategy.h"
#include "TextGradOutlineStrategy.h"
#include "TextOnlyOutlineStrategy.h"
#include "TextNoOutlineStrategy.h"
#include "TextDblGlowStrategy.h"

namespace TextDesigner
{

class PngOutlineText : public IOutlineText
{
public:
	//!  default constructor
	PngOutlineText(void);
	//!  copy constructor
	PngOutlineText(PngOutlineText* rhs);
	//! destructor
	virtual ~PngOutlineText(void);
	//! assignment operator
	PngOutlineText& operator=(PngOutlineText& rhs);

	/** Setting Text Glow effect
	
	@param[in]		clrText is the text color
	@param[in]		clrOutline is the outline color, alpha should be 12 to 32 for best glow effect
	@param[in]		nThickness is the outline thickness which should be 8 or greater for best glow effext
	*/
	void TextGlow(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline, 
		int nThickness);

	void TextGlow(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline, 
		int nThickness);

	/** Setting  Outlined Text effect
	
	@param[in]		clrText is the text color
	@param[in]		clrOutline is the outline color
	@param[in]		nThickness is the outline thickness
	*/
	void TextOutline(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline, 
		int nThickness);

	void TextOutline(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline, 
		int nThickness);

	/** Double Outline Text
	
	@param[in]		clrText is the text color
	@param[in]		clrOutline1 is the inner outline color
	@param[in]		clrOutline2 is the outer outline color
	@param[in]		nThickness1 is the inner outline thickness
	@param[in]		nThickness2 is the outer outline thickness
	*/
	void TextDblOutline(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness1, 
		int nThickness2);

	void TextDblOutline(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness1, 
		int nThickness2);

	/** Double Glow Text(Only the outer outline has the glow)
	
	@param[in]		clrText is the text color
	@param[in]		clrOutline1 is the inner outline color
	@param[in]		clrOutline2 is the outer outline color with an Alpha
	@param[in]		nThickness1 is the inner outline thickness
	@param[in]		nThickness2 is the outer outline thickness
	*/
	void TextDblGlow(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness1, 
		int nThickness2);

	void TextDblGlow(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness1, 
		int nThickness2);

	/** Setting Gradient Outlined Text effect
	
	@param[in]		clrText is the text color
	@param[in]		clrOutline1 is the inner outline color
	@param[in]		clrOutline2 is the outer outline color
	@param[in]		nThickness is the outline thickness
	*/
	void TextGradOutline(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness);

	void TextGradOutline(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness);

	/** Setting just Text effect with no outline
	
	@param[in]		clrText is the text color
	*/
	void TextNoOutline(Gdiplus::Color clrText);
	void TextNoOutline(Gdiplus::Brush* pbrushText);

	/** Setting Outlined Text effect with no text fill
	
	@param[in]		clrOutline is the outline color
	@param[in]		nThickness is the outline thickness
	@param[in]		bRoundedEdge specifies rounded or sharp edges
	*/
	void TextOnlyOutline(
		Gdiplus::Color clrOutline, 
		int nThickness,
		bool bRoundedEdge);

	//! Set destination bitmap
	void SetPngImage(Gdiplus::Bitmap* pBitmap);

	//! Get destination bitmap
	Gdiplus::Bitmap* GetPngImage();

	//! Set the shadow bitmap with a bitmap
	void SetShadowBkgd(Gdiplus::Bitmap* pBitmap);

	//! Set the shadow bitmap with a color, width and height
	void SetShadowBkgd(Gdiplus::Color clrBkgd, int nWidth, int nHeight);

	//! Set text effect to nothing
	void SetNullTextEffect();

	//! Set shadow to nothing, reseting previous shadow settings
	void SetNullShadow();
	
	//! Enable shadow
	void EnableShadow(bool bEnable);

	bool IsShadowEnabled() { return m_bEnableShadow; }

	void EnableReflection(bool bEnable) { m_bEnableReflection = bEnable; }

	bool IsReflectionEnabled() { return m_bEnableReflection; }

	/** Reflection Settings
	
	@param[in]		fBegAlpha is the beginning transparency to use(0.0 - 1.0)
	@param[in]		fEndAlpha is the ending transparency to use(0.0 - 1.0)(Can be the same as fBegAlpha)
	@param[in]		fShown is how much of the relection image can be seen.
	@param[in]		fGap is the gap between the original image and the reflection image in pixel.
	*/
	void Reflection(float fBegAlpha, float fEndAlpha, float fShown);

	Gdiplus::Bitmap* GetReflectionPngImage() { if(m_bEnableReflection) return m_pReflectionPngBitmap; return NULL; }

	/** Shadow Settings
	
	@param[in]		color is the text color
	@param[in]		nThickness is the shadow outline thickness
	@param[in]		ptOffset is the relative offset
	*/
	void Shadow(
		Gdiplus::Color color, 
		int nThickness,
		Gdiplus::Point ptOffset);

	/** Diffused Shadow Settings

	@param[in]		color is the text color
	@param[in]		nThickness is the shadow outline thickness
	@param[in]		ptOffset is the relative offset
	*/
	void DiffusedShadow(
		Gdiplus::Color color, 
		int nThickness,
		Gdiplus::Point ptOffset);

	/** Extrude the font body
	
	@param[in]		color is the text color
	@param[in]		nThickness is the shadow outline thickness
	@param[in]		ptOffset is the direction of Extrude
	*/
	void Extrude(
		Gdiplus::Color color, 
		int nThickness,
		Gdiplus::Point ptOffset);

	/** Draw String, using a point as the starting point
	
	@param[in]		pGraphics is the graphics context
	@param[in]		pFontFamily is font family which is used(Collection of similar fonts)
	@param[in]		fontStyle like Bold, Italic or Regular should be specified.
	@param[in]		nfontSize is font size
	@param[in]		pszText is the text which is displayed.
	@param[in]		ptDraw is the staring point to draw
	@param[in]		pStrFormat is the string format to be specified(can be left at default)
	@return true for success
	*/
	bool DrawString(
		Gdiplus::Graphics* pGraphics, 
		Gdiplus::FontFamily* pFontFamily,
		Gdiplus::FontStyle fontStyle,
		int nfontSize,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw, 
		Gdiplus::StringFormat* pStrFormat);

	/** Draw String, using a rectangle
	
	@param[in]		pGraphics is the graphics context
	@param[in]		pFontFamily is font family which is used(Collection of similar fonts)
	@param[in]		fontStyle like Bold, Italic or Regular should be specified.
	@param[in]		nfontSize is font size
	@param[in]		pszText is the text which is displayed.
	@param[in]		rtDraw is the rectangle where the whole drawing will be centralized
	@param[in]		pStrFormat is the string format to be specified(can be left at default)
	@return true for success
	*/
	bool DrawString(
		Gdiplus::Graphics* pGraphics, 
		Gdiplus::FontFamily* pFontFamily,
		Gdiplus::FontStyle fontStyle,
		int nfontSize,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw,
		Gdiplus::StringFormat* pStrFormat);

	/** Draw String using a starting point. Using GDI paths, instead of GDI+ paths
	
	@param[in]		pGraphics is the graphics context
	@param[in]		pLogFont is the LOGFONT from which the font will be created.
	@param[in]		pszText is the text which is displayed.
	@param[in]		ptDraw is the staring point to draw
	@return true for success
	*/
	bool GdiDrawString(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw);

	/** Draw String using a rectangle. Using GDI paths, instead of GDI+ paths
	
	@param[in]		pGraphics is the graphics context
	@param[in]		pLogFont is the LOGFONT from which the font will be created.
	@param[in]		pszText is the text which is displayed.
	@param[in]		rtDraw is the rectangle where the whole drawing will be centralized
	@return true for success
	*/
	bool GdiDrawString(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw);

	/** Measure String, using a point as the starting point

	@param[in]		pGraphics is the graphics context
	@param[in]		pFontFamily is font family which is used(Collection of similar fonts)
	@param[in]		fontStyle like Bold, Italic or Regular should be specified.
	@param[in]		nfontSize is font size
	@param[in]		pszText is the text which is displayed.
	@param[in]		ptDraw is the staring point to draw
	@param[in]		pStrFormat is the string format to be specified(can be left at default)
	@param[out]		pfDestWidth is the destination pixels width
	@param[out]		pfDestHeight is the destination pixels height
	@return true for success
	*/
	bool MeasureString(
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
		float* pfDestHeight );

	/** Measure String, using a rectangle

	@param[in]		pGraphics is the graphics context
	@param[in]		pFontFamily is font family which is used(Collection of similar fonts)
	@param[in]		fontStyle like Bold, Italic or Regular should be specified.
	@param[in]		nfontSize is font size
	@param[in]		pszText is the text which is displayed.
	@param[in]		rtDraw is the rectangle where the whole drawing will be centralized
	@param[in]		pStrFormat is the string format to be specified(can be left at default)
	@param[out]		pfDestWidth is the destination pixels width
	@param[out]		pfDestHeight is the destination pixels height
	@return true for success
	*/
	bool MeasureString(
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
		float* pfDestHeight );

	/** Measure String using a starting point. Using GDI paths, instead of GDI+ paths

	@param[in]		pGraphics is the graphics context
	@param[in]		pLogFont is the LOGFONT from which the font will becreated.
	@param[in]		pszText is the text which is displayed.
	@param[in]		ptDraw is the staring point to draw
	@param[out]		pfDestWidth is the destination pixels width
	@param[out]		pfDestHeight is the destination pixels height
	@return true for success
	*/
	bool GdiMeasureString(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw,
		float* pfPixelsStartX,
		float* pfPixelsStartY,
		float* pfDestWidth,
		float* pfDestHeight );

	/** Measure String using a rectangle. Using GDI paths, instead of GDI+ paths

	@param[in]		pGraphics is the graphics context
	@param[in]		pLogFont is the LOGFONT from which the font will becreated.
	@param[in]		pszText is the text which is displayed.
	@param[in]		rtDraw is the rectangle where the whole drawing will be centralized
	@param[out]		pfDestWidth is the destination pixels width
	@param[out]		pfDestHeight is the destination pixels height
	@return true for success
	*/
	bool GdiMeasureString(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw,
		float* pfPixelsStartX,
		float* pfPixelsStartY,
		float* pfDestWidth,
		float* pfDestHeight );

	/** Measure String using a starting point. Using GDI paths, instead of GDI+ paths

	@param[in]		pGraphics is the graphics context
	@param[in]		pLogFont is the LOGFONT from which the font will becreated.
	@param[in]		pszText is the text which is displayed.
	@param[in]		ptDraw is the staring point to draw
	@param[out]		pfDestWidth is the destination pixels width
	@param[out]		pfDestHeight is the destination pixels height
	@return true for success
	*/
	bool GdiMeasureStringRealHeight(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw,
		float* pfPixelsStartX,
		float* pfPixelsStartY,
		float* pfDestWidth,
		float* pfDestHeight );

	/** Measure String using a rectangle. Using GDI paths, instead of GDI+ paths

	@param[in]		pGraphics is the graphics context
	@param[in]		pLogFont is the LOGFONT from which the font will becreated.
	@param[in]		pszText is the text which is displayed.
	@param[in]		rtDraw is the rectangle where the whole drawing will be centralized
	@param[out]		pfDestWidth is the destination pixels width
	@param[out]		pfDestHeight is the destination pixels height
	@return true for success
	*/
	bool GdiMeasureStringRealHeight(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw,
		float* pfPixelsStartX,
		float* pfPixelsStartY,
		float* pfDestWidth,
		float* pfDestHeight );

	//! Save to PNG file format
	bool SavePngFile(const wchar_t* pszFile);

	//! Save to PNG file format
	bool SaveReflectionPngFile(const wchar_t* pszFile);

	//! Reduce the alpha channel by multiplying by nMultiplyAlpha/255
	//! Make more transparent, using this method.
	bool MultiplyAlpha(BYTE nMultiplyAlpha);

	//! Bit blit the src image to the dest image. (Parameters are self-explanatory)
	static bool BitBlt(
		Gdiplus::Bitmap* pBmpSrc,
		UINT nSrcStartX,
		UINT nSrcStartY,
		UINT nSrcWidth,
		UINT nSrcHeight,
		Gdiplus::Bitmap* pBmpDest,
		UINT nDestStartX,
		UINT nDestStartY);

protected:
	void Init(PngOutlineText* rhs);
	/** Render Font Body Shadow(not the outline!)  using a specified starting point, using GDI+ paths
	
	@param[in]		pGraphics is the graphics context
	@param[in]		pFontFamily is font family which is used(Collection of similar fonts)
	@param[in]		fontStyle like Bold, Italic or Regular should be specified.
	@param[in]		nfontSize is font size
	@param[in]		pszText is the text which is displayed.
	@param[in]		ptDraw is the staring point to draw
	@param[in]		pStrFormat is the string format to be specified(can be left at default)
	*/
	bool RenderFontShadow(
		Gdiplus::Graphics* pGraphicsDrawn, 
		Gdiplus::Graphics* pGraphicsMask, 
		Gdiplus::Bitmap* pBitmapDrawn,
		Gdiplus::Bitmap* pBitmapMask,
		Gdiplus::FontFamily* pFontFamily,
		Gdiplus::FontStyle fontStyle,
		int nfontSize,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw, 
		Gdiplus::StringFormat* pStrFormat);

	/** Render Font Body Shadow(not the outline!) using a specified rectangle, using GDI+ paths
	
	@param[in]		pGraphics is the graphics context
	@param[in]		pFontFamily is font family which is used(Collection of similar fonts)
	@param[in]		fontStyle like Bold, Italic or Regular should be specified.
	@param[in]		nfontSize is font size
	@param[in]		pszText is the text which is displayed.
	@param[in]		rtDraw is the rectangle where the whole drawing will be centralized
	@param[in]		pStrFormat is the string format to be specified(can be left at default)
	*/
	bool RenderFontShadow(
		Gdiplus::Graphics* pGraphicsDrawn, 
		Gdiplus::Graphics* pGraphicsMask,
		Gdiplus::Bitmap* pBitmapDrawn,
		Gdiplus::Bitmap* pBitmapMask,
		Gdiplus::FontFamily* pFontFamily,
		Gdiplus::FontStyle fontStyle,
		int nfontSize,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw, 
		Gdiplus::StringFormat* pStrFormat);

	/** Render Font Body Shadow(not the outline!)  using a specified starting point, using GDI paths
	
	@param[in]		pGraphicsDrawn is the destination graphics context
	@param[in]		pGraphicsMask is the masked graphics context
	@param[in]		pBitmapDrawn is the destination bitmap
	@param[in]		pBitmapMask is the masked bitmap
	@param[in]		pLogFont is the LOGFONT from which the font will be created.
	@param[in]		pszText is the text which is displayed.
	@param[in]		ptDraw is the staring point to draw
	*/
	bool GdiRenderFontShadow(	
		Gdiplus::Graphics* pGraphicsDrawn, 
		Gdiplus::Graphics* pGraphicsMask,
		Gdiplus::Bitmap* pBitmapDrawn,
		Gdiplus::Bitmap* pBitmapMask,
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw);

	/** Render Font Body Shadow(not the outline!)  using a specified rectangle, using GDI paths
	
	@param[in]		pGraphics is the graphics context
	@param[in]		pLogFont is the LOGFONT from which the font will be created.
	@param[in]		pszText is the text which is displayed.
	@param[in]		rtDraw is the rectangle where the whole drawing will be centralized
	*/
	bool GdiRenderFontShadow(	
		Gdiplus::Graphics* pGraphicsDrawn, 
		Gdiplus::Graphics* pGraphicsMask,
		Gdiplus::Bitmap* pBitmapDrawn,
		Gdiplus::Bitmap* pBitmapMask,
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw);

	/** Alphablend function
	
	@param[in]		dest is the destination color
	@param[in]		source is the source color
	@param[in]		nAlpha is the alpha
	@param[in]		rtDraw is the rectangle where the whole drawing will be centralized
	@return blended color
	*/
	static inline UINT Alphablend(UINT dest, UINT source, BYTE nAlpha);

	bool RenderTransShadowA(
		Gdiplus::Graphics* pGraphics,
		Gdiplus::Graphics** ppGraphicsMask,
		Gdiplus::Bitmap** ppBmpMask,
		Gdiplus::Graphics** ppGraphicsDrawn,
		Gdiplus::Bitmap** ppBmpDrawn);

	bool RenderTransShadowB(
		Gdiplus::Graphics* pGraphics,
		Gdiplus::Graphics* pGraphicsMask,
		Gdiplus::Bitmap* pBmpMask,
		Gdiplus::Graphics* pGraphicsDrawn,
		Gdiplus::Bitmap* pBmpDrawn);

	//! Get saving image clsid(Copied from MSDN)
	bool GetEncoderClsid(const WCHAR* format, CLSID* pClsid);

	bool ProcessReflection();
	
	//! Text effect strategy polymorphic pointer
	ITextStrategy* m_pTextStrategy;
	//! Shadow effect strategy polymorphic pointer to draw the outline(Right now only 1 effect, TextOutlineStrategy)
	ITextStrategy* m_pShadowStrategy;
	//! Shadow effect strategy alpha mask polymorphic pointer to draw the outline
	ITextStrategy* m_pShadowStrategyMask;
	//! Object to draw the text body, not the outline
	ITextStrategy* m_pFontBodyShadow;
	//! Object to draw the text body alpha mask, not the outline
	ITextStrategy* m_pFontBodyShadowMask;
	//! Specify how much to offset the shadow relatively.
	Gdiplus::Point m_ptShadowOffset;
	//! Shadow color
	Gdiplus::Color m_clrShadow;
	//! Background Bitmap for the shadow because shadow is transparent.
	Gdiplus::Bitmap* m_pBkgdBitmap;
	//! Destination Bitmap to be rendered to.
	Gdiplus::Bitmap* m_pPngBitmap;
	//! Reflection Bitmap to be rendered to.
	Gdiplus::Bitmap* m_pReflectionPngBitmap;
	//! Enable Reflection
	bool m_bEnableReflection;
	//! Enable Shadow
	bool m_bEnableShadow;
	//! DiffuseShadow
	bool m_bDiffuseShadow;
	//! Shadow Thickness
	int m_nShadowThickness;
	//! Extrude the font body to look like 3D text
	bool m_bExtrudeShadow;
	//! Beginning alpha for the reflection image
	float m_fBegAlpha;
	//! Ending alpha for the reflection image
	float m_fEndAlpha; 
	//! How much of the reflection image to show
	float m_fShown;
};

} // namespace TextDesigner

#endif // _PNGOUTLINETEXT_H_