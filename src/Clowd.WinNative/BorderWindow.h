#pragma once
#include "pch.h"

class BorderWindow
{

private:
    bool _disposed;
    HINSTANCE _hInstance;
    HWND _window;
    //WndProcDel^ _del;
    uint16_t _lineWidth;
    System::Drawing::Color _lineColor;
    System::String _overlayTxt;
    System::Drawing::Rectangle _position;
    size_t _threadId;

    void UpdateLayer();
    static LRESULT CALLBACK WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

public:
    BorderWindow(System::Drawing::Color color, System::Drawing::Rectangle area);
    ~BorderWindow();
    void SetOverlayText(std::wstring& txt);

};
