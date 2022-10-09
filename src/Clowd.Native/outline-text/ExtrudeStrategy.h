/*
Text Designer Outline Text Library 

Copyright (c) 2009 Wong Shao Voon

The Code Project Open License (CPOL)
http://www.codeproject.com/info/cpol10.aspx
*/

#ifndef _EXTRUDESTRATEGY_H_
#define _EXTRUDESTRATEGY_H_

#include <Gdiplus.h>
#include "ITextStrategy.h"

namespace TextDesigner
{

class ExtrudeStrategy : public ITextStrategy
{
public:
	//!  default constructor
	ExtrudeStrategy(void);
	//! destructor
	virtual ~ExtrudeStrategy(void);

	ITextStrategy* Clone();

	/** Initialize the strategy
	
	@param[in]		clrText is the text color
	@param[in]		clrOutline is the outline color
	@param[in]		nThickness is the outline thickness
	*/
	void Init(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline, 
		int nThickness,
		int nOffsetX,
		int nOffsetY );

	/** Initialize the strategy
	
	@param[in]		pbrushText is the text brush(Must cleaned up by caller)
	@param[in]		clrOutline is the outline color
	@param[in]		nThickness is the outline thickness
	*/
	void Init(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline, 
		int nThickness,
		int nOffsetX,
		int nOffsetY );

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

protected:
	//! m_clrText is the text color
	Gdiplus::Color m_clrText;
	//! m_clrOutline is the outline color
	Gdiplus::Color m_clrOutline;
	//! m_nThickness is the outline thickness
	int m_nThickness;
	//! X Direction of Extrude
	int m_nOffsetX;
	//! Y Direction of Extrude
	int m_nOffsetY;
	//! Text Brush
	Gdiplus::Brush* m_pbrushText; 
	//! Using color or brush for text
	bool m_bClrText;

};

} // namespace TextDesigner

#endif // _EXTRUDESTRATEGY_H_
