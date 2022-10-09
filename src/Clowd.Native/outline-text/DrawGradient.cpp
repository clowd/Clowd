#include "pch.h"
#include "DrawGradient.h"
using namespace TextDesigner;

DrawGradient::DrawGradient(void)
{
}


DrawGradient::~DrawGradient(void)
{
}

bool DrawGradient::Draw(Gdiplus::Bitmap& bmp, const std::vector<Gdiplus::Color>& colors, bool bHorizontal)
{
	if(colors.size()==0)
		return false;

	using namespace Gdiplus;
	if(colors.size()==1)
	{
		Graphics graph(&bmp);
		SolidBrush brush(colors.at(0));
		graph.FillRectangle(&brush, 0, 0, bmp.GetWidth(), bmp.GetHeight());
	}
	else if(bHorizontal)
	{
		Graphics graph(&bmp);

		int gradRectNum = colors.size()-1;
		int gradWidth = bmp.GetWidth()/gradRectNum;
		int remainder = bmp.GetWidth()%gradRectNum;

		int TotalWidthRendered = 0;
		int WidthToBeRendered = 0;

		for(int i=0; i<gradRectNum; ++i)
		{
			int addRemainder = 0;
			if(i<remainder)
				addRemainder = 1;
			WidthToBeRendered = gradWidth + addRemainder;
			Rect rect(TotalWidthRendered-1, 0, WidthToBeRendered+1, bmp.GetHeight());
			LinearGradientBrush brush(rect, colors.at(i), colors.at(i+1), LinearGradientModeHorizontal);
			graph.FillRectangle(&brush, TotalWidthRendered, 0, WidthToBeRendered, bmp.GetHeight());
			TotalWidthRendered += WidthToBeRendered;
		}
	}
	else
	{
		Graphics graph(&bmp);

		int gradRectNum = colors.size()-1;
		int gradHeight = bmp.GetHeight()/gradRectNum;
		int remainder = bmp.GetHeight()%gradRectNum;

		int TotalHeightRendered = 0;
		int HeightToBeRendered = 0;
		for(int i=0; i<gradRectNum; ++i)
		{
			int addRemainder = 0;
			if(i<remainder)
				addRemainder = 1;
			HeightToBeRendered = gradHeight + addRemainder;
			Rect rect(0, TotalHeightRendered-1, bmp.GetWidth(), HeightToBeRendered+1);
			LinearGradientBrush brush(rect, colors.at(i), colors.at(i+1), LinearGradientModeVertical);
			graph.FillRectangle(&brush, 0, TotalHeightRendered, bmp.GetWidth(), HeightToBeRendered);
			TotalHeightRendered += HeightToBeRendered;
		}
	}
	return true;
}