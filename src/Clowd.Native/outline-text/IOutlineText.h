/*
Text Designer Outline Text Library 

Copyright (c) 2009 Wong Shao Voon

The Code Project Open License (CPOL)
http://www.codeproject.com/info/cpol10.aspx
*/


#ifndef _IOUTLINETEXT_H_
#define _IOUTLINETEXT_H_

#include <Gdiplus.h>

namespace TextDesigner
{

class IOutlineText
{
public:
	//!  default constructor
	//IOutlineText(void);
	//! destructor
	//virtual ~IOutlineText(void);

	/** Setting Text Glow effect
	
	@param[in]		clrText is the text color
	@param[in]		clrOutline is the outline color, alpha should be 12 to 32 for best glow effect
	@param[in]		nThickness is the outline thickness which should be 8 or greater for best glow effext
	*/
	virtual void TextGlow(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline, 
		int nThickness) = 0;

	virtual void TextGlow(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline, 
		int nThickness) = 0;

	/** Setting  Outlined Text effect
	
	@param[in]		clrText is the text color
	@param[in]		clrOutline is the outline color
	@param[in]		nThickness is the outline thickness
	*/
	virtual void TextOutline(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline, 
		int nThickness) = 0;

	virtual void TextOutline(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline, 
		int nThickness) = 0;

	/** Double Outline Text
	
	@param[in]		clrText is the text color
	@param[in]		clrOutline1 is the inner outline color
	@param[in]		clrOutline2 is the outer outline color
	@param[in]		nThickness1 is the inner outline thickness
	@param[in]		nThickness2 is the outer outline thickness
	*/
	virtual void TextDblOutline(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness1, 
		int nThickness2) = 0;

	virtual void TextDblOutline(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness1, 
		int nThickness2) = 0;

	/** Double Glow Text(Only the outer outline has the glow)
	
	@param[in]		clrText is the text color
	@param[in]		clrOutline1 is the inner outline color
	@param[in]		clrOutline2 is the outer outline color with an Alpha
	@param[in]		nThickness1 is the inner outline thickness
	@param[in]		nThickness2 is the outer outline thickness
	*/
	virtual void TextDblGlow(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness1, 
		int nThickness2) = 0;

	virtual void TextDblGlow(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness1, 
		int nThickness2) = 0;

	/** Setting Gradient Outlined Text effect
	
	@param[in]		clrText is the text color
	@param[in]		clrOutline1 is the inner outline color
	@param[in]		clrOutline2 is the outer outline color
	@param[in]		nThickness is the outline thickness
	*/
	virtual void TextGradOutline(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness) = 0;

	virtual void TextGradOutline(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness) = 0;

	/** Setting just Text effect with no outline
	
	@param[in]		clrText is the text color
	*/
	virtual void TextNoOutline(Gdiplus::Color clrText) = 0;
	virtual void TextNoOutline(Gdiplus::Brush* pbrushText) = 0;

	/** Setting Outlined Text effect with no text fill
	
	@param[in]		clrOutline is the outline color
	@param[in]		nThickness is the outline thickness
	@param[in]		bRoundedEdge specifies rounded or sharp edges
	*/
	virtual void TextOnlyOutline(
		Gdiplus::Color clrOutline, 
		int nThickness,
		bool bRoundedEdge) = 0;

	//! Set the shadow bitmap with a bitmap
	virtual void SetShadowBkgd(Gdiplus::Bitmap* pBitmap) = 0;

	//! Set the shadow bitmap with a color, width and height
	virtual void SetShadowBkgd(Gdiplus::Color clrBkgd, int nWidth, int nHeight) = 0;

	//! Set text effect to nothing, sometimes example to get the shadow for OpenGL
	virtual void SetNullTextEffect() = 0;

	//! Set shadow to nothing, reseting previous shadow settings
	virtual void SetNullShadow() = 0;
	
	//! Enable shadow
	virtual void EnableShadow(bool bEnable) = 0;

