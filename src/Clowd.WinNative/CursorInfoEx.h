#pragma once

class CursorData
{

private:
    HCURSOR _hCursor;
    ICONINFO _ii;

public:
    RECT Location;
    CursorData(HCURSOR hCursor, ICONINFO iconInfo, RECT r);
    ~CursorData();
    void DrawCursor(HDC hdc, int x, int y);

};

struct CCache
{
    DWORD id;
    const wchar_t* regKeyName;
};

class CursorInfoEx
{

private:
    std::map<HCURSOR, CCache> _cursors;

public:
    CursorInfoEx();
    std::unique_ptr<CursorData> SnapshotCurrent();

};

