/*
Text Designer Outline Text Library 

Copyright (c) 2009 Wong Shao Voon

The Code Project Open License (CPOL)
http://www.codeproject.com/info/cpol10.aspx
*/

#ifndef _MASKCOLOR_H_
#define _MASKCOLOR_H_

#include <Gdiplus.h>

namespace TextDesigner
{

//! Simple helper class to use mask color
class MaskColor
{
public:

	//! Method to return a primary red color to be used as mask
	static Gdiplus::Color Red()
	{
		return Gdiplus::Color(0xFF, 0, 0);
	}
	//! Method to return a primary green color to be used as mask
	static Gdiplus::Color Green()
	{
		return Gdiplus::Color(0, 0xFF, 0);
	}
	//! Method to return a primary blue color to be used as mask
	static Gdiplus::Color Blue()
	{
		return Gdiplus::Color(0, 0, 0xFF);
	}
	/** Method to compare 2 GDI+ color

	@param clr1 is 1st color
	@param clr2 is 2nd color
	@return true if equal
	*/
	static bool IsEqual(Gdiplus::Color clr1, Gdiplus::Color clr2)
	{
		if(clr1.GetR()==clr2.GetR()&&clr1.GetG()==clr2.GetG()&&clr1.GetB()==clr2.GetB())
			return true;

		return false;
	}

};

} // namespace TextDesigner

#endif // _MASKCOLOR_H_