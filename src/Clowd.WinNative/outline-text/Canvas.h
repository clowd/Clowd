/*
Text Designer Outline Text Library 

Copyright (c) 2009 Wong Shao Voon

The Code Project Open License (CPOL)
http://www.codeproject.com/info/cpol10.aspx
*/

#ifndef _CANVAS_H_
#define _CANVAS_H_

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

//! structure to store the font info to pass as parameter to the drawing methods.
struct TextContext
{
public:
	TextContext()
		:
		pFontFamily(NULL),
		fontStyle(Gdiplus::FontStyleRegular),
		nfontSize(20),
		pszText(NULL),
		ptDraw(0, 0),
		strFormat()
	{
	}

	//! fontFamily is the font family
	Gdiplus::FontFamily* pFontFamily;
	//! fontStyle is the font style, eg, bold, italic or bold
	Gdiplus::FontStyle fontStyle;
	//! nfontSize is font size
	int nfontSize;
	//! pszText is the text to be displayed
	const wchar_t* pszText;
	//! ptDraw is the point to draw
	Gdiplus::Point ptDraw;
	//! strFormat is the string format
	Gdiplus::StringFormat strFormat;
};

//! class to draw text outlines
class Canvas
{
public:

	/** Generate Text Glow strategy

	@param clrText is the color of the text
	@param clrOutline is the color of the glow outline
	@param nThickness is the thickness of the outline in pixels
	@return valid ITextStrategy pointer if successful
	*/
	static ITextStrategy* TextGlow(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline, 
		int nThickness);

	/** Generate Text Glow strategy

	@param pbrushText is the brush of the text
	@param clrOutline is the color of the glow outline
	@param nThickness is the thickness of the outline in pixels
	@return valid ITextStrategy pointer if successful
	*/
	static ITextStrategy* TextGlow(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline, 
		int nThickness);

	/** Generate Text Outline strategy

	@param clrText is the color of the text
	@param clrOutline is the color of the outline
	@param nThickness is the thickness of the outline in pixels
	@return valid ITextStrategy pointer if successful
	*/
	static ITextStrategy* TextOutline(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline, 
		int nThickness);

	/** Generate Text Outline strategy

	@param pbrushText is the brush of the text
	@param clrOutline is the color of the outline
	@param nThickness is the thickness of the outline in pixels
	@return valid ITextStrategy pointer if successful
	*/
	static ITextStrategy* TextOutline(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline, 
		int nThickness);

	/** Setting Gradient Outlined Text effect
	
	@param[in]		clrText is the text color
	@param[in]		clrOutline1 is the inner outline color
	@param[in]		clrOutline2 is the outer outline color
	@param[in]		nThickness is the outline thickness
	*/
	static ITextStrategy* TextGradOutline(
		Gdiplus::Color clrText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness);

	/** Setting Gradient Outlined Text effect
	
	@param[in]		pbrushText is the text brush
	@param[in]		clrOutline1 is the inner outline color
	@param[in]		clrOutline2 is the outer outline color
	@param[in]		nThickness is the outline thickness
	*/
	static ITextStrategy* TextGradOutline(
		Gdiplus::Brush* pbrushText, 
		Gdiplus::Color clrOutline1, 
		Gdiplus::Color clrOutline2, 
		int nThickness);

	/** Setting just Text effect with no outline
	
	@param[in]		clrText is the text color
	*/
	static ITextStrategy* TextNoOutline(Gdiplus::Color clrText);

	/** Setting just Text effect with no outline
	
	@param[in]		pbrushText is the text brush
	*/
	static ITextStrategy* TextNoOutline(Gdiplus::Brush* pbrushText);

	/** Setting Outlined Text effect with no text fill
	
	@param[in]		clrOutline is the outline color
	@param[in]		nThickness is the outline thickness
	@param[in]		bRoundedEdge specifies rounded or sharp edges
	*/
	static ITextStrategy* TextOnlyOutline(
		Gdiplus::Color clrOutline, 
		int nThickness,
		bool bRoundedEdge);

	/** Generate a canvas image based on width and height

	@param width is the image width
	@param height is the image height
	@return a valid canvas image if successful
	*/
	static Gdiplus::Bitmap* GenImage(int width, int height);
	
	/** Generate a canvas image with gradients based on width and height

	@param width is the image width
	@param height is the image height
	@param vec is the vector of colors for the gradient
	@param bHorizontal specifies whether the gradient is horizontal
	@return a valid canvas image if successful
	*/
	static Gdiplus::Bitmap* GenImage(int width, int height, std::vector<Gdiplus::Color>& vec, bool bHorizontal);

	/** Generate a canvas image based on color, width and height

	@param width is the image width
	@param height is the image height
	@param clr is the color to paint the image
	@return a valid canvas image if successful
	*/
	static Gdiplus::Bitmap* GenImage(int width, int height, Gdiplus::Color clr);

	/** Generate a canvas image based on color, width and height

	@param width is the image width
	@param height is the image height
	@param clr is the color to paint the image
	@param alpha is alpha of the color to paint the image
	@return a valid canvas image if successful
	*/
	static Gdiplus::Bitmap* GenImage(int width, int height, Gdiplus::Color clr, BYTE alpha=0xff);

	/** Generate mask image of the text strategy.

	@param pStrategy is text strategy
	@param width is the mask image width
	@param height is the mask image height
	@param offset offset the text (typically used for shadows)
	@param pTextContext is text context
	@return a valid mask image if successful
	*/
	static Gdiplus::Bitmap* GenMask(
		ITextStrategy* pStrategy, 
		int width, 
		int height, 
		Gdiplus::Point offset,
		TextContext* pTextContext);

