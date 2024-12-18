#pragma once
#include <GdiPlus.h>
#include <vector>

namespace TextDesigner
{

class DrawGradient
{
public:
	DrawGradient(void);
	~DrawGradient(void);

	static bool Draw(Gdiplus::Bitmap& bmp, const std::vector<Gdiplus::Color>& colors, bool bHorizontal);
};


} // ns TextDesigner