	/** Shadow Settings
	
	@param[in]		color is the text color
	@param[in]		nThickness is the shadow outline thickness
	@param[in]		ptOffset is the relative offset
	*/
	virtual void Shadow(
		Gdiplus::Color color, 
		int nThickness,
		Gdiplus::Point ptOffset) = 0;

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
	virtual bool DrawString(
		Gdiplus::Graphics* pGraphics, 
		Gdiplus::FontFamily* pFontFamily,
		Gdiplus::FontStyle fontStyle,
		int nfontSize,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw, 
		Gdiplus::StringFormat* pStrFormat) = 0;

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
	virtual bool DrawString(
		Gdiplus::Graphics* pGraphics, 
		Gdiplus::FontFamily* pFontFamily,
		Gdiplus::FontStyle fontStyle,
		int nfontSize,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw,
		Gdiplus::StringFormat* pStrFormat) = 0;

	/** Draw String using a starting point. Using GDI paths, instead of GDI+ paths
	
	@param[in]		pGraphics is the graphics context
	@param[in]		pLogFont is the LOGFONT from which the font will be created.
	@param[in]		pszText is the text which is displayed.
	@param[in]		ptDraw is the staring point to draw
	@return true for success
	*/
	virtual bool GdiDrawString(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw) = 0;

	/** Draw String using a rectangle. Using GDI paths, instead of GDI+ paths
	
	@param[in]		pGraphics is the graphics context
	@param[in]		pLogFont is the LOGFONT from which the font will be created.
	@param[in]		pszText is the text which is displayed.
	@param[in]		rtDraw is the rectangle where the whole drawing will be centralized
	@return true for success
	*/
	virtual bool GdiDrawString(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw) = 0;

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
	virtual bool MeasureString(
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
		float* pfDestHeight ) = 0;

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
	virtual bool MeasureString(
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
		float* pfDestHeight ) = 0;

	/** Measure String using a starting point. Using GDI paths, instead of GDI+ paths

	@param[in]		pGraphics is the graphics context
	@param[in]		pLogFont is the LOGFONT from which the font will becreated.
	@param[in]		pszText is the text which is displayed.
	@param[in]		ptDraw is the staring point to draw
	@param[out]		pfDestWidth is the destination pixels width
	@param[out]		pfDestHeight is the destination pixels height
	@return true for success
	*/
	virtual bool GdiMeasureString(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw,
		float* pfPixelsStartX,
		float* pfPixelsStartY,
		float* pfDestWidth,
		float* pfDestHeight ) = 0;

	/** Measure String using a rectangle. Using GDI paths, instead of GDI+ paths

	@param[in]		pGraphics is the graphics context
	@param[in]		pLogFont is the LOGFONT from which the font will becreated.
	@param[in]		pszText is the text which is displayed.
	@param[in]		rtDraw is the rectangle where the whole drawing will be centralized
	@param[out]		pfDestWidth is the destination pixels width
	@param[out]		pfDestHeight is the destination pixels height
	@return true for success
	*/
	virtual bool GdiMeasureString(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw,
		float* pfPixelsStartX,
		float* pfPixelsStartY,
		float* pfDestWidth,
		float* pfDestHeight ) = 0;

	/** Measure String using a starting point. Using GDI paths, instead of GDI+ paths

	@param[in]		pGraphics is the graphics context
	@param[in]		pLogFont is the LOGFONT from which the font will becreated.
	@param[in]		pszText is the text which is displayed.
	@param[in]		ptDraw is the staring point to draw
	@param[out]		pfDestWidth is the destination pixels width
	@param[out]		pfDestHeight is the destination pixels height
	@return true for success
	*/
	virtual bool GdiMeasureStringRealHeight(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Point ptDraw,
		float* pfPixelsStartX,
		float* pfPixelsStartY,
		float* pfDestWidth,
		float* pfDestHeight ) = 0;

	/** Measure String using a rectangle. Using GDI paths, instead of GDI+ paths

	@param[in]		pGraphics is the graphics context
	@param[in]		pLogFont is the LOGFONT from which the font will becreated.
	@param[in]		pszText is the text which is displayed.
	@param[in]		rtDraw is the rectangle where the whole drawing will be centralized
	@param[out]		pfDestWidth is the destination pixels width
	@param[out]		pfDestHeight is the destination pixels height
	@return true for success
	*/
	virtual bool GdiMeasureStringRealHeight(
		Gdiplus::Graphics* pGraphics, 
		LOGFONTW* pLogFont,
		const wchar_t*pszText, 
		Gdiplus::Rect rtDraw,
		float* pfPixelsStartX,
		float* pfPixelsStartY,
		float* pfDestWidth,
		float* pfDestHeight ) = 0;

};

} // namespace TextDesigner

#endif // _IOUTLINETEXT_H_