	/** Generate mask image of the text strategy.

	@param pStrategy is text strategy
	@param width is the mask image width
	@param height is the mask image height
	@param offset offset the text (typically used for shadows)
	@param pTextContext is text context
	@param mat is transform matrix
	@return a valid mask image if successful
	*/
	static Gdiplus::Bitmap* GenMask(
		ITextStrategy* pStrategy, 
		int width, 
		int height, 
		Gdiplus::Point offset,
		TextContext* pTextContext,
		Gdiplus::Matrix& mat);

	/** Measure the mask image based on the mask color.

	@param pMask is the mask image to be measured
	@param maskColor is mask color used in pMask image
	@param top returns the topmost Y 
	@param left returns the leftmost X
	@param bottom returns the bottommost Y
	@param right returns the rightmost X
	@return true if successful
	*/
	static bool MeasureMaskLength( 
		Gdiplus::Bitmap* pMask, 
		Gdiplus::Color maskColor,
		UINT& top,
		UINT& left,
		UINT& bottom,
		UINT& right);
	
	/** Apply image to mask onto the canvas

	@param pImage is the image to be used
	@param pMask is the mask image to be read
	@param pCanvas is the destination image to be draw upon
	@param maskColor is mask color used in pMask image
	@return true if successful
	*/
	static bool ApplyImageToMask(
		Gdiplus::Bitmap* pImage, 
		Gdiplus::Bitmap* pMask, 
		Gdiplus::Bitmap* pCanvas, 
		Gdiplus::Color maskColor,
		bool NoAlphaAtBoundary);

	/** Apply color to mask onto the canvas

	@param clr is the color to be used
	@param pMask is the mask image to be read
	@param pCanvas is the destination image to be draw upon
	@param maskColor is mask color used in pMask image
	@return true if successful
	*/
	static bool ApplyColorToMask(
		Gdiplus::Color clr, 
		Gdiplus::Bitmap* pMask, 
		Gdiplus::Bitmap* pCanvas, 
		Gdiplus::Color maskColor);


	/** Apply color to mask onto the canvas

	@param clr is the color to be used
	@param pMask is the mask image to be read
	@param pCanvas is the destination image to be draw upon
	@param maskColor is mask color used in pMask image
	@param offset determine how much to offset the mask
	@return true if successful
	*/
	static bool ApplyColorToMask(
		Gdiplus::Color clr, 
		Gdiplus::Bitmap* pMask, 
		Gdiplus::Bitmap* pCanvas, 
		Gdiplus::Color maskColor,
		Gdiplus::Point offset);

	/** Apply shadow to mask onto the canvas

	@param clr is the shadow color to be used
	@param pMask is the mask image to be read
	@param pCanvas is the destination image to be draw upon
	@param maskColor is mask color used in pMask image
	@return true if successful
	*/
	static bool ApplyShadowToMask(
		Gdiplus::Color clrShadow, 
		Gdiplus::Bitmap* pMask, 
		Gdiplus::Bitmap* pCanvas, 
		Gdiplus::Color maskColor);


	/** Apply shadow to mask onto the canvas

	@param clr is the shadow color to be used
	@param pMask is the mask image to be read
	@param pCanvas is the destination image to be draw upon
	@param maskColor is mask color used in pMask image
	@param offset determine how much to offset the mask
	@return true if successful
	*/
	static bool ApplyShadowToMask(
		Gdiplus::Color clrShadow, 
		Gdiplus::Bitmap* pMask, 
		Gdiplus::Bitmap* pCanvas, 
		Gdiplus::Color maskColor,
		Gdiplus::Point offset);


	/** Draw outline on image

	@param pStrategy is text strategy
	@param pImage is the image to be draw upon
	@param offset offset the text (typically used for shadows)
	@param pTextContext is text context
	@return true if successful
	*/
	static bool DrawTextImage(
		ITextStrategy* pStrategy, 
		Gdiplus::Bitmap* pImage, 
		Gdiplus::Point offset,
		TextContext* pTextContext);

	/** Draw outline on image

	@param pStrategy is text strategy
	@param pImage is the image to be draw upon
	@param offset offset the text (typically used for shadows)
	@param pTextContext is text context
	@param mat is transform matrix
	@return true if successful
	*/
	static bool DrawTextImage(
		ITextStrategy* pStrategy, 
		Gdiplus::Bitmap* pImage, 
		Gdiplus::Point offset,
		TextContext* pTextContext,
		Gdiplus::Matrix& mat);

private:
	/** Set alpha to color

	@param dest color in ARGB
	@param source color in ARGB
	@param nAlpha is alpha channel
	@return destination color
	*/
	static inline UINT AddAlpha(UINT dest, UINT source, BYTE nAlpha);
	/** Perform alphablend

	@param dest color in ARGB
	@param source color in ARGB
	@param nAlpha is alpha channel
	@param nAlphaFinal sets alpha channel of the returned UINT
	@return destination color
	*/
	static inline UINT AlphablendNoAlphaAtBoundary(UINT dest, UINT source, BYTE nAlpha, BYTE nAlphaFinal);
	/** Perform alphablend

	@param dest color in ARGB
	@param source color in ARGB
	@param nAlpha is alpha channel
	@param nAlphaFinal sets alpha channel of the returned UINT
	@return destination color
	*/
	static inline UINT Alphablend(UINT dest, UINT source, BYTE nAlpha, BYTE nAlphaFinal);

	/** Perform PreMultipliedAlphablend

	@param dest color in ARGB
	@param source color in ARGB
	@return destination color
	*/
	static inline UINT Canvas::PreMultipliedAlphablend(UINT dest, UINT source);

};

} // namespace TextDesigner

#endif // _CANVAS_H_