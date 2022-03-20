#pragma once
#include <WinGDI.h>

// StringType = std::string or std::wstring
template<typename StringType>
class NonSystemFontLoader
{
public:
	NonSystemFontLoader(const StringType& font_file)
	{
		m_FontFile = font_file;
		AddFontResourceEx(m_FontFile.c_str(), FR_PRIVATE, NULL);
	}
	~NonSystemFontLoader(void)
	{
		RemoveFontResourceEx(m_FontFile.c_str(), FR_PRIVATE, NULL);
	}
private:
	StringType m_FontFile;
};

