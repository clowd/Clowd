#pragma once
#include "pch.h"

#ifndef SCREENS_H
#define SCREENS_H

struct ScreenInfo {
	ScreenInfo(HMONITOR hMonitor, int index, UINT dpi, bool primary, RECT realWork, RECT realBounds, RECT virtualWork, RECT virtualBounds, std::wstring name) :
		hMonitor(hMonitor), index(index), dpi(dpi), primary(primary), realWork(realWork),
		realBounds(realBounds), workspaceWork(virtualWork), workspaceBounds(virtualBounds), name(name) {}

	HMONITOR hMonitor;
	int index;
	bool primary;
	RECT realWork;
	RECT realBounds;
	RECT workspaceWork;
	RECT workspaceBounds;
	UINT dpi;
	std::wstring name;
};

struct ScreenDebugDisplay {
	ScreenDebugDisplay(RECT renderRect, std::wstring detail) : renderRect(renderRect), detail(detail) {}
	RECT renderRect;
	std::wstring detail;
};

class Screens
{
private:
	int _virtX;
	int _virtY;
	RECT _workspace;
	POINT _anchor;
	ScreenInfo* _primary;
	std::vector<ScreenInfo> _monitors;

public:
	Screens();

	bool IsAnchorPt(POINT& pt);
	void MouseAnchorStart(POINTFF& position);
	void MouseAnchorUpdate(POINTFF& position, POINT& sysPt, double zoom);
	void MouseAnchorStop(POINTFF& position);

	POINTFF ToWorkspacePt(const POINT& systemPt);
	POINT ToSystemPt(const POINTFF& workspacePt);

	void TranslateToWorkspace(RECT& rect);
	void TranslateToSystem(RECT& rect);

	const RECT& WorkspaceBounds();
    const ScreenInfo& ScreenFromWorkspaceRect(RECT& rect);
    const ScreenInfo& ScreenFromSystemRect(RECT& rect);
    const ScreenInfo& ScreenFromWorkspacePt(POINTFF& pt);
	const ScreenInfo& ScreenFromSystemPt(POINT& pt);
	const ScreenInfo& ScreenFromHMONITOR(HMONITOR hMon);
	void GetDebugDetail(std::vector<ScreenDebugDisplay>& details, RECT& primaryDebugRect);
	int VirtX();
	int VirtY();
};

#endif