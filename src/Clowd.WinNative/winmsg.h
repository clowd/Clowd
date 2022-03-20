#pragma once
#include "pch.h"

std::wstring GetHitTestText(DWORD nchittest);
std::wstring GetWindowStyleText(DWORD dwStyle);
std::wstring GetWindowStyleExtendedText(DWORD dwStyle);
std::wstring GetWndMsgTxt(UINT msg);
void PrintWndMsg(UINT msg, bool mouse);